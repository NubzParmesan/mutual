using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Mutual.Stream;

// the hosts pointer drawn over the viewer when the host is the one moving it
// tiny click thru window with real transparency so it looks like a pointer. owned by the viewer so it hides with it
sealed class PointerOverlay : Form
{
    Bitmap? image;
    Point hot;

    public PointerOverlay()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(32, 32);
    }

    protected override bool ShowWithoutActivation => true;
    // layered, click thru, tool window, never activates
    protected override CreateParams CreateParams { get { var p = base.CreateParams; p.ExStyle |= 0x80000 | 0x20 | 0x80 | 0x08000000; return p; } }

    public void SetImage(Bitmap bmp, Point hotspot)
    {
        if (image == bmp) return;
        image = bmp; hot = hotspot;
        if (IsHandleCreated) Push(Location + new Size(hot));
    }

    public void MoveTip(Point tip)
    {
        if (image == null) return;
        if (!Visible) Show(Owner);
        Push(tip);
    }

    void Push(Point tip)
    {
        if (image == null) return;
        var screen = GetDC(0);
        var mem = CreateCompatibleDC(screen);
        var hbm = image.GetHbitmap(Color.FromArgb(0));
        var old = SelectObject(mem, hbm);
        try
        {
            var size = new SIZE { cx = image.Width, cy = image.Height };
            var src = new POINT();
            var dst = new POINT { x = tip.X - hot.X, y = tip.Y - hot.Y };
            var blend = new BLENDFUNCTION { op = 0, flags = 0, alpha = 255, format = 1 };
            UpdateLayeredWindow(Handle, screen, ref dst, ref size, mem, ref src, 0, ref blend, 2);
        }
        finally
        {
            SelectObject(mem, old); DeleteObject(hbm); DeleteDC(mem); ReleaseDC(0, screen);
        }
    }

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLENDFUNCTION { public byte op, flags, alpha, format; }
    [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(nint h, nint dstDc, ref POINT dst, ref SIZE size, nint srcDc, ref POINT src, int key, ref BLENDFUNCTION blend, int flags);
    [DllImport("user32.dll")] static extern nint GetDC(nint h);
    [DllImport("user32.dll")] static extern int ReleaseDC(nint h, nint dc);
    [DllImport("gdi32.dll")] static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] static extern nint SelectObject(nint dc, nint o);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(nint o);
}

// turns the hosts pointer picture into a real windows cursor for your own mouse
static class CursorFactory
{
    public static Cursor? Make(Bitmap bmp, int hotX, int hotY)
    {
        nint color = 0, mask = 0;
        try
        {
            color = bmp.GetHbitmap(Color.FromArgb(0));
            using var monoMask = new Bitmap(bmp.Width, bmp.Height, PixelFormat.Format1bppIndexed);
            mask = monoMask.GetHbitmap();
            var ii = new ICONINFO { icon = false, hotX = hotX, hotY = hotY, mask = mask, color = color };
            var h = CreateIconIndirect(ref ii);
            return h == 0 ? null : new Cursor(h);
        }
        catch { return null; }
        finally { if (color != 0) DeleteObject(color); if (mask != 0) DeleteObject(mask); }
    }

    [StructLayout(LayoutKind.Sequential)] struct ICONINFO { public bool icon; public int hotX, hotY; public nint mask, color; }
    [DllImport("user32.dll")] static extern nint CreateIconIndirect(ref ICONINFO ii);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(nint o);
}
