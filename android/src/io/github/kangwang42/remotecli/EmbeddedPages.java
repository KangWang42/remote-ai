package io.github.kangwang42.remotecli;

import android.content.res.AssetManager;
import android.webkit.WebResourceRequest;
import android.webkit.WebResourceResponse;
import java.io.ByteArrayInputStream;
import java.io.IOException;
import java.io.InputStream;
import java.util.HashMap;
import java.util.Map;
import java.util.Properties;

/** App-versioned UI at the original HTTP URL; cookies, API calls and WebSockets stay on the selected relay. */
final class EmbeddedPages {
    private final AssetManager assets;
    private final PageAssets paths;
    private final Map<String, String> headers = new HashMap<>();

    EmbeddedPages(AssetManager assets, String address) {
        this.assets = assets;
        Properties files = new Properties();
        try (InputStream input = assets.open("web-assets.properties")) { files.load(input); }
        catch (IOException missing) { throw new IllegalStateException("应用页面清单缺失，请重新安装完整安装包。", missing); }
        paths = new PageAssets(address, files);
        headers.put("Cache-Control", "no-store");
        headers.put("X-Content-Type-Options", "nosniff");
        headers.put("Referrer-Policy", "no-referrer");
        headers.put("X-Frame-Options", "DENY");
        headers.put("Content-Security-Policy", "default-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; "
            + "media-src 'self' blob:; worker-src 'self' blob:; font-src 'self' data:; connect-src 'self'; "
            + "frame-ancestors 'none'; base-uri 'none'; form-action 'none'");
    }

    WebResourceResponse intercept(WebResourceRequest request) {
        String name = paths.resolve(request.getUrl().toString(), request.getMethod());
        if (name == null) return null;
        String mime = paths.mime(name);
        String encoding = mime.startsWith("text/") || mime.equals("application/json") ? "UTF-8" : null;
        try {
            return new WebResourceResponse(mime, encoding, 200, "OK", headers, assets.open("web/" + name));
        } catch (IOException missing) {
            // Mixing one old server script into the app's page would recreate a version mismatch.
            return new WebResourceResponse("text/plain", "UTF-8", 404, "Not Found", headers,
                new ByteArrayInputStream("应用页面资源缺失，请重新安装完整安装包。".getBytes(java.nio.charset.StandardCharsets.UTF_8)));
        }
    }
}
