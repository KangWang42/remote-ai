using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RemoteCli {
/// The look of the windows: the skins of the phone app, and the small things drawn with them.
public static class Theme {
    // One row per skin of the phone app (web/terminal/skins.js, Kit.java in the app): background, panel, raised,
    // line, ink, muted, accent, on accent, good, busy, bad. A new installation starts with the skin the app starts with.
    public static readonly string[] Names = { "night", "slate", "pine", "dusk", "paper", "dawn" }, Titles = { "夜航", "墨岩", "松林", "暮紫", "纸白 · 明亮", "晨光 · 明亮" };
    static readonly int[][] Skins = {
        new[] { 0x1a1b26, 0x222436, 0x2c2f47, 0x2f334d, 0xc0caf5, 0x8189ad, 0x7aa2f7, 0x10121c, 0x9ece6a, 0xe0af68, 0xf7768e },
        new[] { 0x16181d, 0x1e2127, 0x2a2e37, 0x2c313a, 0xd7dae0, 0x7f8795, 0x61afef, 0x0f1114, 0x98c379, 0xe5c07b, 0xe06c75 },
        new[] { 0x1e2326, 0x272e33, 0x323c41, 0x343f44, 0xd3c6aa, 0x859289, 0xa7c080, 0x1a2023, 0xa7c080, 0xdbbc7f, 0xe67e80 },
        new[] { 0x1e1e2e, 0x262637, 0x313244, 0x313244, 0xcdd6f4, 0x8c8fa8, 0xcba6f7, 0x181825, 0xa6e3a1, 0xf9e2af, 0xf38ba8 },
        new[] { 0xfbfbfc, 0xf0f1f4, 0xe4e6eb, 0xd5d8df, 0x2b2f3a, 0x666b78, 0x2f6fe4, 0xffffff, 0x3d8a3a, 0xa36a00, 0xc93c37 },
        new[] { 0xfaf4ed, 0xf4ede4, 0xebe1d5, 0xddd3c6, 0x4a4566, 0x7a7089, 0x286983, 0xffffff, 0x3b7d5c, 0xb9781a, 0xb4506a } };
    public const string Default = "paper";
    public static Color Bg, Side, Panel, Raised, Hover, Line, Ink, Muted, Faint, Accent, AccentOver, AccentDown, OnAccent, Good, Busy, Bad;
    public static bool Light;
    public static string Skin = "";
    static Theme() { Use(Default); }
    public static Color Mix(Color a, Color b, float t) { return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t)); }
    /// Takes the skin with this name; a name that is not one of them gives the default.
    public static void Use(string name) {
        int at = Array.IndexOf(Names, name);
        if (at < 0) at = Array.IndexOf(Names, Default);
        Color[] c = Array.ConvertAll(Skins[at], value => Color.FromArgb(255, Color.FromArgb(value)));
        Skin = Names[at];
        Bg = c[0]; Panel = c[1]; Raised = c[2]; Line = c[3]; Ink = c[4]; Muted = c[5]; Accent = c[6]; OnAccent = c[7]; Good = c[8]; Busy = c[9]; Bad = c[10];
        Light = (Bg.R * 299 + Bg.G * 587 + Bg.B * 114) / 1000 > 150;
        // What the app's skins do not name: the side bar, a raised thing under the pointer, the faintest text.
        Side = Mix(Bg, Panel, 0.6f); Hover = Mix(Raised, Ink, 0.1f); Faint = Mix(Muted, Bg, 0.42f);
        AccentOver = Mix(Accent, Light ? Color.Black : Color.White, Light ? 0.1f : 0.18f); AccentDown = Mix(Accent, Color.Black, 0.14f);
    }
    // Controls are given their colours when they are made. To change the skin of a window that is open, the part of
    // the palette each control was given is noted once (under the default skin, whose colours all differ), and the
    // same part of the new palette is given to it. A colour that is no part of the palette, as the white under the
    // code a phone reads, stays.
    static Color[] Palette() { return new[] { Bg, Side, Panel, Raised, Line, Ink, Muted, Faint, Accent, OnAccent, Good, Busy, Bad }; }
    public static Dictionary<Control, int[]> Parts(Control root) {
        var found = new Dictionary<Control, int[]>();
        Color[] palette = Palette();
        Action<Control> note = null;
        note = control => {
            int back = Array.FindIndex(palette, 0, 6, c => c.ToArgb() == control.BackColor.ToArgb()), fore = Array.FindIndex(palette, c => c.ToArgb() == control.ForeColor.ToArgb());
            if (back >= 0 || fore >= 0) found[control] = new[] { back, fore };
            foreach (Control inner in control.Controls) note(inner);
        };
        note(root);
        return found;
    }
    public static void Paint(Dictionary<Control, int[]> parts) {
        Color[] palette = Palette();
        foreach (var part in parts) {
            if (part.Value[0] >= 0) part.Key.BackColor = palette[part.Value[0]];
            if (part.Value[1] >= 0) part.Key.ForeColor = palette[part.Value[1]];
            part.Key.Invalidate();
        }
        foreach (Control list in scrolled) if (list.IsHandleCreated) try { SetWindowTheme(list.Handle, Light ? "Explorer" : "DarkMode_Explorer", null); } catch { }
    }
    public const string Family = "Microsoft YaHei UI", Icons = "Segoe MDL2 Assets";
    // Glyphs of the icon typeface that ships with Windows 10 and 11.
    public const string IconLink = "", IconFolder = "", IconPulse = "", IconGear = "", IconCopy = "", IconRefresh = "",
        IconAdd = "", IconDelete = "", IconEdit = "", IconOpen = "", IconWifi = "", IconGlobe = "", IconServer = "",
        IconLock = "", IconDownload = "", IconCheck = "", IconPhone = "", IconPower = "", IconTerminal = "";

    public static Font Text(float size = 9f, bool bold = false) { return new Font(Family, size, bold ? FontStyle.Bold : FontStyle.Regular); }
    public static float Dpi(Graphics g) { return g.DpiX / 96f; }
    public static void Smooth(Graphics g) { g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit; }

    public static GraphicsPath Rounded(RectangleF r, float radius) {
        float d = Math.Max(0.1f, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90); path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
    public static void Fill(Graphics g, RectangleF r, float radius, Color fill, Color? edge = null, float edgeWidth = 1f) {
        using (var path = Rounded(r, radius)) {
            using (var brush = new SolidBrush(fill)) g.FillPath(brush, path);
            if (edge.HasValue) using (var pen = new Pen(edge.Value, edgeWidth)) g.DrawPath(pen, path);
        }
    }
    /// The program's mark: a terminal window with a prompt, drawn at any size.
    public static Bitmap Mark(int size) {
        var picture = new Bitmap(size, size);
        using (var g = Graphics.FromImage(picture)) {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float u = size / 32f;
            using (var path = Rounded(new RectangleF(1 * u, 1 * u, 30 * u, 30 * u), 7 * u))
            using (var fill = new LinearGradientBrush(new PointF(0, 0), new PointF(size, size), Color.FromArgb(58, 64, 104), Color.FromArgb(30, 32, 48))) g.FillPath(fill, path);
            using (var pen = new Pen(Accent, 3.2f * u) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                g.DrawLines(pen, new[] { new PointF(8 * u, 10.5f * u), new PointF(14.5f * u, 16 * u), new PointF(8 * u, 21.5f * u) });
            using (var pen = new Pen(Good, 3.2f * u) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawLine(pen, 17 * u, 22 * u, 24.5f * u, 22 * u);
        }
        return picture;
    }
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr handle);
    public static Icon MarkIcon(int size) {
        using (var picture = Mark(size)) {
            IntPtr handle = picture.GetHicon();
            try { return (Icon)Icon.FromHandle(handle).Clone(); } finally { DestroyIcon(handle); }
        }
    }
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr window, string app, string ids);
    /// A title bar that goes with the skin where Windows offers one (10 version 2004 and later); older systems keep theirs.
    public static void Title(IntPtr window) {
        try { int dark = Light ? 0 : 1; DwmSetWindowAttribute(window, 20, ref dark, 4); int colour = Bg.R | Bg.G << 8 | Bg.B << 16; DwmSetWindowAttribute(window, 35, ref colour, 4); } catch { }
    }
    /// Scroll bars for a list that go with the skin.
    static readonly List<Control> scrolled = new List<Control>();
    public static void Scroll(Control control) { scrolled.Add(control); control.HandleCreated += (s, e) => { try { SetWindowTheme(control.Handle, Light ? "Explorer" : "DarkMode_Explorer", null); } catch { } }; }
    /// A menu in the look of the windows: what is chosen has a mark, and a note may stand at the right of an entry.
    public static ContextMenuStrip Menu() { return new ContextMenuStrip { Renderer = new MenuLook(), ShowImageMargin = false, ShowCheckMargin = true, Font = Text(9f), BackColor = Panel, ForeColor = Ink }; }
    sealed class MenuLook : ToolStripProfessionalRenderer {
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) { using (var back = new SolidBrush(Panel)) e.Graphics.FillRectangle(back, e.AffectedBounds); }
        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) { using (var pen = new Pen(Line)) e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1); }
        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }
        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e) {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            Smooth(e.Graphics);
            Fill(e.Graphics, new RectangleF(3, 1, e.Item.Width - 6, e.Item.Height - 2), 6f * Dpi(e.Graphics), Raised);
        }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e) {
            // The note at the right (how long a relay took to answer) is quieter than the name; a heading is quieter still.
            e.TextColor = !e.Item.Enabled ? Faint : e.Text != e.Item.Text ? Muted : Ink;
            base.OnRenderItemText(e);
        }
        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e) { using (var pen = new Pen(Line)) e.Graphics.DrawLine(pen, 10, e.Item.Height / 2, e.Item.Width - 10, e.Item.Height / 2); }
        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e) { e.ArrowColor = Muted; base.OnRenderArrow(e); }
        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e) {
            Smooth(e.Graphics);
            float k = Dpi(e.Graphics), x = e.ImageRectangle.X + e.ImageRectangle.Width / 2f, y = e.Item.Height / 2f;
            using (var pen = new Pen(Accent, 1.8f * k) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                e.Graphics.DrawLines(pen, new[] { new PointF(x - 4 * k, y), new PointF(x - 1 * k, y + 3 * k), new PointF(x + 5 * k, y - 4 * k) });
        }
    }

    public static Button Button(string text, bool primary = false) { return new RoundButton { Text = text, Kind = primary ? ButtonKind.Primary : ButtonKind.Normal }; }
    public static Label Label(string text, Color color, Color back, float size = 0, bool bold = false) {
        var label = new Label { Text = text, ForeColor = color, BackColor = back, AutoEllipsis = true, UseMnemonic = false };
        if (size > 0 || bold) label.Font = Text(size > 0 ? size : 9f, bold);
        return label;
    }
}

public enum ButtonKind { Normal, Primary, Ghost, Danger }

/// A button with rounded corners, an optional icon before its text, and the four looks used in the windows.
public sealed class RoundButton : Button {
    public ButtonKind Kind = ButtonKind.Normal;
    public string Glyph = "";
    bool over, down;
    public RoundButton() {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; Cursor = Cursors.Hand; UseVisualStyleBackColor = false; BackColor = Theme.Bg; ForeColor = Theme.Ink;
    }
    protected override void OnMouseEnter(EventArgs e) { over = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { over = down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }
    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics; Theme.Smooth(g);
        float k = Theme.Dpi(g);
        using (var back = new SolidBrush(Parent == null ? Theme.Bg : Parent.BackColor)) g.FillRectangle(back, ClientRectangle);
        Color fill, ink, edge;
        switch (Kind) {
            case ButtonKind.Primary: fill = down ? Theme.AccentDown : over ? Theme.AccentOver : Theme.Accent; ink = Theme.OnAccent; edge = fill; break;
            case ButtonKind.Ghost: fill = down ? Theme.Line : over ? Theme.Raised : (Parent == null ? Theme.Bg : Parent.BackColor); ink = over ? Theme.Ink : Theme.Muted; edge = fill; break;
            case ButtonKind.Danger: fill = down ? Theme.Mix(Theme.Raised, Theme.Bad, 0.32f) : over ? Theme.Mix(Theme.Raised, Theme.Bad, 0.18f) : Theme.Raised; ink = Theme.Bad; edge = over ? Theme.Bad : Theme.Line; break;
            default: fill = down ? Theme.Line : over ? Theme.Hover : Theme.Raised; ink = Theme.Ink; edge = Theme.Line; break;
        }
        if (!Enabled) { ink = Theme.Faint; if (Kind == ButtonKind.Primary) { fill = Theme.Raised; edge = Theme.Line; } }
        Theme.Fill(g, new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), 8f * k, fill, edge);
        if (Focused && ShowFocusCues) using (var ring = new Pen(Theme.Accent, 1.4f * k)) using (var path = Theme.Rounded(new RectangleF(1.5f, 1.5f, Width - 3, Height - 3), 7f * k)) g.DrawPath(ring, path);
        using (var brush = new SolidBrush(ink)) using (var icon = new Font(Theme.Icons, Font.Size + 1f)) {
            SizeF text = g.MeasureString(Text, Font), glyph = Glyph.Length > 0 ? g.MeasureString(Glyph, icon) : SizeF.Empty;
            float gap = Glyph.Length > 0 && Text.Length > 0 ? 3 * k : 0, total = text.Width + glyph.Width + gap, x = (Width - total) / 2;
            if (Glyph.Length > 0) { g.DrawString(Glyph, icon, brush, x, (Height - glyph.Height) / 2 + 1 * k); x += glyph.Width + gap; }
            g.DrawString(Text, Font, brush, x, (Height - text.Height) / 2);
        }
    }
}

/// A rounded panel that groups related controls.
public sealed class Card : Panel {
    public Color? Fill;         // a colour of its own; without one it has the panel's
    public Card() { BackColor = Theme.Panel; DoubleBuffered = true; }
    protected override void OnPaint(PaintEventArgs e) {
        Theme.Smooth(e.Graphics);
        using (var back = new SolidBrush(Parent == null ? Theme.Bg : Parent.BackColor)) e.Graphics.FillRectangle(back, ClientRectangle);
        Theme.Fill(e.Graphics, new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), 12f * Theme.Dpi(e.Graphics), Fill ?? Theme.Panel, Theme.Line);
    }
}

/// A text box on a rounded dark field.
public sealed class Field : Panel {
    public readonly TextBox Box = new TextBox { BorderStyle = BorderStyle.None, BackColor = Theme.Bg, ForeColor = Theme.Ink };
    public Field() { BackColor = Theme.Panel; DoubleBuffered = true; Controls.Add(Box); Box.GotFocus += (s, e) => Invalidate(); Box.LostFocus += (s, e) => Invalidate(); Click += (s, e) => Box.Focus(); }
    void Arrange() { int inset = Math.Max(8, Height / 3); Box.SetBounds(inset, (Height - Box.PreferredHeight) / 2, Math.Max(10, Width - inset * 2), Box.PreferredHeight); }
    // Scaling the window for the screen also moves the box; it is centred again afterwards.
    protected override void ScaleControl(SizeF factor, BoundsSpecified specified) { base.ScaleControl(factor, specified); Arrange(); }
    protected override void OnLayout(LayoutEventArgs e) { base.OnLayout(e); Arrange(); }
    protected override void OnPaint(PaintEventArgs e) {
        Theme.Smooth(e.Graphics);
        using (var back = new SolidBrush(Parent == null ? Theme.Bg : Parent.BackColor)) e.Graphics.FillRectangle(back, ClientRectangle);
        Theme.Fill(e.Graphics, new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), 8f * Theme.Dpi(e.Graphics), Theme.Bg, Box.Focused && !Box.ReadOnly ? Theme.Accent : Theme.Line);
    }
}

/// One of several choices: a title, a line of explanation, and a mark when it is the chosen one. (Used by the installer.)
public sealed class Option : Control {
    public string Title = "", About = "";
    bool chosen, hover;
    public event EventHandler Chosen;
    public bool Checked { get { return chosen; } set { if (chosen == value) return; chosen = value; Invalidate(); } }
    public Option() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true); BackColor = Theme.Panel; Cursor = Cursors.Hand; TabStop = true; }
    void Pick() { if (chosen) return; if (Chosen != null) Chosen(this, EventArgs.Empty); }
    protected override void OnClick(EventArgs e) { Focus(); Pick(); base.OnClick(e); }
    protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) Pick(); base.OnKeyDown(e); }
    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics; Theme.Smooth(g);
        float k = Theme.Dpi(g);
        Theme.Fill(g, new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), 8f * k, chosen ? Theme.Raised : hover ? Theme.Mix(Theme.Panel, Theme.Raised, 0.5f) : Theme.Panel, chosen ? Theme.Accent : Theme.Line, chosen ? 1.6f : 1f);
        float ring = 16 * k, x = 14 * k, y = (Height - ring) / 2;
        using (var pen = new Pen(chosen ? Theme.Accent : Theme.Muted, 1.6f * k)) g.DrawEllipse(pen, x, y, ring, ring);
        if (chosen) using (var dot = new SolidBrush(Theme.Accent)) g.FillEllipse(dot, x + 4 * k, y + 4 * k, ring - 8 * k, ring - 8 * k);
        float left = x + ring + 12 * k;
        using (var bold = new Font(Font, FontStyle.Bold)) using (var ink = new SolidBrush(Theme.Ink)) using (var muted = new SolidBrush(Theme.Muted))
        using (var format = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap }) {
            float line = bold.GetHeight(g), top = (Height - line * 2 - 2 * k) / 2;
            g.DrawString(Title, bold, ink, new RectangleF(left, top, Width - left - 8 * k, line), format);
            g.DrawString(About, Font, muted, new RectangleF(left, top + line + 2 * k, Width - left - 8 * k, line), format);
        }
    }
}

/// An on/off switch.
public sealed class Switch : Control {
    bool on, over;
    public event EventHandler Changed;
    public bool On { get { return on; } set { if (on == value) return; on = value; Invalidate(); } }
    public Switch() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true); Cursor = Cursors.Hand; TabStop = true; BackColor = Theme.Panel; }
    void Flip() { on = !on; Invalidate(); if (Changed != null) Changed(this, EventArgs.Empty); }
    protected override void OnClick(EventArgs e) { Focus(); Flip(); base.OnClick(e); }
    protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space) Flip(); base.OnKeyDown(e); }
    protected override void OnMouseEnter(EventArgs e) { over = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { over = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics; Theme.Smooth(g);
        using (var back = new SolidBrush(Parent == null ? Theme.Bg : Parent.BackColor)) g.FillRectangle(back, ClientRectangle);
        float h = Math.Min(Height - 2, Width / 1.9f), y = (Height - h) / 2, knob = h - 6 * Theme.Dpi(g) * 0.8f;
        Theme.Fill(g, new RectangleF(1, y, Width - 2, h), h / 2, on ? (over ? Theme.AccentOver : Theme.Accent) : (over ? Theme.Hover : Theme.Raised), on ? (Color?)null : Theme.Faint);
        float pad = (h - knob) / 2, x = on ? Width - 1 - pad - knob : 1 + pad;
        using (var brush = new SolidBrush(on ? Theme.OnAccent : Theme.Muted)) g.FillEllipse(brush, x, y + pad, knob, knob);
        if (Focused && ShowFocusCues) using (var ring = new Pen(Theme.Ink, 1f)) using (var path = Theme.Rounded(new RectangleF(0.5f, y - 0.5f, Width - 1, h + 1), h / 2)) g.DrawPath(ring, path);
    }
}

/// A row of choices of which one is chosen.
public sealed class Segmented : Control {
    public string[] Items = new string[0];
    int chosen, over = -1;
    public event EventHandler Changed;
    public int Chosen { get { return chosen; } set { if (chosen == value) return; chosen = value; Invalidate(); } }
    public Segmented() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true); Cursor = Cursors.Hand; TabStop = true; BackColor = Theme.Panel; }
    int At(int x) { return Items.Length == 0 ? -1 : Math.Max(0, Math.Min(Items.Length - 1, x * Items.Length / Math.Max(1, Width))); }
    void Pick(int index) { if (index < 0 || index == chosen) return; chosen = index; Invalidate(); if (Changed != null) Changed(this, EventArgs.Empty); }
    /// As a click on that part would do.
    public void Choose(int index) { Pick(index); }
    protected override void OnMouseMove(MouseEventArgs e) { int at = At(e.X); if (at != over) { over = at; Invalidate(); } base.OnMouseMove(e); }
    protected override void OnMouseLeave(EventArgs e) { over = -1; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { Focus(); Pick(At(e.X)); base.OnMouseDown(e); }
    protected override bool IsInputKey(Keys key) { return key == Keys.Left || key == Keys.Right || base.IsInputKey(key); }
    protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Left) Pick(Math.Max(0, chosen - 1)); else if (e.KeyCode == Keys.Right) Pick(Math.Min(Items.Length - 1, chosen + 1)); base.OnKeyDown(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics; Theme.Smooth(g);
        float k = Theme.Dpi(g);
        using (var back = new SolidBrush(Parent == null ? Theme.Bg : Parent.BackColor)) g.FillRectangle(back, ClientRectangle);
        Theme.Fill(g, new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), 9f * k, Theme.Bg, Focused && ShowFocusCues ? Theme.Muted : Theme.Line);
        if (Items.Length == 0) return;
        float each = (Width - 6 * k) / Items.Length;
        using (var bold = new Font(Font, FontStyle.Bold)) using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap })
            for (int i = 0; i < Items.Length; i++) {
                var cell = new RectangleF(3 * k + i * each + 1, 3 * k + 1, each - 2, Height - 6 * k - 2);
                if (i == chosen) Theme.Fill(g, cell, 7f * k, Theme.Raised, Theme.Accent);
                else if (i == over) Theme.Fill(g, cell, 7f * k, Theme.Panel);
                using (var brush = new SolidBrush(i == chosen ? Theme.Ink : Theme.Muted)) g.DrawString(Items[i], i == chosen ? bold : Font, brush, cell, format);
            }
    }
}

/// An entry of the side bar: an icon, a name, and a small count when there is something to show.
public sealed class NavItem : Control {
    public string Glyph = "", Title = "", Badge = "";
    bool chosen, over;
    public bool Chosen { get { return chosen; } set { if (chosen == value) return; chosen = value; Invalidate(); } }
    public NavItem() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true); Cursor = Cursors.Hand; TabStop = true; BackColor = Theme.Side; }
    public void SetBadge(string text) { if (Badge == text) return; Badge = text; Invalidate(); }
    protected override void OnMouseEnter(EventArgs e) { over = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { over = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) OnClick(EventArgs.Empty); base.OnKeyDown(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics; Theme.Smooth(g);
        float k = Theme.Dpi(g);
        using (var back = new SolidBrush(Theme.Side)) g.FillRectangle(back, ClientRectangle);
        if (chosen || over || (Focused && ShowFocusCues)) Theme.Fill(g, new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), 9f * k, chosen ? Theme.Panel : Theme.Mix(Theme.Side, Theme.Panel, 0.5f), Focused && ShowFocusCues ? Theme.Muted : (Color?)null);
        if (chosen) Theme.Fill(g, new RectangleF(0, Height * 0.28f, 3 * k, Height * 0.44f), 1.5f * k, Theme.Accent);
        using (var icon = new Font(Theme.Icons, Font.Size + 2.5f)) using (var bold = new Font(Font, chosen ? FontStyle.Bold : FontStyle.Regular))
        using (var ink = new SolidBrush(chosen ? Theme.Ink : Theme.Muted)) using (var accent = new SolidBrush(chosen ? Theme.Accent : Theme.Muted))
        using (var format = new StringFormat { LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap }) {
            g.DrawString(Glyph, icon, accent, new RectangleF(13 * k, 1 * k, 30 * k, Height), format);
            g.DrawString(Title, bold, ink, new RectangleF(44 * k, 0, Width - 44 * k, Height), format);
            if (Badge.Length > 0) {
                SizeF size = g.MeasureString(Badge, Font);
                var pill = new RectangleF(Width - size.Width - 22 * k, (Height - size.Height - 2 * k) / 2, size.Width + 10 * k, size.Height + 2 * k);
                Theme.Fill(g, pill, pill.Height / 2, Theme.Raised);
                using (var brush = new SolidBrush(Theme.Ink)) g.DrawString(Badge, Font, brush, pill.X + 5 * k, pill.Y + 1 * k);
            }
        }
    }
}

/// A coloured dot followed by a line of text: the state of something at a glance.
public sealed class StatusLine : Control {
    Color dot = Theme.Busy; string line = "";
    public StatusLine() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true); BackColor = Theme.Bg; }
    public void Set(string text, Color colour) { if (text == line && colour == dot) return; line = text; dot = colour; Invalidate(); }
    public string Line { get { return line; } }
    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics; Theme.Smooth(g);
        float k = Theme.Dpi(g), size = 8 * k;
        using (var back = new SolidBrush(Parent == null ? Theme.Bg : Parent.BackColor)) g.FillRectangle(back, ClientRectangle);
        using (var halo = new SolidBrush(Color.FromArgb(50, dot))) g.FillEllipse(halo, 1 * k, (Height - size) / 2 - 3 * k, size + 6 * k, size + 6 * k);
        using (var brush = new SolidBrush(dot)) g.FillEllipse(brush, 4 * k, (Height - size) / 2, size, size);
        using (var brush = new SolidBrush(Theme.Ink)) using (var format = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
            g.DrawString(line, Font, brush, new RectangleF(22 * k, 0, Width - 22 * k, Height), format);
    }
}

/// One row of a RowList.
public sealed class Row { public string Glyph = "", Title = "", About = "", Tag = ""; public Color TagColour = Theme.Muted; public object Value; }

/// A list of rows with an icon, a title, a second line and an optional tag at the right; a hint is shown while it is empty.
public sealed class RowList : ListBox {
    public string EmptyTitle = "", EmptyAbout = "";
    public readonly List<Row> Rows = new List<Row>();
    public RowList() {
        DrawMode = DrawMode.OwnerDrawFixed; BorderStyle = BorderStyle.None; IntegralHeight = false; BackColor = Theme.Panel; ForeColor = Theme.Ink; ItemHeight = 54;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Theme.Scroll(this);
    }
    public void Show(IEnumerable<Row> rows) {
        int keep = SelectedIndex;
        BeginUpdate(); Rows.Clear(); Rows.AddRange(rows); Items.Clear(); foreach (var row in Rows) Items.Add(row.Title); EndUpdate();
        if (keep >= 0 && keep < Items.Count) SelectedIndex = keep;
        Invalidate();
    }
    public Row Selected { get { return SelectedIndex >= 0 && SelectedIndex < Rows.Count ? Rows[SelectedIndex] : null; } }
    protected override void OnDrawItem(DrawItemEventArgs e) {
        if (e.Index < 0 || e.Index >= Rows.Count) return;
        var g = e.Graphics; Theme.Smooth(g);
        float k = Theme.Dpi(g);
        Row row = Rows[e.Index];
        bool chosen = (e.State & DrawItemState.Selected) != 0;
        using (var back = new SolidBrush(Theme.Panel)) g.FillRectangle(back, e.Bounds);
        var inner = new RectangleF(e.Bounds.X + 2 * k, e.Bounds.Y + 2 * k, e.Bounds.Width - 4 * k, e.Bounds.Height - 4 * k);
        if (chosen) Theme.Fill(g, inner, 9f * k, Theme.Raised, Theme.Line);
        var tile = new RectangleF(inner.X + 8 * k, inner.Y + (inner.Height - 34 * k) / 2, 34 * k, 34 * k);
        Theme.Fill(g, tile, 9f * k, chosen ? Theme.Hover : Theme.Bg);
        using (var icon = new Font(Theme.Icons, Font.Size + 3f)) using (var accent = new SolidBrush(Theme.Accent)) using (var centre = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            g.DrawString(row.Glyph, icon, accent, new RectangleF(tile.X, tile.Y + 1 * k, tile.Width, tile.Height), centre);
        float left = tile.Right + 12 * k, right = inner.Right - 10 * k;
        if (row.Tag.Length > 0) {
            SizeF size = g.MeasureString(row.Tag, Font);
            var pill = new RectangleF(right - size.Width - 14 * k, inner.Y + (inner.Height - size.Height - 4 * k) / 2, size.Width + 14 * k, size.Height + 4 * k);
            Theme.Fill(g, pill, pill.Height / 2, Color.FromArgb(36, row.TagColour));
            using (var brush = new SolidBrush(row.TagColour)) g.DrawString(row.Tag, Font, brush, pill.X + 7 * k, pill.Y + 2 * k);
            right = pill.X - 8 * k;
        }
        using (var bold = new Font(Font, FontStyle.Bold)) using (var ink = new SolidBrush(Theme.Ink)) using (var muted = new SolidBrush(Theme.Muted))
        using (var one = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap }) using (var path = new StringFormat { Trimming = StringTrimming.EllipsisPath, FormatFlags = StringFormatFlags.NoWrap }) {
            float line = bold.GetHeight(g), top = inner.Y + (inner.Height - line * 2 - 2 * k) / 2;
            g.DrawString(row.Title, bold, ink, new RectangleF(left, top, right - left, line), one);
            g.DrawString(row.About, Font, muted, new RectangleF(left, top + line + 2 * k, right - left, line), path);
        }
    }
    const int Paint = 0x000F, EraseBackground = 0x0014;
    protected override void WndProc(ref Message m) {
        base.WndProc(ref m);
        // An empty list says what belongs in it.
        if (m.Msg == Paint && Items.Count == 0 && EmptyTitle.Length > 0) using (var g = CreateGraphics()) {
            Theme.Smooth(g);
            using (var back = new SolidBrush(Theme.Panel)) g.FillRectangle(back, ClientRectangle);
            using (var bold = new Font(Font.FontFamily, Font.Size + 1.5f, FontStyle.Bold)) using (var ink = new SolidBrush(Theme.Ink)) using (var muted = new SolidBrush(Theme.Muted))
            using (var centre = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center }) {
                float mid = Height / 2f, line = bold.GetHeight(g);
                g.DrawString(EmptyTitle, bold, ink, new RectangleF(0, mid - line * 1.5f, Width, line * 1.4f), centre);
                g.DrawString(EmptyAbout, Font, muted, new RectangleF(Width * 0.1f, mid, Width * 0.8f, line * 2.6f), centre);
            }
        }
    }
}

/// A choice shown as a field: what is chosen, a note at its right (how fast, which kind) and an arrow. A press asks
/// for the list, which the window shows as a menu under it.
public sealed class Picker : Control {
    string title = "", note = "";
    int tone;       // of the note: 0 quiet, 1 good, 2 bad
    bool over;
    public event EventHandler Opening;
    public string Title { get { return title; } }
    public string Note { get { return note; } }
    public void Set(string chosen, string beside = "", int kind = 0) { if (chosen == title && beside == note && kind == tone) return; title = chosen; note = beside; tone = kind; Invalidate(); }
    public Picker() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true); Cursor = Cursors.Hand; TabStop = true; BackColor = Theme.Panel; }
    /// As a press on it would do.
    public void Open() { if (Opening != null) Opening(this, EventArgs.Empty); }
    protected override void OnClick(EventArgs e) { Focus(); Open(); base.OnClick(e); }
    protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter || e.KeyCode == Keys.Down) { e.Handled = true; Open(); } base.OnKeyDown(e); }
    protected override bool IsInputKey(Keys key) { return key == Keys.Down || base.IsInputKey(key); }
    protected override void OnMouseEnter(EventArgs e) { over = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { over = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics; Theme.Smooth(g);
        float k = Theme.Dpi(g);
        using (var back = new SolidBrush(Parent == null ? Theme.Bg : Parent.BackColor)) g.FillRectangle(back, ClientRectangle);
        Theme.Fill(g, new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), 8f * k, over ? Theme.Mix(Theme.Bg, Theme.Raised, 0.5f) : Theme.Bg, Focused && ShowFocusCues || over ? Theme.Accent : Theme.Line);
        using (var bold = new Font(Font, FontStyle.Bold)) using (var arrow = new Font(Theme.Icons, Font.Size - 1f)) using (var ink = new SolidBrush(Theme.Ink)) using (var muted = new SolidBrush(Theme.Muted))
        using (var beside = new SolidBrush(tone == 1 ? Theme.Good : tone == 2 ? Theme.Bad : Theme.Muted))
        using (var left = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
        using (var right = new StringFormat { LineAlignment = StringAlignment.Center, Alignment = StringAlignment.Far, FormatFlags = StringFormatFlags.NoWrap }) {
            float end = Width - 30 * k, taken = note.Length > 0 ? g.MeasureString(note, Font).Width + 8 * k : 0;
            g.DrawString("\uE70D", arrow, muted, new RectangleF(Width - 30 * k, 1 * k, 24 * k, Height), right);
            if (note.Length > 0) g.DrawString(note, Font, beside, new RectangleF(end - taken, 0, taken, Height), right);
            g.DrawString(title, bold, ink, new RectangleF(12 * k, 0, end - taken - 14 * k, Height), left);
        }
    }
}

/// A title, a line of explanation and a control at the right: one setting.
public sealed class SettingRow : Panel {
    readonly Control side; readonly Label name, text;
    /// What the setting is for, at length: shown where the pointer rests on its words.
    public void Tip(ToolTip tips, string words) { foreach (Control over in new Control[] { this, name, text }) tips.SetToolTip(over, words); }
    /// The line under the title says how the setting stands, where that changes: in the colour of what it says.
    public void Say(string about, Color colour) { if (text.Text != about) text.Text = about; if (text.ForeColor != colour) text.ForeColor = colour; }
    public SettingRow(string title, string about, Control control, int controlWidth, int controlHeight) {
        BackColor = Theme.Panel; Height = 53; side = control;
        name = Theme.Label(title, Theme.Ink, Theme.Panel, 9.5f, true); name.SetBounds(18, 3, 360, 20);
        text = Theme.Label(about, Theme.Muted, Theme.Panel, 8.5f); text.SetBounds(18, 22, controlWidth <= 60 ? 540 : 370, 18);      // beside a switch there is room for a longer line
        Controls.Add(name); Controls.Add(text);
        if (control != null) { control.SetBounds(0, 0, controlWidth, controlHeight); Controls.Add(control); control.BringToFront(); }
    }
    // The control keeps to the right edge, also after the window was scaled for the screen.
    protected override void OnLayout(LayoutEventArgs e) {
        base.OnLayout(e);
        if (side == null) return;
        var wanted = new Point(Width - side.Width - Height * 18 / 53, (Height - side.Height) / 2);
        if (side.Location != wanted) side.Location = wanted;
    }
    protected override void OnPaint(PaintEventArgs e) {
        base.OnPaint(e);
        using (var pen = new Pen(Theme.Line)) e.Graphics.DrawLine(pen, 18 * e.Graphics.DpiX / 96f, Height - 1, Width - 18 * e.Graphics.DpiX / 96f, Height - 1);
    }
}
}
