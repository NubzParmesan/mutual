// mutual lab, runs each part on its own and measures it
//   capture x y w h frames out.png     grab a region, report fps, save the last frame
using System.Diagnostics;
using System.Drawing;
using Mutual.Stream;

static class Lab
{
    [System.Runtime.InteropServices.DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool ShowWindow(nint h, int cmd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern nint WindowFromPoint(Point p);
    // copies a gpu frame back to the cpu and writes it out as a png
    static void SaveTexture(Vortice.Direct3D11.ID3D11Device dev, Vortice.Direct3D11.ID3D11DeviceContext ctx, Vortice.Direct3D11.ID3D11Texture2D tex, int w, int h, string path)
    {
        var d = tex.Description;
        d.Usage = Vortice.Direct3D11.ResourceUsage.Staging; d.BindFlags = 0; d.CPUAccessFlags = Vortice.Direct3D11.CpuAccessFlags.Read; d.MiscFlags = 0;
        using var st = dev.CreateTexture2D(d);
        ctx.CopyResource(st, tex);
        var map = ctx.Map(st, 0, Vortice.Direct3D11.MapMode.Read);
        try
        {
            using var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            var bits = bmp.LockBits(new Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.WriteOnly, bmp.PixelFormat);
            unsafe { for (int y = 0; y < h; y++) Buffer.MemoryCopy((byte*)map.DataPointer + y * map.RowPitch, (byte*)bits.Scan0 + y * bits.Stride, bits.Stride, w * 4); }
            bmp.UnlockBits(bits);
            bmp.Save(path);
        }
        finally { ctx.Unmap(st, 0); }
    }

    static int Main(string[] a)
    {
        timeBeginPeriod(1);
        try
        {
            switch (a.Length > 0 ? a[0] : "")
            {
                case "api":
                {
                    // api <assembly> <type> [member]: what does vortice call things
                    var asm = AppDomain.CurrentDomain.GetAssemblies().Concat(new[] {
                        typeof(Vortice.MediaFoundation.MediaFactory).Assembly, typeof(Vortice.Direct3D11.D3D11).Assembly, typeof(Vortice.DXGI.DXGI).Assembly })
                        .Where(x => x.GetName().Name!.Contains(a[1], StringComparison.OrdinalIgnoreCase)).Distinct();
                    foreach (var asmb in asm)
                        foreach (var t in asmb.GetTypes().Where(t => t.Name.Contains(a[2], StringComparison.OrdinalIgnoreCase)))
                        {
                            Console.WriteLine("== " + t.FullName + (t.IsEnum ? " (enum)" : ""));
                            if (t.IsEnum) { Console.WriteLine("   " + string.Join(", ", Enum.GetNames(t).Take(60))); continue; }
                            foreach (var m in t.GetMembers(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly))
                            {
                                if (a.Length > 3 && !m.Name.Contains(a[3], StringComparison.OrdinalIgnoreCase)) continue;
                                if (m is System.Reflection.MethodInfo mi && !mi.IsSpecialName)
                                    Console.WriteLine("   " + (mi.IsStatic ? "static " : "") + mi.ReturnType.Name + " " + mi.Name + "(" + string.Join(", ", mi.GetParameters().Select(p => (p.IsOut ? "out " : p.ParameterType.IsByRef ? "ref " : "") + p.ParameterType.Name.TrimEnd('&') + " " + p.Name)) + ")");
                                else if (m is System.Reflection.PropertyInfo pi) Console.WriteLine("   prop " + pi.PropertyType.Name + " " + pi.Name);
                                else if (m is System.Reflection.FieldInfo fi && fi.IsStatic) Console.WriteLine("   field " + fi.FieldType.Name + " " + fi.Name);
                            }
                        }
                    return 0;
                }
                case "monitors":
                    foreach (var m in DesktopCapture.Monitors()) Console.WriteLine($"{m.Adapter}/{m.Output} {m.Name} {m.Bounds}");
                    return 0;
                case "capture":
                {
                    var region = new Rectangle(int.Parse(a[1]), int.Parse(a[2]), int.Parse(a[3]), int.Parse(a[4]));
                    int frames = int.Parse(a[5]);
                    using var cap = DesktopCapture.ForPoint(region.Location);
                    Console.WriteLine($"monitor {cap.MonitorName} {cap.MonitorBounds}, region {cap.Normalize(region)}");
                    var sw = Stopwatch.StartNew();
                    int got = 0, polls = 0;
                    Vortice.Direct3D11.ID3D11Texture2D? last = null;
                    while (sw.ElapsedMilliseconds < frames * 1000 / 60 + 2000 && got < frames)
                    {
                        polls++;
                        var t = cap.Next(region, 100);
                        if (t != null) { got++; last = t; }
                    }
                    double secs = sw.Elapsed.TotalSeconds;
                    Console.WriteLine($"{got} new frames in {secs:F2}s ({got / secs:F1} fps, {polls} polls)");
                    if (last != null) { using var bmp = cap.Snapshot(last); bmp.Save(a[6]); Console.WriteLine("saved " + a[6]); }
                    else Console.WriteLine("screen never changed; nothing to save");
                    return 0;
                }
                case "encode":
                {
                    // encode x y w h seconds out.h264: capture -> nv12 -> hardware h264, timed
                    var region = new Rectangle(int.Parse(a[1]), int.Parse(a[2]), int.Parse(a[3]), int.Parse(a[4]));
                    double seconds = double.Parse(a[5]);
                    using var cap = DesktopCapture.ForPoint(region.Location);
                    var r = cap.Normalize(region);
                    using var conv = new GpuColorConverter(cap.Device, (uint)r.Width, (uint)r.Height, Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DXGI.Format.NV12);
                    using var enc = new H264Encoder(cap.Device, r.Width, r.Height, 60, 12_000_000);
                    Console.WriteLine($"encoder: {enc.Name} ({(enc.Hardware ? "hardware" : "software")}), {r.Width}x{r.Height}");
                    using var file = File.Create(a[6]);
                    var sentAt = new System.Collections.Concurrent.ConcurrentDictionary<long, long>();
                    long bytes = 0; int outFrames = 0, keys = 0; double latSum = 0, latMax = 0;
                    enc.Encoded += (data, ts, key) =>
                    {
                        lock (file) file.Write(data);
                        bytes += data.Length; outFrames++; if (key) keys++;
                        if (sentAt.TryRemove(ts, out var t0)) { double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency; latSum += ms; latMax = Math.Max(latMax, ms); }
                    };
                    var sw = Stopwatch.StartNew();
                    int inFrames = 0;
                    var frameTick = Stopwatch.StartNew();
                    while (sw.Elapsed.TotalSeconds < seconds)
                    {
                        var tex = cap.Next(region, 0);
                        if (tex == null) { Thread.Sleep(1); continue; }
                        // steady 60, newest frame goes out every 16.7 ms and the ones in between get skipped
                        if (tex == null || frameTick.Elapsed.TotalMilliseconds < 16.4) continue;
                        frameTick.Restart();
                        var nv12 = conv.Convert(tex);
                        long ts = (long)(sw.Elapsed.TotalSeconds * 10_000_000);
                        sentAt[ts] = Stopwatch.GetTimestamp();
                        enc.Submit(nv12, ts);
                        inFrames++;
                        if (enc.Failure != null) throw enc.Failure;
                    }
                    Thread.Sleep(300);
                    double secs = sw.Elapsed.TotalSeconds;
                    Console.WriteLine($"in {inFrames} frames, out {outFrames} ({outFrames / secs:F1} fps), {keys} keyframes, {bytes * 8 / secs / 1e6:F1} Mbit/s");
                    if (outFrames > 0) Console.WriteLine($"encode latency avg {latSum / Math.Max(1, outFrames):F1} ms, max {latMax:F1} ms");
                    if (enc.Failure != null) Console.WriteLine("encoder failure: " + enc.Failure.Message);
                    return 0;
                }
                case "roundtrip":
                {
                    // roundtrip x y w h seconds out.png: capture -> encode -> decode on a second gpu device -> back to bgra
                    // like the viewer does. saves the last frame and reports latency
                    var region = new Rectangle(int.Parse(a[1]), int.Parse(a[2]), int.Parse(a[3]), int.Parse(a[4]));
                    double seconds = double.Parse(a[5]);
                    using var cap = DesktopCapture.ForPoint(region.Location);
                    var r = cap.Normalize(region);
                    using var conv = new GpuColorConverter(cap.Device, (uint)r.Width, (uint)r.Height, Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DXGI.Format.NV12);
                    using var enc = new H264Encoder(cap.Device, r.Width, r.Height, 60, 12_000_000);
                    Vortice.Direct3D11.D3D11.D3D11CreateDevice(null, Vortice.Direct3D.DriverType.Hardware,
                        Vortice.Direct3D11.DeviceCreationFlags.BgraSupport | Vortice.Direct3D11.DeviceCreationFlags.VideoSupport,
                        new[] { Vortice.Direct3D.FeatureLevel.Level_11_1, Vortice.Direct3D.FeatureLevel.Level_11_0 },
                        out Vortice.Direct3D11.ID3D11Device viewDev, out Vortice.Direct3D11.ID3D11DeviceContext viewCtx).CheckError();
                    using (var mt = viewDev.QueryInterface<Vortice.Direct3D11.ID3D11Multithread>()) mt.SetMultithreadProtected(true);
                    using var dec = new H264Decoder(viewDev);
                    Console.WriteLine($"encoder {enc.Name}, decoder {dec.Name}, {r.Width}x{r.Height}");
                    GpuColorConverter? back = null;
                    var q = new System.Collections.Concurrent.BlockingCollection<(byte[] d, long ts)>();
                    var sentAt = new System.Collections.Concurrent.ConcurrentDictionary<long, long>();
                    enc.Encoded += (d, ts, k) => q.Add((d, ts));
                    int decoded = 0; double latSum = 0, latMax = 0; Vortice.Direct3D11.ID3D11Texture2D? lastBgra = null;
                    Exception? decodeError = null;
                    var decodeThread = new Thread(() =>
                    {
                        try
                        {
                            foreach (var (d, ts) in q.GetConsumingEnumerable())
                                foreach (var f in dec.Decode(d, ts))
                                {
                                    var desc = f.Texture.Description;
                                    back ??= new GpuColorConverter(viewDev, desc.Width, desc.Height, (uint)r.Width, (uint)r.Height,
                                        Vortice.DXGI.Format.NV12, Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.Direct3D11.BindFlags.RenderTarget | Vortice.Direct3D11.BindFlags.ShaderResource);
                                    lastBgra = back.Convert(f.Texture, f.Slice);
                                    viewCtx.Flush();
                                    f.Texture.Dispose(); f.Owner.Dispose();
                                    decoded++;
                                    if (sentAt.TryRemove(f.Timestamp, out var t0)) { double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency; latSum += ms; latMax = Math.Max(latMax, ms); }
                                }
                        }
                        catch (Exception e) { decodeError = e; }
                    }) { IsBackground = true };
                    decodeThread.Start();
                    var sw = Stopwatch.StartNew(); var tick = Stopwatch.StartNew(); int sent = 0;
                    while (sw.Elapsed.TotalSeconds < seconds)
                    {
                        var tex = cap.Next(region, 0);
                        if (tex == null) { Thread.Sleep(1); continue; }
                        if (tick.Elapsed.TotalMilliseconds < 16.4) continue;
                        tick.Restart();
                        long ts = (long)(sw.Elapsed.TotalSeconds * 10_000_000);
                        sentAt[ts] = Stopwatch.GetTimestamp();
                        enc.Submit(conv.Convert(tex), ts); sent++;
                        if (enc.Failure != null) throw enc.Failure;
                    }
                    Thread.Sleep(400); q.CompleteAdding(); decodeThread.Join(2000);
                    if (decodeError != null) Console.WriteLine("decode error: " + decodeError);
                    Console.WriteLine($"sent {sent}, decoded {decoded}, capture->decoded latency avg {latSum / Math.Max(1, decoded):F1} ms, max {latMax:F1} ms");
                    if (lastBgra != null)
                    {
                        var d = lastBgra.Description;
                        d.Usage = Vortice.Direct3D11.ResourceUsage.Staging; d.BindFlags = 0; d.CPUAccessFlags = Vortice.Direct3D11.CpuAccessFlags.Read; d.MiscFlags = 0;
                        using var st = viewDev.CreateTexture2D(d);
                        viewCtx.CopyResource(st, lastBgra);
                        var map = viewCtx.Map(st, 0, Vortice.Direct3D11.MapMode.Read);
                        using var bmp = new Bitmap((int)d.Width, (int)d.Height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
                        var bits = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), System.Drawing.Imaging.ImageLockMode.WriteOnly, bmp.PixelFormat);
                        unsafe { for (int y = 0; y < bmp.Height; y++) Buffer.MemoryCopy((byte*)map.DataPointer + y * map.RowPitch, (byte*)bits.Scan0 + y * bits.Stride, bits.Stride, bmp.Width * 4); }
                        bmp.UnlockBits(bits); viewCtx.Unmap(st, 0);
                        bmp.Save(a[6]); Console.WriteLine("saved decoded frame " + a[6]);
                    }
                    back?.Dispose(); viewCtx.Dispose(); viewDev.Dispose();
                    return 0;
                }
                case "netloop":
                {
                    // netloop x y w h seconds [anim] [win] [udp] [drop=0.05] [window]: host and viewer over a real link on localhost
                    // input playback is off so it never moves the mouse
                    var region = new Rectangle(int.Parse(a[1]), int.Parse(a[2]), int.Parse(a[3]), int.Parse(a[4]));
                    double seconds = double.Parse(a[5]);
                    bool window = a.Skip(6).Contains("window");
                    bool useUdp = a.Skip(6).Contains("udp");
                    double drop = a.Skip(6).Where(x => x.StartsWith("drop=")).Select(x => double.Parse(x[5..])).FirstOrDefault();
                    UdpVideo? hostLane = null;
                    AnimForm? animForm = null;
                    if (a.Skip(6).Contains("anim"))
                    {
                        // something that changes every frame so theres real video to lose
                        var t = new Thread(() =>
                        {
                            var f = new AnimForm { StartPosition = FormStartPosition.Manual, Bounds = region, FormBorderStyle = FormBorderStyle.None, TopMost = true, ShowInTaskbar = false };
                            animForm = f;
                            Application.Run(f);
                        }) { IsBackground = true };
                        t.SetApartmentState(ApartmentState.STA); t.Start();
                        Thread.Sleep(500);
                    }
                    using var hostId = Mutual.Core.Pairing.CreateIdentity("lab-host");
                    using var viewId = Mutual.Core.Pairing.CreateIdentity("lab-viewer");
                    var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); l.Start();
                    int port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port; l.Stop();
                    var hs = Mutual.Core.PeerLink.OpenAsync(Mutual.Core.LinkRole.Server, System.Net.IPAddress.Loopback, "", port, hostId, viewId.RawData, TimeSpan.FromSeconds(10));
                    var vs = Mutual.Core.PeerLink.OpenAsync(Mutual.Core.LinkRole.Client, System.Net.IPAddress.Loopback, "127.0.0.1", port, viewId, hostId.RawData, TimeSpan.FromSeconds(10));
                    using var hostLink = hs.GetAwaiter().GetResult();
                    using var viewLink = vs.GetAwaiter().GetResult();
                    hostLink.ReadTimeout = viewLink.ReadTimeout = System.Threading.Timeout.Infinite;
                    int udpPort = 0;
                    Func<byte[], UdpVideo>? hostUdp = useUdp ? key => { hostLane = UdpVideo.Listen(System.Net.IPAddress.Loopback, 0, key, isHost: true); hostLane.DropRate = drop; udpPort = hostLane.Port; return hostLane; } : null;
                    Func<byte[], UdpVideo>? viewUdp = useUdp ? key => UdpVideo.Connect(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, udpPort), key, isHost: false) : null;
                    // "win" shares the test window with window capture instead of the screen (works with the monitor asleep)
                    StreamSource src = a.Skip(6).Contains("win") && animForm != null ? new WindowSource((nint)animForm.Invoke(() => animForm.Handle), "anim") : new BoxSource(region);
                    using var host = new StreamHost(new Wire(hostLink), src, allowInput: false, openUdp: hostUdp, shareSound: false);
                    host.Status += s => Console.WriteLine("host: " + s);
                    using var rx = new StreamReceiver(new Wire(viewLink), viewUdp);
                    var statsPrinter = new Thread(() => { while (true) { Thread.Sleep(2000); var st = host.LastStats; if (st != null) Console.WriteLine($"  viewer: {st.Fps:F0} fps, {st.Transport}, loss {st.Loss:P1}, lost {st.FramesLost}, rebuilt {st.FramesRebuilt}, rtt {st.NetworkMs:F1} ms, bitrate {host.Bitrate / 1e6:F1}"); } }) { IsBackground = true };
                    statsPrinter.Start();
                    double latSum = 0, latMax = 0; int latN = 0;
                    var lat = new List<double>(); Vortice.Direct3D11.ID3D11Texture2D? stage = null;
                    rx.FrameReady += (tex, w, h) =>
                    {
                        if (!a.Skip(6).Contains("anim")) return;
                        long drawn = AnimForm.ReadStamp(rx.Device, rx.Context, tex, ref stage);
                        if (drawn < 0) return;
                        long now = AnimForm.Clock.ElapsedMilliseconds & 0xFFFFFF;
                        long d = (now - drawn) & 0xFFFFFF;
                        if (d < 2000) lock (lat) lat.Add(d);
                    };
                    rx.InfoChanged += info => Console.WriteLine($"viewer: {info.Width}x{info.Height} via {info.Encoder}");
                    var probe = new Thread(() =>
                    {
                        int last = 0;
                        while (true) { Thread.Sleep(5); if (rx.FramesDecoded != last) { last = rx.FramesDecoded; } }
                    }) { IsBackground = true };
                    if (window)
                    {
                        Application.EnableVisualStyles();
                        // off screen and never activated so it doesnt get in the way
                        var form = new ViewerForm(rx, "Mutual lab") { StartPosition = FormStartPosition.Manual, Location = new Point(-6000, -6000), ShowInTaskbar = false };
                        var closeAt = new System.Windows.Forms.Timer { Interval = (int)(seconds * 1000) };
                        closeAt.Tick += (_, _) => form.Close();
                        closeAt.Start();
                        var snapAt = new System.Windows.Forms.Timer { Interval = (int)(seconds * 500) };
                        snapAt.Tick += (_, _) => { snapAt.Stop(); if (a.Length > 7) form.SnapshotNext(a[7]); };
                        snapAt.Start();
                        form.Load += (_, _) => ShowWindow(form.Handle, 4);   // SW_SHOWNOACTIVATE
                        Application.Run(form);
                        Console.WriteLine($"viewer window presented {form.Presented} frames");
                    }
                    else Thread.Sleep((int)(seconds * 1000));
                    lock (lat) if (lat.Count > 10)
                    {
                        lat.Sort();
                        Console.WriteLine($"drawn -> decoded on the viewer: median {lat[lat.Count / 2]:F0} ms, 90% under {lat[(int)(lat.Count * 0.9)]:F0} ms, best {lat[0]:F0} ms ({lat.Count} frames)");
                    }
                    Console.WriteLine($"transport {rx.Transport}; host sent {host.FramesSent}, viewer decoded {rx.FramesDecoded} ({rx.FramesDecoded / seconds:F1} fps), last encode {host.LastEncodeMs:F1} ms, last decode {rx.DecodeMs:F1} ms");
                    if (host.Failure != null) Console.WriteLine("host failure: " + host.Failure);
                    return 0;
                }
                case "inputloop":
                {
                    // inputloop: a test window shared as a box, driven by a viewer over a real link
                    // checks clicks, wheel and keys land right and the pointer comes back. moves the mouse for a sec then puts it back
                    Application.EnableVisualStyles();
                    var got = new System.Collections.Concurrent.ConcurrentQueue<string>();
                    Form? target = null; var ready = new ManualResetEventSlim();
                    var ui = new Thread(() =>
                    {
                        target = new Form { Text = "mutual input target", StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(300, 300, 640, 440), TopMost = true, BackColor = Color.DarkSlateGray, KeyPreview = true };
                        target.MouseDown += (_, e) => got.Enqueue($"down {e.Button} {e.X} {e.Y}");
                        target.MouseUp += (_, e) => got.Enqueue($"up {e.Button} {e.X} {e.Y}");
                        target.MouseWheel += (_, e) => got.Enqueue($"wheel {e.Delta}");
                        target.KeyDown += (_, e) => { got.Enqueue($"keydown {e.KeyCode}"); e.Handled = true; };
                        target.KeyUp += (_, e) => got.Enqueue($"keyup {e.KeyCode}");
                        target.PreviewKeyDown += (_, e) => e.IsInputKey = true;
                        target.Shown += (_, _) => ready.Set();
                        Application.Run(target);
                    }) { IsBackground = true };
                    ui.SetApartmentState(ApartmentState.STA); ui.Start();
                    ready.Wait(5000); Thread.Sleep(300);
                    var client = (Rectangle)target!.Invoke(() => target.RectangleToScreen(target.ClientRectangle));
                    client = new Rectangle(client.X, client.Y, client.Width & ~1, client.Height & ~1);
                    Console.WriteLine("target client area " + client);
                    var savedPos = Cursor.Position;

                    using var hostId = Mutual.Core.Pairing.CreateIdentity("lab-host");
                    using var viewId = Mutual.Core.Pairing.CreateIdentity("lab-viewer");
                    var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); l.Start();
                    int port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port; l.Stop();
                    var hs = Mutual.Core.PeerLink.OpenAsync(Mutual.Core.LinkRole.Server, System.Net.IPAddress.Loopback, "", port, hostId, viewId.RawData, TimeSpan.FromSeconds(10));
                    var vs = Mutual.Core.PeerLink.OpenAsync(Mutual.Core.LinkRole.Client, System.Net.IPAddress.Loopback, "127.0.0.1", port, viewId, hostId.RawData, TimeSpan.FromSeconds(10));
                    using var hostLink = hs.GetAwaiter().GetResult();
                    using var viewLink = vs.GetAwaiter().GetResult();
                    hostLink.ReadTimeout = viewLink.ReadTimeout = System.Threading.Timeout.Infinite;
                    using var host = new StreamHost(new Wire(hostLink), new BoxSource(client), allowInput: true);
                    using var rx = new StreamReceiver(new Wire(viewLink));
                    var pointer = (x: (ushort)0, y: (ushort)0, vis: false, shape: 0); int shapes = 0;
                    rx.CursorMoved += (x, y, v, sh) => pointer = (x, y, v, sh);
                    rx.CursorShape += (id, hx, hy, px) => { Interlocked.Increment(ref shapes); using var bm = CursorPixels.Unpack(px); bm?.Save(Path.Combine(Path.GetTempPath(), $"mutual-cursor-{id}.png")); };
                    var t0 = Stopwatch.StartNew();
                    while (rx.Info == null && t0.ElapsedMilliseconds < 5000) Thread.Sleep(20);
                    Console.WriteLine(rx.Info == null ? "no stream info!" : $"stream {rx.Info.Width}x{rx.Info.Height}");

                    ushort N(double f) => (ushort)Math.Round(f * 65535);
                    int failures = 0;
                    void Expect(string what, Func<string?, bool> ok, int waitMs = 1500)
                    {
                        var sw = Stopwatch.StartNew();
                        while (sw.ElapsedMilliseconds < waitMs)
                        {
                            while (got.TryDequeue(out var e)) if (ok(e)) { Console.WriteLine($"  ok   {what}: {e}"); return; } else Console.WriteLine($"       (also saw {e})");
                            Thread.Sleep(5);
                        }
                        Console.WriteLine($"  FAIL {what}"); failures++;
                    }
                    bool Near(string e, string prefix, int x, int y)
                    {
                        if (!e.StartsWith(prefix)) return false;
                        var parts = e.Split(' ');
                        return Math.Abs(int.Parse(parts[^2]) - x) <= 2 && Math.Abs(int.Parse(parts[^1]) - y) <= 2;
                    }
                    try
                    {
                        // the host pointer should come back to the viewer at the same spot
                        rx.Send(new InputEvent(InputKind.Move, N(0.25), N(0.25), 0));
                        var sw2 = Stopwatch.StartNew();
                        while (sw2.ElapsedMilliseconds < 1500 && (Math.Abs(pointer.x - N(0.25)) > 300 || !pointer.vis)) Thread.Sleep(5);
                        Console.WriteLine((Math.Abs(pointer.x - N(0.25)) <= 300 && pointer.vis ? "  ok  " : "  FAIL") + $" host pointer reported at {pointer.x / 655.35:F1}%,{pointer.y / 655.35:F1}% visible={pointer.vis} shape={pointer.shape} (shapes sent: {shapes})");
                        if (!(Math.Abs(pointer.x - N(0.25)) <= 300 && pointer.vis)) failures++;

                        int cx = (int)(0.5 * (client.Width - 1)), cy = (int)(0.5 * (client.Height - 1));
                        rx.Send(new InputEvent(InputKind.Down, N(0.5), N(0.5), 0));
                        Expect("left down at center", e => Near(e!, "down Left", cx, cy));
                        rx.Send(new InputEvent(InputKind.Up, N(0.5), N(0.5), 0));
                        Expect("left up", e => Near(e!, "up Left", cx, cy));
                        int rx2 = (int)(0.8 * (client.Width - 1)), ry2 = (int)(0.2 * (client.Height - 1));
                        rx.Send(new InputEvent(InputKind.Down, N(0.8), N(0.2), 1));
                        Expect("right down at 80%,20%", e => Near(e!, "down Right", rx2, ry2));
                        rx.Send(new InputEvent(InputKind.Up, N(0.8), N(0.2), 1));
                        Expect("right up", e => e!.StartsWith("up Right"));
                        rx.Send(new InputEvent(InputKind.Wheel, N(0.5), N(0.5), 120));
                        Expect("wheel", e => e == "wheel 120");
                        // keys how the viewer sends them: vk | scan << 16 | extended << 24
                        int Code(Keys k, int scan, bool ext = false) => (int)k | scan << 16 | (ext ? 1 : 0) << 24;
                        foreach (var (k, scan, ext) in new[] { (Keys.A, 0x1E, false), (Keys.Tab, 0x0F, false), (Keys.Left, 0x4B, true), (Keys.Space, 0x39, false) })
                        {
                            rx.Send(new InputEvent(InputKind.KeyDown, 0, 0, Code(k, scan, ext)));
                            Expect("key down " + k, e => e == "keydown " + k);
                            rx.Send(new InputEvent(InputKind.KeyUp, 0, 0, Code(k, scan, ext)));
                            Expect("key up " + k, e => e == "keyup " + k);
                        }
                        // old plain vk still works
                        rx.Send(new InputEvent(InputKind.KeyDown, 0, 0, (int)Keys.B));
                        Expect("old-style key B", e => e == "keydown B");
                        rx.Send(new InputEvent(InputKind.KeyUp, 0, 0, (int)Keys.B));
                        Expect("old-style key B up", e => e == "keyup B");
                    }
                    finally
                    {
                        Cursor.Position = savedPos;
                        target.Invoke(() => target.Close());
                    }
                    Console.WriteLine(failures == 0 ? "ALL PASSED" : failures + " FAILED");
                    return failures == 0 ? 0 : 1;
                }
                case "soundloop":
                {
                    // soundloop seconds [udp]: plays a really quiet tone, shares sound over a real link and checks it shows up
                    // the viewer side doesnt play it back
                    double seconds = double.Parse(a[1]);
                    bool useUdp = a.Contains("udp");
                    using var tone = new SoundPlayer();
                    var gen = new Thread(() =>
                    {
                        uint seq = 0; long frame = 0; var sw = Stopwatch.StartNew();
                        while (true)
                        {
                            while (frame < sw.Elapsed.TotalSeconds * 48000 + 4800)
                            {
                                var pcm = new byte[240 * 4];
                                for (int i = 0; i < 240; i++, frame++)
                                {
                                    short v = (short)(40 * Math.Sin(2 * Math.PI * 440 * frame / 48000.0));
                                    System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 4), v);
                                    System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 4 + 2), v);
                                }
                                tone.Add(SoundCapture.Pack(++seq, pcm));
                            }
                            Thread.Sleep(5);
                        }
                    }) { IsBackground = true };
                    gen.Start();
                    Thread.Sleep(300);
                    using var hostId = Mutual.Core.Pairing.CreateIdentity("lab-host");
                    using var viewId = Mutual.Core.Pairing.CreateIdentity("lab-viewer");
                    var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); l.Start();
                    int port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port; l.Stop();
                    var hs = Mutual.Core.PeerLink.OpenAsync(Mutual.Core.LinkRole.Server, System.Net.IPAddress.Loopback, "", port, hostId, viewId.RawData, TimeSpan.FromSeconds(10));
                    var vs = Mutual.Core.PeerLink.OpenAsync(Mutual.Core.LinkRole.Client, System.Net.IPAddress.Loopback, "127.0.0.1", port, viewId, hostId.RawData, TimeSpan.FromSeconds(10));
                    using var hostLink = hs.GetAwaiter().GetResult();
                    using var viewLink = vs.GetAwaiter().GetResult();
                    hostLink.ReadTimeout = viewLink.ReadTimeout = System.Threading.Timeout.Infinite;
                    int udpPort = 0;
                    Func<byte[], UdpVideo>? hostUdp = useUdp ? key => { var u = UdpVideo.Listen(System.Net.IPAddress.Loopback, 0, key, isHost: true); udpPort = u.Port; return u; } : null;
                    Func<byte[], UdpVideo>? viewUdp = useUdp ? key => UdpVideo.Connect(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, udpPort), key, isHost: false) : null;
                    using var host = new StreamHost(new Wire(hostLink), new BoxSource(new Rectangle(0, 0, 320, 240)), allowInput: false, openUdp: hostUdp);
                    using var rx = new StreamReceiver(new Wire(viewLink), viewUdp) { PlaySoundAloud = false };
                    var t0 = Stopwatch.StartNew();
                    Thread.Sleep(1000);
                    int startChunks = rx.SoundChunks; var startT = t0.Elapsed.TotalSeconds;
                    Thread.Sleep((int)(seconds * 1000));
                    double rate = (rx.SoundChunks - startChunks) / (t0.Elapsed.TotalSeconds - startT);
                    Console.WriteLine($"transport {rx.Transport}: {rx.SoundChunks} packets, {rate:F0}/s (expect ~100)" + (host.SoundFailure != null ? " capture failed: " + host.SoundFailure.Message : ""));
                    var last = rx.LastSoundChunk;
                    if (last == null) { Console.WriteLine("FAIL no sound arrived"); return 1; }
                    Console.WriteLine($"last packet {last.Length} bytes -> about {last.Length * 8 * 100 / 1000} kbit/s of sound (was 1536 raw)");
                    // decode it and look for the tone
                    var unpack = new OpusUnpacker();
                    var pcmOut = unpack.Decode(last.AsSpan(4));
                    int peak = 0, crossings = 0; short prev = 0;
                    for (int i = 0; i + 3 < pcmOut.Length; i += 4)
                    {
                        short v = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(pcmOut.AsSpan(i));
                        peak = Math.Max(peak, Math.Abs((int)v));
                        if (i > 0 && (prev < 0) != (v < 0)) crossings++;
                        prev = v;
                    }
                    Console.WriteLine($"decoded {pcmOut.Length / 4} samples, peak {peak}, {crossings} zero crossings");
                    bool ok = rate > 80 && peak >= 5 && pcmOut.Length == 480 * 4 && last.Length < 400;
                    Console.WriteLine(ok ? "PASSED" : "FAILED");
                    return ok ? 0 : 1;
                }
                case "viewerinput":
                {
                    // viewerinput: the real viewer window driven by injected mouse and keys with a fake host recording what shows up
                    // nothing gets played back, the viewer eats the keys. puts the mouse back after
                    Application.EnableVisualStyles();
                    using var hostId = Mutual.Core.Pairing.CreateIdentity("lab-host");
                    using var viewId = Mutual.Core.Pairing.CreateIdentity("lab-viewer");
                    var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); l.Start();
                    int port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port; l.Stop();
                    var hs = Mutual.Core.PeerLink.OpenAsync(Mutual.Core.LinkRole.Server, System.Net.IPAddress.Loopback, "", port, hostId, viewId.RawData, TimeSpan.FromSeconds(10));
                    var vs = Mutual.Core.PeerLink.OpenAsync(Mutual.Core.LinkRole.Client, System.Net.IPAddress.Loopback, "127.0.0.1", port, viewId, hostId.RawData, TimeSpan.FromSeconds(10));
                    using var hostLink = hs.GetAwaiter().GetResult();
                    using var viewLink = vs.GetAwaiter().GetResult();
                    hostLink.ReadTimeout = viewLink.ReadTimeout = System.Threading.Timeout.Infinite;
                    var hostWire = new Wire(hostLink);
                    var got = new System.Collections.Concurrent.ConcurrentQueue<InputEvent>();
                    new Thread(() => { try { while (hostWire.Read() is { } m) { if (Environment.GetEnvironmentVariable("LABDEBUG") == "1") Console.WriteLine("  host got " + m.kind); if (m.kind == Msg.Input) got.Enqueue(Wire.ReadInput(m.payload)); } Console.WriteLine("  host link closed"); } catch (Exception e) { Console.WriteLine("  host reader died: " + e.Message); } }) { IsBackground = true }.Start();
                    // fake picture size so it lays out 4:3
                    hostWire.SendJson(Msg.Hello, new StreamInfo(800, 600, 60, "lab", "none"));
                    using var rx = new StreamReceiver(new Wire(viewLink));
                    rx.Ended += why => Console.WriteLine("  receiver ended: " + why);
                    var view = new ViewerForm(rx, "Mutual lab viewer") { StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(200, 200, 816, 639), TopMost = true };
                    var saved = Cursor.Position;
                    int failures = 0;
                    var script = new Thread(() =>
                    {
                        try
                        {
                            Thread.Sleep(800);
                            var pic = (Rectangle)view.Invoke(() => { var c = view.Controls.OfType<Panel>().First(); return c.RectangleToScreen(c.ClientRectangle); });
                            Console.WriteLine("picture on screen " + pic);
                            Point At(double fx, double fy) => new(pic.X + (int)(fx * (pic.Width - 1)), pic.Y + (int)(fy * (pic.Height - 1)));
                            void Drain() { Thread.Sleep(250); }
                            List<InputEvent> Take() { var list = new List<InputEvent>(); while (got.TryDequeue(out var e)) list.Add(e); return list; }
                            void Check(string what, bool ok, string detail) { Console.WriteLine((ok ? "  ok   " : "  FAIL ") + what + "  " + detail); if (!ok) failures++; }

                            // click the middle (also focuses it)
                            Cursor.Position = At(0.5, 0.5); Thread.Sleep(80);
                            Inject.Click(); Drain();
                            var under = WindowFromPoint(Cursor.Position);
                            var picHandle = (nint)view.Invoke(() => view.Controls.OfType<Panel>().First().Handle);
                            Console.WriteLine($"  under cursor 0x{under:X} picture 0x{picHandle:X} form 0x{(nint)view.Invoke(() => view.Handle):X}");
                            Console.WriteLine("  cursor now " + Cursor.Position + ", window under it: " + view.Invoke(() => view.Bounds) + " focused=" + view.Invoke(() => view.ContainsFocus));
                            var ev = Take();
                            var down = ev.FirstOrDefault(e => e.Kind == InputKind.Down);
                            Check("click maps to the middle", down.Kind == InputKind.Down && Math.Abs(down.X - 32767) < 400 && Math.Abs(down.Y - 32767) < 400, $"down at {down.X},{down.Y}");
                            Check("button released", ev.Any(e => e.Kind == InputKind.Up), "");

                            Cursor.Position = At(0.1, 0.9); Thread.Sleep(80);
                            Inject.Move(1, 0); Drain();
                            ev = Take();
                            var mv = ev.LastOrDefault(e => e.Kind == InputKind.Move);
                            Check("move to 10%,90%", Math.Abs(mv.X - 6553) < 500 && Math.Abs(mv.Y - 58981) < 500, $"move at {mv.X},{mv.Y}");

                            Inject.Wheel(-120); Drain();
                            ev = Take();
                            Check("wheel", ev.Any(e => e.Kind == InputKind.Wheel && e.Value == -120), string.Join(",", ev.Select(e => e.Kind + ":" + e.Value)));

                            foreach (var (vk, name) in new[] { (0x09, "Tab"), (0x25, "Left"), (0x12, "Alt"), (0x79, "F10"), (0x41, "A"), (0x20, "Space") })
                            {
                                Inject.Key((ushort)vk); Drain();
                                ev = Take();
                                bool d = ev.Any(e => e.Kind == InputKind.KeyDown && (e.Value & 0xFFFF) == vk), u = ev.Any(e => e.Kind == InputKind.KeyUp && (e.Value & 0xFFFF) == vk);
                                Check("key " + name + " reaches the host", d && u, string.Join(",", ev.Select(e => e.Kind + ":0x" + e.Value.ToString("X"))));
                            }
                            // hold a key then lose focus, the host still has to get the key up
                            Inject.KeyDown(0x57); Thread.Sleep(100);   // W
                            view.Invoke(() => { var other = new Form { StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(1100, 200, 200, 200), TopMost = true }; other.Show(); other.Activate(); });
                            Drain();
                            ev = Take();
                            Check("held W released when focus leaves", ev.Any(e => e.Kind == InputKind.KeyUp && (e.Value & 0xFFFF) == 0x57), string.Join(",", ev.Select(e => e.Kind + ":0x" + e.Value.ToString("X"))));
                            Inject.KeyUp(0x57);
                        }
                        catch (Exception e) { Console.WriteLine("script error: " + e); failures++; }
                        finally { Cursor.Position = saved; view.Invoke(() => { foreach (var f in Application.OpenForms.Cast<Form>().ToList()) f.Close(); }); }
                    }) { IsBackground = true };
                    view.Shown += (_, _) => script.Start();
                    Application.Run(view);
                    Console.WriteLine(failures == 0 ? "ALL PASSED" : failures + " FAILED");
                    return failures == 0 ? 0 : 1;
                }
                case "rehearsal-setup":
                {
                    // rehearsal-setup [host:port] [relay]: two mutual profiles on this pc paired with each other over loopback
                    // on their own ports, accepting everything and not sharing sound (it would feed back)
                    var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mutual", "profiles");
                    string da = Path.Combine(root, "rehearsal-a"), db = Path.Combine(root, "rehearsal-b");
                    foreach (var d in new[] { da, db }) { if (Directory.Exists(d)) Directory.Delete(d, true); Directory.CreateDirectory(d); }
                    var ia = Mutual.Core.Pairing.CreateIdentity("rehearsal a"); var ib = Mutual.Core.Pairing.CreateIdentity("rehearsal b");
                    var pa = new Mutual.Core.Ports(38792, 38800, 38790); var pb = new Mutual.Core.Ports(48792, 48800, 48790);
                    // "relay" tells each side wrong ports for the other so only the rendezvous server can join them
                    bool relayOnly = a.Contains("relay");
                    var toldB = relayOnly ? new Mutual.Core.Ports(59792, 59800, 59790) : pb; var toldA = relayOnly ? new Mutual.Core.Ports(59892, 59900, 59890) : pa;
                    // "names=x,y" for screenshots
                    var names = a.FirstOrDefault(x => x.StartsWith("names="))?[6..].Split(',') ?? new[] { "rehearsal a", "rehearsal b" };
                    var a1 = Mutual.Core.Pairing.FromOffer(Mutual.Core.Pairing.ReadCode(Mutual.Core.Pairing.MakeCode(names[1], ib, toldB, new[] { "127.0.0.1" })), ia, da, pa, "127.0.0.1");
                    var b1 = Mutual.Core.Pairing.FromOffer(Mutual.Core.Pairing.ReadCode(Mutual.Core.Pairing.MakeCode(names[0], ia, toldA, new[] { "127.0.0.1" })), ib, db, pb, "127.0.0.1");
                    var settingsJson = "{\"AutoAccept\": true, \"ShareSound\": false, \"HandlePings\": true, \"Rendezvous\": \"" + (a.Length > 1 && a[1].Contains(':') ? a[1] : "") + "\", \"UseUpnp\": false}";
                    File.WriteAllText(Path.Combine(da, "settings.json"), settingsJson);
                    File.WriteAllText(Path.Combine(db, "settings.json"), settingsJson);
                    Console.WriteLine($"a is {a1.Role}, b is {b1.Role}, safety code {a1.SafetyCode} / {b1.SafetyCode}");
                    return 0;
                }
                case "wincap":
                {
                    // wincap out.png: an animated window half covered by another one, grabbed with window capture
                    // the picture should show the animation all the way across, not the cover
                    Form? anim = null, cover = null; var ready = new ManualResetEventSlim();
                    var ui = new Thread(() =>
                    {
                        anim = new AnimForm { Text = "mutual wincap target", StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(200, 200, 640, 400) };
                        cover = new Form { Text = "cover", StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(500, 150, 500, 500), BackColor = Color.Magenta, TopMost = true, ShowInTaskbar = false };
                        anim.Shown += (_, _) => { cover.Show(); ready.Set(); };
                        Application.Run(anim);
                    }) { IsBackground = true };
                    ui.SetApartmentState(ApartmentState.STA); ui.Start();
                    ready.Wait(5000); Thread.Sleep(500);
                    var src = new WindowSource(anim!.Handle, "target");
                    Console.WriteLine("supported: " + WindowCapture.Supported + ", client " + src.Current());
                    using var cap = new WindowCapture(anim.Handle);
                    Vortice.Direct3D11.ID3D11Texture2D? tex = null; int frames = 0; var sw = Stopwatch.StartNew();
                    while (sw.ElapsedMilliseconds < 2000) { var t = cap.Next(cap.Normalize(src.Current()), 0); if (t != null) { tex = t; frames++; } Thread.Sleep(5); }
                    Console.WriteLine($"{frames} frames in 2 s");
                    if (tex == null) { Console.WriteLine("FAIL no frames"); return 1; }
                    using var bmp = DesktopCapture.Snapshot(cap.Device, cap.Context, tex);
                    bmp.Save(a[1]);
                    // the covered half cant have any magenta
                    int magenta = 0, total = 0;
                    for (int y = 10; y < bmp.Height - 10; y += 7) for (int x = bmp.Width / 2; x < bmp.Width - 5; x += 7) { var c = bmp.GetPixel(x, y); total++; if (c.R > 230 && c.G < 30 && c.B > 230) magenta++; }
                    Console.WriteLine($"{bmp.Width}x{bmp.Height}, covered half magenta pixels: {magenta}/{total}");
                    anim.Invoke(() => { cover!.Close(); anim.Close(); });
                    Console.WriteLine(magenta == 0 && frames > 5 ? "PASSED" : "FAILED");
                    return magenta == 0 && frames > 5 ? 0 : 1;
                }
                case "animwin":
                {
                    // animwin seconds: js shows the animated window, a target for rehearsals
                    var f = new AnimForm { Text = "mutual rehearsal target", StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(160, 160, 800, 500) };
                    var t = new System.Windows.Forms.Timer { Interval = (int)(double.Parse(a[1]) * 1000) };
                    t.Tick += (_, _) => f.Close(); t.Start();
                    Application.Run(f);
                    return 0;
                }
                case "upnp-check":
                {
                    // upnp-check: does the router do upnp. only reads, opens nothing
                    var g = Mutual.Core.Upnp.Discover();
                    if (g == null) { Console.WriteLine("no upnp gateway answered (off on the router, or not supported)"); return 0; }
                    Console.WriteLine($"gateway service {g.Service}, this pc as seen by it {g.LocalIp}");
                    var ip = Mutual.Core.Upnp.ExternalIp(g);
                    Console.WriteLine(ip == null ? "it didn't say its public address" : "public address reported (not shown)");
                    return 0;
                }
                case "splitfake":
                {
                    // splitfake: a fake splitcolony mod (announces a split, records what it gets)
                    // then a viewers input over a real link goes thru StreamHost into it
                    using var fakeMod = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 28794));
                    var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
                    var stopFake = false;
                    new Thread(() => { var from = new System.Net.IPEndPoint(0, 0); while (!stopFake) try { lines.Enqueue(System.Text.Encoding.ASCII.GetString(fakeMod.Receive(ref from))); } catch { } }) { IsBackground = true }.Start();
                    new Thread(() => { while (!stopFake) { var b = System.Text.Encoding.ASCII.GetBytes("SPLIT 1000 100 800 600"); fakeMod.Send(b, b.Length, new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 28795)); Thread.Sleep(250); } }) { IsBackground = true }.Start();
                    var src = new SplitColonySource();
                    Thread.Sleep(700);
                    Console.WriteLine($"active {src.Active}, sharing {src.Current()} ({src.Describe()})");
                    using var hostId = Mutual.Core.Pairing.CreateIdentity("lab-host");
                    using var viewId = Mutual.Core.Pairing.CreateIdentity("lab-viewer");
                    var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); l.Start();
                    int port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port; l.Stop();
                    var hs = Mutual.Core.PeerLink.OpenAsync(Mutual.Core.LinkRole.Server, System.Net.IPAddress.Loopback, "", port, hostId, viewId.RawData, TimeSpan.FromSeconds(10));
                    var vs = Mutual.Core.PeerLink.OpenAsync(Mutual.Core.LinkRole.Client, System.Net.IPAddress.Loopback, "127.0.0.1", port, viewId, hostId.RawData, TimeSpan.FromSeconds(10));
                    using var hostLink = hs.GetAwaiter().GetResult();
                    using var viewLink = vs.GetAwaiter().GetResult();
                    hostLink.ReadTimeout = viewLink.ReadTimeout = System.Threading.Timeout.Infinite;
                    var savedPos = Cursor.Position;
                    var host = new StreamHost(new Wire(hostLink), src, allowInput: true, shareSound: false);
                    using var rx = new StreamReceiver(new Wire(viewLink));
                    Thread.Sleep(500);
                    ushort N(double f) => (ushort)Math.Round(f * 65535);
                    rx.Send(new InputEvent(InputKind.Move, N(0.5), N(0.5), 0));
                    rx.Send(new InputEvent(InputKind.Down, N(0.25), N(0.75), 1));
                    rx.Send(new InputEvent(InputKind.Up, N(0.25), N(0.75), 1));
                    rx.Send(new InputEvent(InputKind.Wheel, N(0.5), N(0.5), -120));
                    rx.Send(new InputEvent(InputKind.KeyDown, 0, 0, 0x20 | 0x39 << 16));
                    rx.Send(new InputEvent(InputKind.KeyUp, 0, 0, 0x20 | 0x39 << 16));
                    Thread.Sleep(600);
                    host.Dispose();
                    Thread.Sleep(300);
                    stopFake = true;
                    var got = lines.ToArray();
                    Console.WriteLine("mod got: " + string.Join(" | ", got));
                    string[] want = { "M 1399 399", "M 1199 549", "D 1", "U 1", "W -120", "K 32 1", "K 32 0", "R" };
                    bool ok = want.All(w => got.Contains(w)) && Cursor.Position == savedPos;
                    Console.WriteLine("real cursor untouched: " + (Cursor.Position == savedPos));
                    Console.WriteLine(ok ? "PASSED" : "FAILED (wanted " + string.Join(" | ", want) + ")");
                    return ok ? 0 : 1;
                }
                case "controlcheck":
                {
                    // controlcheck: control starts off (nothing gets thru), handing it over lets input thru,
                    // taking it back releases what was held. uses a fake splitcolony mod so the real mouse never moves
                    using var fakeMod = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 28794));
                    var lines = new System.Collections.Concurrent.ConcurrentQueue<string>(); var stopFake = false;
                    new Thread(() => { var from = new System.Net.IPEndPoint(0, 0); while (!stopFake) try { lines.Enqueue(System.Text.Encoding.ASCII.GetString(fakeMod.Receive(ref from))); } catch { } }) { IsBackground = true }.Start();
                    new Thread(() => { while (!stopFake) { var b = System.Text.Encoding.ASCII.GetBytes("SPLIT 1000 100 800 600"); fakeMod.Send(b, b.Length, new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 28795)); Thread.Sleep(250); } }) { IsBackground = true }.Start();
                    var src = new SplitColonySource(); Thread.Sleep(500);
                    using var hostId = Mutual.Core.Pairing.CreateIdentity("h"); using var viewId = Mutual.Core.Pairing.CreateIdentity("v");
                    var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); l.Start(); int port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port; l.Stop();
                    var hs = Mutual.Core.PeerLink.OpenAsync(Mutual.Core.LinkRole.Server, System.Net.IPAddress.Loopback, "", port, hostId, viewId.RawData, TimeSpan.FromSeconds(10));
                    var vs = Mutual.Core.PeerLink.OpenAsync(Mutual.Core.LinkRole.Client, System.Net.IPAddress.Loopback, "127.0.0.1", port, viewId, hostId.RawData, TimeSpan.FromSeconds(10));
                    using var hostLink = hs.GetAwaiter().GetResult(); using var viewLink = vs.GetAwaiter().GetResult();
                    hostLink.ReadTimeout = viewLink.ReadTimeout = System.Threading.Timeout.Infinite;
                    var host = new StreamHost(new Wire(hostLink), src, allowInput: false, shareSound: false);
                    using var rx = new StreamReceiver(new Wire(viewLink));
                    Thread.Sleep(500);
                    string[] Take() { var r = new List<string>(); while (lines.TryDequeue(out var x)) if (!x.StartsWith("R")) r.Add(x); return r.ToArray(); }
                    Take();
                    rx.Send(new InputEvent(InputKind.KeyDown, 0, 0, 0x41)); Thread.Sleep(300);
                    var off = Take();
                    Console.WriteLine($"control off: viewer can control {rx.CanControl}, mod got [{string.Join(" | ", off)}]");
                    host.SetControl(true); Thread.Sleep(300);
                    rx.Send(new InputEvent(InputKind.KeyDown, 0, 0, 0x41)); Thread.Sleep(300);
                    var on = Take();
                    Console.WriteLine($"control on: viewer can control {rx.CanControl}, mod got [{string.Join(" | ", on)}]");
                    host.SetControl(false); Thread.Sleep(300);
                    var back = Take();
                    Console.WriteLine($"taken back: viewer can control {rx.CanControl}");
                    host.Dispose(); stopFake = true;
                    Console.WriteLine($"released on take back: [{string.Join(" | ", back)}]");
                    bool ok = off.Length == 0 && on.Contains("K 65 1") && !rx.CanControl && back.Contains("K 65 0");
                    Console.WriteLine(ok ? "PASSED" : "FAILED");
                    return ok ? 0 : 1;
                }
                case "pairbegin":
                case "pairfinish":
                {
                    // pairbegin profile name address: makes a --profile copy (own ports, auto accept, no install
                    // offer) and prints its pairing code. pairfinish profile code friendaddress: saves the pairing
                    // and prints the safety code to read out. for live tests on a pc nobody is sitting at
                    string profile = a[1];
                    var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mutual", "profiles", profile);
                    Directory.CreateDirectory(dir);
                    var idPath = Path.Combine(dir, "identity.bin");
                    var myPorts = new Mutual.Core.Ports(Ping: 28892, Stream: 28900, Consent: 28890);
                    if (a[0] == "pairbegin")
                    {
                        if (!File.Exists(idPath))
                            File.WriteAllBytes(idPath, Mutual.Core.Dpapi.Protect(Mutual.Core.Pairing.CreateIdentity("Mutual " + a[2]).Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx)));
                        File.WriteAllText(Path.Combine(dir, "settings.json"), System.Text.Json.JsonSerializer.Serialize(new
                        {
                            MyName = a[2], HandlePings = true, Rendezvous = "", UseUpnp = false, ShareSound = true, ShareClipboard = false,
                            DeclinedInstall = true, ShortcutsMade = true, AutoAccept = true,
                        }));
                    }
                    using var id = new System.Security.Cryptography.X509Certificates.X509Certificate2(Mutual.Core.Dpapi.Unprotect(File.ReadAllBytes(idPath)), (string?)null,
                        System.Security.Cryptography.X509Certificates.X509KeyStorageFlags.UserKeySet | System.Security.Cryptography.X509Certificates.X509KeyStorageFlags.Exportable);
                    if (a[0] == "pairbegin") { Console.WriteLine(Mutual.Core.Pairing.MakeCode(a[2], id, myPorts, new[] { a[3] })); return 0; }
                    var offer = Mutual.Core.Pairing.ReadCode(a[2]);
                    var pairing = Mutual.Core.Pairing.FromOffer(offer, id, dir, myPorts, a[3]);
                    Console.WriteLine($"paired with {offer.Name} at {a[3]}, their ports {offer.Ports}, role {pairing.Role}");
                    Console.WriteLine("safety code: " + Mutual.Core.Pairing.SafetyCodeFor(id.RawData, offer.Cert));
                    return 0;
                }
                case "splitrelay":
                {
                    // splitrelay: the real game, the real stream. hosts the actual splitcolony right half to a
                    // viewer over a real link and takes the test scripts lines (M x y, D b, K vk 1, ...) on udp
                    // 28796 as if the viewer did them, so every click goes viewer -> link -> host -> mod.
                    // DBG and T lines go straight to the mod. SHOT path saves the next frame the viewer decodes
                    using var src = new SplitColonySource();
                    var wait = Stopwatch.StartNew();
                    while (!src.Active && wait.Elapsed.TotalSeconds < 5) Thread.Sleep(100);
                    Console.WriteLine($"split {(src.Active ? "up" : "not up yet")}, sharing {src.Current()}");
                    using var hostId = Mutual.Core.Pairing.CreateIdentity("relay-host");
                    using var viewId = Mutual.Core.Pairing.CreateIdentity("relay-viewer");
                    var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); l.Start();
                    int port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port; l.Stop();
                    var hs = Mutual.Core.PeerLink.OpenAsync(Mutual.Core.LinkRole.Server, System.Net.IPAddress.Loopback, "", port, hostId, viewId.RawData, TimeSpan.FromSeconds(10));
                    var vs = Mutual.Core.PeerLink.OpenAsync(Mutual.Core.LinkRole.Client, System.Net.IPAddress.Loopback, "127.0.0.1", port, viewId, hostId.RawData, TimeSpan.FromSeconds(10));
                    using var hostLink = hs.GetAwaiter().GetResult();
                    using var viewLink = vs.GetAwaiter().GetResult();
                    hostLink.ReadTimeout = viewLink.ReadTimeout = System.Threading.Timeout.Infinite;
                    var host = new StreamHost(new Wire(hostLink), src, allowInput: true, shareSound: false);
                    using var rx = new StreamReceiver(new Wire(viewLink));
                    string? shotPath = null; int frameW = 0, frameH = 0;
                    rx.FrameReady += (tex, w, h) =>
                    {
                        frameW = w; frameH = h;
                        var path = Interlocked.Exchange(ref shotPath, null);
                        if (path == null) return;
                        try { SaveTexture(rx.Device, rx.Context, tex, w, h, path); Console.WriteLine("saved " + path); }
                        catch (Exception e) { Console.WriteLine("shot failed: " + e.Message); }
                    };
                    host.Status += s => Console.WriteLine("host: " + s);
                    rx.Ended += s => Console.WriteLine("viewer: stream ended (" + s + ")");

                    // screen pixel -> the 0..65535 the viewer would send, picked so the host lands on that exact pixel
                    ushort To(int p, int origin, int size)
                    {
                        int span = Math.Max(1, size - 1);
                        int n = Math.Clamp((int)Math.Round((p - origin) * 65535.0 / span), 0, 65535);
                        for (int d = -3; d <= 3; d++)
                        {
                            int c = Math.Clamp(n + d, 0, 65535);
                            if (origin + (int)(c / 65535.0 * span) == p) return (ushort)c;
                        }
                        return (ushort)n;
                    }
                    var modEp = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 28794);
                    using var toMod = new System.Net.Sockets.UdpClient();
                    using var cmds = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 28796));
                    Console.WriteLine("relay up on 28796");
                    ushort lx = 0, ly = 0; var from = new System.Net.IPEndPoint(0, 0);
                    var stats = Stopwatch.StartNew();
                    while (true)
                    {
                        if (stats.Elapsed.TotalSeconds >= 10)
                        {
                            stats.Restart();
                            Console.WriteLine($"stats: sent {host.FramesSent}, decoded {rx.FramesDecoded} at {frameW}x{frameH}, {rx.Transport}, decode {rx.DecodeMs:F1} ms, control {rx.CanControl}");
                        }
                        if (cmds.Available == 0) { Thread.Sleep(2); continue; }
                        var line = System.Text.Encoding.ASCII.GetString(cmds.Receive(ref from)).Trim();
                        var p = line.Split(' ');
                        if (p[0] == "QUIT") break;
                        if (p[0] == "SHOT") { shotPath = line.Substring(5); continue; }
                        if (p[0] == "DBG" || p[0] == "T") { var b = System.Text.Encoding.ASCII.GetBytes(line); toMod.Send(b, b.Length, modEp); continue; }
                        var r = src.Current();
                        try
                        {
                            switch (p[0])
                            {
                                case "M":
                                    lx = To(int.Parse(p[1]), r.X, r.Width); ly = To(int.Parse(p[2]), r.Y, r.Height);
                                    rx.Send(new InputEvent(InputKind.Move, lx, ly, 0)); break;
                                case "D": rx.Send(new InputEvent(InputKind.Down, lx, ly, int.Parse(p[1]))); break;
                                case "U": rx.Send(new InputEvent(InputKind.Up, lx, ly, int.Parse(p[1]))); break;
                                case "W": rx.Send(new InputEvent(InputKind.Wheel, lx, ly, int.Parse(p[1]))); break;
                                case "K": rx.Send(new InputEvent(p[2] == "1" ? InputKind.KeyDown : InputKind.KeyUp, 0, 0, int.Parse(p[1]))); break;
                                default: Console.WriteLine("didnt get: " + line); break;
                            }
                        }
                        catch (FormatException) { Console.WriteLine("didnt get: " + line); }
                    }
                    Console.WriteLine($"done: sent {host.FramesSent}, decoded {rx.FramesDecoded}, {rx.Transport}");
                    host.Dispose();
                    return 0;
                }
                case "encbench":
                {
                    // encbench w h seconds: the encoder alone, same frame over and over
                    int w = int.Parse(a[1]), h = int.Parse(a[2]); double seconds = double.Parse(a[3]);
                    using var cap = DesktopCapture.ForPoint(new Point(0, 0));
                    var tex = cap.Next(new Rectangle(0, 0, w, h), 500) ?? cap.Next(new Rectangle(0, 0, w, h), 500);
                    if (tex == null) { Console.WriteLine("no frame"); return 1; }
                    using var conv = new GpuColorConverter(cap.Device, (uint)w, (uint)h, Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DXGI.Format.NV12);
                    using var enc = new H264Encoder(cap.Device, w, h, 60, 12_000_000);
                    int outFrames = 0; double latSum = 0;
                    var sentAt = new System.Collections.Concurrent.ConcurrentDictionary<long, long>();
                    enc.Encoded += (d, ts, k) => { outFrames++; if (sentAt.TryRemove(ts, out var t0)) latSum += (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency; };
                    var sw = Stopwatch.StartNew(); int n = 0;
                    while (sw.Elapsed.TotalSeconds < seconds)
                    {
                        long ts = n * 166_666L;
                        sentAt[ts] = Stopwatch.GetTimestamp();
                        enc.Submit(conv.Convert(tex), ts); n++;
                        Thread.Sleep(int.Parse(a.Length > 4 ? a[4] : "16"));
                    }
                    Thread.Sleep(300);
                    Console.WriteLine($"{enc.Name}: submitted {n}, encoded {outFrames} ({outFrames / sw.Elapsed.TotalSeconds:F1} fps), latency avg {latSum / Math.Max(1, outFrames):F1} ms");
                    return 0;
                }
                default:
                    Console.WriteLine("monitors | capture x y w h frames out.png | encode x y w h seconds out.h264");
                    return 1;
            }
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 2; }
    }
}

sealed class AnimForm : Form
{
    int n;
    readonly System.Windows.Forms.Timer t = new() { Interval = 15 };
    // when each frame was drawn, stamped top left as 24 black/white blocks (ms, low 24 bits)
    public static readonly Stopwatch Clock = Stopwatch.StartNew();
    public const int Bits = 24, Block = 16;
    public AnimForm() { DoubleBuffered = true; t.Tick += (_, _) => { n++; Invalidate(); }; t.Start(); }
    protected override bool ShowWithoutActivation => true;
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Color.FromArgb(20, 24, 30));
        var rng = new Random(n);
        for (int i = 0; i < 60; i++)
            using (var b = new SolidBrush(Color.FromArgb(rng.Next(256), rng.Next(256), rng.Next(256))))
                g.FillEllipse(b, (float)((Math.Sin(n * 0.03 + i) * 0.45 + 0.5) * Width), (float)((Math.Cos(n * 0.021 + i * 1.7) * 0.45 + 0.5) * Height), 60, 60);
        g.DrawString(n.ToString(), new Font("Consolas", 40), Brushes.White, 20, 40);
        long ms = Clock.ElapsedMilliseconds & 0xFFFFFF;
        for (int b = 0; b < Bits; b++) g.FillRectangle(((ms >> b) & 1) != 0 ? Brushes.White : Brushes.Black, b * Block, 0, Block, Block);
    }

    // reads the stamp back out of a decoded frame, -1 if its not there
    public static long ReadStamp(Vortice.Direct3D11.ID3D11Device dev, Vortice.Direct3D11.ID3D11DeviceContext ctx, Vortice.Direct3D11.ID3D11Texture2D tex, ref Vortice.Direct3D11.ID3D11Texture2D? staging)
    {
        staging ??= dev.CreateTexture2D(new Vortice.Direct3D11.Texture2DDescription
        {
            Width = Bits * Block, Height = Block, MipLevels = 1, ArraySize = 1, Format = Vortice.DXGI.Format.B8G8R8A8_UNorm,
            SampleDescription = new Vortice.DXGI.SampleDescription(1, 0), Usage = Vortice.Direct3D11.ResourceUsage.Staging, CPUAccessFlags = Vortice.Direct3D11.CpuAccessFlags.Read,
        });
        ctx.CopySubresourceRegion(staging, 0, 0, 0, 0, tex, 0, new Vortice.Mathematics.Box(0, 0, 0, Bits * Block, Block, 1));
        var map = ctx.Map(staging, 0, Vortice.Direct3D11.MapMode.Read);
        try
        {
            long v = 0;
            unsafe
            {
                byte* row = (byte*)map.DataPointer + (Block / 2) * map.RowPitch;
                for (int b = 0; b < Bits; b++)
                {
                    int g = row[(b * Block + Block / 2) * 4 + 1];
                    if (g > 90 && g < 165) return -1;   // not black or white, not a stamp
                    if (g >= 165) v |= 1L << b;
                }
            }
            return v;
        }
        finally { ctx.Unmap(staging, 0); }
    }
}

static class Inject
{
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] struct MI { public int dx, dy; public uint data, flags, time; public nint extra; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] struct KI { public ushort vk, scan; public uint flags, time; public nint extra; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)] struct U { [System.Runtime.InteropServices.FieldOffset(0)] public MI mi; [System.Runtime.InteropServices.FieldOffset(0)] public KI ki; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] struct IN { public uint type; public U u; }
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern uint SendInput(uint n, IN[] i, int size);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern uint MapVirtualKey(uint c, uint t);
    static void M(uint flags, int dx = 0, int dy = 0, uint data = 0) { var i = new IN { type = 0 }; i.u.mi = new MI { dx = dx, dy = dy, flags = flags, data = data }; SendInput(1, new[] { i }, System.Runtime.InteropServices.Marshal.SizeOf<IN>()); }
    public static void Click() { M(0x2); Thread.Sleep(30); M(0x4); }
    public static void Move(int dx, int dy) => M(0x1, dx, dy);
    public static void Wheel(int delta) => M(0x800, data: (uint)delta);
    static bool Ext(ushort vk) => vk is >= 0x21 and <= 0x2E;
    public static void KeyDown(ushort vk) { var i = new IN { type = 1 }; i.u.ki = new KI { vk = vk, scan = (ushort)MapVirtualKey(vk, 0), flags = Ext(vk) ? 1u : 0u }; SendInput(1, new[] { i }, System.Runtime.InteropServices.Marshal.SizeOf<IN>()); }
    public static void KeyUp(ushort vk) { var i = new IN { type = 1 }; i.u.ki = new KI { vk = vk, scan = (ushort)MapVirtualKey(vk, 0), flags = (Ext(vk) ? 1u : 0u) | 2u }; SendInput(1, new[] { i }, System.Runtime.InteropServices.Marshal.SizeOf<IN>()); }
    public static void Key(ushort vk) { KeyDown(vk); Thread.Sleep(30); KeyUp(vk); }
}
