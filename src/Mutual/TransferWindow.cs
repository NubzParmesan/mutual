namespace Mutual;

// progress for one file either way. cancel stops both ends
sealed class TransferWindow : Form
{
    readonly ProgressBar bar = new() { Dock = DockStyle.Top, Height = 18, Maximum = 1000 };
    readonly Label text = new() { Dock = DockStyle.Fill, ForeColor = Theme.Text, Font = Theme.Body };
    public CancellationTokenSource Cancel { get; } = new();
    readonly long total;
    readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
    readonly string what;

    public TransferWindow(string what, long total)
    {
        this.what = what; this.total = Math.Max(1, total);
        Text = "Mutual";
        Icon = Program.AppIcon;
        BackColor = Theme.Back; ForeColor = Theme.Text;
        ClientSize = new Size(420, 120);
        FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        var stop = Theme.Button("Cancel", (_, _) => { Cancel.Cancel(); Close(); });
        stop.Width = 100;
        var row = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
        row.Controls.Add(stop);
        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14, 12, 14, 0) };
        body.Controls.Add(text);
        body.Controls.Add(bar);
        Controls.Add(body);
        Controls.Add(row);
        text.Text = what;
        FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing) Cancel.Cancel(); };
    }

    public IProgress<long> Progress => new Progress<long>(done =>
    {
        if (IsDisposed) return;
        bar.Value = (int)Math.Clamp(done * 1000 / total, 0, 1000);
        double mbs = done / 1048576.0 / Math.Max(0.1, clock.Elapsed.TotalSeconds);
        text.Text = $"{what}\r\n{Size(done)} of {Size(total)}  ·  {mbs:F1} MB/s";
    });

    public static string Size(long b) => b >= 1L << 30 ? $"{b / (double)(1L << 30):F2} GB" : b >= 1 << 20 ? $"{b / 1048576.0:F1} MB" : $"{b / 1024.0:F0} KB";
}
