using System.Net.Sockets;
using Mutual.Stream;

namespace Mutual;

// what to share. whole screen, one window or a box
sealed class SourcePicker : Form
{
    public StreamSource? Chosen { get; private set; }
    readonly ListBox list = new() { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, IntegralHeight = false };
    readonly List<Func<StreamSource>> makers = new();

    public SourcePicker()
    {
        Text = "Share what?";
        BackColor = Theme.Back; ForeColor = Theme.Text; Font = Theme.Body;
        ClientSize = new Size(440, 420);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
        list.BackColor = Theme.Panel; list.ForeColor = Theme.Text; list.ItemHeight = 22;

        foreach (var m in DesktopCapture.Monitors())
            Add("Whole screen: " + m.Name.Replace(@"\\.\", "") + "  (" + m.Bounds.Width + "x" + m.Bounds.Height + ")", () => new ScreenSource(m));
        Add("Custom box (you drag and resize it, even while streaming)", () =>
        {
            var area = Screen.PrimaryScreen!.Bounds;
            return new BoxSource(new Rectangle(area.X + area.Width / 2, area.Y, area.Width / 2, area.Height));
        });
        if (SplitColonySource.GameRunning())
            Add("RimWorld split: just the right half, their mouse and keys go to the mod (you keep yours)", () => new SplitColonySource());
        foreach (var w in WindowSource.List().OrderBy(w => w.Title))
            Add("Window: " + w.Title, () => w);

        var go = Theme.Button("Share", (_, _) => Pick(), primary: true);
        var cancel = Theme.Button("Cancel", (_, _) => Close());
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 50, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8), BackColor = Theme.Back };
        go.Width = 100; cancel.Width = 90;
        bar.Controls.Add(go); bar.Controls.Add(cancel);
        var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 12, 12, 0) };
        host.Controls.Add(list);
        Controls.Add(host);
        Controls.Add(bar);
        list.DoubleClick += (_, _) => Pick();
        if (list.Items.Count > 0) list.SelectedIndex = 0;
    }

    void Add(string label, Func<StreamSource> make) { list.Items.Add(label); makers.Add(make); }

    void Pick()
    {
        if (list.SelectedIndex < 0) return;
        try { Chosen = makers[list.SelectedIndex](); }
        catch (SocketException) { MessageBox.Show(this, "Something else (probably DuoCursor) is already talking to the SplitColony mod. Close it and try again.", "Mutual"); return; }
        DialogResult = DialogResult.OK;
        Close();
    }
}
