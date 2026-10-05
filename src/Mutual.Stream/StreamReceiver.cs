using System.Diagnostics;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Mutual.Stream;

// the watching side without the window. reads the link, decodes on the gpu and hands frames
// to whatever draws them. asks for a keyframe at the start and when the size changes
public sealed class StreamReceiver : IDisposable
{
    readonly Wire wire;
    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }
    H264Decoder? decoder;
    GpuColorConverter? toBgra;
    readonly Thread reader;
    readonly Func<byte[], UdpVideo>? openUdp;
    UdpVideo? udp;
    SoundPlayer? player;
    readonly object playerLock = new();
    // made when the first sound shows up
    public SoundPlayer? Sound => player;
    public int SoundChunks { get; private set; }

    // off for tests on one pc, playing it would feed right back into the capture
    public bool PlaySoundAloud { get; set; } = true;
    public byte[]? LastSoundChunk { get; private set; }

    void PlaySound(byte[] packed)
    {
        if (!running) return;
        SoundChunks++;
        LastSoundChunk = packed;
        if (!PlaySoundAloud) return;
        if (player == null) lock (playerLock) player ??= new SoundPlayer();
        player.Add(packed);
    }
    readonly object decodeLock = new();
    long lastTs = long.MinValue;
    public string Transport => udp?.Up == true ? "udp" : "tcp";
    public double RttMs => udp?.RttMs ?? 0;
    volatile bool running = true;
    public StreamInfo? Info { get; private set; }
    public event Action<ID3D11Texture2D, int, int>? FrameReady;   // bgra texture, width, height (on the decode thread)
    public event Action<StreamInfo>? InfoChanged;
    public event Action<string>? Ended;
    // the host stopped on purpose (not the link breaking)
    public bool EndedByHost { get; private set; }
    public event Action<string>? ClipboardReceived;
    // whether the host lets this side use their mouse and keyboard rn
    public bool CanControl { get; private set; }
    public event Action<bool>? ControlChanged;
    public void SendClipboard(string text) { if (running) try { wire.Send(Msg.Clipboard, System.Text.Encoding.UTF8.GetBytes(text)); } catch { } }
    // x, y (0..65535 across the stream), visible, shape id
    public event Action<ushort, ushort, bool, int>? CursorMoved;
    // shape id, hotspot x, hotspot y, raw pixels (see CursorPixels)
    public event Action<int, int, int, byte[]>? CursorShape;
    public int FramesDecoded { get; private set; }
    public double DecodeMs { get; private set; }

    // openUdp makes this side of the udp lane once the host sends the key. null = tcp only
    public StreamReceiver(Wire wire, Func<byte[], UdpVideo>? openUdp = null)
    {
        this.wire = wire; this.openUdp = openUdp;
        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
            new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 }, out ID3D11Device dev, out ID3D11DeviceContext ctx).CheckError();
        Device = dev; Context = ctx;
        using (var mt = dev.QueryInterface<ID3D11Multithread>()) mt.SetMultithreadProtected(true);
        reader = new Thread(ReadLoop) { IsBackground = true, Name = "mutual receive" };
        reader.Start();
        // heartbeat back to the host so a quiet viewer doesnt look dead
        new Thread(() =>
        {
            int last = 0;
            while (running)
            {
                Thread.Sleep(2000);
                int f = FramesDecoded;
                var u = udp;
                var (loss, lost, rebuilt) = u?.TakeStats() ?? (0, 0, 0);
                var st = new ViewerStats((f - last) / 2.0, DecodeMs, u?.RttMs ?? 0, loss, lost, rebuilt, Transport);
                try { wire.SendJson(Msg.Stats, st); } catch { break; }
                Tally?.Add(st);
                LastLoss = loss;
                last = f;
            }
        }) { IsBackground = true, Name = "mutual heartbeat" }.Start();
    }

    void ReadLoop()
    {
        string why = "stream ended";
        try
        {
            wire.Send(Msg.KeyFrame, ReadOnlySpan<byte>.Empty);
            while (running)
            {
                var m = wire.Read();
                if (m == null) break;
                var (kind, p) = m.Value;
                switch (kind)
                {
                    case Msg.Hello:
                        lock (decodeLock)
                        {
                            Info = Wire.Json<StreamInfo>(p);
                            // a picture size that makes no sense would just blow up the gpu stuff later
                            if (Info is { } i && (i.Width < 16 || i.Height < 16 || i.Width > 8192 || i.Height > 8192)) throw new InvalidDataException("bad picture size");
                            // new size, fresh decoder that waits for the next keyframe
                            decoder?.Dispose(); decoder = new H264Decoder(Device);
                            toBgra?.Dispose(); toBgra = null;
                        }
                        if (Info != null) Safe(() => InfoChanged?.Invoke(Info));
                        wire.Send(Msg.KeyFrame, ReadOnlySpan<byte>.Empty);
                        break;
                    case Msg.Video:
                        Decode(BitConverter.ToInt64(p, 0), p[8] != 0, p.AsSpan(9).ToArray());
                        break;
                    case Msg.Control: CanControl = p.Length > 0 && p[0] == 1; Safe(() => ControlChanged?.Invoke(CanControl)); break;
                    case Msg.Clipboard: { var t = System.Text.Encoding.UTF8.GetString(p); Safe(() => ClipboardReceived?.Invoke(t)); break; }
                    case Msg.Audio:
                        PlaySound(p);
                        break;
                    case Msg.UdpSetup:
                        if (openUdp == null || udp != null || p.Length != 32) break;
                        try
                        {
                            udp = openUdp(p);
                            udp.Frame += Decode;
                            udp.Audio += PlaySound;
                            udp.Lost += () => { try { wire.Send(Msg.KeyFrame, ReadOnlySpan<byte>.Empty); } catch { } };
                        }
                        catch (Exception) { udp = null; }   // stays on tcp
                        break;
                    case Msg.Cursor: { var c = Wire.ReadCursor(p); Safe(() => CursorMoved?.Invoke(c.x, c.y, c.visible, c.shape)); break; }
                    case Msg.CursorShape: { var c = Wire.ReadCursorShape(p); Safe(() => CursorShape?.Invoke(c.shape, c.hotX, c.hotY, c.pixels)); break; }
                    case Msg.Bye: EndedByHost = true; running = false; why = "they stopped sharing"; break;
                }
            }
        }
        catch (Exception e) { if (running) why = "stream broke: " + e.Message; }   // a throw during shutdown would kill the app off this thread
        running = false;
        Ended?.Invoke(why);
    }

    public double LastLoss { get; private set; }
    // adds up this sides reports for the summary at the end
    public SessionTally? Tally { get; set; }

    static void Safe(Action a) { try { a(); } catch (Exception) { } }

    // a frame from either lane. older than the last one shown gets dropped (overlap when switching lanes)
    void Decode(long ts, bool key, byte[] h264)
    {
        lock (decodeLock)
        {
            if (decoder == null || Info == null) return;
            if (ts <= lastTs && !key) return;
            lastTs = ts;
            var t0 = Stopwatch.GetTimestamp();
            foreach (var f in decoder.Decode(h264, ts))
            {
                try
                {
                    var d = f.Texture.Description;
                    toBgra ??= new GpuColorConverter(Device, d.Width, d.Height, (uint)Info.Width, (uint)Info.Height,
                        Format.NV12, Format.B8G8R8A8_UNorm, BindFlags.RenderTarget | BindFlags.ShaderResource);
                    var bgra = toBgra.Convert(f.Texture, f.Slice);
                    FramesDecoded++;
                    DecodeMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                    Safe(() => FrameReady?.Invoke(bgra, Info.Width, Info.Height));
                }
                finally { f.Texture.Dispose(); f.Owner.Dispose(); }
            }
        }
    }

    // testing, cut the link without saying bye
    public void Sever() => wire.Sever();

    public void Send(InputEvent e) { if (running && CanControl) try { wire.SendInput(e); } catch { } }

    public void Dispose()
    {
        // only say bye if youre closing a live stream, not after the link already died
        if (running) try { wire.Send(Msg.Bye, ReadOnlySpan<byte>.Empty); } catch { }
        running = false;
        reader.Join(1000);
        udp?.Dispose();
        player?.Dispose();
        toBgra?.Dispose(); decoder?.Dispose();
        Context.Dispose(); Device.Dispose();
    }
}
