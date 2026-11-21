"""Guard Android update wiring and PackageInstaller stream lifetime."""
from pathlib import Path


root = Path(__file__).resolve().parents[1]
java = root / "android/src/io/github/kangwang42/remotecli"
source = (java / "GithubUpdater.java").read_text(encoding="utf-8")
write = source.index('try (InputStream in = new FileInputStream(apk); OutputStream out = session.openWrite("base.apk"')
commit = source.index("session.commit", write)
close = source.index("\n            }\n            Intent status", write)
assert write < close < commit, "PackageInstaller.commit must run after the APK output stream closes"
assert "if (!committed) try { session.abandon();" in source

assert 'getBoolean("update_include_prereleases", false)' in source
assert 'preview ? "/releases?per_page=20" : "/releases/latest"' in source
assert "Release found = fetch(preview)" in source and "preview != includePrereleases()" in source
assert 'activity.previewUpdates(), activity::togglePreviewUpdates' in (java / "AppPages.java").read_text(encoding="utf-8")
bridge = (java / "MainActivity.java").read_text(encoding="utf-8")
assert "@JavascriptInterface public boolean previewUpdates()" in bridge
assert "@JavascriptInterface public void setPreviewUpdates(boolean on)" in bridge
page = (root / "web/computer/computer.js").read_text(encoding="utf-8")
assert "if (native.previewUpdates && native.setPreviewUpdates)" in page
print("android updater checks passed")
