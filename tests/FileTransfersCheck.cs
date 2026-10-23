using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RemoteCli;

public static class FileTransfersCheck {
    static void Require(bool ok, string what) { if (!ok) throw new Exception(what); }
    static void Refused(Action act) { try { act(); } catch (ArgumentException) { return; } catch (IOException) { return; } throw new Exception("Expected refusal"); }
    static Dictionary<string, object> Op(string action, string path, long size = 0, long offset = 0, string data = "", string id = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa") {
        return new Dictionary<string, object> { { "action", "file_upload_" + action }, { "path", path }, { "upload", id }, { "size", size }, { "offset", offset }, { "data", data } };
    }
    public static void Main(string[] args) {
        string root = args[0]; Directory.CreateDirectory(root); Directory.CreateDirectory(Path.Combine(root, "子目录"));
        using (var files = new FileTransfers()) {
            string target = "子目录/手机文件.bin", path = Path.Combine(root, target);
            byte[] data = Enumerable.Range(0, 1600001).Select(i => (byte)(i % 251)).ToArray();
            files.Run(root, Op("start", target, data.Length));
            Require(!File.Exists(path), "partial file published");
            Refused(() => files.Run(root, Op("finish", target)));
            Refused(() => files.Run(root, Op("chunk", target, 0, 1, "YQ==")));
            Refused(() => files.Run(root, Op("chunk", target, 0, 0, "?!")));
            Refused(() => files.Run(root, Op("chunk", "wrong", 0, 0, "YQ==")));
            for (int at = 0; at < data.Length; at += TerminalAgent.FilePiece) {
                int count = Math.Min(TerminalAgent.FilePiece, data.Length - at);
                var reply = files.Run(root, Op("chunk", target, 0, at, Convert.ToBase64String(data, at, count)));
                Require(Convert.ToInt64(reply["offset"]) == at + count, "wrong offset");
            }
            files.Run(root, Op("finish", target));
            Require(File.ReadAllBytes(path).SequenceEqual(data), "bytes differ");
            Refused(() => files.Run(root, Op("start", target, 0)));
            Require(File.ReadAllBytes(path).SequenceEqual(data), "existing file replaced");
            foreach (string bad in new[] { "../outside", "/absolute", "C:/absolute", "x\\bad", "a:stream", "NUL.txt", "COM1", "LPT¹.txt", "trailing.", "../子目录/new", "missing/file" })
                Refused(() => files.Run(root, Op("start", bad, 0)));
            Refused(() => files.Run(root, Op("start", "large", FileTransfers.Limit + 1)));
            files.Run(root, Op("start", "empty", 0)); files.Run(root, Op("finish", "empty"));
            Require(new FileInfo(Path.Combine(root, "empty")).Length == 0, "empty file");
            files.Run(root, Op("start", "cancelled", 1));
            files.Run(root, Op("chunk", "cancelled", 0, 0, "YQ=="));
            files.Run(root, Op("cancel", "cancelled")); files.Run(root, Op("cancel", "cancelled"));
            Require(!File.Exists(Path.Combine(root, "cancelled")), "cancelled file published");
            files.Run(root, Op("start", "race", 0)); File.WriteAllText(Path.Combine(root, "race"), "keep");
            Refused(() => files.Run(root, Op("finish", "race")));
            Require(File.ReadAllText(Path.Combine(root, "race")) == "keep", "racing file replaced");
            files.Run(root, Op("start", "子目录/locked", 0));
            Refused(() => Directory.Move(Path.Combine(root, "子目录"), Path.Combine(root, "moved")));
            files.Expire(DateTime.UtcNow.AddMinutes(11));
            Refused(() => files.Run(root, Op("finish", "子目录/locked")));
            Require(!Directory.GetFiles(root, ".remoteai-upload-*", SearchOption.AllDirectories).Any(), "expired uploads leaked");
            for (int i = 0; i < 4; i++) files.Run(root, Op("start", "pending" + i, 0, 0, "", i.ToString("x32")));
            Refused(() => files.Run(root, Op("start", "fifth")));
            using (var independent = new FileTransfers()) { independent.Run(root, Op("start", "independent")); independent.Run(root, Op("finish", "independent")); }
        }
        Require(!Directory.GetFiles(root, ".remoteai-upload-*", SearchOption.AllDirectories).Any(), "temporary files leaked");
        Console.WriteLine("Windows uploads: bytes, paths, names, partial writes, collisions, parent locks and cleanup passed");
    }
}
