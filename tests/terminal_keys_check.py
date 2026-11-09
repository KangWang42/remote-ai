"""The custom key strip: a tap sends the key, a long press edits it.

Registration: ci.yml's "source-level checks and the JVM fixtures" step.
Pure source guards, no browser needed; node --check covers JS syntax when
Node is on PATH (the CI runner has it).
"""
import shutil
import subprocess
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
js = (ROOT / "web/terminal/terminal.js").read_text(encoding="utf-8")
css = (ROOT / "web/terminal/terminal.css").read_text(encoding="utf-8")

# A saved key must always send, never open the editor on a plain tap.
assert "if (keyPanel) { openEditor(item); return; }" not in js, \
    "a tap on a custom key must send its bytes, not open the editor"
assert "send(bytesOf(item))" in js, "the custom key's compiled bytes must be sent"

# Editing belongs to a long press, with the platform's context menu suppressed.
assert "pointerdown" in js and "setTimeout" in js, "a long press needs a press timer"
assert "contextmenu" in js and "preventDefault()" in js, \
    "Android's long-press context menu must not steal the press"
assert "longPressed" in js, "the synthetic click after a long press must be swallowed"
assert "openEditor(item)" in js, "a long press must open that key's editor"

# Combo/escape keys are marked so they can be told from plain text snippets.
assert "combo ? '*' : ''" in js, "combination keys carry a * suffix on their label"
assert "长按编辑" in js, "the button says how to reach the editor"

# The editor state must not stay open after saving: that was the original bug.
assert "keyPanel = true;\n      renderKeys();" not in js, \
    "saving a key must not leave the strip in edit mode"
assert "keyPanel = false;" in js, "closeEditor() resets the edit mode"

# Editing a combination key must load the base it was saved with, not the
# select's first option (found on the phone: long press offered "A" for a
# Shift+Tab key, and saving would have silently rewritten the key).
assert "querySelector('#key-base').value = item.base" in js, \
    "the editor must restore the key's own base key when editing a combination"

# Long pressing a button must not start a text selection or a callout.
assert "user-select: none" in css and "touch-action: manipulation" in css, \
    "the custom key buttons need long-press friendly styles"

if shutil.which("node"):
    subprocess.run(["node", "--check", str(ROOT / "web/terminal/terminal.js")], check=True)
    subprocess.run(["node", str(ROOT / "tests/terminal_keys_behaviour_check.js")], check=True)
print("Custom key strip checks passed: tap sends, long press edits, combos marked")
