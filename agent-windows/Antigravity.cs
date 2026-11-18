using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace RemoteCli {
internal static class Antigravity {
    // Independent consoles have no Remote AI terminal ID. Report only the start time,
    // never a command line, PID or a guessed conversation based on the working folder.
    internal static long[] ExternalStarts(string executable, int[] ownedRoots) {
        var starts = new List<long>();
        if (String.IsNullOrEmpty(executable)) return starts.ToArray();
        var parents = SessionOrigin.Parents();
        using (var current = Process.GetCurrentProcess()) {
            foreach (var process in Process.GetProcessesByName("agy")) using (process) {
                try {
                    if (process.SessionId != current.SessionId || !String.Equals(executable, process.MainModule.FileName, StringComparison.OrdinalIgnoreCase)) continue;
                    if (ownedRoots.Contains(process.Id) || CodexSessions.Descends(process.Id, ownedRoots, parents)) continue;
                    starts.Add(new DateTimeOffset(process.StartTime).ToUnixTimeMilliseconds());
                } catch (System.ComponentModel.Win32Exception) { } catch (InvalidOperationException) { }
            }
        }
        return starts.OrderByDescending(time => time).Take(16).ToArray();
    }
}
}
