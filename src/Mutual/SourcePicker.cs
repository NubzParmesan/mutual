using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Mutual.Stream;

namespace Mutual;

// what to share. whole screen, one window or a box
sealed class SourcePicker : DarkForm
{
    public StreamSource? Chosen { get; private set; }
    readonly ListBox list = new() { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, IntegralHeight = false, DrawMode = DrawMode.OwnerDrawVariable };
    readonly Field search = new();
    readonly List<Choice> all = new();
    readonly FlatButton go;
    int hover = -1;

    sealed record Choice(string Section, string Title, string Sub, string Glyph, Func<StreamSource> Make, Icon? AppIcon = null);
    sealed record Heading(string Text);

    public SourcePicker()
    {
        Dialog();
        Text = "Share what?";
        ClientSize = new Size(520, 560);
        list.BackColor = Theme.Panel; list.ForeColor = Theme.Text;
        Theme.DarkScroll(list);

        foreach (var m in DesktopCapture.Monitors())
            all.Add(new Choice("Screens", "Whole screen" + (m.Bounds.Location == Point.Empty ? " (main)" : ""), m.Name.Replace(@"\\.\", "") + "  ·  " + m.Bounds.Width + " x " + m.Bounds.Height + "  ·  they see everything on it", Theme.Glyph.Screen, () => new ScreenSource(m)));
        all.Add(new Choice("Screens", "Custom box", "Drag and resize it, even while streaming", Theme.Glyph.Box, () =>
        {
            var area = Screen.PrimaryScreen!.Bounds;
            return new BoxSource(new Rectangle(area.X + area.Width / 2, area.Y, area.Width / 2, area.Height));
        }));
        if (SplitColonySource.GameRunning())
            all.Add(new Choice("Games", "RimWorld split", "Just the right half. Their mouse and keys go to the mod, you keep yours", Theme.Glyph.Game, () => new SplitColonySource()));
        foreach (var w in WindowSource.List().OrderBy(w => w.Title))
        {
            var (app, icon) = AppFor(w.Handle);
            all.Add(new Choice("Windows", w.Title, app, Theme.Glyph.Window, () => w, icon));
        }

        search.WithIcon(Theme.Glyph.Search);
        search.Box.PlaceholderText = "Search windows";
        search.Dock = DockStyle.Top;
        search.Box.TextChanged += (_, _) => Fill();
        search.Box.KeyDown += (_, e) =>
        {
            // arrows from the search box move the list
            if (e.KeyCode is Keys.Down or Keys.Up) { Step(e.KeyCode == Keys.Down ? 1 : -1); e.Handled = true; }
        };

        go = new FlatButton { Text = "Share", Kind = ButtonKind.Primary, Width = 96 };
        go.Click += (_, _) => Pick();
        var cancel = new FlatButton { Text = "Cancel", Width = 90 };
        cancel.Click += (_, _) => Close();
        AcceptButton = go; CancelButton = cancel;
        var hint = new Label { Text = "Nobody can control anything until you say so", ForeColor = Theme.Faint, Font = Theme.Small, AutoSize = true, Dock = DockStyle.Left, Padding = new Padding(0, 9, 0, 0) };
        var bar = ButtonBar(go, cancel);
        bar.Controls.Add(hint);

        var card = new Card { Dock = DockStyle.Fill, Padding = new Padding(2, 6, 2, 6) };
        card.Controls.Add(list);
        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20, 16, 20, 16) };
        body.Controls.Add(card);
        body.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 12 });
        body.Controls.Add(search);
        Controls.Add(body);
        Controls.Add(bar);

        list.MeasureItem += (_, e) => e.ItemHeight = list.Items[e.Index] is Heading ? (e.Index == 0 ? 28 : 38) : 48;
        list.DrawItem += DrawItem;
        list.DoubleClick += (_, _) => { if (list.SelectedItem is Choice) Pick(); };
        list.SelectedIndexChanged += (_, _) => { if (list.SelectedItem is Heading) Step(1); go.Enabled = list.SelectedItem is Choice; };
        list.MouseMove += (_, e) => { int i = list.IndexFromPoint(e.Location); if (i != hover) { hover = i; list.Invalidate(); } };
        list.MouseLeave += (_, _) => { hover = -1; list.Invalidate(); };
        Fill();
        Shown += (_, _) => list.Focus();
    }

    void Fill()
    {
        var q = search.Box.Text.Trim();
        var keep = list.SelectedItem as Choice;
        list.BeginUpdate();
        list.Items.Clear();
        string? section = null;
        foreach (var c in all)
        {
            if (q.Length > 0 && !(c.Title.Contains(q, StringComparison.OrdinalIgnoreCase) || c.Sub.Contains(q, StringComparison.OrdinalIgnoreCase))) continue;
            if (c.Section != section) { list.Items.Add(new Heading(c.Section.ToUpper())); section = c.Section; }
            list.Items.Add(c);
        }
        list.EndUpdate();
        int i = keep != null ? list.Items.IndexOf(keep) : -1;
        if (i < 0) i = Enumerable.Range(0, list.Items.Count).FirstOrDefault(k => list.Items[k] is Choice, -1);
        list.SelectedIndex = i;
        go.Enabled = i >= 0;
    }

    // next choice up or down, skipping the headings
    void Step(int dir)
    {
        for (int i = list.SelectedIndex + dir; i >= 0 && i < list.Items.Count; i += dir)
            if (list.Items[i] is Choice) { list.SelectedIndex = i; return; }
    }

    void DrawItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        var g = e.Graphics; var b = e.Bounds;
        using (var back = new SolidBrush(Theme.Panel)) g.FillRectangle(back, b);
        if (list.Items[e.Index] is Heading h)
        {
            TextRenderer.DrawText(g, h.Text, Theme.SmallBold, new Rectangle(b.X + 16, b.Bottom - 22, b.Width - 32, 18), Theme.Faint, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            return;
        }
        var c = (Choice)list.Items[e.Index];
        bool selected = (e.State & DrawItemState.Selected) != 0;
        var row = new RectangleF(b.X + 6, b.Y + 2, b.Width - 13, b.Height - 4);
        if (selected) { Theme.FillRound(g, Theme.AccentSoft, row, 8); Theme.DrawRound(g, Theme.Mix(Theme.AccentSoft, Theme.Accent, .5f), row, 8); }
        else if (e.Index == hover) Theme.FillRound(g, Theme.Raised, row, 8);
        var iconBox = new Rectangle(b.X + 16, b.Y + (b.Height - 30) / 2, 30, 30);
        if (c.AppIcon != null) g.DrawIcon(c.AppIcon, new Rectangle(iconBox.X + 3, iconBox.Y + 3, 24, 24));
        else
        {
            Theme.FillRound(g, selected ? Theme.Mix(Theme.AccentSoft, Theme.Accent, .25f) : Theme.Raised, iconBox, 7);
            Theme.DrawGlyph(g, c.Glyph, 11, selected ? Theme.Accent : Theme.Dim, iconBox);
        }
        int x = iconBox.Right + 12, w = b.Right - x - 14;
        TextRenderer.DrawText(g, c.Title, Theme.Body, new Rectangle(x, b.Y + 6, w, 20), Theme.Text, TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, c.Sub, Theme.Small, new Rectangle(x, b.Y + 26, w, 18), Theme.Dim, TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
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
        if (list.SelectedItem is not Choice c) return;
        try { Chosen = c.Make(); }
        catch (SocketException) { Prompt.Show(this, "Something else (probably DuoCursor) is already talking to the SplitColony mod. Close it and try again.", "Mutual"); return; }
        DialogResult = DialogResult.OK;
        Close();
    }

    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint h, out uint pid);
}
