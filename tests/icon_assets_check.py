"""Source-level and binary guard for the shared Remote AI application mark across Android, Windows and Web."""
from pathlib import Path
import struct

ROOT = Path(__file__).resolve().parents[1]

# 1. Shared web favicon and HTML links
svg = ROOT / "web/favicon.svg"
assert svg.is_file(), "web favicon source is required"
svg_text = svg.read_text(encoding="utf-8")
for needle in ("terminal prompt", "sparkle", "#223472", "#34d399", "#93c5fd"):
    assert needle in svg_text, f"favicon mark is missing {needle}"

for page in (
    ROOT / "web/index.html",
    ROOT / "web/chat/index.html",
    ROOT / "web/computer/index.html",
    ROOT / "web/files/index.html",
    ROOT / "web/terminal/index.html",
):
    text = page.read_text(encoding="utf-8")
    href = "favicon.svg" if page.parent == ROOT / "web" else "../favicon.svg"
    assert f'type="image/svg+xml" href="{href}"' in text, f"{page} must use the shared favicon"

# 2. Android adaptive launcher & fallback launcher resources
manifest = (ROOT / "android/AndroidManifest.xml").read_text(encoding="utf-8")
assert 'android:icon="@mipmap/ic_launcher"' in manifest, "Android must keep the launcher icon reference"
assert (ROOT / "android/res/mipmap/ic_launcher.xml").is_file(), "Android needs a default launcher resource"
adaptive = (ROOT / "android/res/mipmap-anydpi-v26/ic_launcher.xml").read_text(encoding="utf-8")
assert "@drawable/ic_launcher_background" in adaptive and "@drawable/ic_launcher_foreground" in adaptive, "Android adaptive icon layers missing"
assert "@drawable/ic_launcher_monochrome" in adaptive, "Android adaptive icon must include monochrome layer"

android_bg = (ROOT / "android/res/drawable/ic_launcher_background.xml").read_text(encoding="utf-8")
android_fg = (ROOT / "android/res/drawable/ic_launcher_foreground.xml").read_text(encoding="utf-8")
android_mono = (ROOT / "android/res/drawable/ic_launcher_monochrome.xml").read_text(encoding="utf-8")
assert "#FF223472" in android_bg and "#FF0B1022" in android_bg and "#2C2F47" not in android_bg, "Android background must use the refactored indigo gradient"
assert "#FF34D399" in android_fg and "#FF93C5FD" in android_fg and "#FFC0CAF5" not in android_fg, "Android foreground must use the refactored prompt + sparkle design"
assert 'viewportWidth="108"' in android_mono and "#FFFFFFFF" in android_mono, "Android monochrome layer must be a 108dp vector"

android_build = (ROOT / "android/build.ps1").read_text(encoding="utf-8")
assert "'.svg'='image/svg+xml'" in android_build, "Android build must register favicon.svg MIME type in web-assets.properties"

# 3. Windows multi-size ICO (DIB for small/medium + PNG for large) and unified runtime/installer references
icon = ROOT / "agent-windows/remote-ai.ico"
raw = icon.read_bytes()
reserved, kind, count = struct.unpack_from("<HHH", raw)
assert (reserved, kind) == (0, 1) and count >= 7, "Windows icon must be a multi-size ICO"
entries = {}
for i in range(count):
    w, h, _, _, _, bpp, sz, off = struct.unpack_from("<BBBBHHII", raw, 6 + i * 16)
    size = w or 256
    chunk = raw[off:off + sz]
    entries[size] = (bpp, chunk[:4] == b"\x89PNG")
for required in (16, 24, 32, 48, 64, 128, 256):
    assert required in entries, f"Windows ICO missing {required}px frame"
for dib_size in (16, 24, 32, 48, 64):
    assert entries[dib_size] == (32, False), f"Windows ICO {dib_size}px frame must be 32-bit DIB for .NET/Win32 compatibility"
assert entries[256] == (32, True), "Windows ICO 256px frame must be PNG-compressed"

build = (ROOT / "agent-windows/build.ps1").read_text(encoding="utf-8")
assert '"/win32icon:$icon"' in build and '"/resource:$icon,remote-ai.ico"' in build, "Windows builds must embed the shared ICO as both Win32 icon and managed resource"

package = (ROOT / "tools/package_windows.py").read_text(encoding="utf-8")
assert '"/win32icon:" + str(icon)' in package and '"/resource:%s,remote-ai.ico"' in package, "Windows installer must embed the shared ICO"

controls = (ROOT / "agent-windows/Controls.cs").read_text(encoding="utf-8")
for token in ('"remote-ai.ico"', "ApplyIcon(Form form)", "TrayIcon()", "0x0080"):
    assert token in controls, f"Controls.cs missing unified icon helper: {token}"

app_cs = (ROOT / "agent-windows/RemoteCliApp.cs").read_text(encoding="utf-8")
assert "Theme.ApplyIcon(this)" in app_cs and "Theme.TrayIcon()" in app_cs and "Theme.ApplyIcon(dialog)" in app_cs, "RemoteCliApp must apply the shared icon to windows and tray"

setup_cs = (ROOT / "agent-windows/Setup.cs").read_text(encoding="utf-8")
assert "Theme.ApplyIcon(form)" in setup_cs, "Installer dialog must apply the shared icon"

tool_installer_cs = (ROOT / "agent-windows/ToolInstaller.cs").read_text(encoding="utf-8")
assert "Theme.ApplyIcon(dialog)" in tool_installer_cs, "ToolInstaller dialog must apply the shared icon"

print("Icon asset checks passed")
