using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace RemoteCli {

/// Local conversation metadata and work queue. A running item remains queued on disk until its result is committed.
/// Nothing is sent anywhere until the owner chooses a source (`off` by default): a tool on this computer or an
/// OpenAI-compatible API. A conversation is sent again only when what would be sent of it has changed.
internal sealed class Organizer {
    internal const string Off = "off", ToolSource = "tool", ApiSource = "api";
    internal const string Queued = "queued", Failed = "failed", Done = "done";
    /// How long a conversation must have been quiet before it is organized by itself.
    internal const long Rest = 10 * 60 * 1000;
    const long Interval = 15000;

    public sealed class Source {
        public string Tool = "", Id = "", Dir = "", Folder = "", File = "", Title = "";
        public bool Named;
        public long Updated;
        internal string Key { get { return Organizer.Key(Tool, Id); } }
    }

    public sealed class Metadata {
        public string Title = "", Summary = "";
        public string[] Tags = new string[0];
        public bool FollowUp;
        /// The tokens the request used, where its source told them; not kept.
        [ScriptIgnore] public long Tokens;
    }

    public sealed class Entry {
        public Source Source;
        public string State = "", Error = "", Model = "", ManualTitle = "";
        public bool? ManualFollowUp;
        public Metadata Data;
        public bool Automatic;
        /// What was last sent of the conversation (OrganizeHistory.Reading.Hash), and its file's time then.
        public string Hash = "";
        public long Seen;
        internal Entry Copy() { return (Entry)MemberwiseClone(); }
    }

    public sealed class Store {
        public long AutoSince;
        public bool AutoFollowUp;
        public string Source = Off, Tool = "", ApiUrl = "", ApiKey = "";     // ApiKey: protected for this Windows user
        public Dictionary<string, string> Chosen = new Dictionary<string, string>();   // source kind -> model
        public string Model = "";                                          // the Codex model chosen before there were sources
        public string Day = "";
        public int Calls;
        public long Tokens;
        public Dictionary<string, Entry> Entries = new Dictionary<string, Entry>();
    }

    /// What a request is sent to: "codex", "claude", "agy" or "api", and for an API its address and key.
    public sealed class Choice {
        public string Kind = "", Url = "", Key = "";
    }

    readonly object gate = new object();
    readonly string file;
    readonly Func<long, Source[]> catalog;
    readonly Func<Source, bool> readable, busy;
    readonly Func<string[]> tools;
    readonly Func<Choice, IOrganizeSource> open;
    readonly Func<long> clock;
    readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
    // conversations found not worth sending (short, a team's) at a file time: not read again until written to
    readonly Dictionary<string, long> passed = new Dictionary<string, long>();
    Store state;
    long nextAuto;
    string running = "";
    Entry awaitingSave;
    int working;
    internal event Action Changed;

    internal Organizer(string file, Func<long, Source[]> catalog, Func<Source, bool> readable, Func<Source, bool> busy,
            Func<string[]> tools, Func<Choice, IOrganizeSource> open, Func<long> clock = null) {
        this.file = file;
        this.catalog = catalog;
        this.readable = readable;
        this.busy = busy;
        this.tools = tools;
        this.open = open;
        this.clock = clock ?? (() => Now(DateTime.UtcNow));
    }

    internal static long Now(DateTime utc) { return (long)(utc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds; }
    internal static string Key(string tool, string id) { return tool + ":" + (id ?? "").ToLowerInvariant(); }
    internal static string Text(IDictionary<string, object> words, string field) {
        object value;
        return words != null && words.TryGetValue(field, out value) ? value as string ?? "" : "";
    }

    Store Loaded() {
        if (state == null) {
            state = File.Exists(file) ? json.Deserialize<Store>(File.ReadAllText(file, Encoding.UTF8)) : new Store();
            if (state == null || state.Entries == null) {
                state = null;
                throw new InvalidOperationException("对话整理记录无法读取");
            }
            if (state.Chosen == null) state.Chosen = new Dictionary<string, string>();
            if (!state.Chosen.ContainsKey("codex") && !String.IsNullOrEmpty(state.Model)) state.Chosen["codex"] = state.Model;
            // what earlier versions left waiting is not sent while organizing is off
            if (state.Source == Off) Unqueue(state);
        }
        return state;
    }

    void Save(Store next) {
        // Publish in memory only after the entire queue and its metadata can survive a restart.
        string temporary = file + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(file));
        File.WriteAllText(temporary, json.Serialize(next), new UTF8Encoding(false));
        if (File.Exists(file)) File.Replace(temporary, file, null);
        else File.Move(temporary, file);
        state = next;
    }

    Store Copy() {
        var c = Loaded();
        return new Store { AutoSince = c.AutoSince, AutoFollowUp = c.AutoFollowUp, Source = c.Source, Tool = c.Tool, ApiUrl = c.ApiUrl, ApiKey = c.ApiKey,
            Chosen = new Dictionary<string, string>(c.Chosen), Day = c.Day, Calls = c.Calls, Tokens = c.Tokens,
            Entries = new Dictionary<string, Entry>(c.Entries) };
    }

    static string Kind(Store s) { return s.Source == ToolSource ? s.Tool : s.Source == ApiSource ? "api" : ""; }
    static string Today() { return DateTime.Now.ToString("yyyy-MM-dd"); }
    static string Protect(string key) {
        return key.Length == 0 ? "" : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(key), null, DataProtectionScope.CurrentUser));
    }
    // "" where it cannot be read (kept by another Windows user): the API then refuses, and the owner enters it again
    static string Unprotect(string kept) {
        try { return kept.Length == 0 ? "" : Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(kept), null, DataProtectionScope.CurrentUser)); }
        catch (CryptographicException) { return ""; }
        catch (FormatException) { return ""; }
    }
    static Choice ChoiceOf(Store s) { return new Choice { Kind = Kind(s), Url = s.ApiUrl, Key = Unprotect(s.ApiKey) }; }

    internal string Title(string tool, string id, string original, bool named) {
        var metadata = ForList(tool, id, original, named);
        return metadata == null ? original : Text(metadata, "title");
    }

    internal Dictionary<string, object> ForList(string tool, string id, string original, bool named) {
        try {
            lock (gate) {
                var current = Loaded();
                Entry entry;
                if (!current.Entries.TryGetValue(Key(tool, id), out entry)) return null;
                return Detail(original, named, entry, current.AutoFollowUp);
            }
        } catch (IOException) { return null; }
          catch (UnauthorizedAccessException) { return null; }
          catch (ArgumentException) { return null; }
          catch (InvalidOperationException) { return null; }
    }

    static string DisplayTitle(string original, bool named, Entry entry) {
        if (entry.ManualTitle.Length > 0) return entry.ManualTitle;
        if (named) return original;
        if (entry.Source.Named) return entry.Source.Title;
        return entry.Data == null || entry.Data.Title.Length == 0 ? original : entry.Data.Title;
    }

    internal Dictionary<string, object> Operate(Dictionary<string, object> op) {
        switch (Text(op, "verb")) {
            case "status": return Status();
            case "settings": return Settings(op);
            case "list": return List(op);
            case "count": return Count(op);
            case "start": return Start(op, false);
            case "retry": return Start(op, true);
            case "edit": return Edit(op);
            default: throw new ArgumentException("整理操作无效");
        }
    }

    // The chosen source and its models, read outside the lock (a tool's list may take a while).
    sealed class Offer { public Store Store; public IOrganizeSource Source; public Dictionary<string, object>[] Models = new Dictionary<string, object>[0]; public string Model = "", Default = ""; }
    Offer Offered() {
        Store current;
        lock (gate) current = Loaded();
        var offer = new Offer { Store = current };
        string kind = Kind(current);
        if (kind.Length == 0) return offer;
        offer.Source = open(ChoiceOf(current));
        offer.Models = offer.Source.Models();
        offer.Default = offer.Source.DefaultModel(offer.Models);
        string chosen;
        offer.Model = current.Chosen.TryGetValue(kind, out chosen) && chosen.Length > 0 ? chosen : offer.Default;
        if (kind == "api" && offer.Model.Length > 0) offer.Models = new[] { new Dictionary<string, object> { { "id", offer.Model }, { "label", offer.Model } } };
        return offer;
    }
    static bool Available(Offer offer) {
        if (offer.Source == null || offer.Model.Length == 0) return false;
        return Kind(offer.Store) == "api" ? offer.Store.ApiUrl.Length > 0 : offer.Models.Any(m => Text(m, "id") == offer.Model);
    }

    Dictionary<string, object> Status() {
        var offer = Offered();
        var installed = tools();
        lock (gate) {
            var current = Loaded();
            var result = new Dictionary<string, object> {
                { "source", current.Source }, { "source_tool", current.Source == ToolSource ? current.Tool : "" },
                { "tools", OrganizeSources.Tools.Where(installed.Contains).Select(t => new Dictionary<string, object> { { "id", t }, { "label", Tool.Named(t).Label } }).ToArray() },
                { "api_url", current.ApiUrl }, { "api_key_set", current.ApiKey.Length > 0 },
                { "auto", current.AutoSince > 0 }, { "auto_follow_up", current.AutoFollowUp },
                { "model", offer.Model }, { "default_model", offer.Default },
                { "models", offer.Models }, { "model_available", Available(offer) },
                { "queued", current.Entries.Count(e => e.Value.State == Queued && e.Key != running) },
                { "running", running.Length > 0 ? 1 : 0 },
                { "done", current.Entries.Count(e => e.Value.State == Done) },
                { "failed", current.Entries.Count(e => e.Value.State == Failed) },
                { "calls_today", current.Day == Today() ? current.Calls : 0 }
            };
            if (current.Day == Today() && current.Tokens > 0) result["tokens_today"] = current.Tokens;
            return result;
        }
    }

    Dictionary<string, object> Settings(Dictionary<string, object> op) {
        string source = op.ContainsKey("source") ? Text(op, "source") : null, tool = op.ContainsKey("source_tool") ? Text(op, "source_tool") : null;
        string model = op.ContainsKey("model") ? Text(op, "model") : null, url = op.ContainsKey("api_url") ? Text(op, "api_url").Trim() : null;
        string key = op.ContainsKey("api_key") ? Text(op, "api_key").Trim() : null;
        object automatic, autoFollowUp;
        if (op.TryGetValue("auto", out automatic) && !(automatic is bool)) throw new ArgumentException("自动整理设置无效");
        if (op.TryGetValue("auto_follow_up", out autoFollowUp) && !(autoFollowUp is bool)) throw new ArgumentException("自动待跟进设置无效");
        if (source != null && source != Off && source != ToolSource && source != ApiSource) throw new ArgumentException("整理来源无效");
        if (tool != null && !tools().Contains(tool)) throw new ArgumentException("这台电脑上没有所选的 AI 工具");
        if (url != null && url.Length > 0 && !ApiOrganizer.ValidAddress(url)) throw new ArgumentException("API 地址需以 http:// 或 https:// 开头");
        if (key != null && (key.Length > 400 || key.Any(c => Char.IsControl(c) || Char.IsWhiteSpace(c)))) throw new ArgumentException("API 密钥格式无效");
        if (model != null && !Models.Valid(model)) throw new ArgumentException("模型名称无效");
        Store next;
        lock (gate) {
            next = Copy();
            if (source != null) next.Source = source;
            if (tool != null) next.Tool = tool;
            if (url != null) next.ApiUrl = url;
            if (key != null) next.ApiKey = Protect(key);
            if (next.Source == ToolSource && !tools().Contains(next.Tool)) throw new ArgumentException("请选择这台电脑上的 AI 工具");
            if (next.Source == ApiSource && next.ApiUrl.Length == 0) throw new ArgumentException("请填写 API 地址");
        }
        if (model != null) {
            // a tool's model must be one it lists; an API's is taken as the owner names it
            string kind = Kind(next);
            if (kind.Length == 0) throw new ArgumentException("请先选择整理来源");
            if (kind != "api") {
                var offered = open(ChoiceOf(next)).Models();
                if (!offered.Any(m => Text(m, "id") == model)) throw new ArgumentException("这台电脑没有列出所选的模型");
            }
        }
        lock (gate) {
            var latest = Copy();
            latest.Source = next.Source; latest.Tool = next.Tool; latest.ApiUrl = next.ApiUrl; latest.ApiKey = next.ApiKey;
            if (model != null) latest.Chosen[Kind(latest)] = model;
            if (automatic is bool) latest.AutoSince = (bool)automatic ? (latest.AutoSince > 0 ? latest.AutoSince : clock()) : 0;
            if (autoFollowUp is bool) latest.AutoFollowUp = (bool)autoFollowUp;
            if (latest.Source == Off) Unqueue(latest);
            Save(latest);
            nextAuto = 0;
        }
        Notify();
        return Status();
    }

    // Turned off: what waited is not sent, and stands as it did before it was queued.
    void Unqueue(Store next) {
        foreach (var pair in next.Entries.Where(e => e.Value.State == Queued && e.Key != running).ToArray()) {
            var entry = pair.Value.Copy();
            entry.State = entry.Data != null ? Done : "";
            entry.Automatic = false;
            next.Entries[pair.Key] = entry;
        }
    }

    Dictionary<string, object> List(Dictionary<string, object> op) {
        int offset, limit;
        if (!Int32.TryParse(Convert.ToString(op.ContainsKey("offset") ? op["offset"] : null), out offset)) offset = 0;
        if (!Int32.TryParse(Convert.ToString(op.ContainsKey("limit") ? op["limit"] : null), out limit)) limit = 50;
        string wanted = Target(op);
        var sources = catalog(0).Where(s => wanted.Length == 0 || s.Key == wanted).ToArray();
        lock (gate) {
            var current = Loaded();
            var rows = sources.Select(s => {
                Entry entry;
                if (!current.Entries.TryGetValue(s.Key, out entry)) entry = new Entry { Source = s };
                return Public(s, entry, current.AutoFollowUp);
            }).ToArray();
            offset = Math.Max(0, offset);
            limit = Math.Max(1, Math.Min(120, limit));
            return new Dictionary<string, object> { { "items", rows.Skip(offset).Take(limit).ToArray() },
                { "total", rows.Length }, { "more", (long)offset + limit < rows.Length } };
        }
    }

    static string Target(Dictionary<string, object> op) {
        string tool = Text(op, "tool"), session = Text(op, "session");
        if (tool.Length == 0 && session.Length == 0) return "";
        if (!Models.Tools.Contains(tool) || !SessionLocks.IsUuid(session)) throw new ArgumentException("对话标识无效");
        return Key(tool, session);
    }

    /// How many of the readable conversations "organize all" would send, before anything is sent.
    Dictionary<string, object> Count(Dictionary<string, object> op) {
        if (Text(op, "scope") != "all") throw new ArgumentException("整理范围无效");
        var sources = catalog(0);
        int needed = sources.Count(s => Needed(s, false));
        return new Dictionary<string, object> { { "needed", needed }, { "skipped", sources.Length - needed }, { "total", sources.Length } };
    }

    Dictionary<string, object> Start(Dictionary<string, object> op, bool retry) {
        string wanted = Target(op), scope = Text(op, "scope");
        if (wanted.Length == 0 && scope != "all") throw new ArgumentException("请选择一个对话或所有可读对话");
        lock (gate) {
            if (Loaded().Source == Off) throw new InvalidOperationException("对话整理已关闭，请先在整理设置里选择来源");
        }
        var sources = catalog(0).Where(s => wanted.Length == 0 || s.Key == wanted).ToArray();
        if (wanted.Length > 0 && sources.Length == 0) throw new ArgumentException("没有找到可读取的对话");
        // one conversation asked for by name is sent whatever its size; "all" leaves out what is not worth a request
        var chosen = retry ? sources : sources.Where(s => Needed(s, wanted.Length > 0)).ToArray();
        int added = Enqueue(chosen, false, retry, 0);
        return new Dictionary<string, object> { { "queued", added }, { "skipped", sources.Length - added } };
    }

    /// Whether a conversation would be sent: not waiting or running already, and what would be sent of it differs
    /// from what was sent last. Unless `named`, a short conversation or a team's is not sent either.
    bool Needed(Source source, bool named) {
        Entry entry;
        lock (gate) {
            if (source.Key == running) return false;
            Loaded().Entries.TryGetValue(source.Key, out entry);
            if (entry != null && (entry.State == Queued || (entry.Hash.Length > 0 && entry.Seen == source.Updated))) return false;
            // organized before contents were compared: unchanged while its file has not been written to since
            if (entry != null && entry.State == Done && entry.Hash.Length == 0 && entry.Source != null && entry.Source.Updated >= source.Updated) return false;
            // a failure is sent again by `retry`, not by itself
            if (!named && entry != null && entry.State == Failed && (entry.Hash.Length == 0 || entry.Seen == source.Updated)) return false;
            long at;
            if (!named && passed.TryGetValue(source.Key, out at) && at == source.Updated) return false;
        }
        OrganizeHistory.Reading reading;
        try { reading = OrganizeHistory.Read(source); }
        catch (InvalidOperationException) { return false; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        bool unchanged = entry != null && entry.Hash == reading.Hash;
        if (!named && (reading.Short || reading.Team || unchanged)) {
            lock (gate) passed[source.Key] = source.Updated;
            return false;
        }
        return !unchanged;
    }

    int Enqueue(Source[] sources, bool automatic, bool retry, long since) {
        int added = 0;
        bool changed = false;
        lock (gate) {
            var next = Copy();
            if (next.Source == Off || (automatic && next.AutoSince != since)) return 0;
            foreach (var source in sources) {
                Entry entry;
                if (next.Entries.TryGetValue(source.Key, out entry)) {
                    if (!retry && entry.State == Queued && entry.Automatic && !automatic) {
                        entry = entry.Copy();
                        entry.Automatic = false;
                        next.Entries[source.Key] = entry;
                        changed = true;
                    }
                    if (entry.State == Queued || source.Key == running || (retry && entry.State != Failed)) continue;
                } else {
                    if (retry) continue;
                    entry = new Entry();
                }
                entry = entry.Copy();
                entry.Source = source;
                entry.State = Queued;
                entry.Error = "";
                entry.Automatic = automatic;
                next.Entries[source.Key] = entry;
                added++;
            }
            if (added > 0 || changed) Save(next);
        }
        if (added > 0 || changed) Notify();
        return added;
    }

    static bool EffectiveFollowUp(Entry entry, bool autoFollowUp) {
        if (entry.ManualFollowUp.HasValue) return entry.ManualFollowUp.Value;
        return autoFollowUp && entry.Data != null && entry.Data.FollowUp;
    }

    Dictionary<string, object> Edit(Dictionary<string, object> op) {
        string wanted = Target(op), scope = Text(op, "scope");
        bool hasTitle = op.ContainsKey("title");
        object followUp;
        bool hasFollowUp = op.TryGetValue("follow_up", out followUp);
        if (scope == "all") {
            if (wanted.Length > 0 || hasTitle || !hasFollowUp || !(followUp is bool) || (bool)followUp)
                throw new ArgumentException("批量清除待跟进参数无效");
            int cleared = 0;
            lock (gate) {
                var next = Copy();
                foreach (var pair in next.Entries.ToArray()) {
                    if (!EffectiveFollowUp(pair.Value, next.AutoFollowUp)) continue;
                    var updated = pair.Value.Copy();
                    updated.ManualFollowUp = false;
                    next.Entries[pair.Key] = updated;
                    cleared++;
                }
                if (cleared > 0) Save(next);
            }
            if (cleared > 0) Notify();
            return new Dictionary<string, object> { { "cleared", cleared } };
        }
        if (scope.Length > 0 || wanted.Length == 0 || (!hasTitle && !hasFollowUp))
            throw new ArgumentException("请选择对话，并填写名称或设置待跟进标记");
        string title = null;
        if (hasTitle) {
            title = Text(op, "title").Trim();
            if (title.Length == 0 || title.Length > 80 || title.Any(Char.IsControl)) throw new ArgumentException("名称需为 1 至 80 字");
        }
        if (hasFollowUp && !(followUp is bool)) throw new ArgumentException("待跟进标记无效");
        var source = catalog(0).FirstOrDefault(s => s.Key == wanted);
        if (source == null) throw new ArgumentException("没有找到可读取的对话");
        Dictionary<string, object> result;
        lock (gate) {
            var next = Copy();
            Entry entry;
            entry = next.Entries.TryGetValue(wanted, out entry) ? entry.Copy() : new Entry();
            entry.Source = source;
            if (hasTitle) entry.ManualTitle = title;
            if (hasFollowUp) entry.ManualFollowUp = (bool)followUp;
            next.Entries[wanted] = entry;
            Save(next);
            result = Public(source, entry, next.AutoFollowUp);
        }
        Notify();
        return new Dictionary<string, object> { { "item", result } };
    }

    Dictionary<string, object> Public(Source source, Entry entry, bool autoFollowUp) {
        var result = Detail(source.Title, source.Named, entry, autoFollowUp);
        result["id"] = source.Id;
        result["tool"] = source.Tool;
        result["dir"] = source.Dir;
        result["folder"] = source.Dir.Length == 0 ? source.Folder : "";
        result["updated"] = source.Updated;
        return result;
    }

    Dictionary<string, object> Detail(string original, bool named, Entry entry, bool autoFollowUp) {
        var data = entry.Data ?? new Metadata();
        return new Dictionary<string, object> {
            { "title", DisplayTitle(original, named, entry) }, { "original_title", original }, { "manual_title", entry.ManualTitle },
            { "summary", data.Summary }, { "tags", data.Tags }, { "follow_up", EffectiveFollowUp(entry, autoFollowUp) },
            { "state", entry.Source.Key == running ? "running" : entry.State.Length == 0 ? "pending" : entry.State },
            { "error", entry.Error }, { "model", entry.Model }
        };
    }

    internal void Tick() {
        if (Interlocked.CompareExchange(ref working, 1, 0) != 0) return;
        lock (gate) {
            try {
                var current = Loaded();
                if (current.Source == Off || (!(current.AutoSince > 0 && clock() >= nextAuto) &&
                    !current.Entries.Values.Any(e => e.State == Queued && (!e.Automatic || current.AutoSince > 0)))) {
                    working = 0;
                    return;
                }
            } catch (Exception error) {
                working = 0;
                if (error is IOException || error is UnauthorizedAccessException || error is ArgumentException || error is InvalidOperationException) return;
                throw;
            }
        }
        Task.Run(() => {
            try { Advance(); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            finally { Interlocked.Exchange(ref working, 0); }
        });
    }

    internal void Advance() {
        if (awaitingSave != null) Commit();
        long since = 0;
        lock (gate) {
            var current = Loaded();
            if (current.Source == Off) return;
            if (current.AutoSince > 0 && clock() >= nextAuto) {
                since = current.AutoSince;
                nextAuto = clock() + Interval;
            }
        }
        if (since > 0) {
            long now = clock();
            var eligible = catalog(since).Where(s => now - s.Updated >= Rest && !busy(s) && Needed(s, false)).ToArray();
            Enqueue(eligible, true, false, since);
        }
        Entry[] queued;
        lock (gate) {
            var current = Loaded();
            queued = current.Entries.Values.Where(e => e.State == Queued && (!e.Automatic || current.AutoSince > 0)).ToArray();
        }
        foreach (var candidate in queued) {
            if (busy(candidate.Source)) continue;
            Choice choice;
            lock (gate) {
                if (running.Length > 0) return;
                var current = Loaded();
                var entry = current.Entries[candidate.Source.Key];
                if (current.Source == Off || entry.State != Queued || (entry.Automatic && current.AutoSince == 0)) continue;
                running = candidate.Source.Key;
                choice = ChoiceOf(current);
            }
            Run(candidate.Source, choice);
            return;
        }
    }

    void Run(Source source, Choice choice) {
        Metadata data = null;
        string error = "", model = "", hash = "", label = "整理来源";
        long seen = 0;
        bool called = false;
        try {
            if (!readable(source)) throw new InvalidOperationException("这段对话已不在可读取的项目中");
            var offer = Offered();
            if (Kind(offer.Store) != choice.Kind) throw new InvalidOperationException("整理来源已更改，请重试");
            label = offer.Source.Label;
            model = offer.Model;
            if (!Available(offer)) throw new InvalidOperationException("所选模型当前不可用，请在整理设置里重新选择");
            seen = Now(File.GetLastWriteTimeUtc(source.File));
            var reading = OrganizeHistory.Read(source);
            hash = reading.Hash;
            called = true;
            data = offer.Source.Analyze(reading.Context, model);
            if (data == null) throw new InvalidOperationException("模型没有返回有效的整理结果");
        } catch (InvalidOperationException failure) { error = failure.Message; }
          catch (IOException) { error = "读不到对话记录，或与 " + label + " 的连接已中断"; }
          catch (UnauthorizedAccessException) { error = "没有权限读取对话记录或启动 " + label; }
          catch (System.ComponentModel.Win32Exception) { error = "电脑无法启动 " + label; }
          catch (TimeoutException) { error = label + " 整理超时，请重试"; }
          catch (ArgumentException) { error = "模型返回的整理结果无效，请重试"; }
        finally {
            lock (gate) {
                if (data != null || error.Length > 0) {
                    awaitingSave = new Entry { Source = source, State = error.Length == 0 ? Done : Failed, Error = error, Model = model,
                        Data = data, Hash = hash, Seen = seen };
                    if (called) calls.Add(data != null ? data.Tokens : 0);
                } else running = "";
            }
            Commit();
        }
    }
    // requests made since the last commit: their tokens (0 where the source did not tell them)
    readonly List<long> calls = new List<long>();

    void Commit() {
        lock (gate) {
            if (awaitingSave == null) return;
            var next = Copy();
            string key = awaitingSave.Source.Key;
            var entry = awaitingSave.Copy();
            var latest = next.Entries[key];
            entry.ManualTitle = latest.ManualTitle;
            entry.ManualFollowUp = latest.ManualFollowUp;
            entry.Source = latest.Source;
            // a failed request keeps the last good result, so the list does not lose it
            if (entry.Data == null) entry.Data = latest.Data;
            next.Entries[key] = entry;
            if (next.Day != Today()) { next.Day = Today(); next.Calls = 0; next.Tokens = 0; }
            next.Calls += calls.Count;
            next.Tokens += calls.Sum();
            Save(next);
            calls.Clear();
            awaitingSave = null;
            running = "";
        }
        Notify();
    }

    void Notify() { var changed = Changed; if (changed != null) changed(); }
}
}
