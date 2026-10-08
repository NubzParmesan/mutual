using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;

namespace Mutual.Stream;

// the sharing side. grabs the picture, encodes it, sends it, and plays their mouse and keys back
// onto whats shared if thats allowed. resizing the box or window rebuilds the encoder
public sealed class StreamHost : IDisposable
{
    readonly Wire wire;
    readonly StreamSource source;
    readonly int fps;
    int bitrate;
    readonly int maxBitrate;
    readonly UdpVideo? udp;
    SoundCapture? sound;
    OpusPacker? opus;
    H264Encoder? liveEncoder;
    bool udpWasUp;
    readonly Thread captureThread, inputThread;
    volatile bool running = true;
    volatile bool keyframeWanted = true;
    public bool AllowInput { get; private set; }

    // hands control over or takes it back, and tells the viewer. taking it back lets go of
    // anything they were holding down so nothing stays pressed
    public void SetControl(bool on)
    {
        AllowInput = on;
        if (!on) ReleaseHeld();
        try { wire.Send(Msg.Control, new[] { (byte)(on ? 1 : 0) }); } catch { }
    }

    readonly HashSet<int> heldKeys = new(), heldButtons = new();
    void ReleaseHeld()
    {
        lock (heldKeys)
        {
            var r = source.Current();
            int cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
            // a source that takes input itself (rimworld split) gets the releases too, not the real keyboard
            if (source is IInputSink sink)
            {
                foreach (var k in heldKeys) sink.Handle(new InputEvent(InputKind.KeyUp, 0, 0, k), cx, cy);
                foreach (var btn in heldButtons) sink.Handle(new InputEvent(InputKind.Up, 0, 0, btn), cx, cy);
            }
            else
            {
                foreach (var k in heldKeys) Key(k, true);
                foreach (var btn in heldButtons) Mouse(cx, cy, MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK | ButtonFlag(btn, false), 0);
            }
            heldKeys.Clear(); heldButtons.Clear();
        }
    }
    // they closed it on purpose (not the link breaking)
    public bool EndedByViewer { get; private set; }
    public event Action<string>? ClipboardReceived;
    public void SendClipboard(string text) { try { wire.Send(Msg.Clipboard, System.Text.Encoding.UTF8.GetBytes(text)); } catch { } }
    public event Action<string>? Status;
    public Exception? Failure { get; private set; }
    public int FramesSent { get; private set; }
    public double LastEncodeMs { get; private set; }
    // when the frame timestamps start, for latency tests
    public long ClockStart { get; private set; }
    public ViewerStats? LastStats { get; private set; }
    public int Bitrate => bitrate;
    public string Transport => udp?.Up == true ? "udp" : "tcp";
    public Exception? SoundFailure => sound?.Failure;

    // openUdp makes the udp lane with the key this host just made. null keeps video on tcp
    public StreamHost(Wire wire, StreamSource source, int fps = 60, int bitrate = 12_000_000, bool allowInput = true, Func<byte[], UdpVideo>? openUdp = null, bool shareSound = true)
    {
        this.wire = wire; this.source = source; this.fps = fps; this.bitrate = this.maxBitrate = bitrate;
        SetControl(allowInput);   // the viewer only sends input once its told it can
        if (openUdp != null)
        {
            try
            {
                var key = UdpVideo.NewKey();
                udp = openUdp(key);
                wire.Send(Msg.UdpSetup, key);
            }
            catch (Exception e) { udp?.Dispose(); udp = null; Status?.Invoke("udp unavailable, video stays on tcp: " + e.Message); }
        }
        if (shareSound) { opus = new OpusPacker(); StartSound(); }
        captureThread = new Thread(CaptureLoop) { IsBackground = true, Name = "mutual capture" };
        inputThread = new Thread(InputLoop) { IsBackground = true, Name = "mutual host input" };
        captureThread.Start();
        inputThread.Start();
        new Thread(() => { while (running) { Thread.Sleep(2000); try { wire.Send(Msg.Heartbeat, ReadOnlySpan<byte>.Empty); } catch { running = false; } } })
        { IsBackground = true, Name = "mutual host heartbeat" }.Start();
    }

    [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint ms);

    void CaptureLoop()
    {
        timeBeginPeriod(1);
        ICapture? cap = null; GpuColorConverter? conv = null; H264Encoder? enc = null;
        Size encSize = Size.Empty; Size pending = Size.Empty; var pendingSince = Stopwatch.StartNew();
        var clock = Stopwatch.StartNew(); ClockStart = Stopwatch.GetTimestamp(); var tick = Stopwatch.StartNew(); bool fresh = false; double due = 0; nint grabbed = 0; var regrab = Stopwatch.StartNew();
        var sentAt = new System.Collections.Concurrent.ConcurrentDictionary<long, long>();
        var pointer = new HostCursor();
        try
        {
          bool paused = false;
          while (running)
          {
            try
            {
            if (cap == null)
            {
                // one window gets grabbed on its own (even covered) if windows can, otherwise its spot on screen
                grabbed = source.CaptureWindow;
                var grab = grabbed;
                cap = grab != 0 && WindowCapture.Supported ? new WindowCapture(grab) : DesktopCapture.ForPoint(source.Anchor);
                Status?.Invoke((paused ? "resumed: " : "sharing ") + source.Describe() + " from " + cap.MonitorName);
                paused = false;
            }
            while (running)
            {
                // the game restarted (new window) or opened after we started on the screen: grab the new one
                // instead of freezing on a dead window. if its just closed, stay frozen on the last picture,
                // falling back to the screen there would show them your desktop
                if (regrab.ElapsedMilliseconds > 1000)
                {
                    regrab.Restart();
                    var now0 = source.CaptureWindow;
                    if (now0 != 0 && now0 != grabbed && WindowCapture.Supported)
                    {
                        Status?.Invoke("the window went away or came back, grabbing it again");
                        if (opus != null) StartSound();   // new game process, so its sound is a new program too
                        enc?.Dispose(); conv?.Dispose(); cap?.Dispose();
                        enc = null; conv = null; cap = null; liveEncoder = null; encSize = Size.Empty;
                        break;
                    }
                }
                var want = cap.Normalize(source.Current());
                // only rebuild once the size settles, not every pixel of a drag
                if (want.Size != encSize)
                {
                    if (want.Size != pending) { pending = want.Size; pendingSince.Restart(); }
                    if (enc == null || pendingSince.ElapsedMilliseconds > 250)
                    {
                        enc?.Dispose(); conv?.Dispose();
                        conv = new GpuColorConverter(cap.Device, (uint)want.Width, (uint)want.Height, Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DXGI.Format.NV12);
                        enc = new H264Encoder(cap.Device, want.Width, want.Height, fps, bitrate);
                        liveEncoder = enc;
                        enc.Encoded += (data, ts, key) =>
                        {
                            try
                            {
                                if (udp?.Up == true) udp.SendFrame(ts, key, data);
                                else wire.SendVideo(ts, key, data);
                                FramesSent++;
                            }
                            catch { running = false; }
                            if (sentAt.TryRemove(ts, out var t0)) LastEncodeMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                        };
                        encSize = want.Size;
                        wire.SendJson(Msg.Hello, new StreamInfo(want.Width, want.Height, fps, source.Describe(), enc.Name));
                        keyframeWanted = true;
                    }
                }
                if (enc != null) try { pointer.Poll(wire, want); } catch (IOException) { running = false; break; }
                // a frame that shows up a hair early gets held till its time, not thrown out. throwing it out
                // made anything running just over 60 (or a 144hz screen) come thru at half rate
                if (cap.Next(want, 0) != null) fresh = true;
                // sends go on a fixed beat off the clock. restarting a timer each send let every late wakeup add up
                double now = clock.Elapsed.TotalMilliseconds;
                if (now < due) { Thread.Sleep(1); continue; }
                // nothing changed, still send a frame now and then so someone joining late gets a picture
                if (!fresh && tick.ElapsedMilliseconds < 500) { Thread.Sleep(1); continue; }
                var tex = cap.LastFrame;
                if (tex == null) { Thread.Sleep(1); continue; }
                fresh = false;
                due = Math.Max(due + 1000.0 / fps, now - 1000.0 / fps);
                tick.Restart();
                if (enc == null || conv == null || want.Size != encSize) continue;
                // switching lanes (udp came up or fell back to tcp) starts the viewer over from a keyframe
                bool udpUp = udp?.Up == true;
                if (udpUp != udpWasUp) { udpWasUp = udpUp; keyframeWanted = true; Status?.Invoke(udpUp ? "video on udp" : "video on tcp"); }
                if (keyframeWanted) { keyframeWanted = false; enc.RequestKeyFrame(); }
                long ts = clock.ElapsedTicks * 10_000_000 / Stopwatch.Frequency;
                sentAt[ts] = Stopwatch.GetTimestamp();
                enc.Submit(conv.Convert(tex), ts);
                if (enc.Failure != null) throw enc.Failure;
            }
            }
            catch (SharpGen.Runtime.SharpGenException e) when (running && DesktopUnavailable(e.HResult))
            {
                // lock screen, screensaver or an admin prompt took the screen (or the display changed mode)
                // windows wont let anyone capture that so start over once its back, the viewer keeps the last picture meanwhile
                if (!paused) Status?.Invoke("paused: this screen can't be captured right now (lock screen, screensaver or admin prompt), waiting");
                paused = true;
                enc?.Dispose(); conv?.Dispose(); cap?.Dispose();
                enc = null; conv = null; cap = null; liveEncoder = null; encSize = Size.Empty;
                Thread.Sleep(1000);
            }
          }
        }
        catch (Exception e) { if (running) { Failure = e; Status?.Invoke("stream stopped: " + e.Message); } }   // a throw during shutdown would kill the app off this thread
        finally
        {
            running = false;
            enc?.Dispose(); conv?.Dispose(); cap?.Dispose();
            timeEndPeriod(1);
        }
    }

    // E_ACCESSDENIED (secure desktop), DXGI_ERROR_ACCESS_LOST (desktop switch, mode change)
    // DXGI_ERROR_SESSION_DISCONNECTED, DXGI_ERROR_DEVICE_REMOVED/RESET (driver hiccup)
    static bool DesktopUnavailable(int hr) => (uint)hr is 0x80070005 or 0x887A0026 or 0x887A0028 or 0x887A0005 or 0x887A0007;

    void InputLoop()
    {
        try
        {
            while (running)
            {
                var m = wire.Read();
                if (m == null) break;
                var (kind, p) = m.Value;
                switch (kind)
                {
                    case Msg.KeyFrame: keyframeWanted = true; break;
                    case Msg.Input: if (AllowInput) Inject(Wire.ReadInput(p)); break;
                    case Msg.Stats: { var st = Wire.Json<ViewerStats>(p); if (st != null) { LastStats = st; Adapt(st); } break; }
                    case Msg.Bye: EndedByViewer = true; running = false; break;
                    case Msg.Clipboard: ClipboardReceived?.Invoke(System.Text.Encoding.UTF8.GetString(p)); break;
                }
            }
        }
        catch (Exception) { }
        running = false;
        Status?.Invoke("viewer left");
    }

    // bitrate that follows the connection

    int calmReports;
    double baseRtt = double.MaxValue;
    // every viewer report (abt every 2 s). scattered loss that parity fixes isnt a reason to drop quality
    // (wifi loses packets even when its not full). frames that couldnt be fixed, heavy loss or ping climbing
    // means the link is full so back off. clean for a while, creep back up
    // set by whoever runs the stream, adds up the reports for the summary at the end
    public SessionTally? Tally { get; set; }

    void Adapt(ViewerStats st)
    {
        Tally?.Add(st, bitrate);
        int next = bitrate;
        if (st.NetworkMs > 0) baseRtt = Math.Min(baseRtt, st.NetworkMs);
        bool queueing = st.NetworkMs > 0 && baseRtt < double.MaxValue && st.NetworkMs > baseRtt + 40;
        if (st.Loss > 0.20 || st.FramesLost > 2) { next = (int)(bitrate * 0.6); calmReports = 0; }
        else if (st.Loss > 0.12 || st.FramesLost > 0 || queueing) { next = (int)(bitrate * 0.85); calmReports = 0; }
        else if (++calmReports >= 3) { next = (int)(bitrate * 1.15); calmReports = 0; }
        next = Math.Clamp(next, 1_500_000, maxBitrate);
        if (udp != null) udp.ExpectedLoss = st.Loss;
        if (Math.Abs(next - bitrate) < 100_000) return;
        bitrate = next;
        liveEncoder?.SetBitrate(next);
        Status?.Invoke($"bitrate {next / 1_000_000.0:F1} Mbit/s (loss {st.Loss:P1}, {st.Transport})");
    }

    // sound: sharing a game or a window sends just that program's sound. otherwise its everything except
    // discord. grabbing everything sent your friend's own voice back to them out of your speakers
    void StartSound()
    {
        var (pid, include) = SoundTarget();
        var old = sound;
        var next = new SoundCapture(pid, include);
        next.Chunk += (_, pcm) =>
        {
            if (!ReferenceEquals(sound, next) || opus == null) return;
            foreach (var packed in opus.Add(pcm))
                try { if (udp?.Up == true) udp.SendAudio(packed); else wire.Send(Msg.Audio, packed); } catch { }
        };
        sound = next;
        old?.Dispose();
        Task.Delay(400).ContinueWith(_ => { if (ReferenceEquals(sound, next)) Status?.Invoke("sound: " + next.Grabbing); });
    }

    (int pid, bool include) SoundTarget()
    {
        var w = source.CaptureWindow;
        if (w != 0 && GetWindowThreadProcessId(w, out uint pid) != 0 && pid != 0) return ((int)pid, true);
        var discord = TopProcess("Discord", "DiscordPTB", "DiscordCanary");
        return discord != 0 ? (discord, false) : (0, true);
    }

    // the one at the top of the tree (the others are its children), so excluding it covers all of them
    static int TopProcess(params string[] names)
    {
        var ids = new HashSet<int>();
        foreach (var n in names) foreach (var p in Process.GetProcessesByName(n)) { ids.Add(p.Id); p.Dispose(); }
        foreach (var id in ids)
        {
            try
            {
                using var p = Process.GetProcessById(id);
                var pbi = new PROCESS_BASIC_INFORMATION();
                if (NtQueryInformationProcess(p.Handle, 0, ref pbi, Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(), out _) == 0 && !ids.Contains((int)pbi.InheritedFromUniqueProcessId))
                    return id;
            }
            catch { }
        }
        return 0;
    }

    [StructLayout(LayoutKind.Sequential)] struct PROCESS_BASIC_INFORMATION { public nint ExitStatus, PebBaseAddress, AffinityMask, BasePriority, UniqueProcessId, InheritedFromUniqueProcessId; }
    [DllImport("ntdll.dll")] static extern int NtQueryInformationProcess(nint process, int infoClass, ref PROCESS_BASIC_INFORMATION info, int size, out int returned);

    // playing their input back

    void Inject(InputEvent e)
    {
        lock (heldKeys)
        {
            if (e.Kind == InputKind.KeyDown) heldKeys.Add(e.Value); else if (e.Kind == InputKind.KeyUp) heldKeys.Remove(e.Value);
            if (e.Kind == InputKind.Down) heldButtons.Add(e.Value); else if (e.Kind == InputKind.Up) heldButtons.Remove(e.Value);
        }
        var r = source.Current();
        // a covered window still streams but clicks land on whatevers on top, so bring it forward first
        if (e.Kind == InputKind.Down && source is WindowSource win) BringForward(win.Handle);
        int x = r.X + (int)(e.X / 65535.0 * Math.Max(1, r.Width - 1));
        int y = r.Y + (int)(e.Y / 65535.0 * Math.Max(1, r.Height - 1));
        if (source is IInputSink sink) { sink.Handle(e, x, y); return; }
        // never let their clicks land on mutual itself (send file, settings, accepting their own invites)
        if (e.Kind is InputKind.Down or InputKind.Wheel or InputKind.HWheel && OwnWindowAt(x, y)) return;
        switch (e.Kind)
        {
            case InputKind.Move: Mouse(x, y, MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK, 0); break;
            case InputKind.Down: Mouse(x, y, MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK | ButtonFlag(e.Value, true), 0); break;
            case InputKind.Up: Mouse(x, y, MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK | ButtonFlag(e.Value, false), 0); break;
            case InputKind.Wheel: Mouse(x, y, MOUSEEVENTF_WHEEL, (uint)e.Value); break;
            case InputKind.HWheel: Mouse(x, y, MOUSEEVENTF_HWHEEL, (uint)e.Value); break;
            case InputKind.KeyDown: if (KeyAllowed(e.Value)) Key(e.Value, false); break;
            case InputKind.KeyUp: Key(e.Value, true); break;   // key ups always go thru so nothing sticks
        }
    }

    // keys go to whatever has focus on this pc so they only go thru while the shared thing has it
    // (sharing a box of a game shouldnt let them type in your discord). windows key only when the whole screen is shared
    bool KeyAllowed(int code)
    {
        int vk = code & 0xFFFF;
        if (IsOwn(GetForegroundWindow())) return false;   // no typing into mutual itself
        if (source is ScreenSource) return true;
        if (vk is 0x5B or 0x5C) return false;
        var fg = GetAncestor(GetForegroundWindow(), 2);   // GA_ROOT
        switch (source)
        {
            case WindowSource w: return fg == GetAncestor(w.Handle, 2) || GetWindow(GetForegroundWindow(), 4) == w.Handle;   // or one of its popups (GW_OWNER)
            default:
                var r = source.Current();
                var under = GetAncestor(WindowFromPoint(new POINTSTRUCT { x = r.X + r.Width / 2, y = r.Y + r.Height / 2 }), 2);
                return under != 0 && under == fg;
        }
    }
    static bool OwnWindowAt(int x, int y) => IsOwn(WindowFromPoint(new POINTSTRUCT { x = x, y = y }));
    static bool IsOwn(nint h)
    {
        if (h == 0) return false;
        GetWindowThreadProcessId(h, out uint pid);
        return pid == (uint)Environment.ProcessId;
    }
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint h, out uint pid);
    [StructLayout(LayoutKind.Sequential)] struct POINTSTRUCT { public int x, y; }
    [DllImport("user32.dll")] static extern nint WindowFromPoint(POINTSTRUCT p);
    [DllImport("user32.dll")] static extern nint GetAncestor(nint h, uint flags);
    [DllImport("user32.dll")] static extern nint GetWindow(nint h, uint cmd);

    static void BringForward(nint hwnd)
    {
        if (GetForegroundWindow() == hwnd) return;
        if (IsIconic(hwnd)) ShowWindow(hwnd, 9);   // SW_RESTORE
        // windows only lets the app with the latest input take focus, a quick alt tap makes that us
        keybd_event(0x12, 0, 0, 0); keybd_event(0x12, 0, 2, 0);
        SetForegroundWindow(hwnd);
    }
    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(nint h);
    [DllImport("user32.dll")] static extern bool IsIconic(nint h);
    [DllImport("user32.dll")] static extern bool ShowWindow(nint h, int cmd);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, nint extra);

    const uint MOUSEEVENTF_MOVE = 0x1, MOUSEEVENTF_LEFTDOWN = 0x2, MOUSEEVENTF_LEFTUP = 0x4, MOUSEEVENTF_RIGHTDOWN = 0x8, MOUSEEVENTF_RIGHTUP = 0x10,
        MOUSEEVENTF_MIDDLEDOWN = 0x20, MOUSEEVENTF_MIDDLEUP = 0x40, MOUSEEVENTF_WHEEL = 0x800, MOUSEEVENTF_HWHEEL = 0x1000, MOUSEEVENTF_VIRTUALDESK = 0x4000, MOUSEEVENTF_ABSOLUTE = 0x8000;
    static uint ButtonFlag(int b, bool down) => b switch
    {
        1 => down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP,
        2 => down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP,
        _ => down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP,
    };

    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint data, flags, time; public nint extra; }
    [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort vk, scan; public uint flags, time; public nint extra; }
    [StructLayout(LayoutKind.Explicit)] struct INPUTUNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public INPUTUNION u; }
    [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] i, int size);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint mapType);

    static void Mouse(int x, int y, uint flags, uint data)
    {
        int vx = GetSystemMetrics(76), vy = GetSystemMetrics(77), vw = GetSystemMetrics(78), vh = GetSystemMetrics(79);
        var i = new INPUT { type = 0 };
        i.u.mi = new MOUSEINPUT
        {
            dx = (int)Math.Round((x - vx) * 65535.0 / Math.Max(1, vw - 1)),
            dy = (int)Math.Round((y - vy) * 65535.0 / Math.Max(1, vh - 1)),
            data = data, flags = flags,
        };
        SendInput(1, new[] { i }, Marshal.SizeOf<INPUT>());
    }

    // virtual key in the low 16 bits, scan code and extended flag above it if the viewer sent them
    static void Key(int code, bool up)
    {
        var i = new INPUT { type = 1 };
        ushort vk = (ushort)(code & 0xFFFF);
        uint scan = (uint)(code >> 16) & 0xFF;
        bool ext = code > 0xFFFF ? ((code >> 24) & 1) != 0 : IsExtended(vk);
        if (scan == 0) scan = MapVirtualKey(vk, 0);
        uint flags = (up ? 0x2u : 0u) | (ext ? 0x1u : 0u);
        i.u.ki = new KEYBDINPUT { vk = vk, scan = (ushort)scan, flags = flags };
        SendInput(1, new[] { i }, Marshal.SizeOf<INPUT>());
    }

    static bool IsExtended(ushort vk) => vk is >= 0x21 and <= 0x2E or 0x5B or 0x5C or 0xA3 or 0xA5 or 0x6F or 0x90;

    public bool Running => running;

    public void Dispose()
    {
        running = false;
        try { wire.Send(Msg.Bye, ReadOnlySpan<byte>.Empty); } catch { }
        sound?.Dispose();
        captureThread.Join(2000);
        (source as IDisposable)?.Dispose();
        udp?.Dispose();
    }
}
