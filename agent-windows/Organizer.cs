using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace RemoteCli {

/// Local conversation metadata and work queue. A running item remains queued on disk until its result is committed.
internal sealed class Organizer {
    internal const string DefaultModel = "gpt-6-luna";
    internal const string Queued = "queued", Failed = "failed", Done = "done";

    public sealed class Source {
        public string Tool = "", Id = "", Dir = "", Folder = "", File = "", Title = "";
        public bool Named;
        public long Created, Updated;
        internal string Key { get { return Organizer.Key(Tool, Id); } }
    }

    public sealed class Metadata {
        public string Title = "", Summary = "";
        public string[] Tags = new string[0];
        public bool FollowUp;
    }

    public sealed class Entry {
        public Source Source;
        public string State = "", Error = "", Model = "", ManualTitle = "";
        public bool? ManualFollowUp;
        public Metadata Data;
        public bool Automatic;
        internal Entry Copy() { return (Entry)MemberwiseClone(); }
    }

    public sealed class Store {
        public long AutoSince;
        public string Model = DefaultModel;
        public Dictionary<string, Entry> Entries = new Dictionary<string, Entry>();
    }

    readonly object gate = new object();
    readonly string file;
    readonly Func<long, Source[]> catalog;
    readonly Func<Source, bool> readable, busy;
    readonly Func<Dictionary<string, object>[]> models;
    readonly Func<Source, string, Metadata> classify;
    readonly Func<long> clock;
    readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
    Store state;
    long nextAuto;
    string running = "";
    Entry awaitingSave;
    int working;
    internal event Action Changed;

    internal Organizer(string file, Func<long, Source[]> catalog, Func<Source, bool> readable, Func<Source, bool> busy,
            Func<Dictionary<string, object>[]> models, Func<Source, string, Metadata> classify, Func<long> clock = null) {
        this.file = file;
        this.catalog = catalog;
        this.readable = readable;
        this.busy = busy;
        this.models = models;
        this.classify = classify;
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
        var current = Loaded();
        return new Store { AutoSince = current.AutoSince, Model = current.Model, Entries = new Dictionary<string, Entry>(current.Entries) };
    }

    internal string Title(string tool, string id, string original, bool named) {
        var metadata = ForList(tool, id, original, named);
        return metadata == null ? original : Text(metadata, "title");
    }

    internal Dictionary<string, object> ForList(string tool, string id, string original, bool named) {
        try {
            lock (gate) {
                Entry entry;
                if (!Loaded().Entries.TryGetValue(Key(tool, id), out entry)) return null;
                return Detail(original, named, entry);
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
            case "status": return Status(models());
            case "settings": return Settings(op);
            case "list": return List(op);
            case "start": return Start(op, false);
            case "retry": return Start(op, true);
            case "edit": return Edit(op);
            default: throw new ArgumentException("整理操作无效");
        }
    }

    Dictionary<string, object> Status(Dictionary<string, object>[] offered) {
        lock (gate) {
            var current = Loaded();
            return new Dictionary<string, object> {
                { "auto", current.AutoSince > 0 }, { "model", current.Model }, { "default_model", DefaultModel },
                { "models", offered }, { "model_available", offered.Any(model => Text(model, "id") == current.Model) },
                { "queued", current.Entries.Count(e => e.Value.State == Queued && e.Key != running) },
                { "running", running.Length > 0 ? 1 : 0 },
                { "done", current.Entries.Count(e => e.Value.State == Done) },
                { "failed", current.Entries.Count(e => e.Value.State == Failed) }
            };
        }
    }

    Dictionary<string, object> Settings(Dictionary<string, object> op) {
        var offered = models();
        string model = Text(op, "model");
        if (op.ContainsKey("model") && (!Models.Valid(model) || !offered.Any(choice => Text(choice, "id") == model)))
            throw new ArgumentException("这台电脑没有列出所选的 Codex 模型");
        object automatic;
        if (op.TryGetValue("auto", out automatic) && !(automatic is bool)) throw new ArgumentException("自动整理设置无效");
        lock (gate) {
            var next = Copy();
            if (model.Length > 0) next.Model = model;
            if (automatic is bool) next.AutoSince = (bool)automatic ? (next.AutoSince > 0 ? next.AutoSince : clock()) : 0;
            Save(next);
            nextAuto = 0;
        }
        return Status(offered);
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
                return Public(s, entry);
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

    Dictionary<string, object> Start(Dictionary<string, object> op, bool retry) {
        string wanted = Target(op), scope = Text(op, "scope");
        if (wanted.Length == 0 && scope != "all") throw new ArgumentException("请选择一个对话或所有可读对话");
        var sources = catalog(0).Where(s => wanted.Length == 0 || s.Key == wanted).ToArray();
        if (wanted.Length > 0 && sources.Length == 0) throw new ArgumentException("没有找到可读取的对话");
        int added = Enqueue(sources, false, retry, 0);
        return new Dictionary<string, object> { { "queued", added }, { "skipped", sources.Length - added } };
    }

    int Enqueue(Source[] sources, bool automatic, bool retry, long since) {
        int added = 0;
        bool changed = false;
        lock (gate) {
            var next = Copy();
            if (automatic && next.AutoSince != since) return 0;
            foreach (var source in sources) {
                Entry entry;
                if (next.Entries.TryGetValue(source.Key, out entry)) {
                    if (!retry && entry.State == Queued && entry.Automatic && !automatic) {
                        entry = entry.Copy();
                        entry.Automatic = false;
                        next.Entries[source.Key] = entry;
                        changed = true;
                    }
                    if (entry.State.Length > 0 && !(retry && entry.State == Failed)) continue;
                } else {
                    if (retry) continue;
                    entry = new Entry();
                }
                if (retry && entry.State != Failed) continue;
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

    Dictionary<string, object> Edit(Dictionary<string, object> op) {
        string wanted = Target(op);
        bool hasTitle = op.ContainsKey("title");
        object followUp;
        bool hasFollowUp = op.TryGetValue("follow_up", out followUp);
        if (wanted.Length == 0 || (!hasTitle && !hasFollowUp))
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
            result = Public(source, entry);
        }
        Notify();
        return new Dictionary<string, object> { { "item", result } };
    }

    Dictionary<string, object> Public(Source source, Entry entry) {
        var result = Detail(source.Title, source.Named, entry);
        result["id"] = source.Id;
        result["tool"] = source.Tool;
        result["dir"] = source.Dir;
        result["folder"] = source.Dir.Length == 0 ? source.Folder : "";
        result["updated"] = source.Updated;
        return result;
    }

    Dictionary<string, object> Detail(string original, bool named, Entry entry) {
        var data = entry.Data ?? new Metadata();
        return new Dictionary<string, object> {
            { "title", DisplayTitle(original, named, entry) }, { "original_title", original }, { "manual_title", entry.ManualTitle },
            { "summary", data.Summary }, { "tags", data.Tags }, { "follow_up", entry.ManualFollowUp ?? data.FollowUp },
            { "state", entry.Source.Key == running ? "running" : entry.State.Length == 0 ? "pending" : entry.State },
            { "error", entry.Error }, { "model", entry.Model }
        };
    }

    internal void Tick() {
        if (Interlocked.CompareExchange(ref working, 1, 0) != 0) return;
        lock (gate) {
            try {
                var current = Loaded();
                if (!(current.AutoSince > 0 && clock() >= nextAuto) &&
                    !current.Entries.Values.Any(e => e.State == Queued && (!e.Automatic || current.AutoSince > 0))) {
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
        HashSet<string> known = null;
        lock (gate) {
            var current = Loaded();
            if (current.AutoSince > 0 && clock() >= nextAuto) {
                since = current.AutoSince;
                nextAuto = clock() + 15000;
                known = new HashSet<string>(current.Entries.Where(entry => entry.Value.State.Length > 0).Select(entry => entry.Key));
            }
        }
        if (since > 0) {
            var eligible = catalog(since).Where(s => s.Created >= since && !known.Contains(s.Key) && !busy(s))
                .Where(s => OrganizeHistory.HasExchange(s)).ToArray();
            Enqueue(eligible, true, false, since);
        }
        Entry[] queued;
        lock (gate) {
            var current = Loaded();
            queued = current.Entries.Values.Where(e => e.State == Queued && (!e.Automatic || current.AutoSince > 0)).ToArray();
        }
        foreach (var candidate in queued) {
            if (busy(candidate.Source)) continue;
            string model;
            lock (gate) {
                if (running.Length > 0) return;
                var current = Loaded();
                var entry = current.Entries[candidate.Source.Key];
                if (entry.State != Queued || (entry.Automatic && current.AutoSince == 0)) continue;
                running = candidate.Source.Key;
                model = current.Model;
            }
            Run(candidate.Source, model);
            return;
        }
    }

    void Run(Source source, string model) {
        Metadata data = null;
        string error = "";
        try {
            if (!readable(source)) throw new InvalidOperationException("这段对话已不在可读取的项目中");
            if (!models().Any(choice => Text(choice, "id") == model))
                throw new InvalidOperationException("所选 Codex 模型当前不可用，请重新选择模型后重试");
            data = classify(source, model);
            if (data == null) throw new InvalidOperationException("模型没有返回有效的整理结果");
        } catch (InvalidOperationException failure) { error = failure.Message; }
          catch (IOException) { error = "读不到对话记录，或与 Codex 的连接已中断"; }
          catch (UnauthorizedAccessException) { error = "没有权限读取对话记录或启动 Codex"; }
          catch (System.ComponentModel.Win32Exception) { error = "电脑无法启动 Codex"; }
          catch (TimeoutException) { error = "Codex 整理超时，请重试"; }
          catch (ArgumentException) { error = "模型返回的整理结果无效，请重试"; }
        finally {
            lock (gate) {
                if (data != null || error.Length > 0) {
                    awaitingSave = new Entry { Source = source, State = error.Length == 0 ? Done : Failed, Error = error, Model = model, Data = data };
                } else running = "";
            }
            Commit();
        }
    }

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
            next.Entries[key] = entry;
            Save(next);
            awaitingSave = null;
            running = "";
        }
        Notify();
    }

    void Notify() { var changed = Changed; if (changed != null) changed(); }
}
}
