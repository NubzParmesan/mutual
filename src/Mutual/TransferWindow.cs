namespace Mutual;

// progress for one file either way. cancel stops both ends
sealed class TransferWindow : DarkForm
{
    readonly Meter bar = new() { Dock = DockStyle.Top };
    readonly Label headline = new() { Dock = DockStyle.Top, Height = 24, Font = Theme.Heading, ForeColor = Theme.Text, AutoEllipsis = true, UseMnemonic = false };
    readonly Label detail = new() { Dock = DockStyle.Top, Height = 26, Font = Theme.Small, ForeColor = Theme.Dim, UseMnemonic = false, AutoEllipsis = true };
    readonly Label numbers = new() { Dock = DockStyle.Top, Height = 24, Font = Theme.Small, ForeColor = Theme.Dim, TextAlign = ContentAlignment.TopLeft, UseMnemonic = false };
    public CancellationTokenSource Cancel { get; } = new();
    readonly long total;
    readonly System.Diagnostics.Stopwatch clock = new();

    // sending: true for files going out, false for coming in
    public TransferWindow(string file, long total, bool sending, string status)
    {
        this.total = Math.Max(1, total);
        Text = (sending ? "Sending " : "Receiving ") + file;
        ClientSize = new Size(440, 190);
        FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;

        var icon = new Label { Text = sending ? Theme.Glyph.Upload : Theme.Glyph.Download, Dock = DockStyle.Left, Width = 56, Font = Theme.Icons(18), ForeColor = sending ? Color.FromArgb(110, 170, 255) : Theme.Online, TextAlign = ContentAlignment.TopLeft, Padding = new Padding(0, 6, 0, 0) };
        headline.Text = file;
        detail.Text = status;
        numbers.Text = Size(total);
        var text = new Panel { Dock = DockStyle.Fill };
        text.Controls.Add(numbers);
        text.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 8 });
        text.Controls.Add(bar);
        text.Controls.Add(detail);
        text.Controls.Add(headline);
        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(22, 18, 24, 16) };
        body.Controls.Add(text);
        body.Controls.Add(icon);

        var stop = new FlatButton { Text = "Cancel", Width = 96 };
        stop.Click += (_, _) => { Cancel.Cancel(); Close(); };
        CancelButton = stop;
        Controls.Add(body);
        Controls.Add(ButtonBar(stop));
        FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing) Cancel.Cancel(); };
    }

    // what its doing rn, under the file name
    public string Status { get => detail.Text; set => detail.Text = value; }

    public IProgress<long> Progress => new Progress<long>(done =>
    {
        if (IsDisposed) return;
        if (!clock.IsRunning) clock.Start();
        bar.Value = Math.Clamp(done / (float)total, 0, 1);
        double secs = Math.Max(0.1, clock.Elapsed.TotalSeconds), rate = done / secs;
        string left = rate > 1 && done < total ? "  ·  " + Eta((total - done) / rate) + " left" : "";
        numbers.Text = $"{Size(done)} of {Size(total)}  ·  {rate / 1048576.0:F1} MB/s{left}";
    });

    static string Eta(double s) => s < 60 ? $"{Math.Max(1, (int)s)}s" : s < 3600 ? $"{(int)(s / 60)}m {(int)(s % 60)}s" : $"{(int)(s / 3600)}h {(int)(s % 3600 / 60)}m";

    public static string Size(long b) => b >= 1L << 30 ? $"{b / (double)(1L << 30):F2} GB" : b >= 1 << 20 ? $"{b / 1048576.0:F1} MB" : $"{b / 1024.0:F0} KB";

    public static TransferWindow Demo()
    {
        var w = new TransferWindow("colony.rws", 41_200_000, true, "Sending to alex");
        w.bar.Value = .42f;
        w.numbers.Text = "16.5 MB of 39.3 MB  ·  8.2 MB/s  ·  3s left";
        return w;
    }
}
