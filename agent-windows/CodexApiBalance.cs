using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace RemoteCli {
/// Reads the balance returned by compatible Codex providers at the configured API base plus /usage.
internal sealed class CodexApiBalance {
    static readonly Regex Unit = new Regex(@"\A(?:[A-Z]{2,8}|%)\z");
    readonly Uri endpoint;
    readonly string key;

    public CodexApiBalance(Uri baseUrl, string bearer) {
        endpoint = new UriBuilder(baseUrl) { Path = baseUrl.AbsolutePath.TrimEnd('/') + "/usage", Query = "", Fragment = "" }.Uri;
        key = bearer;
    }

    public Dictionary<string, object> Read() {
        if (endpoint.Scheme != Uri.UriSchemeHttps || endpoint.UserInfo.Length != 0 || String.IsNullOrWhiteSpace(key)) return null;
        try {
            using (var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) {
                Timeout = TimeSpan.FromSeconds(6), MaxResponseContentBufferSize = 65536
            })
            using (var request = new HttpRequestMessage(HttpMethod.Get, endpoint)) {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Trim());
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                using (var response = client.SendAsync(request).GetAwaiter().GetResult()) {
                    return response.IsSuccessStatusCode ? Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult()) : null;
                }
            }
        } catch (HttpRequestException) { } catch (OperationCanceledException) { } catch (FormatException) { }
        return null;
    }

    internal static Dictionary<string, object> Parse(string response) {
        Dictionary<string, object> data;
        try { data = new JavaScriptSerializer().DeserializeObject(response) as Dictionary<string, object>; }
        catch (ArgumentException) { return null; }
        catch (InvalidOperationException) { return null; }
        object rawUnit;
        if (data == null || !data.TryGetValue("unit", out rawUnit) || !(rawUnit is string)) return null;
        string unit = ((string)rawUnit).Trim().ToUpperInvariant();
        if (!Unit.IsMatch(unit)) return null;
        decimal amount;
        if (!TryAmount(data, "balance", out amount) && !TryAmount(data, "remaining", out amount)) return null;
        string balance = amount.ToString("0.############################", CultureInfo.InvariantCulture);
        if (balance.Length > 32) return null;
        return new Dictionary<string, object> { { "type", "api" }, { "balance", balance }, { "unit", unit } };
    }

    static bool TryAmount(Dictionary<string, object> data, string field, out decimal amount) {
        amount = 0;
        object value;
        if (!data.TryGetValue(field, out value) || value == null || value is bool || !(value is IConvertible)) return false;
        return Decimal.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out amount);
    }
}
}
