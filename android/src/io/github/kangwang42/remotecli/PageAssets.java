package io.github.kangwang42.remotecli;

import java.net.URI;
import java.util.Properties;

/** Resolves only packaged page files inside the selected computer's origin and public-relay space. */
final class PageAssets {
    private final URI base;
    private final String prefix;
    private final Properties files;

    PageAssets(String address, Properties files) {
        base = URI.create(address);
        prefix = base.getRawPath().replaceAll("/+$", "") + "/";
        this.files = new Properties();
        this.files.putAll(files);
    }

    String resolve(String url, String method) {
        if (!"GET".equals(method)) return null;
        try {
            URI request = URI.create(url);
            if (request.getUserInfo() != null || request.getHost() == null || base.getHost() == null
                    || !base.getScheme().equalsIgnoreCase(request.getScheme())
                    || !base.getHost().equalsIgnoreCase(request.getHost()) || port(base) != port(request)) return null;
            String path = request.getRawPath();
            if (path == null || !path.startsWith(prefix)) return null;
            String name = path.substring(prefix.length());
            // No decoding or normalization: aliases must not escape a computer's space or disguise an API call.
            if (name.indexOf('%') >= 0 || name.indexOf('\\') >= 0 || name.contains("//")) return null;
            for (String part : name.split("/")) if (part.equals(".") || part.equals("..")) return null;
            if (name.equals("api") || name.startsWith("api/")) return null;
            if (name.isEmpty() || name.endsWith("/")) name += "index.html";
            return files.containsKey(name) ? name : null;
        } catch (IllegalArgumentException invalid) { return null; }
    }

    String mime(String name) { return files.getProperty(name); }

    private static int port(URI uri) {
        return uri.getPort() >= 0 ? uri.getPort() : "https".equalsIgnoreCase(uri.getScheme()) ? 443 : 80;
    }
}
