using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace RemoteCli {
/// The command a tool is running for the step it is at, seen from outside: the shells its own process has started
/// since that step began, each with everything below it. Ending them is what Esc does at the computer: the tool
/// notes the step as failed at once and goes on to its next one, where it can be told to stop, instead of waiting
/// for the command to run out (tried on Claude Code 2.1.283: about a second from the end of the command to the
/// next step).
///
/// Only shells are ended, and only ones started after the step began: what the tool keeps running beside its
/// steps (its servers, a shell it started earlier) is older, or is no shell, and is left alone.
public static class Running {
    static readonly string[] Shells = { "cmd", "powershell", "pwsh", "bash", "sh", "zsh", "wsl" };

    /// The shells that `runner` started at or after `since`, with all that runs below each, deepest first.
    public static int[] Commands(int runner, DateTime since, Dictionary<int, int> parents = null) {
        var found = new List<int>();
        if (runner <= 0) return found.ToArray();
        parents = parents ?? SessionOrigin.Parents();
        DateTime runs;
        if (!Started(runner, out runs)) return found.ToArray();
        foreach (int child in parents.Where(p => p.Value == runner && p.Key != runner).Select(p => p.Key)) {
            DateTime began; string name;
            if (!Started(child, out began, out name) || began < runs || began < since.AddSeconds(-1)) continue;
            if (Array.IndexOf(Shells, name.ToLowerInvariant()) < 0) continue;
            Below(child, began, parents, found, 0);
            found.Add(child);
        }
        return found.ToArray();
    }
    static void Below(int pid, DateTime began, Dictionary<int, int> parents, List<int> found, int depth) {
        if (depth > 24) return;
        foreach (int child in parents.Where(p => p.Value == pid && p.Key != pid).Select(p => p.Key)) {
            DateTime later;
            // (a number is given anew once its process has ended: what is below is not older than what is above)
            if (!Started(child, out later) || later < began.AddSeconds(-1) || found.Contains(child)) continue;
            Below(child, later, parents, found, depth + 1);
            found.Add(child);
        }
    }
    static bool Started(int pid, out DateTime began) { string name; return Started(pid, out began, out name); }
    static bool Started(int pid, out DateTime began, out string name) {
        began = DateTime.MinValue; name = "";
        try { using (var process = Process.GetProcessById(pid)) { began = process.StartTime.ToUniversalTime(); name = process.ProcessName; return true; } }
        catch (ArgumentException) { return false; } catch (InvalidOperationException) { return false; } catch (System.ComponentModel.Win32Exception) { return false; }
    }
    /// Ends them. How many were ended.
    public static int End(int runner, DateTime since) {
        int ended = 0;
        foreach (int pid in Commands(runner, since)) {
            try { using (var process = Process.GetProcessById(pid)) { process.Kill(); ended++; } }
            catch (ArgumentException) { } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
        }
        return ended;
    }
}
}
