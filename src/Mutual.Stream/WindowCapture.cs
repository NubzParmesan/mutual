using System.Drawing;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Mathematics;
using Size = System.Drawing.Size;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace Mutual.Stream;

// what the host needs from a capture, whichever way its grabbed
public interface ICapture : IDisposable
{
    ID3D11Device Device { get; }
    string MonitorName { get; }
    // the rect that actually gets captured (screen pixels, even size)
    Rectangle Normalize(Rectangle region);
    // a new frame cropped to the region, null if nothing new
    ID3D11Texture2D? Next(Rectangle region, int timeoutMs);
    ID3D11Texture2D? LastFrame { get; }
}

// one window thru windows graphics capture so it streams even with stuff on top of it
// (windows 10 draws a yellow outline around it, only on screen not in the stream). client area only
public sealed class WindowCapture : ICapture
{
    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }
    public string MonitorName => "window";
    public ID3D11Texture2D? LastFrame => crop;

    readonly nint hwnd;
    readonly IDirect3DDevice winrtDevice;
    readonly GraphicsCaptureItem item;
    Direct3D11CaptureFramePool pool;
    readonly GraphicsCaptureSession session;
    Windows.Graphics.SizeInt32 poolSize;
    ID3D11Texture2D? crop;
    Size cropSize;

    public static bool Supported
    {
        get { try { return GraphicsCaptureSession.IsSupported(); } catch { return false; } }
    }

    public WindowCapture(nint window)
    {
        hwnd = window;
        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
            new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 }, out ID3D11Device dev, out ID3D11DeviceContext ctx).CheckError();
        Device = dev; Context = ctx;
        using (var mt = dev.QueryInterface<ID3D11Multithread>()) mt.SetMultithreadProtected(true);
        using (var dxgi = dev.QueryInterface<Vortice.DXGI.IDXGIDevice>())
        {
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out var inspectable));
            winrtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
            Marshal.Release(inspectable);
        }
        item = CreateItem(window);
        poolSize = item.Size;
        pool = Direct3D11CaptureFramePool.CreateFreeThreaded(winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, poolSize);
        session = pool.CreateCaptureSession(item);
        try { session.IsCursorCaptureEnabled = false; } catch { }   // the pointer goes separately
        // turning off the yellow outline needs a windows 11 api
        session.StartCapture();
    }

    public Rectangle Normalize(Rectangle region) => new(region.X, region.Y, Math.Max(16, region.Width) & ~1, Math.Max(16, region.Height) & ~1);

    public ID3D11Texture2D? Next(Rectangle region, int timeoutMs)
    {
        using var frame = pool.TryGetNextFrame();
        if (frame == null) return null;
        // window changed size, the pool has to match or frames come out stretched
        if (frame.ContentSize.Width != poolSize.Width || frame.ContentSize.Height != poolSize.Height)
        {
            poolSize = frame.ContentSize;
            pool.Recreate(winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, poolSize);
            return null;
        }
        using var tex = SurfaceTexture(frame.Surface);
        var want = Normalize(region);
        EnsureCrop(want.Size);
        // the frame is the whole window, find the inside part
        var frameBounds = ExtendedFrame();
        int left = Math.Max(0, want.X - frameBounds.X), top = Math.Max(0, want.Y - frameBounds.Y);
        var d = tex.Description;
        int w = Math.Min(want.Width, (int)d.Width - left), h = Math.Min(want.Height, (int)d.Height - top);
        if (w <= 0 || h <= 0) return null;
        Context.CopySubresourceRegion(crop!, 0, 0, 0, 0, tex, 0, new Box(left, top, 0, left + w, top + h, 1));
        return crop;
    }

    void EnsureCrop(Size s)
    {
        if (crop != null && cropSize == s) return;
        crop?.Dispose();
        crop = Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)s.Width, Height = (uint)s.Height, MipLevels = 1, ArraySize = 1, Format = Vortice.DXGI.Format.B8G8R8A8_UNorm,
            SampleDescription = new Vortice.DXGI.SampleDescription(1, 0), Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
        });
        cropSize = s;
    }

    Rectangle ExtendedFrame()
    {
        if (DwmGetWindowAttribute(hwnd, 9, out RECT r, Marshal.SizeOf<RECT>()) == 0) return Rectangle.FromLTRB(r.L, r.T, r.R, r.B);   // DWMWA_EXTENDED_FRAME_BOUNDS
        GetWindowRect(hwnd, out r);
        return Rectangle.FromLTRB(r.L, r.T, r.R, r.B);
    }

    static GraphicsCaptureItem CreateItem(nint hwnd)
    {
        var factory = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
        var interop = factory.AsInterface<IGraphicsCaptureItemInterop>();
        var iid = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");   // IGraphicsCaptureItem
        var ptr = interop.CreateForWindow(hwnd, ref iid);
        try { return GraphicsCaptureItem.FromAbi(ptr); }
        finally { Marshal.Release(ptr); }
    }

    static ID3D11Texture2D SurfaceTexture(IDirect3DSurface surface)
    {
        var access = surface.As<IDirect3DDxgiInterfaceAccess>();
        var iid = typeof(ID3D11Texture2D).GUID;
        return new ID3D11Texture2D(access.GetInterface(ref iid));
    }

    public void Dispose()
    {
        try { session.Dispose(); } catch { }
        try { pool.Dispose(); } catch { }
        crop?.Dispose();
        Context.Dispose(); Device.Dispose();
    }

    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IGraphicsCaptureItemInterop
    {
        nint CreateForWindow(nint window, ref Guid iid);
        nint CreateForMonitor(nint monitor, ref Guid iid);
    }

    [ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDirect3DDxgiInterfaceAccess { nint GetInterface(ref Guid iid); }

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [DllImport("d3d11.dll")] static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(nint h, int attr, out RECT value, int size);
    [DllImport("user32.dll")] static extern bool GetWindowRect(nint h, out RECT r);
}
