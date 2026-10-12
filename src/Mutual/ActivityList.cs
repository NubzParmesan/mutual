using Mutual.Core;

namespace Mutual;

// the activity feed on the main window. grouped by day, an icon for what kind of thing it was,
// green or red for how it went, long lines wrap instead of getting cut off
sealed class ActivityList : Panel
{
    readonly ListBox list = new() { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, DrawMode = DrawMode.OwnerDrawVariable, IntegralHeight = false, SelectionMode = SelectionMode.None };
    readonly Label empty = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Theme.Faint, Font = Theme.Small, Padding = new Padding(30, 0, 30, 0) };
    List<ActivityRow> rows = new();
    string signature = "";
    int hover = -1;
    int TextX => this.S(44);
    int TimeW => this.S(70);

    record Header(string Text);

    public string Empty { get => empty.Text; set => empty.Text = value; }
    // rows that start with their name keep it as they typed it
    public string PeerName { get; set; } = "";

    public ActivityList()
    {
        list.BackColor = Theme.Panel; list.ForeColor = Theme.Text;
        Theme.DarkScroll(list);
        list.MeasureItem += Measure;
        list.DrawItem += Draw;
        list.MouseMove += (_, e) => { int i = list.IndexFromPoint(e.Location); if (i != hover) { Redraw(hover); hover = i; Redraw(hover); } };
        list.MouseLeave += (_, _) => { Redraw(hover); hover = -1; };
        list.Resize += (_, _) => { if (rows.Count > 0) Rebuild(); };
        Controls.Add(list);
        Controls.Add(empty);
        empty.BringToFront();
    }

    void Redraw(int i) { if (i >= 0 && i < list.Items.Count) list.Invalidate(list.GetItemRectangle(i)); }

    public void SetRows(List<ActivityRow> newest)
    {
        var sig = newest.Count + "|" + (newest.Count > 0 ? newest[0].Time.Ticks + "|" + newest[^1].Time.Ticks : "");
        if (sig == signature) return;
        signature = sig;
        rows = newest;
        Rebuild();
    }

    void Rebuild()
    {
        int top = list.TopIndex;
        list.BeginUpdate();
        list.Items.Clear();
        DateTime? day = null;
        foreach (var r in rows)
        {
            var d = r.Time.ToLocalTime().Date;
            if (d != day) { list.Items.Add(new Header(DayName(d))); day = d; }
            list.Items.Add(r);
        }
        if (top > 0 && top < list.Items.Count) list.TopIndex = top;
        list.EndUpdate();
        empty.Visible = rows.Count == 0;
        list.Visible = rows.Count > 0;
    }

    static string DayName(DateTime d) =>
        d == DateTime.Today ? "TODAY" : d == DateTime.Today.AddDays(-1) ? "YESTERDAY" : d.Year == DateTime.Today.Year ? d.ToString("dddd, MMM d").ToUpper() : d.ToString("MMM d, yyyy").ToUpper();

    // mutuals own rows start with "Mutual: " for the old viewer, no point showing it here
    string Clean(string s)
    {
        if (s.StartsWith("Mutual: ")) s = s[8..];
        if (PeerName.Length > 0 && s.StartsWith(PeerName)) return s;
        return s.Length > 0 ? char.ToUpper(s[0]) + s[1..] : s;
    }

    // guesses the icon from the words, the log only has text
    static string IconFor(ActivityRow r)
    {
        var a = r.Action.ToLowerInvariant();
        if (a.Contains("rimworld")) return Theme.Glyph.Game;
        if (a.Contains("ssh") || a.Contains("peer")) return Theme.Glyph.Terminal;
        if (a.Contains("paired") || a.Contains("pairing")) return Theme.Glyph.People;
        if (a.Contains("update") || a.Contains(" is out")) return Theme.Glyph.Update;
        if (a.Contains("received") || a.Contains("receiving")) return Theme.Glyph.Download;
        if (a.Contains("sent ") || a.Contains("sending") || a.Contains("offered ") && !a.Contains("stream") || a.Contains("dropped")) return Theme.Glyph.Upload;
        if (a.Contains("stream") || a.Contains("watch") || a.Contains("video") || a.Contains("sound")) return Theme.Glyph.Screen;
        if (a.Contains("setup") || a.Contains("firewall")) return Theme.Glyph.Shield;
        return r.Result == ActivityResult.FAILED ? Theme.Glyph.Error : Theme.Glyph.Info;
    }

    void Measure(object? sender, MeasureItemEventArgs e)
    {
        if (list.Items[e.Index] is Header) { e.ItemHeight = this.S(e.Index == 0 ? 26 : 34); return; }
        var r = (ActivityRow)list.Items[e.Index];
        int w = Math.Max(this.S(80), list.ClientSize.Width - TextX - TimeW);
        int h = TextRenderer.MeasureText(Clean(r.Action), Theme.Body, new Size(w, 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;
        e.ItemHeight = Math.Min(255, Math.Max(this.S(32), Math.Min(h, 3 * Theme.Body.Height) + this.S(14)));
    }

    void Draw(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= list.Items.Count) return;
        var g = e.Graphics;
        var b = e.Bounds;
        using (var back = new SolidBrush(Theme.Panel)) g.FillRectangle(back, b);
        if (list.Items[e.Index] is Header h)
        {
            TextRenderer.DrawText(g, h.Text, Theme.SmallBold, new Rectangle(b.X + this.S(16), b.Bottom - this.S(22), b.Width - this.S(32), this.S(18)), Theme.Faint, TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            return;
        }
        var r = (ActivityRow)list.Items[e.Index];
        if (e.Index == hover) Theme.FillRound(g, Theme.Raised, new RectangleF(b.X + this.S(6), b.Y + 1, b.Width - this.S(13), b.Height - 2), this.S(6));
        var tint = r.Result switch { ActivityResult.OK => Theme.Online, ActivityResult.FAILED => Theme.Bad, _ => Theme.Dim };
        Theme.DrawGlyph(g, IconFor(r), 10.5f, tint, new Rectangle(b.X + this.S(14), b.Y + this.S(6), this.S(20), this.S(20)));
        var textColor = r.Result == ActivityResult.FAILED ? Theme.Mix(Theme.Bad, Theme.Text, .35f) : Theme.Text;
        TextRenderer.DrawText(g, Clean(r.Action), Theme.Body, new Rectangle(b.X + TextX, b.Y + this.S(7), b.Width - TextX - TimeW, b.Height - this.S(10)), textColor,
            TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(g, r.Time.ToLocalTime().ToString("h:mm tt"), Theme.Small, new Rectangle(b.Right - TimeW, b.Y + this.S(9), TimeW - this.S(14), this.S(18)), Theme.Faint,
            TextFormatFlags.Right | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
    }
}
