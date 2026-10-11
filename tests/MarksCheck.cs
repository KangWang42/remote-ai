using System;
using System.IO;
using System.Linq;
using System.Text;
using RemoteCli;

/// What the owner said of single conversations is kept by the computer: it outlasts the program, and a file that
/// cannot be read marks nothing.
public static class MarksCheck {
    static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    public static int Main() {
        string folder = Path.Combine(Path.GetTempPath(), "rcli-marks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try {
            string file = Path.Combine(folder, "conversation-marks.json"), one = "11111111-2222-3333-4444-555555555555";
            var marks = new Marks(file);
            Require(!marks.Is("pinned", "claude", one) && !File.Exists(file), "nothing is marked, and nothing is written, until something is said");
            Require(marks.Set("pinned", "claude", one, true) && !marks.Set("pinned", "claude", one, true), "said once, it is so; said again, nothing changes");
            Require(marks.Is("pinned", "claude", one) && !marks.Is("hidden", "claude", one) && !marks.Is("pinned", "codex", one), "a mark is of one kind, and of one tool's conversation");
            marks.Set("hidden", "codex", one, true);
            // another start of the program reads what was kept
            var again = new Marks(file);
            Require(again.Is("pinned", "claude", one) && again.Is("hidden", "codex", one), "kept: " + File.ReadAllText(file));
            Require(again.Set("pinned", "claude", one, false) && !new Marks(file).Is("pinned", "claude", one) && new Marks(file).Is("hidden", "codex", one), "taken back, it is gone, and the other stays");
            try { marks.Set("starred", "claude", one, true); Require(false, "a kind that is not known is refused"); } catch (ArgumentException) { }
            // of more than are kept, the ones marked longest ago give way
            for (int n = 0; n < Marks.Most + 5; n++) again.Set("hidden", "claude", "s" + n, true);
            var full = new Marks(file);
            Require(!full.Is("hidden", "claude", "s0") && full.Is("hidden", "claude", "s" + (Marks.Most + 4)) && !full.Is("hidden", "codex", one), "the oldest give way");
            // a file that is no list of marks, or cannot be read as one, marks nothing and is written over by the next change
            foreach (string broken in new[] { "not json", "[1,2]", "{\"pinned\":\"claude:x\",\"hidden\":[3,null,\"claude:kept\"]}" }) {
                File.WriteAllText(file, broken, new UTF8Encoding(false));
                var read = new Marks(file);
                Require(!read.Is("pinned", "claude", "x") && read.Is("hidden", "claude", "kept") == broken.Contains("kept"), "broken: " + broken);
                Require(read.Set("pinned", "codex", one, true) && new Marks(file).Is("pinned", "codex", one), "written over: " + broken);
            }
            Console.WriteLine("marks of conversations: kept by the computer, by tool and conversation, the oldest giving way");
            return 0;
        } finally { try { Directory.Delete(folder, true); } catch (IOException) { } }
    }
}
