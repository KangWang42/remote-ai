using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace RemoteCli {
public sealed class ReleaseUpdate {
    public string Version, Url, Sha256;
}

/// GitHub Releases updater. It accepts only the repository's HTTPS release asset and checks its digest.
public static class AutoUpdater {
    const string Api = "https://api.github.com/repos/KangWang42/remote-cli/releases/latest";
    const string AssetPrefix = "RemoteCli-Setup-";
    public static bool Newer(string latest, string current) {
        Version a, b;
        return Version.TryParse(latest.TrimStart('v'), out a) && Version.TryParse(current.TrimStart('v'), out b) && a > b;
    }
    /// What is done with a newer version that was found. "install": at once (the phone asked for it). "ask": the
    /// person pressed the button in the window, and is asked on the spot whether to restart now, whatever the
    /// automatic setting says: a press is never answered with nothing. "wait": found by the program itself with
    /// automatic updates on, installed in the first quiet moment. "tell": found by itself otherwise; the button says so.
    public static string Step(bool manual, bool remote, bool automatic) {
        return remote ? "install" : manual ? "ask" : automatic ? "wait" : "tell";
    }
    public static Task<ReleaseUpdate> CheckAsync(string current) { return Task.Run(() => Check(current)); }
    static ReleaseUpdate Check(string current) {
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
        var request = (HttpWebRequest)WebRequest.Create(Api); request.Method = "GET"; request.Timeout = 15000;
        request.UserAgent = "RemoteCli/" + current + " (GitHub updater)"; request.Accept = "application/vnd.github+json";
        using (var response = (HttpWebResponse)request.GetResponse()) using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8)) {
            var json = new JavaScriptSerializer().DeserializeObject(reader.ReadToEnd()) as Dictionary<string, object>;
            string tag = Convert.ToString(json["tag_name"]).Trim(); if (!Newer(tag, current)) return null;
            string version = tag.TrimStart('v'); var assets = json["assets"] as object[];
            foreach (var raw in assets ?? new object[0]) {
                var asset = raw as Dictionary<string, object>;
                if (asset == null || !String.Equals(Convert.ToString(asset["name"]), AssetPrefix + version + ".exe", StringComparison.OrdinalIgnoreCase)) continue;
                string url = Convert.ToString(asset["browser_download_url"]);
                if (!url.StartsWith("https://github.com/KangWang42/remote-cli/releases/download/", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("发布文件地址无效");
                string digest = Convert.ToString(asset.ContainsKey("digest") ? asset["digest"] : "");
                return new ReleaseUpdate { Version = version, Url = url, Sha256 = digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest.Substring(7).ToLowerInvariant() : "" };
            }
            throw new InvalidDataException("最新发布没有匹配的 Windows 安装包");
        }
    }
    public static Task<string> DownloadAsync(ReleaseUpdate update, string directory) { return Task.Run(() => Download(update, directory)); }
    static string Download(ReleaseUpdate update, string directory) {
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
        Directory.CreateDirectory(directory); string final = Path.Combine(directory, AssetPrefix + update.Version + ".exe"); string temporary = final + ".part";
        try {
            var request = (HttpWebRequest)WebRequest.Create(update.Url); request.Timeout = 120000; request.UserAgent = "RemoteCli/" + update.Version + " (GitHub updater)";
            using (var response = (HttpWebResponse)request.GetResponse()) using (var input = response.GetResponseStream()) using (var output = File.Create(temporary)) input.CopyTo(output);
            if (!String.IsNullOrEmpty(update.Sha256) && !String.Equals(Hash(temporary), update.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("安装包校验未通过");
            if (new FileInfo(temporary).Length < 100000) throw new InvalidDataException("安装包大小异常");
            if (File.Exists(final)) File.Delete(final); File.Move(temporary, final); return final;
        } catch { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } throw; }
    }
    static string Hash(string path) { using (var sha = SHA256.Create()) using (var input = File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "").ToLowerInvariant(); }
}
}
