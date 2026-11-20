using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace RemoteCli {
/// Verified ownership of a conversation whose writer holds a Windows lock file (Codex thread-writer-locks,
/// Antigravity presence locks). Kept as a shared base so both tools report the same fields and share one takeover path.
public class LockOwner {
    public bool Known, Live, CanTakeover;
    public int Pid;
    public long Started;
    public string Host = "unknown";
    public string Origin = "";
    public string Reason = "正在确认电脑会话的归属，请稍后刷新";
}

/// Shared Windows file-lock probing, Restart Manager single-holder verification, and double-checked process takeover.
/// Never falls back to name-based kills or unverified PIDs.
public static class SessionLocks {
    static readonly Regex UuidPattern = new Regex(@"\A[0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}\z");
    public static bool IsUuid(string value) { return UuidPattern.IsMatch(value ?? ""); }

    public sealed class Holder {
        public int Pid;
        public long Started;
        public string ProcessName = "";
    }

    [StructLayout(LayoutKind.Sequential)] struct UniqueProcess { public uint pid; public System.Runtime.InteropServices.ComTypes.FILETIME started; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct ProcessInfo {
        public UniqueProcess process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string service;
        public uint type, status, session;
        [MarshalAs(UnmanagedType.Bool)] public bool restartable;
    }
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] static extern int RmStartSession(out uint session, int flags, StringBuilder key);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] static extern int RmRegisterResources(uint session, uint files, string[] names, uint processes, IntPtr applications, uint services, IntPtr serviceNames);
    [DllImport("rstrtmgr.dll")] static extern int RmGetList(uint session, out uint needed, ref uint count, [In, Out] ProcessInfo[] processes, ref uint reasons);
    [DllImport("rstrtmgr.dll")] static extern int RmEndSession(uint session);

    /// True when another process holds an exclusive byte-range lock (ERROR_LOCK_VIOLATION = 33) or exclusive file share
    /// (ERROR_SHARING_VIOLATION = 32); false when the file does not exist or can be locked; null when indeterminate.
    public static bool? Held(string path) {
        try {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete)) {
                try { stream.Lock(0, Int64.MaxValue); stream.Unlock(0, Int64.MaxValue); return false; }
                catch (IOException ex) { return (ex.HResult & 0xffff) == 33 ? (bool?)true : null; }
            }
        } catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (IOException ex) {
            int code = ex.HResult & 0xffff;
            return (code == 32 || code == 33) ? (bool?)true : null;
        }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// Identifies the single live process holding `path` via Windows Restart Manager and verifies its exact UTC
    /// FILETIME start timestamp against the open Process handle. Returns null if zero, multiple, or unverifiable.
    public static Holder SingleHolder(string path) {
        uint session;
        if (RmStartSession(out session, 0, new StringBuilder(33)) != 0) return null;
        try {
            if (RmRegisterResources(session, 1, new[] { path }, 0, IntPtr.Zero, 0, IntPtr.Zero) != 0) return null;
            uint needed, count = 8, reasons = 0; var records = new ProcessInfo[count];
            int code = RmGetList(session, out needed, ref count, records, ref reasons);
            if (code == 234 && needed <= 256) { count = needed; records = new ProcessInfo[count]; code = RmGetList(session, out needed, ref count, records, ref reasons); }
            if (code != 0 || count != 1) return null;
            var record = records[0];
            long started = ((long)record.process.started.dwHighDateTime << 32) | (uint)record.process.started.dwLowDateTime;
            using (var process = Process.GetProcessById((int)record.process.pid)) {
                if (process.StartTime.ToUniversalTime().ToFileTimeUtc() != started) return null;
                return new Holder { Pid = process.Id, Started = started, ProcessName = process.ProcessName };
            }
        } catch { return null; }
        finally { RmEndSession(session); }
    }

    /// Double-checked verified termination of the single lock holder, then waits until the OS releases the lock.
    public static void Takeover(Func<LockOwner> inspect, string lockPath, IEnumerable<int> protectedRoots, Dictionary<int, int> parents,
        string unsafeReason, string timeoutReason, string lockReason) {
        LockOwner owner = inspect();
        if (!owner.Known) throw new InvalidOperationException(owner.Reason);
        if (!owner.Live) return;
        if (!owner.CanTakeover || SessionOrigin.Descends(owner.Pid, protectedRoots, parents)) throw new InvalidOperationException(unsafeReason);
        // Process.Kill uses the opened process handle; never invoke a global name-based kill.
        using (var process = Process.GetProcessById(owner.Pid)) {
            if (process.StartTime.ToUniversalTime().ToFileTimeUtc() != owner.Started) throw new InvalidOperationException("会话进程已变化，请刷新后重试");
            LockOwner current = inspect();
            if (!current.CanTakeover || current.Pid != owner.Pid || current.Started != owner.Started) throw new InvalidOperationException("会话归属已变化，请刷新后重试");
            process.Kill();
            if (!process.WaitForExit(5000)) throw new InvalidOperationException(timeoutReason);
        }
        for (int n = 0; n < 50; n++) { if (Held(lockPath) == false) return; Thread.Sleep(50); }
        throw new InvalidOperationException(lockReason);
    }
}
}
