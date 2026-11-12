"""Small source-level guard for the workbench's compact defaults and readable titles."""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
workbench = (ROOT / "android/src/io/github/kangwang42/remotecli/Workbench.java").read_text(encoding="utf-8")
kit = (ROOT / "android/src/io/github/kangwang42/remotecli/Kit.java").read_text(encoding="utf-8")
web = (ROOT / "web/app.js").read_text(encoding="utf-8")
index_html = (ROOT / "web/index.html").read_text(encoding="utf-8")
css = (ROOT / "web/app.css").read_text(encoding="utf-8")
activity = (ROOT / "android/src/io/github/kangwang42/remotecli/MainActivity.java").read_text(encoding="utf-8")
app_pages = (ROOT / "android/src/io/github/kangwang42/remotecli/AppPages.java").read_text(encoding="utf-8")

assert "RECENT = 2" in workbench, "the aggregate recent list must start with two conversations"
assert 'getBoolean("fold-" + section, true)' in workbench, "active and recent sections must start folded"
assert "kit.title(entry.shownTitle()" in workbench and "kit.title(project.name" in workbench, "aggregate titles must use the multi-line view"
assert "view.setMaxLines(2)" in kit, "the title view must retain two lines before ellipsis"
assert "shown = 2" in web and "projectFolds = { active: false, live: false, history: false, ended: false }" in web, "project pages must start compact, the ended terminals folded with the rest"
assert 'id="p-ended"' in index_html and 'data-fold="ended"' in index_html and 'aria-expanded="false"' in index_html, "历史终端 folds away like 历史对话"
assert "applyProjectFold('ended')" in web, "the ended fold is applied when the project page is drawn"
assert ".section-head[data-fold].folded ~ :not(#hidden-history) { display: none; }" in css and "child.hidden = !open" not in web, "folding hides a section's body by style and leaves what draw() shows, the more buttons too, to draw()"
print("Workbench UI checks passed: compact sections, two recent conversations, and two-line titles")
assert 'if (list.length() == 1) open(' not in activity, "a single computer must not bypass the app workbench"
assert 'shell.addView(scroll, new LinearLayout.LayoutParams(-1, 0, 1))' in activity, "app navigation must stay outside the scrolling content"
assert 'if (!project.name.isEmpty() && project.passes(' not in workbench, "unregistered conversations must be accessible in each computer group"
assert 'request != generation' in app_pages and 'CookieManager.getInstance().getCookie(url)' in app_pages, "late responses must not replace a new screen or share another computer's cookie"
assert '"未连接电脑未计入"' in app_pages or '未连接电脑未计入' in app_pages, "partial aggregate statistics must say what is missing"
print("App shell guards passed: aggregate default, fixed navigation, unregistered groups, scoped requests and partial totals")
aggregate = (ROOT / "tests/android_aggregate_check.py").read_text(encoding="utf-8")
assert 'os.name == "nt"' in aggregate, "the JDK tools must be named for the runner's platform, not Windows only"
print("Android check runner guards passed: JDK tools are named for the host")
tabs = (ROOT / "android/src/io/github/kangwang42/remotecli/Tabs.java").read_text(encoding="utf-8")
assert 'if (web != null) { root.removeView(web); filePicker.cancel(); web.destroy();' not in activity, \
    "navigating away must not destroy the WebView: keep its session, bridge and scroll"
assert "onSaveInstanceState" in activity and "connectionBanner" in activity, \
    "a transient disconnect offers an inline retry, and process death restores the tab"
assert "new JSONArray(json)" in activity, "the custom key bridge must validate what the page sends"
assert "detachWeb()" in activity and "getParent() instanceof ViewGroup" in activity, \
    "the reusable WebView must be detached from its real parent, not from root"
assert "dp(44)" not in kit and "COMPLEX_UNIT_DIP, .7f * root()" not in tabs, \
    "touch targets are at least 48dp and tab labels follow the system font scale"
print("Android UI guards passed: one WebView, inline retry, state restore, 48dp and SP text")
assert (ROOT / "android/res/mipmap/ic_launcher.xml").is_file(), \
    "the launcher icon needs a default configuration, not only mipmap-anydpi-v26"
assert "reconnect(view, origin, health)" in activity and "reconnect(checked, origin, health)" in activity, \
    "WebView errors and asynchronous checks must retain their own page's health state"
assert 'if (!health.isActive() || web != failed || !"web".equals(screen)) return;' in activity, \
    "a queued reconnect from a retired page cannot stop the current page"
assert "again.setOnClickListener(v -> openPage(address, password, path))" in activity, \
    "retry must restart connection monitoring as well as reload the page"
assert "catch (IllegalStateException" in activity, \
    "a missing page bundle must answer with a message, not crash the app"
assert 'if (web == null || !"web".equals(screen)) return; bars(shade);' in activity, \
    "a detached page's late chrome() must not recolour the app's own bars"
print("Shell robustness guards passed: page health retained, missing bundle degrades, late chrome is screened")
