package io.github.kangwang42.remotecli;

import java.io.FileInputStream;
import java.util.Properties;

public final class PageAssetsCheck {
    private static void equal(Object expected, Object got) {
        if (expected == null ? got != null : !expected.equals(got)) throw new AssertionError(expected + " != " + got);
    }
    public static void main(String[] args) throws Exception {
        Properties files = new Properties();
        try (FileInputStream input = new FileInputStream(args[0])) { files.load(input); }
        String origin = "https://relay.example/c/0123456789abcdef0123";
        PageAssets publicPages = new PageAssets(origin, files);
        equal("index.html", publicPages.resolve(origin + "/?skin=pond#p=example", "GET"));
        equal("terminal/index.html", publicPages.resolve(origin + "/terminal/?id=test", "GET"));
        equal("terminal/skins.js", publicPages.resolve(origin + "/terminal/skins.js?v=old", "GET"));
        equal("themes/vendor/ascii-pond/koi.js", publicPages.resolve(origin + "/themes/vendor/ascii-pond/koi.js", "GET"));
        equal("text/javascript", publicPages.mime("terminal/skins.js"));
        for (String name : files.stringPropertyNames()) equal(name, publicPages.resolve(origin + "/" + name, "GET"));
        for (String path : new String[]{"/api/terminal", "/api/terminal/ws", "/api/session", "/api/files/download?name=index.html",
                "/terminal/../app.js", "/terminal/%2e%2e/app.js", "/%2fapp.js", "/terminal//skins.js", "/not-packaged.js"})
            equal(null, publicPages.resolve(origin + path, "GET"));
        for (String url : new String[]{"https://evil.example" + "/terminal/skins.js", "http://relay.example/c/0123456789abcdef0123/app.js",
                "https://relay.example:444/c/0123456789abcdef0123/app.js", "https://user@relay.example/c/0123456789abcdef0123/app.js",
                "https://relay.example/c/ffffffffffffffffffff/app.js", "https://relay.example/app.js", origin + "x/app.js", "file:///app.js"})
            equal(null, publicPages.resolve(url, "GET"));
        equal(null, publicPages.resolve(origin + "/app.js", "POST"));
        equal("app.js", publicPages.resolve("https://RELAY.example:443/c/0123456789abcdef0123/app.js", "GET"));
        PageAssets direct = new PageAssets("http://192.168.1.5:8722", files);
        equal("chat/index.html", direct.resolve("http://192.168.1.5:8722/chat/?id=test", "GET"));
        equal(null, direct.resolve("http://192.168.1.5/chat/", "GET"));
        System.out.println("Packaged pages: all assets resolve; direct/public paths, query versions, origin/space boundaries and API bypass passed.");
    }
}
