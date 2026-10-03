namespace Mutual;

// one dark look for every window, same as the old notifier popup
static class Theme
{
    public static readonly Color Back = Color.FromArgb(28, 28, 32);
    public static readonly Color Panel = Color.FromArgb(38, 38, 44);
    public static readonly Color Line = Color.FromArgb(58, 58, 66);
    public static readonly Color Text = Color.FromArgb(235, 235, 235);
    public static readonly Color Dim = Color.FromArgb(150, 150, 160);
    public static readonly Color Online = Color.FromArgb(80, 200, 120);
    public static readonly Color Offline = Color.FromArgb(120, 120, 130);
    public static readonly Color Accent = Color.FromArgb(255, 140, 26);
    public static readonly Color Bad = Color.FromArgb(230, 90, 80);

    public static readonly Font Title = new("Segoe UI Semibold", 13f);
    public static readonly Font Body = new("Segoe UI", 9.5f);
    public static readonly Font Small = new("Segoe UI", 8.5f);

    public static Button Button(string text, EventHandler click, bool primary = false)
    {
        var b = new Button
        {
            Text = text,
            FlatStyle = FlatStyle.Flat,
            ForeColor = primary ? Color.Black : Text,
            BackColor = primary ? Accent : Panel,
            Font = Body,
            Height = 34,
            Cursor = Cursors.Hand,
        };
        b.FlatAppearance.BorderColor = primary ? Accent : Line;
        b.Click += click;
        return b;
    }
}
