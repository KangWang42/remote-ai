"""Small source-level guard for the workbench's compact defaults and readable titles."""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
workbench = (ROOT / "android/src/io/github/kangwang42/remotecli/Workbench.java").read_text(encoding="utf-8")
kit = (ROOT / "android/src/io/github/kangwang42/remotecli/Kit.java").read_text(encoding="utf-8")
web = (ROOT / "web/app.js").read_text(encoding="utf-8")
agent = (ROOT / "agent-windows/RemoteCliAgent.cs").read_text(encoding="utf-8")
activity = (ROOT / "android/src/io/github/kangwang42/remotecli/MainActivity.java").read_text(encoding="utf-8")
app_pages = (ROOT / "android/src/io/github/kangwang42/remotecli/AppPages.java").read_text(encoding="utf-8")

assert "RECENT = 2" in workbench, "the aggregate recent list must start with two conversations"
assert 'getBoolean("fold-" + section, true)' in workbench, "active and recent sections must start folded"
assert "kit.title(entry.shownTitle()" in workbench and "kit.title(project.name" in workbench, "aggregate titles must use the multi-line view"
assert "view.setMaxLines(2)" in kit, "the title view must retain two lines before ellipsis"
assert "shown = 2" in web and "projectFolds = { active: false, live: false, history: false }" in web, "project pages must start compact"
# one rule for both tools says which of the conversations a program holds are in use now (tests/in_use_check.py checks the rule itself)
assert "public static bool InUse(" in agent and "held.Now = InUse(held.Host, held.Origin, held.Status, Reach.Of(held.Tool, held.Id)" in agent, "Claude Code and Codex must share the rule for what is in use"
print("Workbench UI checks passed: compact sections, two recent conversations, and two-line titles")
assert 'if (list.length() == 1) open(' not in activity, "a single computer must not bypass the app workbench"
assert 'shell.addView(scroll, new LinearLayout.LayoutParams(-1, 0, 1))' in activity, "app navigation must stay outside the scrolling content"
assert 'if (!project.name.isEmpty() && project.passes(' not in workbench, "unregistered conversations must be accessible in each computer group"
assert 'request != generation' in app_pages and 'CookieManager.getInstance().getCookie(url)' in app_pages, "late responses must not replace a new screen or share another computer's cookie"
assert '"未连接电脑未计入"' in app_pages or '未连接电脑未计入' in app_pages, "partial aggregate statistics must say what is missing"
print("App shell guards passed: aggregate default, fixed navigation, unregistered groups, scoped requests and partial totals")
