using System.Runtime.InteropServices;
using Mutual.Core;

namespace Mutual;

// --snapshot-ui name file.png draws one window to a png, title bar and all, without any networking
// for checking how things look and for the readme pictures
static class Snapshots
{
    public static readonly string[] Names = { "main", "main-live", "main-unpaired", "pair", "picker", "settings", "prompt", "request", "transfer", "controlbar" };

    public static int Run(AppSettings settings, string name, string path)
    {
        Form f = name switch
        {
            "main" => new MainForm(settings, offline: true).Demo(online: true),
            "main-live" => new MainForm(settings, offline: true).Demo(online: true, live: true),
            "main-unpaired" => new MainForm(settings, offline: true).Demo(unpaired: true),
            "pair" => new PairDialog(settings, Path.Combine(Path.GetTempPath(), "mutual-snapshot-identity")),
            "picker" => new SourcePicker(),
            "settings" => new SettingsDialog(settings),
            "prompt" => Prompt.Make("The key Mutual trusts for alex changed, but you didn't pair again.\n\nNew safety code: 7D4F-9B98-8960\n\nIf neither of you re-paired, something on this PC changed it. Pair again and compare the safety code out loud.", "Mutual", MessageBoxButtons.OK, MessageBoxIcon.Warning),
            "request" => new RequestPopup("alex", new Request(RequestKind.Stream, "window: RimWorld by Ludeon Studios"), () => { }),
            "transfer" => TransferWindow.Demo(),
            "controlbar" => new ControlBar("alex", false),
            _ => throw new ArgumentException("pick one of " + string.Join(", ", Names)),
        };
        f.StartPosition = FormStartPosition.Manual;
        f.Location = new Point(-6000, 100);
        f.ShowInTaskbar = false;
        f.Show();
        for (int i = 0; i < 30; i++) { Application.DoEvents(); Thread.Sleep(15); }
        GetWindowRect(f.Handle, out var r);
        using var bmp = new Bitmap(r.R - r.L, r.B - r.T);
        using (var g = Graphics.FromImage(bmp))
        {
            // dwm draws the dark title bar, the window paints its own inside
            var dc = g.GetHdc();
            PrintWindow(f.Handle, dc, 2);
            g.ReleaseHdc(dc);
            var origin = f.PointToScreen(Point.Empty);
            // DrawToBitmap on a form draws the whole window, crop the frame off
            using var whole = new Bitmap(f.Width, f.Height);
            f.DrawToBitmap(whole, new Rectangle(0, 0, f.Width, f.Height));
            int dx = origin.X - r.L, dy = origin.Y - r.T;
            g.DrawImage(whole, new Rectangle(dx, dy, f.ClientSize.Width, f.ClientSize.Height), new Rectangle(dx, dy, f.ClientSize.Width, f.ClientSize.Height), GraphicsUnit.Pixel);
        }
        bmp.Save(path);
        f.Close();
        return 0;
    }

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [DllImport("user32.dll")] static extern bool GetWindowRect(nint h, out RECT r);
    [DllImport("user32.dll")] static extern bool PrintWindow(nint h, nint dc, uint flags);
}
