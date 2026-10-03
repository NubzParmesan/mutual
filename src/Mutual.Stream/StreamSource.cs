using System.Drawing;
using System.Runtime.InteropServices;

namespace Mutual.Stream;

// whats being shared. a whole monitor, one window (follows it when it moves) or a box you can drag
// and resize while streaming. all of them come down to this rectangle of the desktop rn
public abstract class StreamSource
{
    public abstract string Describe();
    // the rectangle to share this frame, desktop pixels
    public abstract Rectangle Current();
    // a point thats for sure inside it, to pick which monitor to grab
    public virtual Point Anchor => Current().Location + new Size(Current().Width / 2, Current().Height / 2);
}

public sealed class ScreenSource : StreamSource
{
    readonly DesktopCapture.MonitorInfo monitor;
    public ScreenSource(DesktopCapture.MonitorInfo m) { monitor = m; }
    public override string Describe() => "whole screen (" + monitor.Bounds.Width + "x" + monitor.Bounds.Height + ")";
    public override Rectangle Current() => monitor.Bounds;
}

public sealed class BoxSource : StreamSource
{
    // set it whenever, the next frame picks it up. the box overlay drives this
    public Rectangle Box { get; set; }
    public BoxSource(Rectangle box) { Box = box; }
    public override string Describe() => "custom box (" + Box.Width + "x" + Box.Height + ")";
    public override Rectangle Current() => Box;
}

public sealed class WindowSource : StreamSource
{
    public nint Handle { get; }
    public string Title { get; }
    Rectangle last;
    public WindowSource(nint handle, string title) { Handle = handle; Title = title; }
    public override string Describe() => "window: " + Title;

    // the windows inside part (no title bar or borders), follows it around
    public override Rectangle Current()
    {
        if (!IsWindow(Handle)) return last;
        if (GetClientRect(Handle, out var rc))
        {
            var p = new POINT();
            if (ClientToScreen(Handle, ref p) && rc.R > rc.L && rc.B > rc.T)
                last = new Rectangle(p.X, p.Y, rc.R - rc.L, rc.B - rc.T);
        }
        return last;
    }

    public static List<WindowSource> List()
    {
        var list = new List<WindowSource>();
        var self = System.Diagnostics.Process.GetCurrentProcess().Id;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || IsIconic(h)) return true;
            GetWindowThreadProcessId(h, out uint pid);
            if (pid == self) return true;
            int len = GetWindowTextLength(h);
            if (len == 0) return true;
            var sb = new System.Text.StringBuilder(len + 1);
            GetWindowText(h, sb, sb.Capacity);
            if (GetWindowLong(h, -20) is var ex && (ex & 0x80) != 0) return true;   // tool windows
            if (DwmGetWindowAttribute(h, 14, out int cloaked, 4) == 0 && cloaked != 0) return true;   // hidden store app shells
            var w = new WindowSource(h, sb.ToString());
            var r = w.Current();
            if (r.Width >= 64 && r.Height >= 64) list.Add(w);
            return true;
        }, 0);
        return list;
    }

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    delegate bool EnumProc(nint h, nint l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, nint l);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(nint h);
    [DllImport("user32.dll")] static extern bool IsIconic(nint h);
    [DllImport("user32.dll")] static extern bool IsWindow(nint h);
    [DllImport("user32.dll")] static extern int GetWindowTextLength(nint h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(nint h, System.Text.StringBuilder s, int n);
    [DllImport("user32.dll")] static extern int GetWindowLong(nint h, int i);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint h, out uint pid);
    [DllImport("user32.dll")] static extern bool GetClientRect(nint h, out RECT r);
    [DllImport("user32.dll")] static extern bool ClientToScreen(nint h, ref POINT p);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(nint h, int attr, out int value, int size);
}
