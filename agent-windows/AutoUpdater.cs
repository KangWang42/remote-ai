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
    /// The repository on GitHub, under the name it is to have and the one it had: it is renamed with the product,
    /// and a program installed before or after that finds its releases under either. They are asked in this order;
    /// one that is not there (yet, or any more) is passed over.
    public static readonly string[] Repositories = { "KangWang42/remote-ai", "KangWang42/remote-cli" };
    /// Whether an address is a release file of this repository.
    public static bool Ours(string url) {
        foreach (string name in Repositories) if ((url ?? "").StartsWith("https://github.com/" + name + "/releases/download/", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
    const string AssetPrefix = "RemoteCli-Setup-";
    public static bool Newer(string latest, string current) {
        Version a, b;
        return Version.TryParse(latest.TrimStart('v'), out a) && Version.TryParse(current.TrimStart('v'), out b) && a > b;
    }
    /// What is done about a newer version. "install": it is looked for and installed at once (the phone asked for
    /// it). "ask": the person pressed the button in the window, and is asked on the spot whether to restart now: a
    /// press is never answered with nothing. Otherwise the program acts by itself, and only where that was switched
    /// on: "tell", it looks now and then and says that there is a newer version, installing nothing; "none", it does
    /// not look.
    public static string Step(bool manual, bool remote, bool automatic) {
        return remote ? "install" : manual ? "ask" : automatic ? "tell" : "none";
    }
    /// Which releases are installed: "release", the latest full release (GitHub's `releases/latest`, which passes over
    /// pre-releases), unless "pre" is chosen, which also takes the newest pre-release.
    public static readonly string[] Channels = { "release", "pre" };
    public static Task<ReleaseUpdate> CheckAsync(string current, string channel) { return Task.Run(() => Check(current, channel == "pre")); }
    static ReleaseUpdate Check(string current, bool pre) {
        WebException missing = null;
        foreach (string name in Repositories) {
            try {
                if (!pre) return Pick(new[] { Ask("https://api.github.com/repos/" + name + "/releases/latest", current) }, current, false, true);
                return Pick(Ask("https://api.github.com/repos/" + name + "/releases?per_page=20", current) as object[], current, true, false);
            } catch (WebException error) {
                var answer = error.Response as HttpWebResponse;
                if (answer == null || answer.StatusCode != HttpStatusCode.NotFound) throw;
                missing = error;
            }
        }
        throw missing;
    }
    static object Ask(string api, string current) {
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
        var request = (HttpWebRequest)WebRequest.Create(api); request.Method = "GET"; request.Timeout = 15000;
        request.UserAgent = "RemoteCli/" + current + " (GitHub updater)"; request.Accept = "application/vnd.github+json";
        using (var response = (HttpWebResponse)request.GetResponse()) using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
            return new JavaScriptSerializer().DeserializeObject(reader.ReadToEnd());
    }
    /// Of GitHub's releases, the newest one above `current` that carries the Windows installer; null where there is
    /// none. Drafts are passed over, and pre-releases unless `pre`. `strict`: a newer release without the installer is
    /// an error (the one latest release), not one to pass over (a list, where an older one may have it).
    public static ReleaseUpdate Pick(object[] releases, string current, bool pre, bool strict) {
        ReleaseUpdate best = null;
        foreach (var raw in releases ?? new object[0]) {
            var release = raw as Dictionary<string, object>;
            if (release == null || Object.Equals(Field(release, "draft"), true) || (!pre && Object.Equals(Field(release, "prerelease"), true))) continue;
            string tag = Convert.ToString(Field(release, "tag_name") ?? "").Trim();
            if (!Newer(tag, current) || (best != null && !Newer(tag, best.Version))) continue;
            var found = Installer(release, tag.TrimStart('v'));
            if (found == null && strict) throw new InvalidDataException("最新发布没有匹配的 Windows 安装包");
            if (found != null) best = found;
        }
        return best;
    }
    static object Field(Dictionary<string, object> from, string key) { object value; return from.TryGetValue(key, out value) ? value : null; }
    static ReleaseUpdate Installer(Dictionary<string, object> release, string version) {
        foreach (var raw in Field(release, "assets") as object[] ?? new object[0]) {
            var asset = raw as Dictionary<string, object>;
            if (asset == null || !String.Equals(Convert.ToString(Field(asset, "name")), AssetPrefix + version + ".exe", StringComparison.OrdinalIgnoreCase)) continue;
            string url = Convert.ToString(Field(asset, "browser_download_url"));
            if (!Ours(url)) throw new InvalidDataException("发布文件地址无效");
            string digest = Convert.ToString(Field(asset, "digest") ?? "");
            return new ReleaseUpdate { Version = version, Url = url, Sha256 = digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest.Substring(7).ToLowerInvariant() : "" };
        }
        return null;
    }
    public static Task<string> DownloadAsync(ReleaseUpdate update, string directory) { return Task.Run(() => Download(update, directory)); }
    static string Download(ReleaseUpdate update, string directory) {
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
        Directory.CreateDirectory(directory); string final = Path.Combine(directory, AssetPrefix + update.Version + ".exe"); string temporary = final + ".part";
        try {
            var request = (HttpWebRequest)WebRequest.Create(update.Url); request.Timeout = 120000; request.UserAgent = "RemoteCli/" + update.Version + " (GitHub updater)";
            using (var response = (HttpWebResponse)request.GetResponse()) using (var input = response.GetResponseStream()) using (var output = File.Create(temporary)) input.CopyTo(output);
            if (String.IsNullOrEmpty(update.Sha256)) throw new InvalidDataException("本次发布没有提供 SHA-256，无法校验安装包");
            if (!String.Equals(Hash(temporary), update.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("安装包校验未通过");
            if (new FileInfo(temporary).Length < 100000) throw new InvalidDataException("安装包大小异常");
            if (File.Exists(final)) File.Delete(final); File.Move(temporary, final);
            Prune(directory, final);
            return final;
        } catch { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } throw; }
    }
    /// Installers of earlier updates are of no further use once a newer one is downloaded and checked; each is
    /// about 10 MB, and they were kept for every version before.
    public static void Prune(string directory, string keep) {
        foreach (string old in Directory.GetFiles(directory, AssetPrefix + "*.exe")) {
            if (String.Equals(Path.GetFullPath(old), Path.GetFullPath(keep), StringComparison.OrdinalIgnoreCase)) continue;
            try { File.Delete(old); } catch (IOException) { } catch (UnauthorizedAccessException) { }     // (one still running: the next time)
        }
    }
    static string Hash(string path) { using (var sha = SHA256.Create()) using (var input = File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "").ToLowerInvariant(); }
}
}
