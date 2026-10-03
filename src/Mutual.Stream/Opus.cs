using System.Buffers.Binary;
using Concentus;
using Concentus.Enums;

namespace Mutual.Stream;

// sound squeezed with opus (what discord uses) before it goes out. abt 90 kbit/s instead of 1.5 mbit/s
// raw and you cant really hear a difference. 10 ms frames, low delay mode
// packet: seq (4) + opus bytes
public sealed class OpusPacker
{
    public const int FrameSamples = 480;   // 10 ms at 48 khz
    readonly IOpusEncoder enc;
    readonly short[] pcm = new short[FrameSamples * Wasapi.Channels];
    readonly byte[] outBuf = new byte[1500];
    int filled;
    uint seq;

    public OpusPacker(int bitrate = 96_000)
    {
        enc = OpusCodecFactory.CreateEncoder(Wasapi.SampleRate, Wasapi.Channels, OpusApplication.OPUS_APPLICATION_RESTRICTED_LOWDELAY);
        enc.Bitrate = bitrate;
        enc.Complexity = 6;
    }

    // raw 16 bit stereo in, finished packets out
    public IEnumerable<byte[]> Add(byte[] raw)
    {
        var done = new List<byte[]>();
        for (int i = 0; i + 1 < raw.Length; i += 2)
        {
            pcm[filled++] = BinaryPrimitives.ReadInt16LittleEndian(raw.AsSpan(i));
            if (filled < pcm.Length) continue;
            filled = 0;
            int n = enc.Encode(pcm, FrameSamples, outBuf, outBuf.Length);
            var pkt = new byte[4 + n];
            BinaryPrimitives.WriteUInt32LittleEndian(pkt, ++seq);
            Buffer.BlockCopy(outBuf, 0, pkt, 4, n);
            done.Add(pkt);
        }
        return done;
    }
}

// the other end, opus back to raw sound
public sealed class OpusUnpacker
{
    readonly IOpusDecoder dec = OpusCodecFactory.CreateDecoder(Wasapi.SampleRate, Wasapi.Channels);
    readonly short[] pcm = new short[OpusPacker.FrameSamples * Wasapi.Channels];

    public byte[] Decode(ReadOnlySpan<byte> opus) => Run(opus);

    // a lost packet, opus guesses something that fits instead of a click
    public byte[] Conceal() => Run(ReadOnlySpan<byte>.Empty);

    byte[] Run(ReadOnlySpan<byte> opus)
    {
        int n = dec.Decode(opus, pcm, OpusPacker.FrameSamples, false);
        var raw = new byte[n * Wasapi.Channels * 2];
        for (int i = 0; i < n * Wasapi.Channels; i++) BinaryPrimitives.WriteInt16LittleEndian(raw.AsSpan(i * 2), pcm[i]);
        return raw;
    }
}
