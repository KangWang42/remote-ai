using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;

namespace RemoteCli {

/// An OpenAI-compatible chat completions service: `POST <address>/chat/completions` with the owner's key as a bearer
/// token. Only `messages`, `temperature` and `max_tokens` are sent, since services that take the OpenAI shape differ
/// in what else they accept; the entry is read from the answer's text and `usage.total_tokens` where it is given.
internal sealed class ApiOrganizer : IOrganizeSource {
    const int Timeout = 60000;
    readonly string address, key;

    internal ApiOrganizer(string address, string key) { this.address = address; this.key = key; }

    public string Label { get { return "API"; } }
    public Dictionary<string, object>[] Models() { return new Dictionary<string, object>[0]; }
    public string DefaultModel(Dictionary<string, object>[] offered) { return ""; }

    /// Where requests go: the address as given, or with /chat/completions added to a base address.
    internal static string Endpoint(string address) {
        string trimmed = address.Trim().TrimEnd('/');
        return trimmed.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase) ? trimmed : trimmed + "/chat/completions";
    }

    internal static bool ValidAddress(string address) {
        Uri parsed;
        return !String.IsNullOrWhiteSpace(address) && address.Length <= 300 && Uri.TryCreate(address.Trim(), UriKind.Absolute, out parsed)
            && (parsed.Scheme == Uri.UriSchemeHttps || parsed.Scheme == Uri.UriSchemeHttp) && parsed.Query.Length == 0;
    }

    public Organizer.Metadata Analyze(string context, string model) {
        var json = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 };
        string body = json.Serialize(new Dictionary<string, object> {
            { "model", model }, { "temperature", 0.2 }, { "max_tokens", 600 },
            { "messages", new object[] {
                new Dictionary<string, object> { { "role", "system" }, { "content", OrganizeReply.Instructions + "只输出这个 JSON 对象，不要其他文字。" } },
                new Dictionary<string, object> { { "role", "user" }, { "content", OrganizeReply.Request(context) } } } }
        });
        var request = (HttpWebRequest)WebRequest.Create(Endpoint(address));
        request.Method = "POST";
        request.ContentType = "application/json";
        request.Timeout = request.ReadWriteTimeout = Timeout;
        request.ServicePoint.Expect100Continue = false;
        if (!String.IsNullOrEmpty(key)) request.Headers[HttpRequestHeader.Authorization] = "Bearer " + key;
        byte[] bytes = new UTF8Encoding(false).GetBytes(body);
        string text;
        try {
            using (var stream = request.GetRequestStream()) stream.Write(bytes, 0, bytes.Length);
            using (var response = (HttpWebResponse)request.GetResponse())
            using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8)) text = reader.ReadToEnd();
        } catch (WebException failure) {
            if (failure.Status == WebExceptionStatus.Timeout) throw new TimeoutException();
            var response = failure.Response as HttpWebResponse;
            if (response == null) throw new InvalidOperationException("连不上整理 API：" + failure.Message);
            int code = (int)response.StatusCode;
            response.Dispose();
            throw new InvalidOperationException(code == 401 || code == 403 ? "整理 API 拒绝了密钥（" + code + "）"
                : code == 404 ? "整理 API 地址或模型不存在（404）" : "整理 API 返回错误（" + code + "）");
        }
        var answer = json.Deserialize<Dictionary<string, object>>(text);
        var choices = answer != null && answer.ContainsKey("choices") ? answer["choices"] as IList : null;
        var first = choices != null && choices.Count > 0 ? choices[0] as Dictionary<string, object> : null;
        var message = first != null && first.ContainsKey("message") ? first["message"] as Dictionary<string, object> : null;
        string content = Organizer.Text(message, "content");
        if (content.Length == 0) throw new InvalidOperationException("整理 API 没有返回内容");
        var data = OrganizeReply.Parse(content);
        var usage = answer.ContainsKey("usage") ? answer["usage"] as Dictionary<string, object> : null;
        data.Tokens = usage != null && usage.ContainsKey("total_tokens") ? OrganizeReply.Number(usage["total_tokens"]) : 0;
        return data;
    }
}
}
