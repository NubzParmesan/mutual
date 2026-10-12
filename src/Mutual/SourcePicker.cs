using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Mutual.Stream;

namespace Mutual;

// what to share. whole screen, one window or a box. a grid of cards with a little picture of each
// so you can tell your three chrome windows apart
sealed class SourcePicker : DarkForm
{
    public StreamSource? Chosen { get; private set; }
    readonly Panel grid = new() { Dock = DockStyle.Fill, AutoScroll = true };
    readonly Field search = new();
    readonly List<SourceCard> cards = new();
    readonly List<Label> headings = new();
    readonly FlatButton go;
    readonly CancellationTokenSource closing = new();
    SourceCard? selected;

    public sealed record Choice(string Section, string Title, string Sub, string Glyph, Func<StreamSource> Make, Icon? AppIcon = null, nint Window = 0, Rectangle? Area = null, bool IsBox = false);

    public SourcePicker()
    {
        Dialog();
        Text = "Share what?";
        ClientSize = new Size(580, 640);
        grid.BackColor = Theme.Panel;
        Theme.DarkScroll(grid);

        var all = new List<Choice>();
        foreach (var m in DesktopCapture.Monitors())
            all.Add(new Choice("Screens", "Whole screen" + (m.Bounds.Location == Point.Empty ? " (main)" : ""), m.Bounds.Width + " x " + m.Bounds.Height + ", they see everything on it", Theme.Glyph.Screen, () => new ScreenSource(m), Area: m.Bounds));
        var main = Screen.PrimaryScreen!.Bounds;
        all.Add(new Choice("Screens", "Custom box", "Drag and resize it, even mid stream", Theme.Glyph.Box, () => new BoxSource(new Rectangle(main.X + main.Width / 2, main.Y, main.Width / 2, main.Height)), Area: main, IsBox: true));
        if (SplitColonySource.GameRunning())
            all.Add(new Choice("Games", "RimWorld split", "Right half only, their input goes to the mod", Theme.Glyph.Game, () => new SplitColonySource()));
        foreach (var w in WindowSource.List().OrderBy(w => w.Title))
        {
            var (app, icon) = AppFor(w.Handle);
            all.Add(new Choice("Windows", w.Title, app, Theme.Glyph.Window, () => w, icon, w.Handle));
        }

        string? section = null;
        foreach (var c in all)
        {
            if (c.Section != section)
            {
                section = c.Section;
                var h = new Label { Text = section.ToUpper(), Font = Theme.SmallBold, ForeColor = Theme.Faint, Height = 30, Padding = new Padding(4, 12, 0, 0), Margin = new Padding(0), Tag = section };
                headings.Add(h);
                grid.Controls.Add(h);
            }
            var card = new SourceCard(c) { Margin = new Padding(4) };
            card.Click += (_, _) => Select(card);
            card.DoubleClick += (_, _) => { Select(card); Pick(); };
            cards.Add(card);
            grid.Controls.Add(card);
        }
        grid.Resize += (_, _) => Arrange();

        search.WithIcon(Theme.Glyph.Search);
        search.Box.PlaceholderText = "Search windows";
        search.Dock = DockStyle.Top;
        search.Box.TextChanged += (_, _) => Filter();

        go = new FlatButton { Text = "Share", Kind = ButtonKind.Primary, Width = 96 };
        go.Click += (_, _) => Pick();
        var cancel = new FlatButton { Text = "Cancel", Width = 90 };
        cancel.Click += (_, _) => Close();
        AcceptButton = go; CancelButton = cancel;
        var hint = new Label { Text = "They can only watch until you let them control", ForeColor = Theme.Faint, Font = Theme.Small, AutoSize = true, Dock = DockStyle.Left, Padding = new Padding(0, 9, 0, 0) };
        var bar = ButtonBar(go, cancel);
        bar.Controls.Add(hint);

        var frame = new Card { Dock = DockStyle.Fill, Padding = new Padding(2, 2, 2, 2) };
        frame.Controls.Add(grid);
        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20, 16, 20, 16) };
        body.Controls.Add(frame);
        body.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 12 });
        body.Controls.Add(search);
        Controls.Add(body);
        Controls.Add(bar);

        if (cards.Count > 0) Select(cards[0]);
        Shown += (_, _) => { Arrange(); search.Box.Focus(); _ = LoadPictures(closing.Token); };
        FormClosed += (_, _) => { closing.Cancel(); foreach (var c in cards) c.Thumb?.Dispose(); };
    }

    // lays the cards out in as many columns as fit, each section starting on a new row under its heading
    void Arrange()
    {
        if (cards.Count == 0) return;
        int gap = grid.S(8), pad = grid.S(12), w = cards[0].Width, h = cards[0].Height;
        int width = grid.ClientSize.Width;
        int cols = Math.Max(1, (width - pad * 2 + gap) / (w + gap));
        int left = Math.Max(pad, (width - (cols * w + (cols - 1) * gap)) / 2);
        var scroll = grid.AutoScrollPosition;
        int y = 0;
        grid.SuspendLayout();
        foreach (var head in headings)
        {
            var section = (string)head.Tag!;
            var mine = cards.Where(c => c.Visible && c.Choice.Section == section).ToList();
            head.Visible = mine.Count > 0;
            if (mine.Count == 0) continue;
            head.SetBounds(left, y + scroll.Y, width - left * 2, head.Height);
            y += head.Height;
            for (int i = 0; i < mine.Count; i++)
                mine[i].Location = new Point(left + i % cols * (w + gap), y + scroll.Y + i / cols * (h + gap));
            y += (mine.Count + cols - 1) / cols * (h + gap);
        }
        grid.AutoScrollMinSize = new Size(0, y + pad);
        grid.ResumeLayout();
    }

    void Select(SourceCard c)
    {
        if (selected != null) { selected.Selected = false; selected.Invalidate(); }
        selected = c; c.Selected = true; c.Invalidate();
        grid.ScrollControlIntoView(c);
        go.Enabled = true;
    }

    void Filter()
    {
        var q = search.Box.Text.Trim();
        grid.SuspendLayout();
        foreach (var c in cards)
            c.Visible = q.Length == 0 || c.Choice.Title.Contains(q, StringComparison.OrdinalIgnoreCase) || c.Choice.Sub.Contains(q, StringComparison.OrdinalIgnoreCase);
        grid.ResumeLayout();
        Arrange();
        if (selected is not { Visible: true }) { var first = cards.FirstOrDefault(c => c.Visible); if (first != null) Select(first); else { selected = null; go.Enabled = false; } }
    }

    // arrows move between cards by where they are on screen, enter shares
    protected override bool ProcessCmdKey(ref Message msg, Keys key)
    {
        if (key is Keys.Left or Keys.Right or Keys.Up or Keys.Down && selected != null && !(search.Box.Focused && key is Keys.Left or Keys.Right))
        {
            var from = selected.Bounds;
            var next = cards.Where(c => c.Visible && c != selected).Select(c => (c, b: c.Bounds)).Where(t => key switch
            {
                Keys.Left => t.b.Right <= from.Left && Math.Abs(t.b.Top - from.Top) < from.Height / 2,
                Keys.Right => t.b.Left >= from.Right && Math.Abs(t.b.Top - from.Top) < from.Height / 2,
                Keys.Up => t.b.Bottom <= from.Top,
                _ => t.b.Top >= from.Bottom,
            }).OrderBy(t => Math.Abs(t.b.Top - from.Top) * 4 + Math.Abs(t.b.Left - from.Left)).Select(t => t.c).FirstOrDefault();
            if (next != null) Select(next);
            return true;
        }
        return base.ProcessCmdKey(ref msg, key);
    }

    // pictures come in one at a time off the ui thread, the cards show an icon till theirs arrives
    async Task LoadPictures(CancellationToken ct)
    {
        foreach (var card in cards.ToList())
        {
            if (ct.IsCancellationRequested) return;
            var c = card.Choice;
            Bitmap? pic = null;
            try { pic = await Task.Run(() => c.Window != 0 ? WindowPicture(c.Window) : c.Area is { } a ? ScreenPicture(a, c.IsBox) : null, ct); }
            catch { }
            if (pic == null || ct.IsCancellationRequested || card.IsDisposed) { pic?.Dispose(); continue; }
            card.Thumb = pic;
            card.Invalidate();
        }
    }

    const int ThumbW = 440;

    static Bitmap? ScreenPicture(Rectangle area, bool box)
    {
        using var full = new Bitmap(area.Width, area.Height);
        using (var g = Graphics.FromImage(full)) g.CopyFromScreen(area.Location, Point.Empty, area.Size);
        var small = Shrink(full);
        if (box)
        {
            // show where the box starts out
            using var g = Graphics.FromImage(small);
            using var dim = new SolidBrush(Color.FromArgb(150, 0, 0, 0));
            g.FillRectangle(dim, 0, 0, small.Width / 2, small.Height);
            using var pen = new Pen(Theme.Accent, 3);
            g.DrawRectangle(pen, small.Width / 2 + 1, 1, small.Width / 2 - 3, small.Height - 3);
        }
        return small;
    }

    static Bitmap? WindowPicture(nint hwnd)
    {
        if (!GetWindowRect(hwnd, out var r) || r.R - r.L < 16 || r.B - r.T < 16) return null;
        using var full = new Bitmap(r.R - r.L, r.B - r.T);
        using (var g = Graphics.FromImage(full))
        {
            var dc = g.GetHdc();
            bool ok = PrintWindow(hwnd, dc, 2);
            g.ReleaseHdc(dc);
            if (!ok) return null;
        }
        // games drawn straight to the gpu often come back black, an icon is better than a black box
        if (MostlyBlack(full)) return null;
        return Shrink(full);
    }

    static Bitmap Shrink(Bitmap full)
    {
        float s = Math.Min(1, ThumbW / (float)full.Width);
        var small = new Bitmap(Math.Max(1, (int)(full.Width * s)), Math.Max(1, (int)(full.Height * s)));
        using var g = Graphics.FromImage(small);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(full, 0, 0, small.Width, small.Height);
        return small;
    }

    static bool MostlyBlack(Bitmap b)
    {
        int dark = 0, n = 0;
        for (int y = b.Height / 8; y < b.Height; y += Math.Max(1, b.Height / 12))
            for (int x = b.Width / 8; x < b.Width; x += Math.Max(1, b.Width / 12), n++)
                if (b.GetPixel(x, y) is var p && p.R + p.G + p.B < 12) dark++;
        return n > 0 && dark > n * 9 / 10;
    }

    // the program behind a window, for the second line and its icon
    static (string app, Icon? icon) AppFor(nint hwnd)
    {
        try
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            using var p = Process.GetProcessById((int)pid);
            string? file = null;
            try { file = p.MainModule?.FileName; } catch { }
            string app = file != null ? FileVersionInfo.GetVersionInfo(file).FileDescription is { Length: > 0 } d ? d : p.ProcessName : p.ProcessName;
            Icon? icon = null;
            try { if (file != null) icon = Icon.ExtractAssociatedIcon(file); } catch { }
            return (app, icon);
        }
        catch { return ("Window", null); }
    }

    void Pick()
    {
        if (selected == null) return;
        try { Chosen = selected.Choice.Make(); }
        catch (SocketException) { Prompt.Show(this, "Something else (probably DuoCursor) is already talking to the SplitColony mod. Close it and try again.", "Mutual"); return; }
        DialogResult = DialogResult.OK;
        Close();
    }

    // for --snapshot-ui, a couple of made up windows so the picture isnt empty
    public SourcePicker Demo()
    {
        foreach (var (title, app, sec) in new[] { ("RimWorld by Ludeon Studios", "RimWorld", "Windows"), ("YouTube - Opera", "Opera Internet Browser", "Windows"), ("colony notes - Obsidian", "Obsidian", "Windows") })
        {
            var card = new SourceCard(new Choice(sec, title, app, Theme.Glyph.Window, () => throw new InvalidOperationException())) { Margin = new Padding(4) };
            card.Click += (_, _) => Select(card);
            cards.Add(card);
            if (!headings.Any(h => (string)h.Tag! == sec))
            {
                var h = new Label { Text = sec.ToUpper(), Font = Theme.SmallBold, ForeColor = Theme.Faint, Height = 30, Padding = new Padding(4, 12, 0, 0), Margin = new Padding(0), Tag = sec };
                headings.Add(h); grid.Controls.Add(h);
            }
            grid.Controls.Add(card);
        }
        return this;
    }

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [DllImport("user32.dll")] static extern bool GetWindowRect(nint h, out RECT r);
    [DllImport("user32.dll")] static extern bool PrintWindow(nint h, nint dc, uint flags);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint h, out uint pid);
}

// one thing you could share: its picture on top, the name under it
sealed class SourceCard : Control
{
    public SourcePicker.Choice Choice { get; }
    public Bitmap? Thumb { get; set; }
    public bool Selected { get; set; }
    bool hover;

    public SourceCard(SourcePicker.Choice c)
    {
        Choice = c;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.StandardDoubleClick, true);
        Size = new Size(240, 184);
        Cursor = Cursors.Hand;
        var tip = new ToolTip();
        tip.SetToolTip(this, c.Title);
    }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        float k = Theme.K(this);
        g.Clear(Parent?.BackColor ?? Theme.Panel);
        var outer = new RectangleF(0, 0, Width - 1, Height - 1);
        Theme.FillRound(g, Selected ? Theme.AccentSoft : hover ? Theme.Raised : Theme.Panel, outer, 10 * k);
        if (Selected) Theme.DrawRound(g, Theme.Accent, new RectangleF(0, 0, Width, Height), 10 * k, 2 * k);
        else if (hover) Theme.DrawRound(g, Theme.Line, new RectangleF(0, 0, Width, Height), 10 * k);

        // picture, fit inside its slot with the back color around it
        int pad = this.S(8);
        var slot = new Rectangle(pad, pad, Width - pad * 2, this.S(128));
        using (var clip = Theme.Round(slot, 6 * k))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var back = new SolidBrush(Theme.Back)) g.FillPath(back, clip);
            g.SetClip(clip);
            if (Thumb != null)
            {
                float s = Math.Min(slot.Width / (float)Thumb.Width, slot.Height / (float)Thumb.Height);
                int w = (int)(Thumb.Width * s), h = (int)(Thumb.Height * s);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(Thumb, slot.X + (slot.Width - w) / 2, slot.Y + (slot.Height - h) / 2, w, h);
            }
            else if (Choice.AppIcon != null)
            {
                int d = this.S(40);
                g.DrawIcon(Choice.AppIcon, new Rectangle(slot.X + (slot.Width - d) / 2, slot.Y + (slot.Height - d) / 2, d, d));
            }
            else Theme.DrawGlyph(g, Choice.Glyph, 26, Theme.Faint, slot);
            g.ResetClip();
        }

        // name row
        int y = slot.Bottom + this.S(8), x = pad + this.S(2);
        if (Choice.AppIcon != null) { g.DrawIcon(Choice.AppIcon, new Rectangle(x, y + this.S(1), this.S(16), this.S(16))); x += this.S(22); }
        else { Theme.DrawGlyph(g, Choice.Glyph, 9.5f, Selected ? Theme.Accent : Theme.Dim, new Rectangle(x, y, this.S(16), this.S(18))); x += this.S(22); }
        var flags = TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
        TextRenderer.DrawText(g, Choice.Title, Theme.Body, new Rectangle(x, y, Width - x - pad, this.S(18)), Theme.Text, flags);
        TextRenderer.DrawText(g, Choice.Sub, Theme.Small, new Rectangle(pad + this.S(2), y + this.S(20), Width - pad * 2 - this.S(2), this.S(16)), Theme.Dim, flags);
    }
}
