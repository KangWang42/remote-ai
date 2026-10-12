using System;
using System.Drawing;
using System.Windows.Forms;

namespace RemoteCli {
/// A few words at the corner of the computer's screen, for whoever looks at it while a hook keeps a tool for the
/// phone: what waits, and how to have it here instead. The tool itself shows only that a hook is running, which
/// says nothing to someone who did not set this up. It takes no key and no click (what is pressed goes through it
/// to what lies under it), never becomes the window in use, and is taken away when the tool is let go.
public sealed class Notice : Form {
    string title = "", about = "";
    public string Title { get { return Visible ? title : ""; } }
    public string About { get { return Visible ? about : ""; } }
    public Notice() {
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true; StartPosition = FormStartPosition.Manual;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }
    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams {
        get {
            var made = base.CreateParams;
            made.ExStyle |= 0x08000000 | 0x00000080 | 0x00000008 | 0x00000020;      // never the window in use; not among the windows; above them; clicks pass through
            return made;
        }
    }
    /// Shows these words at the lower right of the screen. `offstage`: where nobody sees them, for a test or a picture.
    public void Say(string what, string how, bool offstage) {
        if (Visible && what == title && how == about) return;
        title = what; about = how;
        float k; using (var g = CreateGraphics()) k = Theme.Dpi(g);
        Size = new Size((int)(372 * k), (int)(62 * k));
        var area = Screen.PrimaryScreen.WorkingArea;
        Location = offstage ? new Point(-30000, -30000) : new Point(area.Right - Width - (int)(16 * k), area.Bottom - Height - (int)(16 * k));
        BackColor = Theme.Bg;
        if (!Visible) Show();
        Invalidate();
    }
    /// Takes the words away.
    public void Clear() { if (Visible) Hide(); }
    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics; Theme.Smooth(g);
        float k = Theme.Dpi(g);
        g.Clear(Theme.Bg);
        Theme.Fill(g, new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), 0, Theme.Panel, Theme.Line);
        using (var mark = new SolidBrush(Theme.Accent)) g.FillRectangle(mark, 0, 0, 4 * k, Height);
        using (var bold = Theme.Text(9.5f, true)) using (var plain = Theme.Text(8.5f)) using (var ink = new SolidBrush(Theme.Ink)) using (var muted = new SolidBrush(Theme.Muted))
        using (var line = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap }) {
            g.DrawString(title, bold, ink, new RectangleF(16 * k, 10 * k, Width - 28 * k, 22 * k), line);
            g.DrawString(about, plain, muted, new RectangleF(16 * k, 33 * k, Width - 28 * k, 20 * k), line);
        }
    }
}
}
