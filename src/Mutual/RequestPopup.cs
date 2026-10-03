using Mutual.Core;

namespace Mutual;

// the "wants to stream" popup in the corner. stays on top but never takes focus so your game keeps the mouse
// its a plain window not a toast because toasts get dropped when notifications are off
// nothing happens unless you hit accept
sealed class RequestPopup : Form
{
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get { var p = base.CreateParams; p.ExStyle |= 0x8 | 0x80 | 0x08000000; return p; }   // topmost, tool window, no activate
    }

    public RequestPopup(string friend, Request req, Action accept)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Theme.Panel;
        Size = new Size(360, 112);
        var area = Screen.PrimaryScreen!.WorkingArea;
        Location = new Point(area.Right - Width - 12, area.Bottom - Height - 12);

        string what = req.Kind switch
        {
            RequestKind.Ssh => "wants to open SSH",
            RequestKind.Stream => "wants to stream to you" + (req.Detail != null ? " (" + req.Detail + ")" : ""),
            RequestKind.Play => "wants to play together",
            RequestKind.File => "wants to send you a file" + (req.Detail != null ? ": " + req.Detail : ""),
            _ => "wants to connect",
        };
        Controls.Add(new Label { Text = "Mutual", ForeColor = Theme.Accent, Font = new Font("Segoe UI Semibold", 10f), AutoSize = true, Location = new Point(14, 10) });
        Controls.Add(new Label
        {
            Text = friend + " " + what + "\r\n" + DateTime.Now.ToString("h:mm tt"),
            ForeColor = Theme.Text, Font = Theme.Body, AutoSize = false, Size = new Size(330, 40), Location = new Point(14, 32),
        });
        var yes = Theme.Button("Accept", (_, _) => { Close(); accept(); }, primary: true);
        yes.SetBounds(186, 74, 80, 28);
        var no = Theme.Button("Ignore", (_, _) => Close());
        no.SetBounds(272, 74, 76, 28);
        Controls.Add(yes); Controls.Add(no);
        // their side stops waiting after 10 min so this goes away too
        var expire = new System.Windows.Forms.Timer { Interval = 600_000 };
        expire.Tick += (_, _) => Close();
        expire.Start();
        System.Media.SystemSounds.Asterisk.Play();
    }
}
