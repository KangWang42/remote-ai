using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace RemoteCli {
/// Shared text filtering, UTF-8 / surrogate-safe truncation, terminal normalization, mixed-encoding decoding,
/// and hardened child-process helpers used across GitReview, Worktrees, Chats, Teams, Peers, and Organizers.
public static class ProcessText {
    /// Hardened read-only Git flags: avoid optional index locks, fsmonitor daemons,
    /// path quoting, GPG subprocesses, and interactive pagers.
    public const string HardenedReadOnlyGitPrefix =
        "--no-optional-locks -c core.fsmonitor=false -c core.quotepath=false -c gpg.program=false --no-pager ";

    static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    static readonly Encoding LenientUtf8 = new UTF8Encoding(false, false);

    // Matches standard ANSI CSI / OSC / single-char escape sequences.
    static readonly Regex AnsiRegex = new Regex(
        @"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07\x1B]*(?:\x07|\x1B\\)|[@-Z\\-_])",
        RegexOptions.Compiled);
    static readonly Regex LineEraseCsi = new Regex(@"\x1B\[(?:2K|0?K)", RegexOptions.Compiled);
    static readonly Regex LeadingClearToEol = new Regex(@"^(?:\x1B\[0?K)+", RegexOptions.Compiled);

    /// Sets non-interactive Git environment variables unless already specified by the caller.
    public static void ApplyNonInteractiveGitEnv(ProcessStartInfo info) {
        if (info == null) return;
        if (!info.EnvironmentVariables.ContainsKey("GIT_TERMINAL_PROMPT"))
            info.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
        if (!info.EnvironmentVariables.ContainsKey("GCM_INTERACTIVE"))
            info.EnvironmentVariables["GCM_INTERACTIVE"] = "never";
    }

    /// Terminates a child process and its entire descendant tree on Windows via System32\taskkill.exe,
    /// falling back to Process.Kill when taskkill is unavailable.
    public static void KillProcessTree(int pid, int waitMs = 5000) {
        if (pid <= 0) return;
        string taskkill = Path.Combine(Environment.SystemDirectory, "taskkill.exe");
        try {
            using (var killer = Process.Start(new ProcessStartInfo(taskkill, "/PID " + pid + " /T /F") {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            })) {
                if (killer != null) killer.WaitForExit(Math.Max(500, waitMs));
            }
            return;
        } catch (System.ComponentModel.Win32Exception) { }
        catch (InvalidOperationException) { }
        catch (IOException) { }
        try {
            using (var proc = Process.GetProcessById(pid)) {
                if (!proc.HasExited) proc.Kill();
            }
        } catch (ArgumentException) { }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    /// Strips ANSI CSI/OSC escape sequences from terminal text.
    public static string StripAnsi(string text) {
        if (String.IsNullOrEmpty(text)) return "";
        return AnsiRegex.Replace(text, "");
    }

    /// Normalizes terminal output by stripping ANSI codes and collapsing carriage-return progress/spinner
    /// overwrites and line-erase sequences (`\x1B[2K`, `\x1B[K`, `\x1B[0K`).
    public static string NormalizeTerminal(string raw) {
        if (String.IsNullOrEmpty(raw)) return "";
        string[] rawLines = raw.Replace("\r\n", "\n").Split('\n');
        var normalized = new List<string>(rawLines.Length);
        for (int i = 0; i < rawLines.Length; i++) {
            string line = rawLines[i];
            if (line.Length == 0) {
                normalized.Add("");
                continue;
            }
            bool hadLineErase = LineEraseCsi.IsMatch(line);
            string working = line;
            int lastFullErase = working.LastIndexOf("\x1B[2K", StringComparison.Ordinal);
            if (lastFullErase >= 0) working = working.Substring(lastFullErase + 4);

            if (working.IndexOf('\r') < 0 && !hadLineErase) {
                normalized.Add(StripAnsi(working));
                continue;
            }

            string currentFrame = "";
            string[] crFrames = working.Split('\r');
            for (int f = 0; f < crFrames.Length; f++) {
                string seg = crFrames[f];
                if (f > 0 && LeadingClearToEol.IsMatch(seg)) {
                    seg = LeadingClearToEol.Replace(seg, "");
                    currentFrame = StripAnsi(seg);
                    continue;
                }
                string cleanSeg = StripAnsi(seg);
                if (cleanSeg.Length > 0) currentFrame = cleanSeg;
            }

            if (hadLineErase && currentFrame.Length == 0) continue;
            normalized.Add(currentFrame);
        }
        return String.Join("\n", normalized.ToArray()).TrimEnd('\r', '\n');
    }

    /// Decodes child-process stdout/stderr bytes as strict UTF-8 first; when non-UTF-8 bytes appear
    /// (common on Chinese Windows when native tools emit CP936/GBK alongside UTF-8 lines), falls back
    /// per line to the active Windows code page so valid UTF-8 lines are never corrupted.
    public static string DecodeProcessOutput(byte[] bytes, Encoding fallbackOverride = null) {
        if (bytes == null || bytes.Length == 0) return "";
        try {
            return StrictUtf8.GetString(bytes);
        } catch (DecoderFallbackException) { }

        Encoding fallback = fallbackOverride ?? Encoding.Default;
        if (fallback == null || fallback.CodePage == 65001) return LenientUtf8.GetString(bytes);

        int firstNl = Array.IndexOf(bytes, (byte)'\n');
        if (firstNl < 0) {
            try { return fallback.GetString(bytes); }
            catch (DecoderFallbackException) { return LenientUtf8.GetString(bytes); }
        }

        var sb = new StringBuilder(bytes.Length);
        int offset = 0;
        while (offset < bytes.Length) {
            int nl = Array.IndexOf(bytes, (byte)'\n', offset);
            int end = nl < 0 ? bytes.Length : nl + 1;
            int len = end - offset;
            try {
                sb.Append(StrictUtf8.GetString(bytes, offset, len));
            } catch (DecoderFallbackException) {
                try { sb.Append(fallback.GetString(bytes, offset, len)); }
                catch (DecoderFallbackException) { sb.Append(LenientUtf8.GetString(bytes, offset, len)); }
            }
            offset = end;
        }
        return sb.ToString();
    }

    /// Slices the prefix of `text` up to `maxChars` without splitting a UTF-16 surrogate pair.
    public static string SliceSafeChars(string text, int maxChars) {
        if (String.IsNullOrEmpty(text) || maxChars <= 0) return "";
        if (text.Length <= maxChars) return text;
        int end = maxChars;
        if (end > 0 && Char.IsHighSurrogate(text[end - 1])) end--;
        return end > 0 ? text.Substring(0, end) : "";
    }

    /// Slices the suffix of `text` up to `maxChars` without splitting a UTF-16 surrogate pair.
    public static string SliceSafeCharsTail(string text, int maxChars) {
        if (String.IsNullOrEmpty(text) || maxChars <= 0) return "";
        if (text.Length <= maxChars) return text;
        int start = text.Length - maxChars;
        if (start < text.Length && Char.IsLowSurrogate(text[start])) start++;
        return start < text.Length ? text.Substring(start) : "";
    }

    /// Surrogate-safe head truncation with an optional suffix, guaranteeing `result.Length <= maxChars`
    /// whenever `maxChars >= suffix.Length`.
    public static string TruncateSafe(string text, int maxChars, string suffix = "…") {
        text = text ?? "";
        if (maxChars <= 0) return "";
        if (text.Length <= maxChars) return text;
        suffix = suffix ?? "";
        if (maxChars <= suffix.Length) return SliceSafeChars(text, maxChars);
        return SliceSafeChars(text, maxChars - suffix.Length) + suffix;
    }

    /// Slices the prefix of `text` so its UTF-8 encoding does not exceed `maxBytes`,
    /// never splitting a surrogate pair or multi-byte UTF-8 character.
    public static string SliceUtf8Bytes(string text, int maxBytes) {
        if (String.IsNullOrEmpty(text) || maxBytes <= 0) return "";
        if (LenientUtf8.GetByteCount(text) <= maxBytes) return text;
        int bytes = 0, end = 0;
        for (int i = 0; i < text.Length;) {
            int unitLen = 1;
            int cp = text[i];
            if (Char.IsHighSurrogate(text[i]) && i + 1 < text.Length && Char.IsLowSurrogate(text[i + 1])) {
                unitLen = 2;
                cp = Char.ConvertToUtf32(text[i], text[i + 1]);
            }
            int chBytes = cp <= 0x7F ? 1 : cp <= 0x7FF ? 2 : cp <= 0xFFFF ? 3 : 4;
            if (bytes + chBytes > maxBytes) break;
            bytes += chBytes;
            i += unitLen;
            end = i;
        }
        return text.Substring(0, end);
    }

    /// Slices the suffix of `text` so its UTF-8 encoding does not exceed `maxBytes`,
    /// never splitting a surrogate pair or multi-byte UTF-8 character.
    public static string SliceUtf8BytesTail(string text, int maxBytes) {
        if (String.IsNullOrEmpty(text) || maxBytes <= 0) return "";
        if (LenientUtf8.GetByteCount(text) <= maxBytes) return text;
        int bytes = 0, start = text.Length;
        for (int i = text.Length; i > 0;) {
            int unitLen = 1;
            int cp = text[i - 1];
            if (i >= 2 && Char.IsLowSurrogate(text[i - 1]) && Char.IsHighSurrogate(text[i - 2])) {
                unitLen = 2;
                cp = Char.ConvertToUtf32(text[i - 2], text[i - 1]);
            }
            int chBytes = cp <= 0x7F ? 1 : cp <= 0x7FF ? 2 : cp <= 0xFFFF ? 3 : 4;
            if (bytes + chBytes > maxBytes) break;
            bytes += chBytes;
            i -= unitLen;
            start = i;
        }
        return text.Substring(start);
    }

    /// Exact UTF-8 byte-bounded truncation with suffix, aligned to newline when reasonable.
    public static string TruncateUtf8(string text, int maxBytes, string suffix = "\n…（后文已省略）") {
        text = text ?? "";
        if (maxBytes <= 0) return "";
        if (LenientUtf8.GetByteCount(text) <= maxBytes) return text;
        suffix = suffix ?? "";
        int suffixBytes = LenientUtf8.GetByteCount(suffix);
        if (maxBytes <= suffixBytes) return SliceUtf8Bytes(text, maxBytes);
        int budget = maxBytes - suffixBytes;
        string slice = SliceUtf8Bytes(text, budget);
        int lastNl = slice.LastIndexOf('\n');
        if (lastNl > 0 && LenientUtf8.GetByteCount(slice.Substring(0, lastNl)) > budget * 0.6)
            slice = slice.Substring(0, lastNl);
        string result = slice + suffix;
        return LenientUtf8.GetByteCount(result) <= maxBytes ? result : SliceUtf8Bytes(result, maxBytes);
    }

    /// Head-tail folding bounded by character count (~38% head + ~62% tail), preserving both initial context /
    /// first root-cause errors and final summaries / test verdicts.
    public static string FoldHeadTail(string text, int maxChars) {
        text = text ?? "";
        if (maxChars <= 0) return "";
        if (text.Length <= maxChars) return text;
        if (maxChars < 120) return TruncateSafe(text, maxChars);

        string worstMarker = "\n…（已折叠中间 " + text.Length + " 字，共 " + text.Length + " 字）…\n";
        int contentBudget = maxChars - worstMarker.Length;
        if (contentBudget < 40) return TruncateSafe(text, maxChars);

        int headBudget = Math.Max(16, (int)(contentBudget * 0.38));
        int tailBudget = Math.Max(24, contentBudget - headBudget);

        string head = SliceSafeChars(text, headBudget);
        int lastNl = head.LastIndexOf('\n');
        if (lastNl > headBudget * 0.5) head = head.Substring(0, lastNl);

        string tail = SliceSafeCharsTail(text, tailBudget);
        int firstNl = tail.IndexOf('\n');
        if (firstNl >= 0 && tail.Length - (firstNl + 1) > tailBudget * 0.5)
            tail = tail.Substring(firstNl + 1);

        int omitted = Math.Max(1, text.Length - head.Length - tail.Length);
        string marker = "\n…（已折叠中间 " + omitted + " 字，共 " + text.Length + " 字）…\n";
        string result = head + marker + tail;
        return result.Length <= maxChars ? result : TruncateSafe(result, maxChars);
    }

    /// Head-tail folding strictly bounded by UTF-8 byte count (~38% head + ~62% tail).
    /// Used to keep Antigravity MCP tool outputs safely below its ~10 KB inline spill threshold.
    public static string FoldHeadTailUtf8(string text, int maxBytes) {
        text = text ?? "";
        if (maxBytes <= 0) return "";
        int totalBytes = LenientUtf8.GetByteCount(text);
        if (totalBytes <= maxBytes) return text;
        if (maxBytes < 140) return TruncateUtf8(text, maxBytes);

        string worstMarker = "\n…（已折叠中间 " + totalBytes + " 字节，共 " + totalBytes + " 字节）…\n";
        int contentBytes = maxBytes - LenientUtf8.GetByteCount(worstMarker);
        if (contentBytes < 48) return TruncateUtf8(text, maxBytes);

        int headBudget = Math.Max(16, (int)(contentBytes * 0.38));
        int tailBudget = Math.Max(24, contentBytes - headBudget);

        string head = SliceUtf8Bytes(text, headBudget);
        int lastNl = head.LastIndexOf('\n');
        if (lastNl > 0 && LenientUtf8.GetByteCount(head.Substring(0, lastNl)) > headBudget * 0.5)
            head = head.Substring(0, lastNl);

        string tail = SliceUtf8BytesTail(text, tailBudget);
        int firstNl = tail.IndexOf('\n');
        if (firstNl >= 0 && LenientUtf8.GetByteCount(tail.Substring(firstNl + 1)) > tailBudget * 0.5)
            tail = tail.Substring(firstNl + 1);

        int headBytes = LenientUtf8.GetByteCount(head);
        int tailBytes = LenientUtf8.GetByteCount(tail);
        int omittedBytes = Math.Max(1, totalBytes - headBytes - tailBytes);
        string marker = "\n…（已折叠中间 " + omittedBytes + " 字节，共 " + totalBytes + " 字节）…\n";
        string result = head + marker + tail;
        return LenientUtf8.GetByteCount(result) <= maxBytes ? result : TruncateUtf8(result, maxBytes);
    }
}
}
