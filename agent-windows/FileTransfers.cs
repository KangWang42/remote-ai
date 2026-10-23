using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace RemoteCli {
/// Bounded uploads owned by one agent. A destination is published only after all bytes arrive; never replaced.
public sealed class FileTransfers : IDisposable {
    public const long Limit = 300L * 1024 * 1024;
    sealed class Upload : IDisposable {
        public string Root, Path, Temporary, Target;
        public long Size;
        public DateTime Touched;
        public FileStream Stream;
        public readonly List<SafeFileHandle> Parents = new List<SafeFileHandle>();
        public void Dispose() {
            if (Stream != null) Stream.Dispose();
            try { if (Temporary != null) File.Delete(Temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            foreach (var parent in Parents) parent.Dispose();
        }
    }
    readonly Dictionary<string, Upload> pending = new Dictionary<string, Upload>();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    static string Text(Dictionary<string, object> op, string key) { object value; return op.TryGetValue(key, out value) ? Convert.ToString(value) : ""; }
    static long Number(Dictionary<string, object> op, string key) {
        object value;
        if (!op.TryGetValue(key, out value) || !(value is int || value is long)) throw new ArgumentException("文件大小或位置无效");
        return Convert.ToInt64(value);
    }
    static Dictionary<string, object> Answer(long offset) { return new Dictionary<string, object> { { "offset", offset } }; }
    public static bool Handles(string action) { return action == "file_upload_start" || action == "file_upload_chunk" || action == "file_upload_finish" || action == "file_upload_cancel"; }
    public void Expire(DateTime? now = null) {
        DateTime cutoff = (now ?? DateTime.UtcNow).AddMinutes(-10);
        lock (pending) foreach (string key in pending.Where(p => p.Value.Touched < cutoff).Select(p => p.Key).ToArray()) Remove(key);
    }
    void Remove(string key) { Upload upload; if (pending.TryGetValue(key, out upload)) { pending.Remove(key); upload.Dispose(); } }
    public void Dispose() { lock (pending) foreach (string key in pending.Keys.ToArray()) Remove(key); }
    public Dictionary<string, object> Run(string root, Dictionary<string, object> op) {
        lock (pending) {
            Expire();
            string action = Text(op, "action"), key = Text(op, "upload"), relative = Text(op, "path");
            if (!Regex.IsMatch(key, @"\A[a-f0-9]{32}\z")) throw new ArgumentException("上传编号无效");
            root = System.IO.Path.GetFullPath(root).TrimEnd('\\');
            if (action == "file_upload_start") {
                if (pending.ContainsKey(key)) throw new ArgumentException("上传编号已使用");
                if (pending.Count >= 4) throw new ArgumentException("同时上传的文件过多，请稍后重试");
                long size = Number(op, "size");
                if (size < 0 || size > Limit) throw new ArgumentException("单个文件上传上限为 300 MB");
                string[] parts = relative.Split('/');
                if (parts.Length == 0 || parts.Any(p => String.IsNullOrWhiteSpace(p) || p == "." || p == ".." || p.EndsWith(".") || p.EndsWith(" ") || p.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 || Regex.IsMatch(p, @"\A(CON|PRN|AUX|NUL|COM[0-9¹²³]|LPT[0-9¹²³])(?:\.|\z)", RegexOptions.IgnoreCase)))
                    throw new ArgumentException("文件名或路径无效，请重命名后再上传");
                var upload = new Upload { Root = root, Path = relative, Size = size, Touched = DateTime.UtcNow };
                try {
                    string realRoot = TerminalAgent.RealPath(root + "\\"), parent = realRoot + "\\";
                    // Hold every parent without delete sharing, so a directory cannot be exchanged for a junction mid-upload.
                    for (int i = 0; i < parts.Length; i++) {
                        if (i > 0) parent = System.IO.Path.Combine(parent, parts[i - 1]);
                        var handle = CreateFileW(parent + "\\", 0, 3, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
                        if (handle.IsInvalid) { handle.Dispose(); throw new ArgumentException("目标文件夹不存在或无法写入"); }
                        upload.Parents.Add(handle);
                        if (i > 0 && (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("不能上传到链接文件夹，请打开实际项目文件夹");
                        string real = TerminalAgent.RealPath(parent + "\\");
                        if (real != realRoot && !real.StartsWith(realRoot + "\\", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("路径不在项目文件夹内");
                    }
                    upload.Target = System.IO.Path.Combine(parent, parts.Last());
                    if (File.Exists(upload.Target) || Directory.Exists(upload.Target)) throw new ArgumentException("已有同名文件或文件夹，请重命名后再上传");
                    upload.Temporary = System.IO.Path.Combine(parent, ".remoteai-upload-" + Guid.NewGuid().ToString("N") + ".tmp");
                    upload.Stream = new FileStream(upload.Temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    pending.Add(key, upload);
                    return Answer(0);
                } catch { upload.Dispose(); throw; }
            }
            Upload current;
            if (!pending.TryGetValue(key, out current)) {
                if (action == "file_upload_cancel") return Answer(0);
                throw new ArgumentException("上传已过期，请重新选择文件");
            }
            if (!String.Equals(root, current.Root, StringComparison.OrdinalIgnoreCase) || relative != current.Path) throw new ArgumentException("上传目标不一致");
            current.Touched = DateTime.UtcNow;
            if (action == "file_upload_cancel") { Remove(key); return Answer(0); }
            if (action == "file_upload_chunk") {
                string encoded = Text(op, "data");
                if (encoded.Length > TerminalAgent.FilePiece * 4 / 3) throw new ArgumentException("上传分块过大");
                byte[] bytes;
                try { bytes = Convert.FromBase64String(encoded); } catch (FormatException) { throw new ArgumentException("上传内容无效"); }
                if (Number(op, "offset") != current.Stream.Position || bytes.Length == 0 || current.Stream.Position + bytes.Length > current.Size) throw new ArgumentException("上传位置或长度不一致");
                try { current.Stream.Write(bytes, 0, bytes.Length); return Answer(current.Stream.Position); }
                catch { Remove(key); throw; }
            }
            if (action != "file_upload_finish") throw new ArgumentException("不支持此操作");
            if (current.Stream.Position != current.Size) throw new ArgumentException("文件尚未完整上传");
            try {
                current.Stream.Flush(true);
                current.Stream.Dispose();
                File.Move(current.Temporary, current.Target); // same directory, atomic, fails if another writer created the target
                return Answer(current.Size);
            } finally { Remove(key); }
        }
    }
}
}
