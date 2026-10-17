package io.github.kangwang42.remotecli;

import java.net.URI;
import java.net.URLDecoder;
import java.net.URLEncoder;
import java.util.LinkedHashMap;
import java.util.Map;
import java.util.UUID;

/** UU's external protocol. No account, relay or Android state belongs here. */
final class DesktopProtocol {
    static final String CALLBACK = "remoteai-uu://result/";
    static final long REQUEST_LIFETIME = 15 * 60 * 1000L;
    static String id() { return UUID.randomUUID().toString().replace("-", ""); }
    static boolean validId(String value) { return value != null && value.matches("[a-f0-9]{32}"); }
    static String encode(String text) {
        try { return URLEncoder.encode(text, "UTF-8"); } catch (Exception impossible) { throw new IllegalArgumentException(); }
    }
    static String callback(String operation, String nonce) {
        if (!("bind".equals(operation) || "control".equals(operation)) || !validId(nonce)) throw new IllegalArgumentException();
        return CALLBACK + operation + "?request=" + nonce;
    }
    static String bind(String nonce) {
        return "uuremote://external/device/authorize?callback=" + encode(callback("bind", nonce)) + "&nonce=" + nonce;
    }
    static String control(String device, boolean small, String nonce) {
        if (!validDevice(device)) throw new IllegalArgumentException();
        return "uuremote://external/device/control?anonymous_device_id=" + encode(device) + "&window_type=" + (small ? "1&callback=" + encode(callback("control", nonce)) : "0");
    }
    static boolean validDevice(String value) {
        if (value == null || value.isEmpty() || value.length() > 8192) return false;
        for (int i = 0; i < value.length(); i++) if (Character.isISOControl(value.charAt(i))) return false;
        return true;
    }
    static boolean fresh(long created, long now) { return created > 0 && now >= created && now - created <= REQUEST_LIFETIME; }
    static Map<String, String> query(URI uri) throws Exception {
        Map<String, String> values = new LinkedHashMap<>();
        String raw = uri.getRawQuery();
        if (raw == null || raw.length() > 24000) throw new IllegalArgumentException();
        for (String pair : raw.split("&", -1)) {
            String[] parts = pair.split("=", 2);
            if (parts.length != 2) throw new IllegalArgumentException();
            String key = URLDecoder.decode(parts[0], "UTF-8"), value = URLDecoder.decode(parts[1], "UTF-8");
            if (values.put(key, value) != null) throw new IllegalArgumentException();
        }
        return values;
    }
    static final class Result {
        final String device, error;
        Result(String device, String error) { this.device = device; this.error = error; }
    }
    /** Null means untrusted/malformed; it must not consume a pending request. */
    static Result receive(String url, String operation, String nonce, long created, long now) {
        try {
            if (url == null || url.length() > 25000 || !validId(nonce) || !fresh(created, now)) return null;
            URI uri = new URI(url);
            if (!"remoteai-uu".equals(uri.getScheme()) || !"result".equals(uri.getRawAuthority()) || uri.getRawFragment() != null
                    || !("/" + operation).equals(uri.getRawPath())) return null;
            Map<String, String> values = query(uri);
            if (!nonce.equals(values.get("request"))) return null;
            if ("bind".equals(operation)) {
                if (!nonce.equals(values.get("nonce"))) return null;
                String device = values.get("anonymous_device_id"), error = values.get("error");
                if (device != null && error == null && validDevice(device)) return new Result(device, "");
                if (device == null && error != null && !error.isEmpty()) return new Result("", error);
            } else if ("control".equals(operation)) {
                String status = values.get("status"), reason = values.get("reason");
                if ("success".equals(status) && reason == null) return new Result("", "");
                if ("failure".equals(status) && reason != null && !reason.isEmpty()) return new Result("", reason);
            }
        } catch (Exception invalid) { }
        return null;
    }
    static String message(String error) {
        switch (error) {
            case "user_cancel": return "已取消。";
            case "no_device": return "UU 账号下没有可关联的 Windows 或 Mac 电脑。";
            case "device_not_found": return "关联已失效，请确认 UU 账号后重新关联。";
            case "device_offline": return "UU 提示电脑离线，请检查电脑是否开机联网。";
            case "busy": return "请先在 UU 结束当前远控或其它操作，再重试。";
            case "pip_unsupported": return "小窗未能开启，可以改用全屏。";
            case "network_unavailable": return "网络不可用，请检查手机网络后重试。";
            case "not_allowed": return "请检查电脑的 UU 允许被控设置；仅支持 Windows 和 Mac。";
            case "not_logined": case "logout": case "login_timeout": return "请在 UU 完成登录后重试。";
            case "timeout": case "connect_timeout": return "UU 等待超时，可以重新尝试。";
            case "superseded": return "UU 中的请求已被另一项操作替代。";
            default: return "UU 未能完成此次操作，请重试。";
        }
    }
}
