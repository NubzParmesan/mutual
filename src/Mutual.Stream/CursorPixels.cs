using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Mutual.Stream;

// pointer pictures go over the wire as plain pixels: w(2) h(2) then w*h bgra. no png, so the
// viewer never runs an image decoder on something the other side made
public static class CursorPixels
{
    public const int MaxSide = 256;

    public static byte[] Pack(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        var outp = new byte[4 + w * h * 4];
        BinaryPrimitives.WriteUInt16LittleEndian(outp, (ushort)w);
        BinaryPrimitives.WriteUInt16LittleEndian(outp.AsSpan(2), (ushort)h);
        var bits = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try { for (int y = 0; y < h; y++) Marshal.Copy(bits.Scan0 + y * bits.Stride, outp, 4 + y * w * 4, w * 4); }
        finally { bmp.UnlockBits(bits); }
        return outp;
    }

    // null if the sizes dont add up or its too big
    public static Bitmap? Unpack(byte[] data)
    {
        if (data.Length < 4) return null;
        int w = BinaryPrimitives.ReadUInt16LittleEndian(data), h = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(2));
        if (w < 1 || h < 1 || w > MaxSide || h > MaxSide || data.Length != 4 + w * h * 4) return null;
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var bits = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try { for (int y = 0; y < h; y++) Marshal.Copy(data, 4 + y * w * 4, bits.Scan0 + y * bits.Stride, w * 4); }
        finally { bmp.UnlockBits(bits); }
        return bmp;
    }
}
