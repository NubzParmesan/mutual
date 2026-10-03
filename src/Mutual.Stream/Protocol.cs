using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace Mutual.Stream;

// message kinds on a stream link. one byte, then a length, then the payload
public enum Msg : byte
{
    Hello = 1,   // host -> viewer: json StreamInfo
    Video = 2,   // host -> viewer: ts(8) key(1) h264
    Cursor = 3,   // host -> viewer: x(2) y(2) visible(1) shape(4), position 0..65535 across the stream
    Input = 4,   // viewer -> host: one InputEvent
    KeyFrame = 5,   // viewer -> host: send a keyframe (joined late or lost something)
    Bye = 6,
    Stats = 7,   // viewer -> host: json ViewerStats
    CursorShape = 8,   // host -> viewer: shape(4) hotX(2) hotY(2) png
    UdpSetup = 9,   // host -> viewer: 32 byte key for the udp lane
    Clipboard = 12,   // either way: utf8 text, only if that side turned clipboard sharing on
    Control = 13,     // host -> viewer: 1 byte, 1 = you can use the mouse and keyboard, 0 = watch only
    Heartbeat = 11,   // host -> viewer: nothing, every 2 s so the tcp link doesnt look dead while video is on udp
    Audio = 10,   // host -> viewer: seq(4) + opus, when udp isnt up
}

public sealed record StreamInfo(int Width, int Height, int Fps, string Source, string Encoder);
public sealed record ViewerStats(double Fps, double DecodeMs, double NetworkMs, double Loss = 0, int FramesLost = 0, int FramesRebuilt = 0, string Transport = "tcp");

// the viewers mouse or keys. mouse is 0..65535 across the picture so it works whatever size either screen is
// for keys Value is the virtual key in the low 16 bits, scan code in 16-23 and the extended flag in bit 24
// (so right ctrl and the arrow keys stay different)
public enum InputKind : byte { Move = 1, Down = 2, Up = 3, Wheel = 4, KeyDown = 5, KeyUp = 6, HWheel = 7 }
public readonly record struct InputEvent(InputKind Kind, ushort X, ushort Y, int Value);

// frames messages over a stream. writes are locked, reads come from one thread
public sealed class Wire
{
    readonly System.IO.Stream s;
    readonly object writeLock = new();
    public Wire(System.IO.Stream s) { this.s = s; }

    // testing, drop the connection with no goodbye like the network died
    public void Sever() { try { s.Dispose(); } catch { } }

    public void Send(Msg kind, ReadOnlySpan<byte> payload)
    {
        Span<byte> head = stackalloc byte[5];
        head[0] = (byte)kind;
        BinaryPrimitives.WriteInt32LittleEndian(head[1..], payload.Length);
        lock (writeLock)
        {
            s.Write(head);
            s.Write(payload);
            s.Flush();
        }
    }

    public void SendJson<T>(Msg kind, T value) => Send(kind, JsonSerializer.SerializeToUtf8Bytes(value));

    public void SendVideo(long ts, bool key, byte[] h264)
    {
        var buf = new byte[9 + h264.Length];
        BinaryPrimitives.WriteInt64LittleEndian(buf, ts);
        buf[8] = (byte)(key ? 1 : 0);
        h264.CopyTo(buf, 9);
        Send(Msg.Video, buf);
    }

    public void SendInput(InputEvent e)
    {
        Span<byte> b = stackalloc byte[9];
        b[0] = (byte)e.Kind;
        BinaryPrimitives.WriteUInt16LittleEndian(b[1..], e.X);
        BinaryPrimitives.WriteUInt16LittleEndian(b[3..], e.Y);
        BinaryPrimitives.WriteInt32LittleEndian(b[5..], e.Value);
        Send(Msg.Input, b);
    }

    public void SendCursor(ushort x, ushort y, bool visible, int shape)
    {
        Span<byte> b = stackalloc byte[9];
        BinaryPrimitives.WriteUInt16LittleEndian(b, x);
        BinaryPrimitives.WriteUInt16LittleEndian(b[2..], y);
        b[4] = (byte)(visible ? 1 : 0);
        BinaryPrimitives.WriteInt32LittleEndian(b[5..], shape);
        Send(Msg.Cursor, b);
    }

    public static (ushort x, ushort y, bool visible, int shape) ReadCursor(byte[] p) =>
        (BinaryPrimitives.ReadUInt16LittleEndian(p), BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(2)), p[4] != 0, BinaryPrimitives.ReadInt32LittleEndian(p.AsSpan(5)));

    public void SendCursorShape(int shape, int hotX, int hotY, byte[] png)
    {
        var b = new byte[8 + png.Length];
        BinaryPrimitives.WriteInt32LittleEndian(b, shape);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4), (ushort)hotX);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(6), (ushort)hotY);
        png.CopyTo(b, 8);
        Send(Msg.CursorShape, b);
    }

    public static (int shape, int hotX, int hotY, byte[] png) ReadCursorShape(byte[] p) =>
        (BinaryPrimitives.ReadInt32LittleEndian(p), BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(4)), BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(6)), p.AsSpan(8).ToArray());

    public static InputEvent ReadInput(byte[] p) => new((InputKind)p[0], BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(1)),
        BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(3)), BinaryPrimitives.ReadInt32LittleEndian(p.AsSpan(5)));

    // blocks for the next message, null when the link closed
    public (Msg kind, byte[] payload)? Read()
    {
        var head = new byte[5];
        if (!Fill(head)) return null;
        int len = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(1));
        var kind = (Msg)head[0];
        if (len < 0 || len > (kind == Msg.Video ? 16 << 20 : 1 << 20)) throw new InvalidDataException("Bad message length.");
        var p = new byte[len];
        if (len > 0 && !Fill(p)) return null;
        return (kind, p);
    }

    bool Fill(byte[] buf)
    {
        int got = 0;
        while (got < buf.Length)
        {
            int n = s.Read(buf, got, buf.Length - got);
            if (n <= 0) return false;
            got += n;
        }
        return true;
    }

    public static T? Json<T>(byte[] p) => JsonSerializer.Deserialize<T>(p);
}
