using System.Drawing;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Mutual.Stream;

// grabs one monitor with desktop duplication and crops it on the gpu to whats shared
// never touches the cpu unless you ask for a snapshot
public sealed class DesktopCapture : ICapture
{
    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }
    public Rectangle MonitorBounds { get; }
    public string MonitorName { get; }

    readonly IDXGIOutputDuplication dup;
    ID3D11Texture2D? crop;
    Rectangle cropRect;
    bool holdingFrame;

    DesktopCapture(ID3D11Device device, ID3D11DeviceContext ctx, IDXGIOutputDuplication dup, Rectangle bounds, string name)
    {
        Device = device; Context = ctx; this.dup = dup; MonitorBounds = bounds; MonitorName = name;
    }

    public sealed record MonitorInfo(int Adapter, int Output, string Name, Rectangle Bounds);

    public static List<MonitorInfo> Monitors()
    {
        var list = new List<MonitorInfo>();
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
        {
            using (adapter)
                for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
                    using (output)
                    {
                        var d = output.Description;
                        var r = d.DesktopCoordinates;
                        list.Add(new MonitorInfo((int)a, (int)o, d.DeviceName, Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom)));
                    }
        }
        return list;
    }

    // the monitor thats got this point on it
    public static DesktopCapture ForPoint(Point p)
    {
        var mons = Monitors();
        var m = mons.FirstOrDefault(x => x.Bounds.Contains(p)) ?? mons.First();
        return Open(m);
    }

    public static DesktopCapture Open(MonitorInfo m)
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        factory.EnumAdapters1((uint)m.Adapter, out var adapter).CheckError();
        using (adapter)
        {
            adapter.EnumOutputs((uint)m.Output, out var output).CheckError();
            using (output)
            using (var output1 = output.QueryInterface<IDXGIOutput1>())
            {
                D3D11.D3D11CreateDevice(adapter, DriverType.Unknown,
                    DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
                    new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 },
                    out ID3D11Device device, out ID3D11DeviceContext ctx).CheckError();
                // the encoder might get fed from another thread
                using (var mt = device.QueryInterface<ID3D11Multithread>()) mt.SetMultithreadProtected(true);
                var dup = output1.DuplicateOutput(device);
                return new DesktopCapture(device, ctx, dup, m.Bounds, m.Name);
            }
        }
    }

    // waits up to timeoutMs for a new frame and copies the shared part into the crop texture
    // null if the screen didnt change. sizes stay even since h.264 needs that
    public ID3D11Texture2D? Next(Rectangle region, int timeoutMs)
    {
        if (holdingFrame) { dup.ReleaseFrame(); holdingFrame = false; }
        var r = dup.AcquireNextFrame((uint)timeoutMs, out var info, out var resource);
        if (r == Vortice.DXGI.ResultCode.WaitTimeout) return null;
        r.CheckError();
        holdingFrame = true;
        using (resource)
        {
            if (info.LastPresentTime == 0 && crop != null && cropRect == Normalize(region)) return null;   // only the cursor moved
            using var desktop = resource.QueryInterface<ID3D11Texture2D>();
            var want = Normalize(region);
            EnsureCrop(want);
            int left = want.X - MonitorBounds.X, top = want.Y - MonitorBounds.Y;
            Context.CopySubresourceRegion(crop!, 0, 0, 0, 0, desktop, 0,
                new Box(left, top, 0, left + want.Width, top + want.Height, 1));
            return crop;
        }
    }

    // clip to this monitor and round down to even
    public Rectangle Normalize(Rectangle region)
    {
        var r = Rectangle.Intersect(region, MonitorBounds);
        if (r.Width < 16 || r.Height < 16) r = MonitorBounds;
        return new Rectangle(r.X, r.Y, r.Width & ~1, r.Height & ~1);
    }

    void EnsureCrop(Rectangle r)
    {
        if (crop != null && cropRect.Size == r.Size) { cropRect = r; return; }
        crop?.Dispose();
        crop = Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)r.Width,
            Height = (uint)r.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
        });
        cropRect = r;
    }

    public Bitmap Snapshot(ID3D11Texture2D tex) => Snapshot(Device, Context, tex);

    // gpu texture to a bitmap, slow, for checks and screenshots
    public static Bitmap Snapshot(ID3D11Device Device, ID3D11DeviceContext Context, ID3D11Texture2D tex)
    {
        var d = tex.Description;
        d.Usage = ResourceUsage.Staging; d.BindFlags = BindFlags.None; d.CPUAccessFlags = CpuAccessFlags.Read; d.MiscFlags = ResourceOptionFlags.None;
        using var staging = Device.CreateTexture2D(d);
        Context.CopyResource(staging, tex);
        var map = Context.Map(staging, 0, MapMode.Read);
        try
        {
            var bmp = new Bitmap((int)d.Width, (int)d.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var bits = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), System.Drawing.Imaging.ImageLockMode.WriteOnly, bmp.PixelFormat);
            unsafe
            {
                for (int y = 0; y < bmp.Height; y++)
                {
                    Buffer.MemoryCopy((byte*)map.DataPointer + y * map.RowPitch, (byte*)bits.Scan0 + y * bits.Stride, bits.Stride, bmp.Width * 4);
                    // desktop alpha is junk, make it solid
                    byte* row = (byte*)bits.Scan0 + y * bits.Stride;
                    for (int x = 0; x < bmp.Width; x++) row[x * 4 + 3] = 255;
                }
            }
            bmp.UnlockBits(bits);
            return bmp;
        }
        finally { Context.Unmap(staging, 0); }
    }

    // the last crop, resent when the screen isnt changing
    public ID3D11Texture2D? LastFrame => crop;

    public void Dispose()
    {
        if (holdingFrame) { try { dup.ReleaseFrame(); } catch { } }
        crop?.Dispose();
        dup.Dispose();
        Context.Dispose();
        Device.Dispose();
    }
}
