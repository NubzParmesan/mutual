using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Mutual;

// one dark look for every window. colors, fonts and the little controls everything is built from
static class Theme
{
    public static readonly Color Back = Color.FromArgb(23, 23, 27);
    public static readonly Color Panel = Color.FromArgb(33, 33, 39);
    public static readonly Color Raised = Color.FromArgb(43, 43, 51);
    public static readonly Color Line = Color.FromArgb(52, 52, 61);
    public static readonly Color Text = Color.FromArgb(236, 236, 239);
    public static readonly Color Dim = Color.FromArgb(154, 154, 166);
    public static readonly Color Faint = Color.FromArgb(105, 105, 116);
    public static readonly Color Online = Color.FromArgb(79, 209, 139);
    public static readonly Color Offline = Color.FromArgb(110, 110, 122);
    public static readonly Color Accent = Color.FromArgb(255, 140, 26);
    public static readonly Color AccentHover = Color.FromArgb(255, 160, 64);
    public static readonly Color AccentDown = Color.FromArgb(229, 122, 16);
    public static readonly Color Bad = Color.FromArgb(240, 100, 90);
    public static readonly Color Warn = Color.FromArgb(242, 179, 61);
    // dark tints for banners and icon circles
    public static readonly Color AccentSoft = Color.FromArgb(58, 40, 22);
    public static readonly Color OnlineSoft = Color.FromArgb(24, 52, 38);
    public static readonly Color BadSoft = Color.FromArgb(61, 30, 30);
    public static readonly Color WarnSoft = Color.FromArgb(56, 46, 22);

    public static readonly Font Hero = new("Segoe UI Semibold", 16f);
    public static readonly Font Title = new("Segoe UI Semibold", 13f);
    public static readonly Font Heading = new("Segoe UI Semibold", 10f);
    public static readonly Font Body = new("Segoe UI", 9.5f);
    public static readonly Font Small = new("Segoe UI", 8.5f);
    public static readonly Font SmallBold = new("Segoe UI Semibold", 8.5f);
    public static readonly Font Mono = new("Consolas", 8.5f);

    // win11 has segoe fluent icons, win10 has mdl2. same code points for everything used here
    public static readonly string IconFamily = new System.Drawing.Text.InstalledFontCollection().Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
    static readonly Dictionary<float, Font> iconFonts = new();
    public static Font Icons(float size) { if (!iconFonts.TryGetValue(size, out var f)) iconFonts[size] = f = new Font(IconFamily, size); return f; }

    public static class Glyph
    {
        public const string Settings = "\uE713", More = "\uE712", Screen = "\uE7F4", Send = "\uE724", Terminal = "\uE756", Game = "\uE7FC";
        public const string Check = "\uE73E", Close = "\uE711", Info = "\uE946", Warning = "\uE7BA", Error = "\uE783", Copy = "\uE8C8", Paste = "\uE77F";
        public const string Download = "\uE896", Upload = "\uE898", Window = "\uE737", Box = "\uE8B3", People = "\uE716", Shield = "\uEA18", Update = "\uE777";
        public const string Down = "\uE70D";
        public const string Stop = "\uE71A", Mouse = "\uE962", Link = "\uE71B", Search = "\uE721", Folder = "\uE8B7", Dot = "\uE915";
    }

    public static GraphicsPath Round(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static void FillRound(Graphics g, Color c, RectangleF r, float radius)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Round(r, radius);
        using var b = new SolidBrush(c);
        g.FillPath(b, path);
    }

    public static void DrawRound(Graphics g, Color c, RectangleF r, float radius, float width = 1)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Round(new RectangleF(r.X + width / 2, r.Y + width / 2, r.Width - width, r.Height - width), radius);
        using var pen = new Pen(c, width);
        g.DrawPath(pen, path);
    }

    public static void DrawGlyph(Graphics g, string glyph, float size, Color c, Rectangle r) =>
        TextRenderer.DrawText(g, glyph, Icons(size), r, c, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);

    public static Color Mix(Color a, Color b, float t) => Color.FromArgb(
        (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    // kept so older call sites still work
    public static FlatButton Button(string text, EventHandler click, bool primary = false)
    {
        var b = new FlatButton { Text = text, Kind = primary ? ButtonKind.Primary : ButtonKind.Secondary, Height = 34 };
        b.Click += click;
        return b;
    }

    public static Label Caption(string text) => new() { Text = text, Dock = DockStyle.Top, Height = 24, Padding = new Padding(0, 6, 0, 0), ForeColor = Theme.Dim, Font = Theme.Small };

    // dark title bar, works on win10 20h1+ and win11
    public static void DarkTitle(nint hwnd)
    {
        int on = 1;
        if (DwmSetWindowAttribute(hwnd, 20, ref on, 4) != 0) DwmSetWindowAttribute(hwnd, 19, ref on, 4);
    }

    // dark scrollbars on lists and text boxes. the allow dark calls are uxtheme ordinals windows itself
    // uses for explorer, theyve been there since 1809. if theyre ever gone it js stays light
    static bool darkAllowed;
    public static void DarkScroll(Control c)
    {
        void Apply()
        {
            try
            {
                if (!darkAllowed) { SetPreferredAppMode(1); darkAllowed = true; }
                AllowDarkModeForWindow(c.Handle, true);
            }
            catch { }
            SetWindowTheme(c.Handle, "DarkMode_Explorer", null);
        }
        if (c.IsHandleCreated) Apply(); else c.HandleCreated += (_, _) => Apply();
    }

    public static ContextMenuStrip Menu()
    {
        var m = new ContextMenuStrip { Renderer = new MenuRenderer(), BackColor = Panel, ForeColor = Text, Font = Body, ShowImageMargin = false, Padding = new Padding(2, 4, 2, 4) };
        m.ItemAdded += (_, e) => { e.Item.ForeColor = Text; e.Item.Padding = new Padding(4, 3, 4, 3); };
        return m;
    }

    sealed class MenuRenderer : ToolStripProfessionalRenderer
    {
        public MenuRenderer() : base(new MenuColors()) { RoundedEdges = false; }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = !e.Item.Enabled ? Faint : Text;
            base.OnRenderItemText(e);
        }
        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e) { e.ArrowColor = Dim; base.OnRenderArrow(e); }
        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var r = new Rectangle(e.ImageRectangle.X - 2, e.ImageRectangle.Y - 2, e.ImageRectangle.Width + 4, e.ImageRectangle.Height + 4);
            DrawGlyph(e.Graphics, Glyph.Check, 9, Accent, r);
        }
    }

    sealed class MenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Panel;
        public override Color MenuBorder => Line;
        public override Color MenuItemBorder => Raised;
        public override Color MenuItemSelected => Raised;
        public override Color MenuItemSelectedGradientBegin => Raised;
        public override Color MenuItemSelectedGradientEnd => Raised;
        public override Color ImageMarginGradientBegin => Panel;
        public override Color ImageMarginGradientMiddle => Panel;
        public override Color ImageMarginGradientEnd => Panel;
        public override Color SeparatorDark => Line;
        public override Color SeparatorLight => Panel;
        public override Color CheckBackground => Panel;
        public override Color CheckSelectedBackground => Raised;
        public override Color CheckPressedBackground => Raised;
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(nint h, int attr, ref int value, int size);
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(nint h, string? app, string? idList);
    [DllImport("uxtheme.dll", EntryPoint = "#133")] static extern bool AllowDarkModeForWindow(nint h, bool allow);
    [DllImport("uxtheme.dll", EntryPoint = "#135")] static extern int SetPreferredAppMode(int mode);
}

// every mutual window starts from this. dark colors, dark title bar
class DarkForm : Form
{
    public DarkForm()
    {
        BackColor = Theme.Back; ForeColor = Theme.Text; Font = Theme.Body;
        Icon = Program.AppIcon;
        AutoScaleMode = AutoScaleMode.None;
    }

    protected void Dialog()
    {
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.DarkTitle(Handle);
    }

    // the bottom strip with the buttons, right to left so the main one ends up on the right
    protected static Panel ButtonBar(params Control[] rightToLeft)
    {
        var bar = new Panel { Dock = DockStyle.Bottom, Height = 64, Padding = new Padding(20, 14, 20, 16) };
        bar.Paint += (_, e) => { using var p = new Pen(Theme.Line); e.Graphics.DrawLine(p, 0, 0, bar.Width, 0); };
        foreach (var c in rightToLeft)
        {
            c.Dock = DockStyle.Right;
            bar.Controls.Add(c);
            bar.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 8 });
        }
        return bar;
    }
}

enum ButtonKind { Primary, Secondary, Ghost, Danger }

// rounded flat button with hover and press. can carry an icon glyph in front of the text
class FlatButton : Button
{
    bool hover, down;
    ButtonKind kind = ButtonKind.Secondary;
    public ButtonKind Kind { get => kind; set { kind = value; Invalidate(); } }
    public string? Glyph { get; set; }
    public int Radius { get; set; } = 6;

    public FlatButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Font = Theme.Body;
        Cursor = Cursors.Hand;
        Height = 34;
        Width = 100;
    }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Cursor = Enabled ? Cursors.Hand : Cursors.Default; base.OnEnabledChanged(e); }

    protected (Color fill, Color text, Color border) Colors()
    {
        if (!Enabled) return (kind == ButtonKind.Ghost ? Parent?.BackColor ?? Theme.Back : Theme.Panel, Theme.Faint, kind == ButtonKind.Ghost ? Color.Empty : Theme.Line);
        return kind switch
        {
            ButtonKind.Primary => (down ? Theme.AccentDown : hover ? Theme.AccentHover : Theme.Accent, Color.FromArgb(20, 14, 6), Color.Empty),
            ButtonKind.Danger => (down ? Theme.Mix(Theme.Bad, Color.Black, .15f) : hover ? Theme.Mix(Theme.Bad, Color.White, .12f) : Theme.Bad, Color.FromArgb(24, 8, 8), Color.Empty),
            ButtonKind.Ghost => (down ? Theme.Line : hover ? Theme.Raised : Parent?.BackColor ?? Theme.Back, hover ? Theme.Text : Theme.Dim, Color.Empty),
            _ => (down ? Theme.Line : hover ? Theme.Raised : Theme.Panel, Theme.Text, hover ? Theme.Faint : Theme.Line),
        };
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Back);
        var (fill, text, border) = Colors();
        var r = new RectangleF(0, 0, Width - 1, Height - 1);
        Theme.FillRound(g, fill, r, Radius);
        if (border != Color.Empty) Theme.DrawRound(g, border, new RectangleF(0, 0, Width, Height), Radius);
        if (Focused && ShowFocusCues) Theme.DrawRound(g, Theme.Accent, new RectangleF(0, 0, Width, Height), Radius, 2);
        DrawContent(g, text);
    }

    protected virtual void DrawContent(Graphics g, Color text)
    {
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine;
        if (Glyph == null) { TextRenderer.DrawText(g, Text, Font, new Rectangle(6, 0, Width - 12, Height), text, flags | TextFormatFlags.HorizontalCenter); return; }
        // glyph + text centered together
        int tw = string.IsNullOrEmpty(Text) ? 0 : TextRenderer.MeasureText(g, Text, Font, Size.Empty, TextFormatFlags.NoPadding).Width;
        int gw = 16, gap = tw > 0 ? 8 : 0, total = gw + gap + tw, x = Math.Max(6, (Width - total) / 2);
        Theme.DrawGlyph(g, Glyph, 10.5f, text, new Rectangle(x, 0, gw, Height));
        if (tw > 0) TextRenderer.DrawText(g, Text, Font, new Rectangle(x + gw + gap, 0, Width - x - gw - gap - 4, Height), text, flags | TextFormatFlags.NoPadding);
    }
}

// a big button with an icon, a title and a line under it. the main window's actions
sealed class Tile : FlatButton
{
    public string Subtitle { get; set; } = "";
    public bool Highlight { get; set; }
    public Color? GlyphColor { get; set; }

    public Tile() { Radius = 10; Height = 76; TextAlign = ContentAlignment.MiddleLeft; }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Back);
        var (fill, text, border) = Colors();
        if (Kind == ButtonKind.Primary) { fill = Theme.Panel; text = Theme.Text; border = Theme.Line; }
        if (Enabled && ClientRectangle.Contains(PointToClient(MousePosition))) { fill = Theme.Raised; border = Theme.Faint; }
        if (Highlight) { border = Theme.Accent; fill = Theme.AccentSoft; }
        Theme.FillRound(g, fill, new RectangleF(0, 0, Width - 1, Height - 1), Radius);
        Theme.DrawRound(g, border, new RectangleF(0, 0, Width, Height), Radius, Highlight ? 2 : 1);
        if (Focused && ShowFocusCues) Theme.DrawRound(g, Theme.Accent, new RectangleF(0, 0, Width, Height), Radius, 2);

        // icon in a soft circle
        var accent = GlyphColor ?? (Kind == ButtonKind.Primary ? Theme.Accent : Theme.Dim);
        if (!Enabled) accent = Theme.Faint;
        var circle = new Rectangle(14, (Height - 40) / 2, 40, 40);
        Theme.FillRound(g, Kind == ButtonKind.Primary && Enabled ? Theme.AccentSoft : Theme.Raised, circle, 20);
        Theme.DrawGlyph(g, Glyph ?? "", 14, accent, circle);

        int x = circle.Right + 12, w = Width - x - 10;
        var flags = TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
        TextRenderer.DrawText(g, Text, Theme.Heading, new Rectangle(x, Height / 2 - 19, w, 20), Enabled ? Theme.Text : Theme.Faint, flags);
        TextRenderer.DrawText(g, Subtitle, Theme.Small, new Rectangle(x, Height / 2 + 2, w, 34), Enabled ? Theme.Dim : Theme.Faint, TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
    }
}

// on/off switch with a title and an optional line of explanation under it
sealed class Switch : CheckBox
{
    public string Description { get; set; } = "";
    bool hover;

    public Switch()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        AutoSize = false;
        Cursor = Cursors.Hand;
        Height = 46;
        Dock = DockStyle.Top;
    }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Back);
        // the switch itself on the right
        var track = new RectangleF(Width - 44, 4, 40, 20);
        var on = Checked;
        Theme.FillRound(g, !Enabled ? Theme.Raised : on ? (hover ? Theme.AccentHover : Theme.Accent) : (hover ? Theme.Line : Theme.Raised), track, 10);
        if (!on) Theme.DrawRound(g, Theme.Faint, track, 10);
        var knob = new RectangleF(on ? track.Right - 17 : track.X + 3, track.Y + 3, 14, 14);
        Theme.FillRound(g, on ? Color.FromArgb(24, 16, 6) : Theme.Dim, knob, 7);
        if (Focused && ShowFocusCues) Theme.DrawRound(g, Theme.Accent, new RectangleF(track.X - 2, track.Y - 2, track.Width + 4, track.Height + 4), 12);

        int w = Width - 60;
        TextRenderer.DrawText(g, Text, Theme.Body, new Rectangle(0, 4, w, 20), Enabled ? Theme.Text : Theme.Faint, TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter);
        if (Description.Length > 0)
            TextRenderer.DrawText(g, Description, Theme.Small, new Rectangle(0, 26, w, Height - 26), Theme.Dim, TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
    }
}

// rounded panel. contents go inside the padding
class Card : Panel
{
    public Color Fill { get; set; } = Theme.Panel;
    public Color Border { get; set; } = Theme.Line;
    public int Radius { get; set; } = 10;

    public Card()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        BackColor = Theme.Panel;
        Padding = new Padding(16);
    }

    // children see the fill as the background so their own edges blend in
    public override Color BackColor { get => Fill; set => Fill = value; }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Back);
        Theme.FillRound(g, Fill, new RectangleF(0, 0, Width - 1, Height - 1), Radius);
        if (Border != Color.Empty) Theme.DrawRound(g, Border, new RectangleF(0, 0, Width, Height), Radius);
    }
}

// a text box in a rounded frame that lights up when its focused
sealed class Field : Panel
{
    public TextBox Box { get; }

    public Field(bool multiline = false, bool mono = false)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        BackColor = Theme.Back;
        Padding = new Padding(10, multiline ? 8 : 8, 6, multiline ? 6 : 4);
        Height = multiline ? 74 : 34;
        Box = new TextBox
        {
            BorderStyle = BorderStyle.None, BackColor = Theme.Back, ForeColor = Theme.Text, Dock = DockStyle.Fill,
            Multiline = multiline, Font = mono ? Theme.Mono : Theme.Body,
        };
        if (multiline) { Box.ScrollBars = ScrollBars.Vertical; Theme.DarkScroll(Box); }
        Box.GotFocus += (_, _) => Invalidate();
        Box.LostFocus += (_, _) => Invalidate();
        Controls.Add(Box);
        Cursor = Cursors.IBeam;
        Click += (_, _) => Box.Focus();
    }

    // an icon in front of the text, like the search one
    public Field WithIcon(string glyph)
    {
        var icon = new Label { Text = glyph, Font = Theme.Icons(10), ForeColor = Theme.Faint, Dock = DockStyle.Left, Width = 24, TextAlign = ContentAlignment.TopLeft, Padding = new Padding(0, 2, 0, 0), BackColor = Theme.Back };
        icon.Click += (_, _) => Box.Focus();
        Controls.Add(icon);
        return this;
    }

    // a little button inside the right end, for things like a dropdown
    public FlatButton AddButton(string glyph, Action click)
    {
        var b = new FlatButton { Kind = ButtonKind.Ghost, Glyph = glyph, Dock = DockStyle.Right, Width = 26, Radius = 4, TabStop = false };
        b.Click += (_, _) => click();
        Padding = new Padding(Padding.Left, 3, 3, 3);
        Box.Margin = new Padding(0);
        // keep the text where it was now that the padding changed
        var pad = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 5, 0, 0), BackColor = Theme.Back };
        Controls.Remove(Box);
        pad.Controls.Add(Box);
        Controls.Add(pad);
        Controls.Add(b);
        pad.BringToFront();
        return b;
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? Theme.Panel);
        Theme.FillRound(e.Graphics, Theme.Back, new RectangleF(0, 0, Width - 1, Height - 1), 6);
        Theme.DrawRound(e.Graphics, Box.Focused ? Theme.Accent : Theme.Line, new RectangleF(0, 0, Width, Height), 6, Box.Focused ? 1.5f : 1);
    }
}

// circle with their first letter, ring shows if theyre online
sealed class Avatar : Control
{
    public string Who { get; set; } = "";
    public Color Ring { get; set; } = Theme.Offline;

    public Avatar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        Size = new Size(52, 52);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Back);
        var r = new RectangleF(0, 0, Width - 1, Height - 1);
        Theme.DrawRound(g, Ring, new RectangleF(0, 0, Width, Height), Width / 2f, 2.5f);
        var inner = RectangleF.Inflate(r, -5, -5);
        Theme.FillRound(g, Who.Length == 0 ? Theme.Raised : Theme.Mix(Hue(Who), Theme.Back, .55f), inner, inner.Width / 2);
        var letter = Who.Length == 0 ? "?" : char.ToUpper(Who.TrimStart()[0]).ToString();
        using var f = new Font("Segoe UI Semibold", Width * .3f);
        TextRenderer.DrawText(g, letter, f, Rectangle.Round(inner), Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    // same name always gets the same color
    static Color Hue(string s)
    {
        var palette = new[] { Color.FromArgb(255, 140, 26), Color.FromArgb(90, 160, 255), Color.FromArgb(180, 110, 255), Color.FromArgb(70, 200, 170), Color.FromArgb(255, 95, 140), Color.FromArgb(230, 200, 70) };
        int h = 0; foreach (var c in s.ToLowerInvariant()) h = h * 31 + c;
        return palette[Math.Abs(h % palette.Length)];
    }
}

// progress bar. with no value yet it does the moving stripe
sealed class Meter : Control
{
    readonly System.Windows.Forms.Timer anim = new() { Interval = 16 };
    float value = -1, phase;
    public Color Bar { get; set; } = Theme.Accent;

    public Meter()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        Height = 8;
        anim.Tick += (_, _) => { phase = (phase + .012f) % 1.4f; Invalidate(); };
        anim.Start();
    }

    // 0..1, or below 0 for "waiting"
    public float Value
    {
        get => value;
        set { this.value = value; if (value >= 0) anim.Stop(); else anim.Start(); Invalidate(); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Back);
        var r = new RectangleF(0, 0, Width - 1, Height - 1);
        Theme.FillRound(g, Theme.Raised, r, Height / 2f);
        if (value >= 0) { if (value > 0) Theme.FillRound(g, Bar, new RectangleF(0, 0, Math.Max(Height, r.Width * Math.Min(1, value)), r.Height), Height / 2f); return; }
        float w = r.Width * .3f, x = (phase - .3f) * r.Width;
        g.SetClip(new RectangleF(0, 0, Width, Height));
        Theme.FillRound(g, Theme.Mix(Bar, Theme.Raised, .35f), new RectangleF(x, 0, w, r.Height), Height / 2f);
    }

    protected override void Dispose(bool disposing) { if (disposing) anim.Dispose(); base.Dispose(disposing); }
}

// a strip across the main window for things that need you: setup, an update, a stream going on
sealed class Banner : Card
{
    readonly Label text = new() { Dock = DockStyle.Fill, Font = Theme.Small, ForeColor = Theme.Text, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(4, 0, 8, 0) };
    readonly Label icon = new() { Dock = DockStyle.Left, Width = 28, Font = Theme.Icons(12), TextAlign = ContentAlignment.MiddleLeft, UseMnemonic = false };
    readonly FlowLayoutPanel buttons = new() { Dock = DockStyle.Right, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Padding = new Padding(0) };

    public Banner()
    {
        Dock = DockStyle.Top;
        Height = 58;
        Radius = 8;
        Padding = new Padding(12, 11, 11, 11);
        Controls.Add(text);
        Controls.Add(icon);
        Controls.Add(buttons);
        Style(Theme.Glyph.Info, Theme.Accent, Theme.AccentSoft);
    }

    public string Message { get => text.Text; set => text.Text = value; }

    // banners sit in a holder panel for spacing, so showing one means showing the holder
    public bool On { get => Parent?.Visible ?? Visible; set { if (Parent != null) Parent.Visible = value; else Visible = value; } }

    public void ClearButtons() { foreach (Control c in buttons.Controls.Cast<Control>().ToList()) c.Dispose(); }

    public void Style(string glyph, Color tint, Color fill)
    {
        icon.Text = glyph; icon.ForeColor = tint; Fill = fill; Border = Theme.Mix(fill, tint, .35f);
        Invalidate(true);
    }

    public FlatButton AddButton(string label, Action click, ButtonKind kind = ButtonKind.Primary)
    {
        var b = new FlatButton { Text = label, Kind = kind, Height = 34, Margin = new Padding(6, 0, 0, 0) };
        b.Width = Math.Max(78, TextRenderer.MeasureText(label, b.Font).Width + 26);
        b.Click += (_, _) => click();
        buttons.Controls.Add(b);
        return b;
    }
}

// numbered steps, a little circle with the number then the line
sealed class StepList : Control
{
    readonly string[] steps;
    public int Done { get; set; }

    public StepList(string[] steps)
    {
        this.steps = steps;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Panel);
        int row = Math.Max(24, Height / Math.Max(1, steps.Length));
        for (int i = 0; i < steps.Length; i++)
        {
            var circle = new Rectangle(0, i * row + (row - 22) / 2, 22, 22);
            bool done = i < Done;
            Theme.FillRound(g, done ? Theme.Online : Theme.AccentSoft, circle, 11);
            if (done) Theme.DrawGlyph(g, Theme.Glyph.Check, 8, Theme.Back, circle);
            else TextRenderer.DrawText(g, (i + 1).ToString(), Theme.SmallBold, circle, Theme.Accent, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, steps[i], Theme.Small, new Rectangle(32, i * row, Width - 32, row), done ? Theme.Dim : Theme.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        }
    }
}
