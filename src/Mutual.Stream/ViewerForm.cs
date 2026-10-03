using System.Drawing;
using System.Windows.Forms;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Mutual.Stream;

// the window you watch in. picture stays at the streams aspect ratio with black bars, drawn thru a
// swapchain right from the decoder. when its focused your mouse and keys go to the host. F11 is fullscreen
public sealed class ViewerForm : Form, IMessageFilter
{
    readonly StreamReceiver rx;
    readonly Panel picture = new() { BackColor = Color.Black };
    IDXGISwapChain1? swap;
    int swapW, swapH;
    readonly object swapLock = new();
    bool fullscreen; FormBorderStyle savedBorder; Rectangle savedBounds;
    readonly System.Windows.Forms.Timer statsTimer = new() { Interval = 1000 };
    int lastFrames;
    readonly Label stats = new() { ForeColor = Color.FromArgb(200, 200, 200), BackColor = Color.FromArgb(20, 20, 20), AutoSize = true, Font = new Font("Consolas", 8.5f) };

    public ViewerForm(StreamReceiver receiver, string title)
    {
        rx = receiver;
        Text = title;
        BackColor = Color.Black;
        ClientSize = new Size(1280, 720);
        KeyPreview = true;
        Controls.Add(stats);
        Controls.Add(picture);
        stats.BringToFront();
        Resize += (_, _) => LayoutPicture();
        rx.InfoChanged += info => Ui(() => { Text = title + " - " + info.Source; LayoutPicture(); });
        rx.FrameReady += Present;
        rx.Ended += why => Ui(() => { Text = title + " - " + why; });
        HandleCreated += (_, _) => { if (rx.Info is { } info) Text = title + " - " + info.Source; LayoutPicture(); };

        picture.MouseMove += (_, e) => { localPointer = (Nx(e.X), Ny(e.Y)); rx.Send(new InputEvent(InputKind.Move, Nx(e.X), Ny(e.Y), 0)); UpdatePointer(); };
        picture.MouseDown += (_, e) => { buttonsDown.Add(Btn(e.Button)); rx.Send(new InputEvent(InputKind.Down, Nx(e.X), Ny(e.Y), Btn(e.Button))); };
        picture.MouseUp += (_, e) => { buttonsDown.Remove(Btn(e.Button)); rx.Send(new InputEvent(InputKind.Up, Nx(e.X), Ny(e.Y), Btn(e.Button))); };
        picture.MouseLeave += (_, _) => { localPointer = null; UpdatePointer(); };
        rx.CursorShape += (id, hx, hy, png) => Ui(() => AddShape(id, hx, hy, png));
        rx.CursorMoved += (x, y, vis, shape) => { host = (x, y, vis, shape); Ui(UpdatePointer); };
        Deactivate += (_, _) => ReleaseEverything();
        Move += (_, _) => UpdatePointer();
        Application.AddMessageFilter(this);
        // click the stats line to mute their sound
        stats.Cursor = Cursors.Hand;
        stats.Click += (_, _) => { if (rx.Sound is { } snd) snd.Muted = !snd.Muted; };
        statsTimer.Tick += (_, _) =>
        {
            int f = rx.FramesDecoded;
            var info = rx.Info;
            stats.Text = info == null ? "waiting for picture..." : $"{info.Width}x{info.Height}  {f - lastFrames} fps  decode {rx.DecodeMs:F1} ms  {rx.Transport}" + (rx.Transport == "udp" ? $" {rx.RttMs:F0} ms rtt  loss {rx.LastLoss:P1}" : "") + $"  {info.Encoder}" + (rx.Sound == null ? "" : rx.Sound.Muted ? "  sound muted (click)" : "  sound on (click to mute)");
            lastFrames = f;
        };
        statsTimer.Start();
    }

    // runs on the window thread, or not at all if the window isnt up. never throws back into the stream
    void Ui(Action a)
    {
        try { if (IsHandleCreated && !IsDisposed) BeginInvoke(a); }
        catch (InvalidOperationException) { }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.F11) { ToggleFullscreen(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // keyboard and wheel come straight off the message queue so tab, arrows, alt and F10 go to the host
    // instead of getting eaten by windows

    readonly HashSet<int> keysDown = new(), buttonsDown = new();

    public bool PreFilterMessage(ref Message m)
    {
        const int WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105, WM_MOUSEWHEEL = 0x20A, WM_MOUSEHWHEEL = 0x20E;
        if (!ContainsFocus || !IsMine(m.HWnd)) return false;
        switch (m.Msg)
        {
            case WM_KEYDOWN or WM_SYSKEYDOWN or WM_KEYUP or WM_SYSKEYUP:
            {
                int vk = (int)m.WParam & 0xFFFF;
                long lp = m.LParam.ToInt64();
                if (vk == (int)Keys.F11) { if (m.Msg == WM_KEYDOWN) ToggleFullscreen(); return true; }
                bool down = m.Msg is WM_KEYDOWN or WM_SYSKEYDOWN;
                int scan = (int)((lp >> 16) & 0xFF), ext = (int)((lp >> 24) & 1);
                int code = vk | scan << 16 | ext << 24;
                if (down) keysDown.Add(code); else keysDown.Remove(code);
                rx.Send(new InputEvent(down ? InputKind.KeyDown : InputKind.KeyUp, 0, 0, code));
                return true;
            }
            case WM_MOUSEWHEEL or WM_MOUSEHWHEEL:
            {
                long lp = m.LParam.ToInt64();
                var p = picture.PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
                int delta = (short)(((long)m.WParam >> 16) & 0xFFFF);
                rx.Send(new InputEvent(m.Msg == WM_MOUSEWHEEL ? InputKind.Wheel : InputKind.HWheel, Nx(p.X), Ny(p.Y), delta));
                return true;
            }
        }
        return false;
    }

    bool IsMine(nint h) { for (var c = Control.FromHandle(h); c != null; c = c.Parent) if (c == this) return true; return false; }

    // losing focus mid press would leave a key stuck down on the host, so let go of everything
    void ReleaseEverything()
    {
        foreach (var k in keysDown) rx.Send(new InputEvent(InputKind.KeyUp, 0, 0, k));
        keysDown.Clear();
        var at = localPointer ?? (host.x, host.y);
        foreach (var b in buttonsDown) rx.Send(new InputEvent(InputKind.Up, at.x, at.y, b));
        buttonsDown.Clear();
    }

    // the hosts pointer

    readonly Dictionary<int, (Bitmap bmp, Cursor? cursor, Point hot)> shapes = new();
    (ushort x, ushort y, bool visible, int shape) host;
    (ushort x, ushort y)? localPointer;
    PointerOverlay? overlay;

    void AddShape(int id, int hx, int hy, byte[] png)
    {
        try
        {
            // a pointer is small, anything big or weird from the other side gets ignored
            if (png.Length > 256 * 1024 || shapes.Count > 256) return;
            using var ms = new MemoryStream(png);
            using var loaded = new Bitmap(ms);
            if (loaded.Width > 256 || loaded.Height > 256 || hx < 0 || hy < 0 || hx >= loaded.Width || hy >= loaded.Height) return;
            var bmp = new Bitmap(loaded);
            shapes[id] = (bmp, CursorFactory.Make(bmp, hx, hy), new Point(hx, hy));
            UpdatePointer();
        }
        catch { }
    }

    // your own mouse wears the hosts pointer while its over the picture
    // when the host moves theirs somewhere else (or youre not over the picture) theirs gets drawn where it really is
    void UpdatePointer()
    {
        if (IsDisposed) return;
        shapes.TryGetValue(host.shape, out var s);
        if (s.cursor != null && picture.Cursor != s.cursor) picture.Cursor = s.cursor;
        bool elsewhere = localPointer is not { } lp || Math.Abs(lp.x - host.x) > 400 || Math.Abs(lp.y - host.y) > 400;
        bool show = host.visible && s.bmp != null && elsewhere && WindowState != FormWindowState.Minimized && Visible;
        if (!show) { overlay?.Hide(); return; }
        if (overlay == null) { overlay = new PointerOverlay(); overlay.Owner = this; }
        overlay.SetImage(s.bmp!, s.hot);
        var origin = picture.PointToScreen(Point.Empty);
        overlay.MoveTip(new Point(origin.X + host.x * picture.Width / 65536, origin.Y + host.y * picture.Height / 65536));
    }

    // where the host pointer was last drawn, for tests
    public Point? OverlayTip => overlay is { Visible: true } o ? o.Location + new Size(shapes.TryGetValue(host.shape, out var s) ? s.hot : Point.Empty) : null;

    static int Btn(MouseButtons b) => b == MouseButtons.Right ? 1 : b == MouseButtons.Middle ? 2 : 0;
    ushort Nx(int x) => (ushort)Math.Clamp(x * 65535L / Math.Max(1, picture.Width - 1), 0, 65535);
    ushort Ny(int y) => (ushort)Math.Clamp(y * 65535L / Math.Max(1, picture.Height - 1), 0, 65535);

    void LayoutPicture()
    {
        var info = rx.Info;
        var area = ClientSize;
        if (info == null || area.Width < 2 || area.Height < 2) { picture.Bounds = new Rectangle(Point.Empty, area); return; }
        double scale = Math.Min(area.Width / (double)info.Width, area.Height / (double)info.Height);
        int w = (int)(info.Width * scale), h = (int)(info.Height * scale);
        picture.Bounds = new Rectangle((area.Width - w) / 2, (area.Height - h) / 2, w, h);
        UpdatePointer();
    }

    void ToggleFullscreen()
    {
        if (!fullscreen) { savedBorder = FormBorderStyle; savedBounds = Bounds; FormBorderStyle = FormBorderStyle.None; Bounds = Screen.FromControl(this).Bounds; }
        else { FormBorderStyle = savedBorder; Bounds = savedBounds; }
        fullscreen = !fullscreen;
        LayoutPicture();
    }

    // decode thread, copy the frame into the swapchain and show it
    void Present(ID3D11Texture2D bgra, int w, int h)
    {
        nint hwnd = 0;
        try { hwnd = (nint)picture.Invoke(() => picture.Handle); } catch { return; }
        lock (swapLock)
        {
            if (swap == null || swapW != w || swapH != h)
            {
                swap?.Dispose();
                using var dxgiDev = rx.Device.QueryInterface<IDXGIDevice>();
                using var adapter = dxgiDev.GetAdapter();
                using var factory = adapter.GetParent<IDXGIFactory2>();
                swap = factory.CreateSwapChainForHwnd(rx.Device, hwnd, new SwapChainDescription1
                {
                    Width = (uint)w, Height = (uint)h, Format = Format.B8G8R8A8_UNorm, BufferCount = 2,
                    BufferUsage = Usage.RenderTargetOutput, SampleDescription = new SampleDescription(1, 0),
                    SwapEffect = SwapEffect.FlipDiscard, Scaling = Scaling.Stretch, AlphaMode = AlphaMode.Ignore,
                });
                swapW = w; swapH = h;
            }
            using var back = swap.GetBuffer<ID3D11Texture2D>(0);
            rx.Context.CopyResource(back, bgra);
            if (snapshotPath != null) { SaveTexture(back, snapshotPath); snapshotPath = null; }
            swap.Present(0, PresentFlags.None);
            Presented++;
        }
    }

    public int Presented { get; private set; }
    volatile string? snapshotPath;
    // saves the next frame that actually hits the screen, for checks
    public void SnapshotNext(string path) => snapshotPath = path;

    void SaveTexture(ID3D11Texture2D tex, string path)
    {
        var d = tex.Description;
        d.Usage = ResourceUsage.Staging; d.BindFlags = BindFlags.None; d.CPUAccessFlags = CpuAccessFlags.Read; d.MiscFlags = ResourceOptionFlags.None;
        using var st = rx.Device.CreateTexture2D(d);
        rx.Context.CopyResource(st, tex);
        var map = rx.Context.Map(st, 0, MapMode.Read);
        try
        {
            using var bmp = new Bitmap((int)d.Width, (int)d.Height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            var bits = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), System.Drawing.Imaging.ImageLockMode.WriteOnly, bmp.PixelFormat);
            unsafe { for (int y = 0; y < bmp.Height; y++) Buffer.MemoryCopy((byte*)map.DataPointer + y * map.RowPitch, (byte*)bits.Scan0 + y * bits.Stride, bits.Stride, bmp.Width * 4); }
            bmp.UnlockBits(bits);
            bmp.Save(path);
        }
        finally { rx.Context.Unmap(st, 0); }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        Application.RemoveMessageFilter(this);
        ReleaseEverything();
        overlay?.Dispose();
        statsTimer.Dispose();
        rx.FrameReady -= Present;
        lock (swapLock) { swap?.Dispose(); swap = null; }
        base.OnFormClosed(e);
    }
}
