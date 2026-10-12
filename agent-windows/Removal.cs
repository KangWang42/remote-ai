using System;
using System.IO;
using System.Runtime.InteropServices;

namespace RemoteCli {
/// A saved conversation its owner no longer wants in the lists, put away from the phone. Nothing is destroyed: a
/// conversation of Claude Code's is a file (with a folder of what belongs to it) and goes to the recycle bin; one of
/// Codex's is archived by Codex itself (`codex archive <id>`), which keeps Codex's own lists right, and
/// `codex unarchive <id>` brings it back.
public static class Removal {
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct FileOperation {
        public IntPtr window; public uint what; public string from, to; public ushort flags;
        [MarshalAs(UnmanagedType.Bool)] public bool aborted; public IntPtr mappings; public string title;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHFileOperationW(ref FileOperation operation);
    const uint Delete = 3;
    const ushort Silent = 0x0004, NoConfirmation = 0x0010, AllowUndo = 0x0040, NoErrors = 0x0400;

    /// Puts a file or a folder into the recycle bin, without a window and without a question. REMOTECLI_REMOVED
    /// names a folder to move it into instead, for a test: what a test makes up does not go into its owner's bin.
    public static void Recycle(string path) {
        string instead = Environment.GetEnvironmentVariable("REMOTECLI_REMOVED");
        if (!String.IsNullOrEmpty(instead)) {
            Directory.CreateDirectory(instead);
            string to = Path.Combine(instead, Path.GetFileName(path.TrimEnd('\\', '/')));
            if (Directory.Exists(path)) Directory.Move(path, to); else File.Move(path, to);
            return;
        }
        var operation = new FileOperation { what = Delete, from = Path.GetFullPath(path) + "\0\0", flags = (ushort)(AllowUndo | NoConfirmation | Silent | NoErrors) };
        int code = SHFileOperationW(ref operation);
        if (code != 0 || operation.aborted) throw new IOException("回收站没有收下这个文件（" + code + "）");
    }
    /// A conversation of Claude Code's: its file, and the folder beside it that has its name (what its helpers wrote).
    public static void Claude(string file) {
        if (!File.Exists(file)) throw new FileNotFoundException();
        Recycle(file);
        string beside = Path.Combine(Path.GetDirectoryName(file), Path.GetFileNameWithoutExtension(file));
        if (Directory.Exists(beside)) Recycle(beside);
    }
    /// A conversation of Codex's, by Codex's own command. InvalidOperationException says, in words for the person,
    /// why it was not archived.
    public static void Codex(string launcher, string session) {
        string program = CodexQueue.Program(launcher);
        if (program == null) throw new InvalidOperationException("电脑上没有找到 Codex，对话没有删除");
        string said; int code;
        try { code = CodexQueue.Run(program, "archive " + CodexQueue.Quoted(session), 30, out said); }
        catch (System.ComponentModel.Win32Exception) { throw new InvalidOperationException("对话没有删除：电脑上启动不了 Codex"); }
        if (code == 0) return;
        said = (said ?? "").Trim();
        if (said.Length > 160) said = said.Substring(said.Length - 160);
        throw new InvalidOperationException(code == -1 ? "Codex 没有及时归档这段对话，请稍后重试" : "Codex 没有归档这段对话" + (said.Length > 0 ? "：" + said : "，请确认 Codex 是带 archive 命令的版本"));
    }
}
}
