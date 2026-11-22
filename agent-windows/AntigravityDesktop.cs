using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace RemoteCli {
/// Native ConnectRPC client for Antigravity Desktop (`Antigravity.exe` + `language_server.exe`).
/// Discovers the running Hub instance from `~/.gemini/antigravity/daemon/hub_*.json`, verifies process ownership,
/// keeps `csrfToken` strictly in memory, and exposes read/create/send/stop/answer operations over loopback HTTP(S).
public sealed class AntigravityDesktopClient {
    public const string TransportName = "agy-desktop";
    public const string OriginName = "Antigravity 桌面版";
    const string ServicePath = "/exa.language_server_pb.LanguageServerService/";
    const string CsrfHeader = "x-codeium-csrf-token";
    static readonly Regex Uuid = new Regex(@"\A[0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}\z");
    static readonly Regex OpId = new Regex(@"\A[a-f0-9]{16,32}\z");

    public sealed class Endpoint {
        public int Pid, HttpPort, HttpsPort;
        public string LsVersion = "";
        internal string CsrfToken = "";
        internal bool PortPinned, UseHttps;
        public DateTime VerifiedAt;
        public override string ToString() {
            return "AntigravityDesktop(pid=" + Pid + ", http=" + HttpPort + ", https=" + HttpsPort + ", version=" + LsVersion + ")";
        }
    }

    public sealed class State {
        public string Id = "", Title = "", Folder = "", Status = "", TrajectoryId = "";
        public bool Live, Ready, Busy, Waiting;
        public string AskId = "", AskText = "", AskKind = "";
        public object[] AskOptions = new object[0];
        public int PendingStepIndex = -1;
        public long Updated;
        public DateTime At;
        internal string PendingInteractionKind = "", PendingCommandLine = "", PendingPathUri = "", PendingQuestionId = "", PendingPersistRule = "";
        internal string[] PendingChoiceIds = new string[0];
        internal Dictionary<string, object> LastModel;

        internal bool SameAs(State other) {
            if (other == null) return false;
            return Id == other.Id && Title == other.Title && Folder == other.Folder && Status == other.Status
                && Live == other.Live && Ready == other.Ready && Busy == other.Busy && Waiting == other.Waiting
                && AskId == other.AskId && AskText == other.AskText && AskKind == other.AskKind
                && PendingStepIndex == other.PendingStepIndex && Updated == other.Updated
                && (AskOptions == null ? 0 : AskOptions.Length) == (other.AskOptions == null ? 0 : other.AskOptions.Length);
        }
    }

    readonly string homeDir;
    readonly Func<int, bool> verifyProcess;
    readonly Func<Endpoint, bool> verifyPorts;
    readonly Func<Endpoint, string, Dictionary<string, object>, int, Dictionary<string, object>> transport;
    readonly int timeoutMs;
    readonly object endpointGate = new object();
    Endpoint cachedEndpoint;
    Dictionary<string, object> cachedDefaultModel;
    DateTime lastDiscoveryMiss = DateTime.MinValue;

    public static string DefaultHome {
        get {
            string env = Environment.GetEnvironmentVariable("AGY_DESKTOP_HOME");
            return !String.IsNullOrWhiteSpace(env)
                ? env
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "antigravity");
        }
    }

    public string Home { get { return homeDir; } }

    public AntigravityDesktopClient(
        string homeOverride = null,
        Func<int, bool> verifyProcess = null,
        Func<Endpoint, string, Dictionary<string, object>, int, Dictionary<string, object>> transport = null,
        int timeoutMs = 8000,
        Func<Endpoint, bool> verifyPorts = null) {
        homeDir = String.IsNullOrWhiteSpace(homeOverride) ? DefaultHome : homeOverride;
        this.verifyProcess = verifyProcess ?? VerifyLanguageServerProcess;
        this.verifyPorts = verifyPorts ?? (verifyProcess == null && transport == null ? (Func<Endpoint, bool>)VerifyEndpointPorts : (ep => ep != null));
        this.transport = transport ?? SendConnectJson;
        this.timeoutMs = timeoutMs > 0 ? timeoutMs : 8000;
    }

    public bool Available { get { return Discover(false) != null; } }

    const int AF_INET = 2;
    const int TCP_TABLE_OWNER_PID_LISTENER = 3;
    const uint MIB_TCP_STATE_LISTEN = 2;
    [StructLayout(LayoutKind.Sequential)]
    struct MIB_TCPROW_OWNER_PID {
        public uint state, localAddr, localPort, remoteAddr, remotePort, owningPid;
    }
    [DllImport("iphlpapi.dll", SetLastError = true)]
    static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int dwOutBufLen, bool sort, int ipVersion, int tblClass, uint reserved);

    /// Checks whether `pid` owns a listening IPv4 loopback/wildcard TCP socket on `port`.
    public static bool IsLoopbackPortOwnedByPid(int port, int pid) {
        if (port <= 0 || port > 65535 || pid <= 0) return false;
        int size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0);
        if (size <= 0) return false;
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try {
            if (GetExtendedTcpTable(buffer, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0) != 0) return false;
            int count = Marshal.ReadInt32(buffer);
            IntPtr rowPtr = IntPtr.Add(buffer, 4);
            int rowSize = Marshal.SizeOf(typeof(MIB_TCPROW_OWNER_PID));
            for (int i = 0; i < count; i++) {
                var row = (MIB_TCPROW_OWNER_PID)Marshal.PtrToStructure(rowPtr, typeof(MIB_TCPROW_OWNER_PID));
                rowPtr = IntPtr.Add(rowPtr, rowSize);
                if (row.state != MIB_TCP_STATE_LISTEN || (int)row.owningPid != pid) continue;
                // 127.0.0.1 in network byte order (little-endian uint) is 0x0100007F; 0 is 0.0.0.0.
                if (row.localAddr != 0x0100007Fu && row.localAddr != 0u) continue;
                int listenPort = (ushort)IPAddress.NetworkToHostOrder((short)(row.localPort & 0xFFFF));
                if (listenPort == port) return true;
            }
            return false;
        } finally {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// Verifies that at least one advertised port in `ep` is actually owned by `ep.Pid` on loopback,
    /// and clears any advertised port not owned by `ep.Pid`.
    public static bool VerifyEndpointPorts(Endpoint ep) {
        if (ep == null || ep.Pid <= 0) return false;
        bool httpOk = ep.HttpPort > 0 && IsLoopbackPortOwnedByPid(ep.HttpPort, ep.Pid);
        bool httpsOk = ep.HttpsPort > 0 && IsLoopbackPortOwnedByPid(ep.HttpsPort, ep.Pid);
        if (!httpOk) ep.HttpPort = 0;
        if (!httpsOk) ep.HttpsPort = 0;
        return httpOk || httpsOk;
    }

    /// Verifies that a PID belongs to a live Antigravity `language_server.exe` process inside an Antigravity install folder.
    public static bool VerifyLanguageServerProcess(int pid) {
        if (pid <= 0) return false;
        try {
            using (var proc = Process.GetProcessById(pid)) {
                if (proc.HasExited) return false;
                if (!String.Equals(proc.ProcessName, "language_server", StringComparison.OrdinalIgnoreCase)) return false;
                string exe = proc.MainModule.FileName ?? "";
                if (exe.Length == 0) return false;
                return exe.EndsWith("language_server.exe", StringComparison.OrdinalIgnoreCase)
                    && exe.IndexOf("antigravity", StringComparison.OrdinalIgnoreCase) >= 0;
            }
        } catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    /// Discovers and verifies the active Antigravity Desktop `language_server.exe` endpoint.
    public Endpoint Discover(bool force = false) {
        lock (endpointGate) {
            if (!force && cachedEndpoint != null && DateTime.UtcNow - cachedEndpoint.VerifiedAt < TimeSpan.FromSeconds(5)) {
                if (verifyProcess(cachedEndpoint.Pid)) return cachedEndpoint;
                cachedEndpoint = null;
            }
            if (!force && cachedEndpoint == null && DateTime.UtcNow - lastDiscoveryMiss < TimeSpan.FromSeconds(2)) {
                return null;
            }
        }
        Endpoint found = ProbeDaemonDirectory();
        lock (endpointGate) {
            cachedEndpoint = found;
            lastDiscoveryMiss = found == null ? DateTime.UtcNow : DateTime.MinValue;
            return found;
        }
    }

    void InvalidateEndpoint(Endpoint failed) {
        lock (endpointGate) {
            if (cachedEndpoint == failed) { cachedEndpoint = null; cachedDefaultModel = null; }
            lastDiscoveryMiss = DateTime.MinValue;
        }
    }

    Endpoint ProbeDaemonDirectory() {
        string daemonDir = Path.Combine(homeDir, "daemon");
        if (!Directory.Exists(daemonDir)) return null;
        FileInfo[] files;
        try {
            files = new DirectoryInfo(daemonDir).GetFiles("hub_*.json")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToArray();
        } catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }

        var serializer = NewSerializer();
        foreach (FileInfo file in files) {
            Endpoint candidate = ParseDaemonFile(file.FullName, serializer);
            if (candidate == null) continue;
            if (!verifyProcess(candidate.Pid)) continue;
            if (!verifyPorts(candidate)) continue;
            try {
                transport(candidate, "GetStatus", new Dictionary<string, object>(), Math.Min(timeoutMs, 1500));
                candidate.VerifiedAt = DateTime.UtcNow;
                return candidate;
            } catch (Exception) {
                // Stale or unreachable daemon file: continue checking remaining candidates.
            }
        }
        return null;
    }

    static Endpoint ParseDaemonFile(string path, JavaScriptSerializer serializer) {
        try {
            string raw = File.ReadAllText(path, Encoding.UTF8);
            var obj = serializer.Deserialize<Dictionary<string, object>>(raw);
            if (obj == null) return null;
            int pid = AsInt(obj, "pid", 0);
            int httpPort = AsInt(obj, "httpPort", 0);
            int httpsPort = AsInt(obj, "httpsPort", 0);
            string csrf = Text(obj, "csrfToken");
            string ver = Text(obj, "lsVersion");
            if (pid <= 0 || (httpPort <= 0 && httpsPort <= 0) || String.IsNullOrWhiteSpace(csrf)) return null;
            return new Endpoint { Pid = pid, HttpPort = httpPort, HttpsPort = httpsPort, LsVersion = ver, CsrfToken = csrf };
        } catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (ArgumentException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    Dictionary<string, object> Call(string method, Dictionary<string, object> payload, int overrideTimeoutMs = 0, bool allowRetry = true) {
        Endpoint ep = Discover(false);
        if (ep == null) throw new InvalidOperationException("未检测到运行中的 Antigravity 桌面版服务");
        int ms = overrideTimeoutMs > 0 ? overrideTimeoutMs : timeoutMs;
        try {
            return transport(ep, method, payload ?? new Dictionary<string, object>(), ms);
        } catch (IOException) {
            InvalidateEndpoint(ep);
            if (!allowRetry) throw;
            Endpoint retry = Discover(true);
            if (retry == null) throw;
            return transport(retry, method, payload ?? new Dictionary<string, object>(), ms);
        } catch (UnauthorizedAccessException) {
            InvalidateEndpoint(ep);
            if (!allowRetry) throw;
            Endpoint retry = Discover(true);
            if (retry == null) throw;
            return transport(retry, method, payload ?? new Dictionary<string, object>(), ms);
        }
    }

    internal string[] ActiveTrajectoryIds() {
        if (Discover(false) == null) return new string[0];
        var reply = Call("GetAllCascadeTrajectories", new Dictionary<string, object>());
        var summaries = Obj(reply, "trajectorySummaries");
        if (summaries == null) return new string[0];
        var ids = new List<string>();
        foreach (var pair in summaries) {
            string id = (pair.Key ?? "").Trim();
            var item = pair.Value as Dictionary<string, object>;
            if (!Uuid.IsMatch(id) || item == null) continue;
            if (IsCliSource(item) || Text(item, "parentConversationId").Length > 0) continue;
            ids.Add(id);
        }
        return ids.ToArray();
    }

    static Dictionary<string, object> SendConnectJson(Endpoint ep, string method, Dictionary<string, object> payload, int ms) {
        if (ep == null || String.IsNullOrWhiteSpace(ep.CsrfToken)) throw new InvalidOperationException("Antigravity 桌面版连接凭据不可用");
        var serializer = NewSerializer();
        byte[] body = new UTF8Encoding(false).GetBytes(serializer.Serialize(payload ?? new Dictionary<string, object>()));
        Exception lastError = null;
        var targets = new List<KeyValuePair<bool, string>>();
        if (ep.PortPinned) {
            if (ep.UseHttps && ep.HttpsPort > 0) targets.Add(new KeyValuePair<bool, string>(true, "https://127.0.0.1:" + ep.HttpsPort + ServicePath + method));
            else if (!ep.UseHttps && ep.HttpPort > 0) targets.Add(new KeyValuePair<bool, string>(false, "http://127.0.0.1:" + ep.HttpPort + ServicePath + method));
        } else {
            if (ep.HttpPort > 0) targets.Add(new KeyValuePair<bool, string>(false, "http://127.0.0.1:" + ep.HttpPort + ServicePath + method));
            if (ep.HttpsPort > 0) targets.Add(new KeyValuePair<bool, string>(true, "https://127.0.0.1:" + ep.HttpsPort + ServicePath + method));
        }
        foreach (var target in targets) {
            bool isHttps = target.Key;
            string url = target.Value;
            try {
                var request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "POST";
                request.ContentType = "application/json";
                request.KeepAlive = false;
                request.AllowAutoRedirect = false;
                request.Timeout = ms;
                request.ReadWriteTimeout = ms;
                request.Headers.Add("Connect-Protocol-Version", "1");
                request.Headers.Add(CsrfHeader, ep.CsrfToken);
                if (isHttps) {
                    request.ServerCertificateValidationCallback = (sender, cert, chain, errors) => true;
                }
                request.ContentLength = body.Length;
                using (var reqStream = request.GetRequestStream()) reqStream.Write(body, 0, body.Length);
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8)) {
                    ep.PortPinned = true;
                    ep.UseHttps = isHttps;
                    string text = reader.ReadToEnd();
                    if (String.IsNullOrWhiteSpace(text)) return new Dictionary<string, object>();
                    var parsed = serializer.Deserialize<Dictionary<string, object>>(text);
                    return parsed ?? new Dictionary<string, object>();
                }
            } catch (WebException ex) {
                var resp = ex.Response as HttpWebResponse;
                if (resp != null) {
                    int code = (int)resp.StatusCode;
                    string errText = "";
                    try {
                        using (var reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) errText = reader.ReadToEnd();
                    } catch { }
                    resp.Dispose();
                    if (code == 401 || code == 403) {
                        throw new UnauthorizedAccessException("Antigravity 桌面版鉴权被拒绝（HTTP " + code + "）");
                    }
                    string msg = ExtractConnectError(errText, serializer);
                    throw new IOException("Antigravity 桌面版接口调用失败（" + method + ", HTTP " + code + (msg.Length > 0 ? ": " + msg : "") + "）");
                }
                lastError = new IOException("无法连接 Antigravity 桌面版服务（" + method + "）", ex);
            }
        }
        throw lastError ?? new IOException("无法连接 Antigravity 桌面版服务（" + method + "）");
    }

    static string ExtractConnectError(string raw, JavaScriptSerializer serializer) {
        if (String.IsNullOrWhiteSpace(raw)) return "";
        try {
            var obj = serializer.Deserialize<Dictionary<string, object>>(raw);
            string msg = Text(obj, "message");
            if (msg.Length > 0) return msg.Length > 160 ? msg.Substring(0, 160) : msg;
        } catch { }
        return "";
    }

    /// Lists desktop conversations by merging live RPC `GetAllCascadeTrajectories` with offline SQLite summaries,
    /// excluding CLI-only conversations (`CORTEX_TRAJECTORY_SOURCE_CLI` / `antigravity-cli`).
    public List<Antigravity.Summary> Summaries(string homeOverride = null, bool all = false) {
        string home = String.IsNullOrWhiteSpace(homeOverride) ? homeDir : homeOverride;
        var map = Antigravity.ReadSummariesDb(home, false, true, all);

        if (Discover(false) != null) {
            try {
                var reply = Call("GetAllCascadeTrajectories", new Dictionary<string, object>());
                var summaries = Obj(reply, "trajectorySummaries");
                if (summaries != null) {
                    foreach (var pair in summaries) {
                        string id = (pair.Key ?? "").Trim();
                        var item = pair.Value as Dictionary<string, object>;
                        if (!Uuid.IsMatch(id) || item == null) continue;
                        if (IsCliSource(item)) continue;
                        if (Text(item, "parentConversationId").Length > 0) continue;

                        Antigravity.Summary existing;
                        map.TryGetValue(id, out existing);
                        string folder = ExtractWorkspaceFolder(item);
                        if (folder.Length == 0 && existing != null) folder = existing.Folder;

                        string annTitle = Antigravity.CleanTitle(Text(Obj(item, "annotations"), "title"));
                        if (annTitle.Length == 0) annTitle = Antigravity.AnnotationTitle(home, id);
                        string rawSummary = Antigravity.CleanTitle(Text(item, "summary"));
                        string title = annTitle.Length > 0 ? annTitle : rawSummary;
                        bool named = annTitle.Length > 0 || (existing != null && existing.Named);
                        if (title.Length == 0 && existing != null) title = existing.Title;

                        DateTime modified = Antigravity.ParseTimestamp(Text(item, "lastModifiedTime"),
                            existing != null ? existing.Created : DateTime.UtcNow);
                        DateTime created = Antigravity.ParseTimestamp(Text(item, "createdTime"), modified);
                        string status = Antigravity.MapStatus(Text(item, "status"), false);

                        map[id] = new Antigravity.Summary {
                            Id = id,
                            Title = title,
                            Named = named,
                            Folder = folder,
                            Status = status,
                            Updated = Antigravity.Milliseconds(modified),
                            Created = created
                        };
                    }
                }
            } catch (Exception) {
                // Keep SQLite summaries when RPC fails transiently.
            }
        }

        foreach (var s in map.Values) {
            if (s.Title.Length == 0) s.Title = "Antigravity 桌面对话 " + s.Created.ToLocalTime().ToString("MM-dd HH:mm");
        }
        return map.Values.OrderByDescending(s => s.Updated).ToList();
    }

    /// Inspects a single desktop conversation's live state and pending user interaction (approval / question).
    public State Inspect(string id) {
        if (!Uuid.IsMatch(id ?? "")) throw new ArgumentException("对话编号无效");
        var trajReply = Call("GetCascadeTrajectory", new Dictionary<string, object> { { "cascadeId", id } });
        var trajectory = Obj(trajReply, "trajectory");
        string trajectoryId = Text(trajectory, "trajectoryId");
        string rawStatus = Text(trajReply, "status");
        var steps = AsObjectList(trajectory != null ? At(trajectory, "steps") : null);
        int totalSteps = AsInt(trajReply, "numTotalSteps", steps.Count);
        if (steps.Count == 0 && totalSteps > 0) {
            var stepsReply = Call("GetCascadeTrajectorySteps", new Dictionary<string, object> { { "cascadeId", id }, { "stepOffset", 0 } });
            steps = AsObjectList(At(stepsReply, "steps"));
        }

        string folder = ExtractWorkspaceFolder(trajectory != null ? Obj(trajectory, "metadata") : null);
        string title = Antigravity.AnnotationTitle(homeDir, id);
        DateTime updated = DateTime.UtcNow;
        bool inActiveSummaries = false;

        try {
            var allReply = Call("GetAllCascadeTrajectories", new Dictionary<string, object>());
            var summaries = Obj(allReply, "trajectorySummaries");
            var one = summaries != null ? Obj(summaries, id) : null;
            if (one != null) {
                inActiveSummaries = true;
                if (trajectoryId.Length == 0) trajectoryId = Text(one, "trajectoryId");
                if (rawStatus.Length == 0) rawStatus = Text(one, "status");
                if (folder.Length == 0) folder = ExtractWorkspaceFolder(one);
                if (title.Length == 0) title = Antigravity.CleanTitle(Text(Obj(one, "annotations"), "title"));
                if (title.Length == 0) title = Antigravity.CleanTitle(Text(one, "summary"));
                updated = Antigravity.ParseTimestamp(Text(one, "lastModifiedTime"), updated);
            }
        } catch (Exception) { }

        if (steps.Count > 0) {
            var lastMeta = Obj(steps[steps.Count - 1], "metadata");
            DateTime stepTime = Antigravity.ParseTimestamp(Text(lastMeta, "createdAt"), DateTime.MinValue);
            if (stepTime > DateTime.MinValue) updated = stepTime;
            if (trajectoryId.Length == 0) {
                trajectoryId = Text(Obj(lastMeta, "sourceTrajectoryStepInfo"), "trajectoryId");
            }
            if (title.Length == 0) {
                for (int i = 0; i < steps.Count; i++) {
                    if (Text(steps[i], "type") == "CORTEX_STEP_TYPE_USER_INPUT") {
                        string first = Antigravity.CleanTitle(ExtractUserInputText(Obj(steps[i], "userInput")));
                        if (first.Length > 0) { title = first; break; }
                    }
                }
            }
        }

        var state = new State {
            Id = id,
            Title = title,
            Folder = folder,
            TrajectoryId = trajectoryId.Length > 0 ? trajectoryId : id,
            Live = inActiveSummaries || trajectory != null,
            Updated = Antigravity.Milliseconds(updated),
            At = DateTime.UtcNow,
            LastModel = ExtractModelFromSteps(steps)
        };

        ExtractPendingInteraction(state, steps);
        if (state.PendingStepIndex >= 0 || rawStatus.IndexOf("WAITING", StringComparison.OrdinalIgnoreCase) >= 0) {
            state.Status = "confirm";
            state.Waiting = true;
            state.Busy = false;
            state.Ready = false;
        } else if (rawStatus.IndexOf("RUNNING", StringComparison.OrdinalIgnoreCase) >= 0
            || rawStatus.IndexOf("BUSY", StringComparison.OrdinalIgnoreCase) >= 0
            || rawStatus.IndexOf("CANCELING", StringComparison.OrdinalIgnoreCase) >= 0) {
            state.Status = "busy";
            state.Busy = true;
            state.Ready = false;
        } else {
            state.Status = "idle";
            state.Ready = true;
            state.Busy = false;
        }
        return state;
    }

    /// Reads conversation messages/steps via `GetCascadeTrajectorySteps` (with incremental `since` stamp check),
    /// falling back to offline `brain/<id>/.system_generated/logs/transcript.jsonl` when the desktop app is unavailable.
    public Dictionary<string, object> ReadConversation(string id, string since = "", string homeOverride = null) {
        if (!Uuid.IsMatch(id ?? "")) throw new ArgumentException("对话编号无效");
        string home = String.IsNullOrWhiteSpace(homeOverride) ? homeDir : homeOverride;

        if (Discover(false) != null) {
            try {
                return ReadConversationRpc(id, since ?? "", home);
            } catch (Exception) {
                // Fall back to offline transcript if available; otherwise rethrow below.
                string offline = OfflineTranscriptPath(home, id);
                if (offline == null) throw;
            }
        }

        string path = OfflineTranscriptPath(home, id);
        if (path == null) throw new IOException("读不到这个 Antigravity 桌面对话的内容");
        var info = new FileInfo(path);
        string fileStamp = info.Length + "." + info.LastWriteTimeUtc.Ticks;
        if (!String.IsNullOrEmpty(since) && since == fileStamp) {
            return new Dictionary<string, object> {
                { "same", true },
                { "stamp", fileStamp },
                { "status", "idle" },
                { "ask", null }
            };
        }
        var said = Transcripts.Said(path, "agy");
        said["same"] = false;
        said["stamp"] = fileStamp;
        said["status"] = "idle";
        said["ask"] = null;
        return said;
    }

    Dictionary<string, object> ReadConversationRpc(string id, string since, string home) {
        var allSteps = new List<Dictionary<string, object>>();
        int offset = 0;
        for (int page = 0; page < 20; page++) {
            var reply = Call("GetCascadeTrajectorySteps", new Dictionary<string, object> {
                { "cascadeId", id },
                { "stepOffset", offset }
            });
            var batch = AsObjectList(At(reply, "steps"));
            if (batch.Count == 0) break;
            allSteps.AddRange(batch);
            offset += batch.Count;
            if (batch.Count < 200) break;
        }

        string status = "idle";
        string trajectoryId = id;
        long updatedMs = 0;
        try {
            var allReply = Call("GetAllCascadeTrajectories", new Dictionary<string, object>());
            var one = Obj(Obj(allReply, "trajectorySummaries"), id);
            if (one != null) {
                status = Antigravity.MapStatus(Text(one, "status"), false);
                string tid = Text(one, "trajectoryId");
                if (tid.Length > 0) trajectoryId = tid;
                DateTime mod = Antigravity.ParseTimestamp(Text(one, "lastModifiedTime"), DateTime.MinValue);
                if (mod > DateTime.MinValue) updatedMs = Antigravity.Milliseconds(mod);
            }
        } catch (Exception) { }

        var state = new State { Id = id, TrajectoryId = trajectoryId };
        ExtractPendingInteraction(state, allSteps);
        if (state.PendingStepIndex >= 0) status = "confirm";
        if (updatedMs == 0 && allSteps.Count > 0) {
            DateTime stepTime = Antigravity.ParseTimestamp(Text(Obj(allSteps[allSteps.Count - 1], "metadata"), "createdAt"), DateTime.UtcNow);
            updatedMs = Antigravity.Milliseconds(stepTime);
        }
        if (updatedMs == 0) updatedMs = Antigravity.Milliseconds(DateTime.UtcNow);

        string stamp = ComputeStepsStamp(allSteps, status, state.PendingStepIndex, updatedMs);
        object askObj = BuildAskPayload(state);
        if (!String.IsNullOrEmpty(since) && since == stamp) {
            return new Dictionary<string, object> {
                { "same", true },
                { "stamp", stamp },
                { "status", status },
                { "ask", askObj }
            };
        }

        long askedMs;
        var built = ConvertStepsToMessages(allSteps, out askedMs);
        var kept = Transcripts.Talk(built, 40, 30);
        var serializer = NewSerializer();
        int letters = kept.Sum(m => Convert.ToString(m["text"]).Length);
        var sizes = kept.Select(m => Encoding.UTF8.GetByteCount(serializer.Serialize(m)) + 1).ToArray();
        int bytes = sizes.Sum(), removed = 0;
        while (removed < kept.Count - 1 && (letters > 256 * 1024 || bytes > 3 * 1024 * 1024)) {
            letters -= Convert.ToString(kept[removed]["text"]).Length;
            bytes -= sizes[removed++];
        }
        if (removed > 0) kept.RemoveRange(0, removed);
        int withResult = 0;
        for (int i = kept.Count - 1; i >= 0; i--) {
            if (kept[i].ContainsKey("result") && ++withResult > Transcripts.Results) kept[i].Remove("result");
        }

        return new Dictionary<string, object> {
            { "messages", kept },
            { "more", kept.Count < built.Count },
            { "updated", updatedMs },
            { "asked", askedMs },
            { "stamp", stamp },
            { "same", false },
            { "status", status },
            { "ask", askObj }
        };
    }

    /// Creates a new desktop conversation in `folder` via `StartCascade` and, when `draft` is non-empty,
    /// sends the first message via `SendUserCascadeMessage` with `clientMessageId = operationId`.
    public Dictionary<string, object> CreateAndSend(string folder, string draft, string operationId) {
        ValidateFolderAndDraft(folder, draft);
        if (!Directory.Exists(folder)) throw new InvalidOperationException("项目文件夹已不存在");
        string opId = String.IsNullOrEmpty(operationId) ? Guid.NewGuid().ToString("N") : operationId;
        if (!OpId.IsMatch(opId)) throw new ArgumentException("操作编号无效");

        string fullFolder = Path.GetFullPath(folder);
        var startReq = new Dictionary<string, object> {
            { "workspaceUris", new[] { ToFileUri(fullFolder) } },
            { "source", "CORTEX_TRAJECTORY_SOURCE_CASCADE_CLIENT" }
        };
        var startRes = Call("StartCascade", startReq, 0, false);
        string cascadeId = Text(startRes, "cascadeId");
        if (!Uuid.IsMatch(cascadeId)) throw new IOException("Antigravity 桌面版未返回有效的新对话编号");

        string cleanDraft = (draft ?? "").Trim();
        bool sent = false;
        if (cleanDraft.Length > 0) {
            var sendReq = new Dictionary<string, object> {
                { "cascadeId", cascadeId },
                { "items", new object[] { new Dictionary<string, object> { { "text", cleanDraft } } } },
                { "clientMessageId", opId }
            };
            var cascadeConfig = BuildCascadeConfig(ResolveDefaultModel());
            if (cascadeConfig != null) sendReq["cascadeConfig"] = cascadeConfig;
            Call("SendUserCascadeMessage", sendReq, 0, false);
            sent = true;
        }
        return new Dictionary<string, object> {
            { "status", "created" },
            { "tool", "agy" },
            { "session", cascadeId },
            { "sent", sent },
            { "transport", TransportName },
            { "message", sent ? "已在 Antigravity 桌面版新建对话并发送消息。" : "已在 Antigravity 桌面版新建对话。" }
        };
    }

    /// Sends a message to an existing desktop conversation via `SendUserCascadeMessage`.
    public Dictionary<string, object> Send(string id, string operationId, string text) {
        return Send(id, operationId, text, null);
    }

    internal Dictionary<string, object> Send(string id, string operationId, string text, Dictionary<string, object> modelOverride) {
        if (!Uuid.IsMatch(id ?? "")) throw new ArgumentException("对话编号无效");
        if (!OpId.IsMatch(operationId ?? "")) throw new ArgumentException("操作编号无效");
        string clean = (text ?? "").Trim();
        if (clean.Length == 0 || clean.Length > 8000 || clean.Any(c => Char.IsControl(c) && c != '\n' && c != '\r' && c != '\t')) {
            throw new ArgumentException("消息需为 1 至 8000 字，且不能含控制字符");
        }
        var model = modelOverride ?? ResolveConversationModel(id) ?? ResolveDefaultModel();
        var payload = new Dictionary<string, object> {
            { "cascadeId", id },
            { "items", new object[] { new Dictionary<string, object> { { "text", clean } } } },
            { "clientMessageId", operationId }
        };
        var cascadeConfig = BuildCascadeConfig(model);
        if (cascadeConfig != null) payload["cascadeConfig"] = cascadeConfig;
        Call("SendUserCascadeMessage", payload, 0, false);
        return new Dictionary<string, object> {
            { "status", "accepted" },
            { "sent", true },
            { "session", id },
            { "operation", operationId },
            { "now", true },
            { "transport", TransportName }
        };
    }

    static Dictionary<string, object> BuildCascadeConfig(Dictionary<string, object> model) {
        if (model == null || model.Count == 0) return null;
        return new Dictionary<string, object> {
            { "plannerConfig", new Dictionary<string, object> {
                { "requestedModel", model },
                { "conversational", new Dictionary<string, object>() }
            } }
        };
    }

    static Dictionary<string, object> ExtractModelFromSteps(List<Dictionary<string, object>> steps) {
        if (steps == null) return null;
        for (int i = steps.Count - 1; i >= 0; i--) {
            var step = steps[i];
            if (step == null || Text(step, "type") != "CORTEX_STEP_TYPE_USER_INPUT") continue;
            var planner = Obj(Obj(Obj(step, "userInput"), "userConfig"), "plannerConfig");
            if (planner == null) continue;
            var requested = Obj(planner, "requestedModel");
            if (requested != null && (Text(requested, "model").Length > 0 || Text(requested, "alias").Length > 0)) return requested;
            string planModel = Text(planner, "planModel");
            if (planModel.Length > 0) return new Dictionary<string, object> { { "model", planModel } };
        }
        return null;
    }

    Dictionary<string, object> ResolveConversationModel(string id) {
        try {
            var reply = Call("GetCascadeTrajectorySteps", new Dictionary<string, object> { { "cascadeId", id }, { "stepOffset", 0 } });
            return ExtractModelFromSteps(AsObjectList(At(reply, "steps")));
        } catch (Exception) {
            return null;
        }
    }

    Dictionary<string, object> ResolveDefaultModel() {
        lock (endpointGate) {
            if (cachedDefaultModel != null) return cachedDefaultModel;
        }
        try {
            var data = Call("GetCascadeModelConfigData", new Dictionary<string, object>());
            var model = Obj(Obj(data, "defaultOverrideModelConfig"), "modelOrAlias");
            if (model == null || (Text(model, "model").Length == 0 && Text(model, "alias").Length == 0)) {
                foreach (var cfg in AsObjectList(At(data, "clientModelConfigs"))) {
                    var candidate = Obj(cfg, "modelOrAlias");
                    if (candidate != null && (Text(candidate, "model").Length > 0 || Text(candidate, "alias").Length > 0)) {
                        model = candidate;
                        break;
                    }
                }
            }
            if (model != null) {
                lock (endpointGate) cachedDefaultModel = model;
            }
            return model;
        } catch (Exception) {
            return null;
        }
    }

    /// Cancels the running invocation of a desktop conversation via `CancelCascadeInvocation`.
    public Dictionary<string, object> Stop(string id) {
        if (!Uuid.IsMatch(id ?? "")) throw new ArgumentException("对话编号无效");
        Call("CancelCascadeInvocation", new Dictionary<string, object> { { "cascadeId", id } }, 0, false);
        return new Dictionary<string, object> {
            { "status", "confirmed" },
            { "session", id },
            { "transport", TransportName }
        };
    }

    /// Answers a pending desktop interaction (command/file/MCP/permission approval or `askQuestion` selection)
    /// via `HandleCascadeUserInteraction`.
    public Dictionary<string, object> Answer(string id, string askId, bool allow, string scope = "once", string choice = "") {
        if (!Uuid.IsMatch(id ?? "")) throw new ArgumentException("对话编号无效");
        var state = Inspect(id);
        if (state.PendingStepIndex < 0) throw new InvalidOperationException("这段对话现在没有在等确认");
        if (!String.IsNullOrEmpty(askId) && !String.Equals(state.AskId, askId, StringComparison.OrdinalIgnoreCase)) {
            throw new InvalidOperationException("这个确认请求已经处理过了，请刷新后重试");
        }

        var interaction = new Dictionary<string, object> {
            { "trajectoryId", state.TrajectoryId },
            { "stepIndex", state.PendingStepIndex }
        };

        string kind = state.PendingInteractionKind;
        string normalizedScope = String.Equals(scope, "always", StringComparison.OrdinalIgnoreCase)
            ? "PERMISSION_SCOPE_WORKSPACE"
            : "PERMISSION_SCOPE_ONCE";

        if (kind == "askQuestion") {
            if (!allow) {
                interaction["askQuestion"] = new Dictionary<string, object> {
                    { "cancelled", true },
                    { "responses", new object[0] }
                };
            } else {
                var selectedIds = new List<string>();
                string writeIn = "";
                string rawChoice = (choice ?? "").Trim();
                if (rawChoice.StartsWith("choice:", StringComparison.OrdinalIgnoreCase)) {
                    int idx;
                    if (Int32.TryParse(rawChoice.Substring(7), out idx) && idx >= 0 && idx < state.PendingChoiceIds.Length) {
                        selectedIds.Add(state.PendingChoiceIds[idx]);
                    } else {
                        throw new ArgumentException("所选选项无效");
                    }
                } else if (rawChoice.Length > 0) {
                    if (state.PendingChoiceIds.Contains(rawChoice, StringComparer.Ordinal)) selectedIds.Add(rawChoice);
                    else writeIn = rawChoice;
                } else if (state.PendingChoiceIds.Length > 0) {
                    selectedIds.Add(state.PendingChoiceIds[0]);
                }
                var resp = new Dictionary<string, object> {
                    { "selectedOptionIds", selectedIds.ToArray() },
                    { "skipped", false }
                };
                if (state.PendingQuestionId.Length > 0) resp["questionId"] = state.PendingQuestionId;
                if (writeIn.Length > 0) resp["writeInResponse"] = writeIn;
                interaction["askQuestion"] = new Dictionary<string, object> {
                    { "cancelled", false },
                    { "responses", new object[] { resp } }
                };
            }
        } else if (kind == "permission") {
            var perm = new Dictionary<string, object> {
                { "allow", allow },
                { "scope", normalizedScope }
            };
            if (allow && String.Equals(scope, "always", StringComparison.OrdinalIgnoreCase) && state.PendingPersistRule.Length > 0) {
                perm["persistGrants"] = new Dictionary<string, object> { { "allow", new[] { state.PendingPersistRule } } };
            }
            interaction["permission"] = perm;
        } else if (kind == "runCommand") {
            var cmd = new Dictionary<string, object> { { "confirm", allow } };
            if (state.PendingCommandLine.Length > 0) {
                cmd["proposedCommandLine"] = state.PendingCommandLine;
                cmd["submittedCommandLine"] = state.PendingCommandLine;
            }
            interaction["runCommand"] = cmd;
        } else if (kind == "filePermission") {
            var fp = new Dictionary<string, object> {
                { "allow", allow },
                { "scope", normalizedScope }
            };
            if (state.PendingPathUri.Length > 0) fp["absolutePathUri"] = state.PendingPathUri;
            interaction["filePermission"] = fp;
        } else if (kind == "mcp") {
            interaction["mcp"] = new Dictionary<string, object> { { "confirm", allow } };
        } else {
            interaction["approvalInteraction"] = new Dictionary<string, object> { { "confirm", allow } };
        }

        Call("HandleCascadeUserInteraction", new Dictionary<string, object> {
            { "cascadeId", id },
            { "interaction", interaction }
        }, 0, false);
        return new Dictionary<string, object> {
            { "status", "accepted" },
            { "session", id },
            { "transport", TransportName }
        };
    }

    public static void ValidateFolderAndDraft(string folder, string draft) {
        if (String.IsNullOrWhiteSpace(folder) || !Path.IsPathRooted(folder) || folder.Any(c => Char.IsControl(c))) {
            throw new ArgumentException("项目文件夹无效");
        }
        string text = draft ?? "";
        if (text.Length > 8000 || text.Any(c => Char.IsControl(c) && c != '\n' && c != '\r' && c != '\t')) {
            throw new ArgumentException("消息需在 8000 字以内，且不能含控制字符");
        }
    }

    public static string ToFileUri(string path) {
        string full = Path.GetFullPath(path).TrimEnd('\\', '/');
        if (full.Length == 2 && full[1] == ':') full += "\\";
        return new Uri(full).AbsoluteUri;
    }

    // ---- Step & interaction translation helpers

    static void ExtractPendingInteraction(State state, List<Dictionary<string, object>> steps) {
        state.PendingStepIndex = -1;
        state.AskId = ""; state.AskText = ""; state.AskKind = ""; state.AskOptions = new object[0];
        state.PendingInteractionKind = ""; state.PendingCommandLine = ""; state.PendingPathUri = "";
        state.PendingQuestionId = ""; state.PendingPersistRule = ""; state.PendingChoiceIds = new string[0];
        if (steps == null || steps.Count == 0) return;

        for (int i = steps.Count - 1; i >= 0; i--) {
            var step = steps[i];
            if (step == null) continue;
            string stepStatus = Text(step, "status");
            var req = Obj(step, "requestedInteraction");
            var completed = AsObjectList(At(step, "completedInteractions"));
            bool isWaiting = stepStatus == "CORTEX_STEP_STATUS_WAITING"
                || (req != null && completed.Count == 0 && stepStatus != "CORTEX_STEP_STATUS_DONE"
                    && stepStatus != "CORTEX_STEP_STATUS_CANCELED" && stepStatus != "CORTEX_STEP_STATUS_ERROR");
            if (!isWaiting) continue;

            state.PendingStepIndex = i;
            string stepType = Text(step, "type");
            if (req != null && req.ContainsKey("askQuestion") || stepType == "CORTEX_STEP_TYPE_ASK_QUESTION") {
                PopulateAskQuestion(state, step, req != null ? Obj(req, "askQuestion") : null);
            } else if (req != null && req.ContainsKey("permission")) {
                PopulatePermission(state, step, Obj(req, "permission"));
            } else if (req != null && req.ContainsKey("filePermission")) {
                PopulateFilePermission(state, step, Obj(req, "filePermission"));
            } else if (req != null && req.ContainsKey("runCommand") || stepType == "CORTEX_STEP_TYPE_RUN_COMMAND") {
                PopulateRunCommand(state, step, req != null ? Obj(req, "runCommand") : null);
            } else if (req != null && req.ContainsKey("mcp") || stepType == "CORTEX_STEP_TYPE_MCP_TOOL") {
                PopulateMcp(state, step, req != null ? Obj(req, "mcp") : null);
            } else {
                PopulateGenericApproval(state, step, req != null ? Obj(req, "approvalInteraction") : null);
            }
            state.AskId = ShortHash(state.Id + ":" + state.TrajectoryId + ":" + i + ":" + state.PendingInteractionKind);
            return;
        }
    }

    static void PopulateAskQuestion(State state, Dictionary<string, object> step, Dictionary<string, object> reqAsk) {
        state.PendingInteractionKind = "askQuestion";
        state.AskKind = "question";
        var askStep = Obj(step, "askQuestion");
        var questions = AsObjectList(reqAsk != null ? At(reqAsk, "questions") : null);
        if (questions.Count == 0 && askStep != null) questions = AsObjectList(At(askStep, "questions"));
        if (questions.Count == 0) {
            state.AskText = "Antigravity 正在等待你的回答";
            state.AskOptions = new object[] { MakeOption("once", "确认", "继续") };
            return;
        }
        var q = questions[0];
        state.PendingQuestionId = Text(q, "id");
        string prompt = FirstNonEmpty(Text(q, "question"), Text(q, "text"), Text(q, "prompt"), Text(q, "header"), "请选择一项回答");
        state.AskText = prompt;
        var rawOpts = AsObjectList(At(q, "options"));
        var options = new List<object>();
        var choiceIds = new List<string>();
        for (int idx = 0; idx < rawOpts.Count; idx++) {
            var opt = rawOpts[idx];
            string label = FirstNonEmpty(Text(opt, "text"), Text(opt, "label"), Text(opt, "title"), Text(opt, "id"), "选项 " + (idx + 1));
            string optId = FirstNonEmpty(Text(opt, "id"), label);
            string detail = FirstNonEmpty(Text(opt, "description"), Text(opt, "detail"), label);
            choiceIds.Add(optId);
            options.Add(MakeOption("choice:" + idx, label, detail));
        }
        state.PendingChoiceIds = choiceIds.ToArray();
        state.AskOptions = options.ToArray();
    }

    static void PopulatePermission(State state, Dictionary<string, object> step, Dictionary<string, object> perm) {
        state.PendingInteractionKind = "permission";
        var resource = Obj(perm, "resource");
        string action = Text(resource, "action");
        string target = Text(resource, "target");
        string runCmd = FirstNonEmpty(Text(Obj(step, "runCommand"), "proposedCommandLine"), Text(Obj(step, "runCommand"), "commandLine"));
        if (target.Length == 0) target = runCmd;
        string pattern = Text(perm, "suggestedPersistPattern");
        string persistType = Text(perm, "persistSuggestionType");
        state.AskKind = action == "command" ? "command" : "permission";
        state.AskText = target.Length > 0
            ? (action.Length > 0 ? action + "：" + target : target)
            : FirstNonEmpty(Text(perm, "description"), "请求执行操作权限");

        var options = new List<object> { MakeOption("once", "允许一次", state.AskText) };
        if (pattern.Length > 0 && persistType.IndexOf("DISABLED", StringComparison.OrdinalIgnoreCase) < 0 && persistType.IndexOf("NONE", StringComparison.OrdinalIgnoreCase) < 0) {
            state.PendingPersistRule = action.Length > 0 ? action + "(" + pattern + ")" : pattern;
            options.Add(MakeOption("always", "始终允许", state.PendingPersistRule));
        }
        options.Add(MakeOption("deny", "拒绝", "拒绝本次操作"));
        state.AskOptions = options.ToArray();
    }

    static void PopulateFilePermission(State state, Dictionary<string, object> step, Dictionary<string, object> fp) {
        state.PendingInteractionKind = "filePermission";
        state.AskKind = "file";
        string uri = FirstNonEmpty(Text(fp, "absolutePathUri"), Text(Obj(step, "viewFile"), "absolutePathUri"));
        state.PendingPathUri = uri;
        string shown = UriToPath(uri);
        if (shown.Length == 0) shown = FirstNonEmpty(Text(fp, "path"), uri, "工作区外文件");
        state.AskText = "请求访问文件：" + shown;
        state.AskOptions = new object[] {
            MakeOption("once", "允许一次", shown),
            MakeOption("always", "允许工作区访问", shown),
            MakeOption("deny", "拒绝", "拒绝访问该文件")
        };
    }

    static void PopulateRunCommand(State state, Dictionary<string, object> step, Dictionary<string, object> reqCmd) {
        state.PendingInteractionKind = "runCommand";
        state.AskKind = "command";
        var rc = Obj(step, "runCommand");
        string cmd = FirstNonEmpty(Text(reqCmd, "proposedCommandLine"), Text(rc, "proposedCommandLine"), Text(rc, "commandLine"));
        state.PendingCommandLine = cmd;
        state.AskText = cmd.Length > 0 ? "请求运行命令：" + cmd : "请求运行命令";
        state.AskOptions = new object[] {
            MakeOption("once", "允许运行", cmd.Length > 0 ? cmd : "允许本次命令执行"),
            MakeOption("deny", "拒绝", "拒绝运行该命令")
        };
    }

    static void PopulateMcp(State state, Dictionary<string, object> step, Dictionary<string, object> reqMcp) {
        state.PendingInteractionKind = "mcp";
        state.AskKind = "mcp";
        var mcp = Obj(step, "mcpTool");
        string server = FirstNonEmpty(Text(reqMcp, "serverName"), Text(mcp, "serverName"));
        string tool = FirstNonEmpty(Text(Obj(mcp, "toolCall"), "name"), Text(reqMcp, "toolName"));
        string label = server.Length > 0 && tool.Length > 0 ? server + "/" + tool : FirstNonEmpty(tool, server, "MCP 工具");
        state.AskText = "请求调用 MCP：" + label;
        state.AskOptions = new object[] {
            MakeOption("once", "允许一次", label),
            MakeOption("deny", "拒绝", "拒绝本次 MCP 调用")
        };
    }

    static void PopulateGenericApproval(State state, Dictionary<string, object> step, Dictionary<string, object> approval) {
        state.PendingInteractionKind = "approvalInteraction";
        state.AskKind = "approval";
        state.AskText = FirstNonEmpty(Text(approval, "description"), Text(approval, "title"), Text(step, "type"), "等待确认操作");
        state.AskOptions = new object[] {
            MakeOption("once", "允许", state.AskText),
            MakeOption("deny", "拒绝", "拒绝本次操作")
        };
    }

    static Dictionary<string, object> MakeOption(string id, string label, string detail) {
        return new Dictionary<string, object> {
            { "id", id },
            { "label", label.Length > 40 ? label.Substring(0, 39) + "…" : label },
            { "detail", detail.Length > 600 ? detail.Substring(0, 599) + "…" : detail }
        };
    }

    static object BuildAskPayload(State state) {
        if (state == null || state.PendingStepIndex < 0 || state.AskId.Length == 0) return null;
        return new Dictionary<string, object> {
            { "id", state.AskId },
            { "kind", state.AskKind },
            { "text", state.AskText },
            { "options", state.AskOptions }
        };
    }

    static List<Dictionary<string, object>> ConvertStepsToMessages(List<Dictionary<string, object>> steps, out long askedMs) {
        var messages = new List<Dictionary<string, object>>();
        askedMs = 0;
        for (int i = 0; i < steps.Count; i++) {
            var step = steps[i];
            if (step == null) continue;
            string type = Text(step, "type");
            if (type == "CORTEX_STEP_TYPE_USER_INPUT") {
                string text = ExtractUserInputText(Obj(step, "userInput"));
                if (text.Length > 0) {
                    AddMessage(messages, "user", text, null, null);
                    DateTime at = Antigravity.ParseTimestamp(Text(Obj(step, "metadata"), "createdAt"), DateTime.MinValue);
                    if (at > DateTime.MinValue) askedMs = Antigravity.Milliseconds(at);
                }
            } else if (type == "CORTEX_STEP_TYPE_PLANNER_RESPONSE") {
                var pr = Obj(step, "plannerResponse");
                if (pr == null) continue;
                string thinking = FirstNonEmpty(Text(pr, "thinking"), Text(pr, "rawThinking"));
                if (thinking.Length > 0) AddMessage(messages, "thought", thinking, null, null);
                bool nextIsDedicatedTool = i + 1 < steps.Count && IsDedicatedToolStep(Text(steps[i + 1], "type"));
                if (!nextIsDedicatedTool) {
                    foreach (var call in AsObjectList(At(pr, "toolCalls"))) {
                        string name = Text(call, "name");
                        if (name.Length == 0) continue;
                        string args = Text(call, "argumentsJson");
                        AddMessage(messages, "tool", name, args, null);
                    }
                }
                string reply = FirstNonEmpty(Text(pr, "modifiedResponse"), Text(pr, "response"));
                if (reply.Length > 0) AddMessage(messages, "assistant", reply, null, null);
            } else if (type == "CORTEX_STEP_TYPE_RUN_COMMAND") {
                var rc = Obj(step, "runCommand");
                string cmd = FirstNonEmpty(Text(rc, "commandLine"), Text(rc, "proposedCommandLine"));
                string title = cmd.Length > 0 ? "运行：" + Cut(cmd, 160) : "运行命令";
                string detail = cmd.Length > 160 ? Cut(cmd, 400) : "";
                string exitCode = rc != null && rc.ContainsKey("exitCode") ? Convert.ToString(rc["exitCode"]) : "";
                string combined = FirstNonEmpty(Text(rc, "combinedOutput"), CombineStreams(Text(rc, "stdout"), Text(rc, "stderr")));
                string result = (exitCode.Length > 0 && exitCode != "0" ? "（退出码 " + exitCode + "）\n" : "") + combined;
                AddMessage(messages, "tool", title, detail, Transcripts.First(result));
            } else if (type == "CORTEX_STEP_TYPE_VIEW_FILE") {
                var vf = Obj(step, "viewFile");
                string path = UriToPath(Text(vf, "absolutePathUri"));
                string title = path.Length > 0 ? "读取：" + ShortName(path) : "读取文件";
                string result = Text(vf, "content");
                AddMessage(messages, "tool", title, path.Length > 0 ? Cut(path, 400) : "", Transcripts.First(result));
            } else if (type == "CORTEX_STEP_TYPE_CODE_ACTION") {
                var ca = Obj(step, "codeAction");
                string desc = Text(ca, "description");
                string target = UriToPath(FirstNonEmpty(Text(Obj(ca, "actionSpec"), "targetFile"), Text(Obj(ca, "actionSpec"), "filePathUri")));
                string title = desc.Length > 0 ? "修改：" + Cut(desc, 160) : target.Length > 0 ? "修改：" + ShortName(target) : "修改代码";
                AddMessage(messages, "tool", title, target, Transcripts.First(Text(ca, "actionResult")));
            } else if (type == "CORTEX_STEP_TYPE_LIST_DIRECTORY") {
                var ld = Obj(step, "listDirectory");
                string path = UriToPath(Text(ld, "directoryPathUri"));
                AddMessage(messages, "tool", path.Length > 0 ? "查看目录：" + ShortName(path) : "查看目录", path, "");
            } else if (type == "CORTEX_STEP_TYPE_GREP_SEARCH" || type == "CORTEX_STEP_TYPE_FIND") {
                var gs = Obj(step, "grepSearch") ?? Obj(step, "find");
                string q = FirstNonEmpty(Text(gs, "query"), Text(gs, "pattern"));
                string path = UriToPath(FirstNonEmpty(Text(gs, "searchPathUri"), Text(gs, "directoryPathUri")));
                AddMessage(messages, "tool", q.Length > 0 ? "搜索：" + Cut(q, 160) : "搜索文件", path, Transcripts.First(Text(gs, "rawOutput")));
            } else if (type == "CORTEX_STEP_TYPE_MCP_TOOL") {
                var mcp = Obj(step, "mcpTool");
                string server = Text(mcp, "serverName");
                string tool = Text(Obj(mcp, "toolCall"), "name");
                string label = server.Length > 0 && tool.Length > 0 ? server + "/" + tool : FirstNonEmpty(tool, server, "MCP");
                AddMessage(messages, "tool", "MCP：" + label, "", Transcripts.First(FirstNonEmpty(Text(mcp, "resultString"), Text(mcp, "result"))));
            } else if (type == "CORTEX_STEP_TYPE_ASK_QUESTION") {
                var aq = Obj(step, "askQuestion");
                var qs = AsObjectList(aq != null ? At(aq, "questions") : null);
                string qText = qs.Count > 0 ? FirstNonEmpty(Text(qs[0], "question"), Text(qs[0], "text"), Text(qs[0], "prompt")) : "";
                AddMessage(messages, "tool", qText.Length > 0 ? "提问：" + Cut(qText, 160) : "等待回答问题", "", "");
            } else if (type == "CORTEX_STEP_TYPE_ERROR_MESSAGE") {
                var err = Obj(Obj(step, "errorMessage"), "error") ?? Obj(step, "error");
                string msg = FirstNonEmpty(Text(err, "userErrorMessage"), Text(err, "shortError"), Text(err, "fullError"));
                if (msg.Length > 0) AddMessage(messages, "assistant", "错误：" + msg, null, null);
            }
        }
        return messages;
    }

    static bool IsDedicatedToolStep(string type) {
        return type == "CORTEX_STEP_TYPE_RUN_COMMAND" || type == "CORTEX_STEP_TYPE_VIEW_FILE"
            || type == "CORTEX_STEP_TYPE_CODE_ACTION" || type == "CORTEX_STEP_TYPE_LIST_DIRECTORY"
            || type == "CORTEX_STEP_TYPE_GREP_SEARCH" || type == "CORTEX_STEP_TYPE_FIND"
            || type == "CORTEX_STEP_TYPE_MCP_TOOL" || type == "CORTEX_STEP_TYPE_ASK_QUESTION";
    }

    static void AddMessage(List<Dictionary<string, object>> messages, string role, string text, string detail, string result) {
        text = role == "tool" ? Regex.Replace(text ?? "", @"\s+", " ").Trim() : (text ?? "").Trim();
        if (text.Length == 0) return;
        const int maxChars = 128 * 1024;
        int limit = role == "tool" ? 1500 : maxChars;
        bool truncated = text.Length > limit;
        if (truncated) text = text.Substring(0, Char.IsHighSurrogate(text[limit - 2]) ? limit - 2 : limit - 1) + "…";
        var item = new Dictionary<string, object> { { "role", role }, { "text", text } };
        if (truncated) item["truncated"] = true;
        if (role == "tool") {
            detail = (detail ?? "").Trim();
            if (detail.Length > 0 && text.IndexOf(detail.Length > 40 ? detail.Substring(0, 40) : detail, StringComparison.Ordinal) < 0) {
                item["detail"] = detail.Length > 400 ? detail.Substring(0, 400) + "…" : detail;
            }
            if (!String.IsNullOrEmpty(result)) item["result"] = result;
        }
        messages.Add(item);
    }

    static string ComputeStepsStamp(List<Dictionary<string, object>> steps, string status, int pendingIdx, long updatedMs) {
        // Relay validates `since` with `^[0-9.]{1,48}$`. Keep stamp strictly digits and dots.
        int statusCode = status == "confirm" ? 3 : status == "busy" ? 2 : 1;
        int lastLen = 0;
        if (steps.Count > 0) {
            var last = steps[steps.Count - 1];
            var pr = Obj(last, "plannerResponse");
            if (pr != null) lastLen = Text(pr, "modifiedResponse").Length + Text(pr, "response").Length + Text(pr, "thinking").Length;
            else lastLen = Text(Obj(last, "runCommand"), "combinedOutput").Length + Text(Obj(last, "userInput"), "userResponse").Length;
        }
        return steps.Count + "." + statusCode + "." + Math.Max(0, pendingIdx + 1) + "." + lastLen + "." + updatedMs;
    }

    static string ExtractUserInputText(Dictionary<string, object> ui) {
        if (ui == null) return "";
        string resp = Text(ui, "userResponse");
        if (resp.Length > 0) return Antigravity.ExtractUserRequest(resp);
        var parts = new List<string>();
        foreach (var item in AsObjectList(At(ui, "items"))) {
            string t = Text(item, "text");
            if (t.Length > 0) parts.Add(t);
        }
        return Antigravity.ExtractUserRequest(String.Join("\n", parts.ToArray()));
    }

    static bool IsCliSource(Dictionary<string, object> item) {
        string source = Text(item, "source");
        if (String.Equals(source, "CORTEX_TRAJECTORY_SOURCE_CLI", StringComparison.OrdinalIgnoreCase)) return true;
        string appDir = Text(item, "appDataDir");
        if (String.Equals(appDir, "antigravity-cli", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    static string ExtractWorkspaceFolder(Dictionary<string, object> container) {
        if (container == null) return "";
        foreach (var ws in AsObjectList(At(container, "workspaces"))) {
            string path = UriToPath(Text(ws, "workspaceFolderAbsoluteUri"));
            if (path.Length > 0) return path;
        }
        var uris = At(container, "workspaceUris") as IEnumerable;
        if (uris != null && !(uris is string)) {
            foreach (object raw in uris) {
                string path = UriToPath(Convert.ToString(raw ?? ""));
                if (path.Length > 0) return path;
            }
        }
        var meta = Obj(container, "trajectoryMetadata");
        if (meta != null) return ExtractWorkspaceFolder(meta);
        return "";
    }

    static string UriToPath(string uri) {
        if (String.IsNullOrWhiteSpace(uri)) return "";
        if (!uri.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) return "";
        return Antigravity.WorkspacePath("[\"" + uri.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"]");
    }

    static string OfflineTranscriptPath(string home, string id) {
        string path = Path.Combine(home, "brain", id, ".system_generated", "logs", "transcript.jsonl");
        return File.Exists(path) ? path : null;
    }

    static string ShortName(string path) {
        string trimmed = (path ?? "").TrimEnd('\\', '/');
        int cut = trimmed.LastIndexOfAny(new[] { '\\', '/' });
        return cut >= 0 && cut < trimmed.Length - 1 ? trimmed.Substring(cut + 1) : trimmed;
    }

    static string CombineStreams(string stdout, string stderr) {
        if (stdout.Length == 0) return stderr;
        if (stderr.Length == 0) return stdout;
        return stdout + "\n" + stderr;
    }

    static string Cut(string text, int max) {
        text = (text ?? "").Trim();
        return text.Length <= max ? text : text.Substring(0, max) + "…";
    }

    static string FirstNonEmpty(params string[] values) {
        for (int i = 0; i < values.Length; i++) {
            if (!String.IsNullOrWhiteSpace(values[i])) return values[i].Trim();
        }
        return "";
    }

    static string ShortHash(string value) {
        using (var sha = SHA256.Create()) {
            byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? ""));
            return BitConverter.ToString(hash, 0, 8).Replace("-", "").ToLowerInvariant();
        }
    }

    static JavaScriptSerializer NewSerializer() {
        return new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024, RecursionLimit = 200 };
    }

    static object At(Dictionary<string, object> d, string key) {
        object v;
        return d != null && d.TryGetValue(key, out v) ? v : null;
    }

    static string Text(Dictionary<string, object> d, string key) {
        object v = At(d, key);
        return v == null ? "" : Convert.ToString(v);
    }

    static int AsInt(Dictionary<string, object> d, string key, int fallback) {
        object v = At(d, key);
        if (v == null) return fallback;
        int parsed;
        return Int32.TryParse(Convert.ToString(v), out parsed) ? parsed : fallback;
    }

    static Dictionary<string, object> Obj(Dictionary<string, object> d, string key) {
        return At(d, key) as Dictionary<string, object>;
    }

    static List<Dictionary<string, object>> AsObjectList(object raw) {
        var list = new List<Dictionary<string, object>>();
        var seq = raw as IEnumerable;
        if (seq == null || raw is string) return list;
        foreach (object item in seq) {
            var dict = item as Dictionary<string, object>;
            if (dict != null) list.Add(dict);
        }
        return list;
    }
}

/// Instance-scoped background probe cache for Antigravity Desktop conversations (mirroring CodexDesktopSessions).
public sealed class AntigravityDesktopSessions {
    readonly object gate = new object();
    readonly Dictionary<string, AntigravityDesktopClient.State> states = new Dictionary<string, AntigravityDesktopClient.State>(StringComparer.OrdinalIgnoreCase);
    readonly AntigravityDesktopClient client;
    bool refreshing;

    /// Fired when a probed desktop conversation changed state, became reachable, or dropped out.
    public event Action Changed;

    public AntigravityDesktopSessions(AntigravityDesktopClient client = null) {
        this.client = client ?? new AntigravityDesktopClient();
    }

    public AntigravityDesktopClient.State Get(string id) {
        if (String.IsNullOrEmpty(id)) return null;
        lock (gate) {
            AntigravityDesktopClient.State state;
            return states.TryGetValue(id, out state) && DateTime.UtcNow - state.At < TimeSpan.FromSeconds(15) ? state : null;
        }
    }

    public AntigravityDesktopClient.State[] All() {
        lock (gate) {
            var cutoff = DateTime.UtcNow - TimeSpan.FromSeconds(15);
            return states.Values.Where(s => s.At >= cutoff).ToArray();
        }
    }

    public void Refresh(IEnumerable<string> ids = null) {
        string[] explicitIds = ids == null ? null : ids.Where(id => !String.IsNullOrEmpty(id)).Distinct(StringComparer.OrdinalIgnoreCase).Take(20).ToArray();
        lock (gate) {
            if (refreshing) return;
            refreshing = true;
        }
        Task.Run(() => {
            bool changed = false;
            try {
                if (!client.Available) {
                    lock (gate) {
                        if (states.Count > 0) { states.Clear(); changed = true; }
                    }
                    return;
                }
                string[] wanted = explicitIds;
                if (wanted == null) {
                    wanted = client.ActiveTrajectoryIds().Take(20).ToArray();
                }
                var fresh = new Dictionary<string, AntigravityDesktopClient.State>(StringComparer.OrdinalIgnoreCase);
                Parallel.ForEach(wanted, new ParallelOptions { MaxDegreeOfParallelism = 2 }, id => {
                    try {
                        var state = client.Inspect(id);
                        if (state != null) {
                            lock (fresh) fresh[id] = state;
                        }
                    } catch (Exception) { }
                });
                lock (gate) {
                    foreach (var pair in fresh) {
                        AntigravityDesktopClient.State prior;
                        if (!states.TryGetValue(pair.Key, out prior) || !pair.Value.SameAs(prior)) changed = true;
                        states[pair.Key] = pair.Value;
                    }
                    foreach (string old in states.Keys.Where(id => !fresh.ContainsKey(id)).ToArray()) {
                        states.Remove(old);
                        changed = true;
                    }
                }
            } finally {
                lock (gate) refreshing = false;
                if (changed) {
                    var told = Changed;
                    if (told != null) { try { told(); } catch { } }
                }
            }
        });
    }
}

/// Direct desktop adapter for Antigravity Desktop: creates a conversation via ConnectRPC and sends the first message.
public sealed class AntigravityDesktopAdapter : IDirectDesktopAdapter {
    readonly AntigravityDesktopClient client;
    public AntigravityDesktopAdapter(AntigravityDesktopClient client = null) {
        this.client = client ?? new AntigravityDesktopClient();
    }
    public string Tool { get { return "agy"; } }
    public bool Available { get { return client.Available; } }
    public void Check(string folder, string draft) { AntigravityDesktopClient.ValidateFolderAndDraft(folder, draft); }
    public Dictionary<string, object> Open(string folder, string draft) {
        return Open(Guid.NewGuid().ToString("N"), folder, draft);
    }
    public Dictionary<string, object> Open(string id, string folder, string draft) {
        if (!Available) throw new InvalidOperationException("未找到运行中的 Antigravity 桌面应用，请先打开 Antigravity 后重试");
        Check(folder, draft);
        return client.CreateAndSend(folder, draft, id);
    }
}
}
