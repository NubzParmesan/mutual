using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Mutual.Stream;

// the hosts sound, whatever the speakers are playing, in 5 ms chunks of 48 khz stereo 16 bit
// the host squeezes it with opus before sending
public sealed class SoundCapture : IDisposable
{
    public const int ChunkFrames = 240;   // 5 ms
    readonly Thread thread;
    volatile bool running = true;
    public event Action<uint, byte[]>? Chunk;
    public Exception? Failure { get; private set; }

    public SoundCapture()
    {
        thread = new Thread(Run) { IsBackground = true, Name = "mutual sound capture", Priority = ThreadPriority.AboveNormal };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    void Run()
    {
        Wasapi.IAudioClient? client = null;
        try
        {
            client = Wasapi.DefaultSpeakers();
            var fmt = Wasapi.WaveFormatEx.Pcm16(Wasapi.SampleRate, Wasapi.Channels);
            Wasapi.Check(client.Initialize(0, Wasapi.LOOPBACK | Wasapi.AUTOCONVERTPCM | Wasapi.SRC_DEFAULT_QUALITY, 200_000, 0, ref fmt, 0), "loopback capture");
            var cap = Wasapi.Service<Wasapi.IAudioCaptureClient>(client);
            Wasapi.Check(client.Start(), "start capture");
            var pending = new byte[ChunkFrames * Wasapi.BytesPerFrame];
            int filled = 0; uint seq = 0;
            while (running)
            {
                Thread.Sleep(3);
                while (running && cap.GetNextPacketSize(out uint next) >= 0 && next > 0)
                {
                    Wasapi.Check(cap.GetBuffer(out nint data, out uint frames, out uint flags, out _, out _), "read capture");
                    int bytes = (int)frames * Wasapi.BytesPerFrame;
                    var src = new byte[bytes];
                    if ((flags & Wasapi.BUFFERFLAGS_SILENT) == 0) Marshal.Copy(data, src, 0, bytes);
                    cap.ReleaseBuffer(frames);
                    for (int off = 0; off < bytes;)
                    {
                        int take = Math.Min(bytes - off, pending.Length - filled);
                        Buffer.BlockCopy(src, off, pending, filled, take);
                        filled += take; off += take;
                        if (filled == pending.Length) { Chunk?.Invoke(++seq, pending); pending = new byte[pending.Length]; filled = 0; }
                    }
                }
            }
            client.Stop();
        }
        catch (Exception e) { Failure = e; }
        finally { if (client != null) Marshal.ReleaseComObject(client); }
    }

    public void Dispose() { running = false; thread.Join(500); }

    public static byte[] Pack(uint seq, byte[] pcm)
    {
        var b = new byte[4 + pcm.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(b, seq);
        pcm.CopyTo(b, 4);
        return b;
    }
}

// plays the hosts sound on the viewers speakers with a small buffer (abt 40 ms)
// missing bits get filled in instead of stalling. if the buffer creeps up (the two pcs sound clocks never
// match exactly) old sound gets dropped so it stays in sync with the picture
public sealed class SoundPlayer : IDisposable
{
    const int TargetMs = 40, MaxMs = 120;
    readonly Thread thread;
    volatile bool running = true;
    readonly Queue<byte[]> queue = new();
    uint lastSeq;
    int queuedBytes;
    public float Volume { get; set; } = 1f;
    public bool Muted { get; set; }
    public Exception? Failure { get; private set; }
    static int Bytes(int ms) => Wasapi.SampleRate * Wasapi.BytesPerFrame * ms / 1000;

    public SoundPlayer()
    {
        thread = new Thread(Run) { IsBackground = true, Name = "mutual sound playback", Priority = ThreadPriority.AboveNormal };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    readonly OpusUnpacker opus = new();

    // one opus packet from either lane
    public void Add(byte[] packed)
    {
        if (packed.Length < 5 || packed.Length > 1500) return;
        uint seq = BinaryPrimitives.ReadUInt32LittleEndian(packed);
        lock (queue)
        {
            if (lastSeq != 0 && seq <= lastSeq) return;   // late or a duplicate
            // a gap, opus fills it so it doesnt click (up to 4, past that just keep going)
            if (lastSeq != 0 && seq - lastSeq > 1 && seq - lastSeq <= 5)
                for (uint i = lastSeq + 1; i < seq; i++) Enqueue(opus.Conceal());
            lastSeq = seq;
            try { Enqueue(opus.Decode(packed.AsSpan(4))); }
            catch (Exception) { Enqueue(opus.Conceal()); }   // a bad packet counts as a lost one, doesnt stop the sound
            while (queuedBytes > Bytes(MaxMs) && queue.Count > 0) queuedBytes -= queue.Dequeue().Length;
        }
    }

    void Enqueue(byte[] pcm) { queue.Enqueue(pcm); queuedBytes += pcm.Length; }

    void Run()
    {
        Wasapi.IAudioClient? client = null;
        try
        {
            client = Wasapi.DefaultSpeakers();
            var fmt = Wasapi.WaveFormatEx.Pcm16(Wasapi.SampleRate, Wasapi.Channels);
            Wasapi.Check(client.Initialize(0, Wasapi.AUTOCONVERTPCM | Wasapi.SRC_DEFAULT_QUALITY, 300_000, 0, ref fmt, 0), "playback");
            Wasapi.Check(client.GetBufferSize(out uint bufFrames), "playback buffer");
            var render = Wasapi.Service<Wasapi.IAudioRenderClient>(client);
            Wasapi.Check(client.Start(), "start playback");
            bool primed = false;
            byte[]? partial = null; int partialOff = 0;
            while (running)
            {
                Thread.Sleep(3);
                client.GetCurrentPadding(out uint padding);
                // keep abt TargetMs in the device and build up that much first so small hiccups dont click
                int wantFrames = Math.Min((int)(bufFrames - padding), Wasapi.SampleRate * TargetMs / 1000 - (int)padding);
                if (wantFrames <= 0) continue;
                lock (queue)
                {
                    if (!primed) { if (queuedBytes < Bytes(TargetMs)) continue; primed = true; }
                    if (queuedBytes == 0 && partial == null) { primed = false; continue; }
                }
                if (render.GetBuffer((uint)wantFrames, out nint dst) < 0) continue;
                int wantBytes = wantFrames * Wasapi.BytesPerFrame, written = 0;
                var outBuf = new byte[wantBytes];
                lock (queue)
                {
                    while (written < wantBytes)
                    {
                        if (partial == null)
                        {
                            if (queue.Count == 0) break;
                            partial = queue.Dequeue(); partialOff = 0; queuedBytes -= partial.Length;
                        }
                        int take = Math.Min(partial.Length - partialOff, wantBytes - written);
                        Buffer.BlockCopy(partial, partialOff, outBuf, written, take);
                        written += take; partialOff += take;
                        if (partialOff == partial.Length) partial = null;
                    }
                }
                ApplyVolume(outBuf, written);
                Marshal.Copy(outBuf, 0, dst, wantBytes);
                render.ReleaseBuffer((uint)wantFrames, 0);
            }
            client.Stop();
        }
        catch (Exception e) { Failure = e; }
        finally { if (client != null) Marshal.ReleaseComObject(client); }
    }

    void ApplyVolume(byte[] pcm, int len)
    {
        float v = Muted ? 0 : Volume;
        if (v >= 0.999f) return;
        for (int i = 0; i + 1 < len; i += 2)
        {
            short s = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i));
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i), (short)(s * v));
        }
    }

    public void Dispose() { running = false; thread.Join(500); }
}
