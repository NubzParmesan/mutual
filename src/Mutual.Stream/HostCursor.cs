using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Mutual.Stream;

// the hosts pointer, desktop duplication leaves it out of the picture
// sends where it is when it moves and what it looks like once per shape so the viewer can draw the real one
public sealed class HostCursor
{
    nint lastHandle = -1;
    int lastShapeId;
    (ushort x, ushort y, bool visible, int shape) lastSent = (0, 0, false, -1);
    readonly Dictionary<nint, int> shapeIds = new();

    // call from the capture loop, sends whatever changed
    public void Poll(Wire wire, Rectangle shared)
    {
        var ci = new CURSORINFO { size = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref ci)) return;
        bool visible = (ci.flags & 1) != 0 && ci.cursor != 0 && shared.Contains(ci.x, ci.y);
        int shape = lastShapeId;
        if (ci.cursor != 0 && ci.cursor != lastHandle)
        {
            lastHandle = ci.cursor;
            if (!shapeIds.TryGetValue(ci.cursor, out shape))
            {
                var s = Render(ci.cursor);
                if (s != null)
                {
                    shape = shapeIds.Count + 1;
                    shapeIds[ci.cursor] = shape;
                    wire.SendCursorShape(shape, s.Value.hotX, s.Value.hotY, s.Value.pixels);
                }
            }
            lastShapeId = shape;
        }
        ushort nx = (ushort)Math.Clamp((ci.x - shared.X) * 65535L / Math.Max(1, shared.Width - 1), 0, 65535);
        ushort ny = (ushort)Math.Clamp((ci.y - shared.Y) * 65535L / Math.Max(1, shared.Height - 1), 0, 65535);
        var now = (nx, ny, visible, shape);
        if (now == lastSent) return;
        lastSent = now;
        wire.SendCursor(nx, ny, visible, shape);
    }

    // draws the pointer on black and on white and compares them to get its real transparency
    // also handles old style pointers like the text beam that invert whats under them
    static (int hotX, int hotY, byte[] pixels)? Render(nint cursor)
    {
        if (!GetIconInfo(cursor, out var ii)) return null;
        try
        {
            int w = 32, h = 32;
            var bmHandle = ii.color != 0 ? ii.color : ii.mask;
            if (bmHandle != 0 && GetObject(bmHandle, Marshal.SizeOf<BITMAP>(), out var bm) != 0)
            {
                w = bm.width;
                h = ii.color != 0 ? bm.height : bm.height / 2;
            }
            if (w <= 0 || h <= 0 || w > 256 || h > 256) return null;
            using var onBlack = Draw(cursor, w, h, Color.Black);
            using var onWhite = Draw(cursor, w, h, Color.White);
            using var outBmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    var b = onBlack.GetPixel(x, y); var wh = onWhite.GetPixel(x, y);
                    int a = 255 - (wh.G - b.G);
                    if (a > 255)
                    {
                        // inverts whats under it, make it white and it gets an outline below
                        outBmp.SetPixel(x, y, Color.FromArgb(255, 255, 255, 255));
                        continue;
                    }
                    a = Math.Clamp(a, 0, 255);
                    if (a == 0) continue;
                    int un(int c) => Math.Clamp(c * 255 / a, 0, 255);
                    outBmp.SetPixel(x, y, Color.FromArgb(a, un(b.R), un(b.G), un(b.B)));
                }
            OutlineInverted(outBmp, onBlack, onWhite);
            return (ii.hotX, ii.hotY, CursorPixels.Pack(outBmp));
        }
        finally
        {
            if (ii.color != 0) DeleteObject(ii.color);
            if (ii.mask != 0) DeleteObject(ii.mask);
        }
    }

    static Bitmap Draw(nint cursor, int w, int h, Color back)
    {
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppRgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(back);
        var hdc = g.GetHdc();
        DrawIconEx(hdc, 0, 0, cursor, w, h, 0, 0, 3);
        g.ReleaseHdc(hdc);
        return bmp;
    }

    // dark edge around inverting pixels so a white text beam still shows up on white
    static void OutlineInverted(Bitmap outBmp, Bitmap onBlack, Bitmap onWhite)
    {
        int w = outBmp.Width, h = outBmp.Height;
        bool Inv(int x, int y) => x >= 0 && y >= 0 && x < w && y < h && onWhite.GetPixel(x, y).G < onBlack.GetPixel(x, y).G;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (Inv(x, y) || outBmp.GetPixel(x, y).A > 0) continue;
                if (Inv(x - 1, y) || Inv(x + 1, y) || Inv(x, y - 1) || Inv(x, y + 1)) outBmp.SetPixel(x, y, Color.FromArgb(220, 0, 0, 0));
            }
    }

    [StructLayout(LayoutKind.Sequential)] struct CURSORINFO { public int size; public int flags; public nint cursor; public int x, y; }
    [StructLayout(LayoutKind.Sequential)] struct ICONINFO { public bool icon; public int hotX, hotY; public nint mask, color; }
    [StructLayout(LayoutKind.Sequential)] struct BITMAP { public int type, width, height, widthBytes; public ushort planes, bitsPixel; public nint bits; }
    [DllImport("user32.dll")] static extern bool GetCursorInfo(ref CURSORINFO ci);
    [DllImport("user32.dll")] static extern bool GetIconInfo(nint icon, out ICONINFO ii);
    [DllImport("user32.dll")] static extern bool DrawIconEx(nint hdc, int x, int y, nint icon, int w, int h, int step, nint brush, int flags);
    [DllImport("gdi32.dll")] static extern int GetObject(nint h, int size, out BITMAP bm);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(nint h);
}
