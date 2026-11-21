using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace RemoteCli {
/// Validated screenshots attached to a chat message.
public sealed class Attachment {
    public string RelativePath { get; private set; }
    public string AbsolutePath { get; private set; }
    public string MediaType { get; private set; }
    public byte[] Data { get; private set; }

    internal Attachment(string relativePath, string absolutePath, string mediaType, byte[] data) {
        RelativePath = relativePath;
        AbsolutePath = absolutePath;
        MediaType = mediaType;
        Data = data;
    }
}

public static class Attachments {
    public const long Limit = 20L * 1024 * 1024;
    public const int MaxCount = 4;
    static readonly Regex Name = new Regex(@"\A[0-9]{8}-[0-9]{6}-[0-9a-f]{6}\.(png|jpg|jpeg|webp|gif)\z", RegexOptions.None);
    static readonly Regex Path = new Regex(@"\A\.remote-ai/attachments/([^/]+)\z", RegexOptions.None);

    static string Media(string extension) {
        switch (extension.ToLowerInvariant()) {
            case ".png": return "image/png";
            case ".jpg":
            case ".jpeg": return "image/jpeg";
            case ".webp": return "image/webp";
            case ".gif": return "image/gif";
            default: return "";
        }
    }

    static ArgumentException Missing() { return new ArgumentException("\u9644\u56fe\u4e0d\u5b58\u5728\u6216\u5df2\u88ab\u5220\u9664"); }

    /// Validates and reads the attachments in one pass, so a later protocol writer uses
    /// the same bytes and path that were checked here.
    public static List<Attachment> Read(string root, object value) {
        var items = value as IEnumerable;
        if (value == null) return new List<Attachment>();
        if (items == null || value is string) throw Missing();
        var raw = items.Cast<object>().ToList();
        if (raw.Count > MaxCount) throw new ArgumentException("\u9644\u56fe\u6700\u591a 4 \u5f20");
        string fullRoot;
        try { fullRoot = System.IO.Path.GetFullPath(root).TrimEnd('\\'); } catch { throw Missing(); }
        var result = new List<Attachment>();
        foreach (object item in raw) {
            string relative = item as string;
            Match match = relative == null ? null : Path.Match(relative);
            if (match == null || !Name.IsMatch(match.Groups[1].Value)) throw Missing();
            string absolute;
            try { absolute = System.IO.Path.GetFullPath(System.IO.Path.Combine(fullRoot, relative.Replace('/', '\\'))); } catch { throw Missing(); }
            if (!String.Equals(absolute, fullRoot + "\\" + relative.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase) ||
                !absolute.StartsWith(fullRoot + "\\", StringComparison.OrdinalIgnoreCase)) throw Missing();
            try {
                var info = new FileInfo(absolute);
                string parent = System.IO.Path.GetDirectoryName(absolute);
                if (!info.Exists || info.Length > Limit || (File.GetAttributes(absolute) & FileAttributes.ReparsePoint) != 0 ||
                    (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0) throw Missing();
                byte[] data = File.ReadAllBytes(absolute);
                result.Add(new Attachment(relative, absolute, Media(System.IO.Path.GetExtension(absolute)), data));
            } catch (ArgumentException) { throw; }
            catch (IOException) { throw Missing(); }
            catch (UnauthorizedAccessException) { throw Missing(); }
        }
        return result;
    }

    public static string WithText(string text, IEnumerable<Attachment> images) {
        var list = (images ?? new Attachment[0]).ToList();
        return list.Count == 0 ? text : text + "\n\n" + String.Join("\n", list.Select(one => "\u9644\u56fe\uff1a" + one.AbsolutePath));
    }

    public static List<object> ClaudeContent(string text, IEnumerable<Attachment> images) {
        var content = new List<object> {
            new Dictionary<string, object> { { "type", "text" }, { "text", text } }
        };
        foreach (Attachment image in images ?? new Attachment[0]) {
            content.Add(new Dictionary<string, object> {
                { "type", "image" },
                { "source", new Dictionary<string, object> {
                    { "type", "base64" }, { "media_type", image.MediaType },
                    { "data", Convert.ToBase64String(image.Data) }
                } }
            });
        }
        return content;
    }

    public static List<object> AcpContent(string text, IEnumerable<Attachment> images) {
        var content = new List<object> {
            new Dictionary<string, object> { { "type", "text" }, { "text", text } }
        };
        foreach (Attachment image in images ?? new Attachment[0]) {
            content.Add(new Dictionary<string, object> {
                { "type", "image" }, { "data", Convert.ToBase64String(image.Data) }, { "mimeType", image.MediaType }
            });
        }
        return content;
    }

    /// Creates the attachment directories for the reserved upload path. Existing
    /// directories are accepted only when they are real directories.
    public static bool PrepareUpload(string root, string relative, long size) {
        Match match = Path.Match(relative ?? "");
        if (match == null || !Name.IsMatch(match.Groups[1].Value)) return false;
        if (size > Limit) throw Missing();
        string fullRoot;
        try { fullRoot = System.IO.Path.GetFullPath(root).TrimEnd('\\'); } catch { throw Missing(); }
        string remote = System.IO.Path.Combine(fullRoot, ".remote-ai");
        string attachments = System.IO.Path.Combine(remote, "attachments");
        EnsureDirectory(remote);
        EnsureDirectory(attachments);
        string ignore = System.IO.Path.Combine(remote, ".gitignore");
        if (!File.Exists(ignore)) {
            try {
                using (var stream = new FileStream(ignore, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                using (var writer = new StreamWriter(stream)) writer.Write("*");
            } catch (IOException) {
                if (!File.Exists(ignore)) throw;
            }
        }
        return true;
    }

    static void EnsureDirectory(string path) {
        if (File.Exists(path) || (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0))
            throw Missing();
        Directory.CreateDirectory(path);
        if (!Directory.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw Missing();
    }
}
}
