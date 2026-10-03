using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;

namespace Mutual.Stream;

// h.264 back to pictures on the viewers gpu. media foundations decoder with dxva in low latency mode
// so it hands frames back right away instead of holding a few. output stays on the gpu as nv12
public sealed class H264Decoder : IDisposable
{
    readonly IMFTransform mft;
    readonly IMFDXGIDeviceManager manager;
    public string Name { get; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    bool outputSet;

    const uint MFT_ENUM_FLAG_SYNCMFT = 0x1, MFT_ENUM_FLAG_SORTANDFILTER = 0x40;

    public H264Decoder(ID3D11Device device)
    {
        MediaFactory.MFStartup(true).CheckError();
        manager = MediaFactory.MFCreateDXGIDeviceManager();
        manager.ResetDevice(device).CheckError();
        IMFTransform? t = null; string name = "?";
        using (var list = MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoDecoder, MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG_SORTANDFILTER,
            new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.H264 },
            new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.NV12 }))
            foreach (var act in list)
            {
                try { t = act.ActivateObject<IMFTransform>(); try { name = act.GetString(TransformAttributeKeys.MftFriendlyNameAttribute); } catch { name = "h264 decoder"; } break; }
                catch { }
            }
        mft = t ?? throw new NotSupportedException("No H.264 decoder found on this PC.");
        Name = name;
        try { mft.Attributes.Set(CodecApi.LowLatencyMode, 1u); } catch { }
        try { mft.ProcessMessage(TMessageType.MessageSetD3DManager, (nuint)manager.NativePointer); } catch { /* falls back to system memory output */ }
        using (var inType = MediaFactory.MFCreateMediaType())
        {
            inType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            inType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
            mft.SetInputType(0, inType, 0);
        }
        TrySetOutput();
        mft.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, 0);
        mft.ProcessMessage(TMessageType.MessageNotifyStartOfStream, 0);
    }

    bool TrySetOutput()
    {
        for (int i = 0; ; i++)
        {
            IMFMediaType? t;
            try { t = mft.GetOutputAvailableType(0, i); } catch { return false; }
            using (t)
            {
                if (t.GetGUID(MediaTypeAttributeKeys.Subtype) != VideoFormatGuids.NV12) continue;
                mft.SetOutputType(0, t, 0);
                try
                {
                    ulong size = t.GetUInt64(MediaTypeAttributeKeys.FrameSize);
                    Width = (int)(size >> 32); Height = (int)(size & 0xffffffff);
                }
                catch { }
                outputSet = true;
                return true;
            }
        }
    }

    public readonly record struct Frame(ID3D11Texture2D Texture, uint Slice, long Timestamp, IMFSample Owner);

    // feed it some h.264 and get back whatever frames came out. dispose each frames Owner after drawing it
    public List<Frame> Decode(byte[] data, long timestamp)
    {
        var frames = new List<Frame>();
        using (var buf = MediaFactory.MFCreateMemoryBuffer(data.Length))
        {
            buf.Lock(out var ptr, out _, out _);
            System.Runtime.InteropServices.Marshal.Copy(data, 0, ptr, data.Length);
            buf.Unlock();
            buf.CurrentLength = data.Length;
            using var sample = MediaFactory.MFCreateSample();
            sample.AddBuffer(buf);
            sample.SampleTime = timestamp;
            mft.ProcessInput(0, sample, 0);
        }
        while (true)
        {
            var info = mft.GetOutputStreamInfo(0);
            bool provides = ((int)info.Flags & 0x100) != 0;
            IMFSample? own = null;
            if (!provides)
            {
                own = MediaFactory.MFCreateSample();
                using var mb = MediaFactory.MFCreateMemoryBuffer(Math.Max(info.Size, 1));
                own.AddBuffer(mb);
            }
            var odb = new OutputDataBuffer { StreamID = 0, Sample = own };
            var r = mft.ProcessOutput(ProcessOutputFlags.None, 1, ref odb, out _);
            if (r.Code == unchecked((int)0xC00D6D72)) { own?.Dispose(); break; }   // needs more input
            if (r.Code == unchecked((int)0xC00D6D61)) { own?.Dispose(); TrySetOutput(); continue; }   // stream change, size is known now
            r.CheckError();
            var outSample = odb.Sample!;
            odb.Events?.Dispose();
            using var mbuf = outSample.GetBufferByIndex(0);
            using var dxgi = mbuf.QueryInterfaceOrNull<IMFDXGIBuffer>();
            if (dxgi == null) { outSample.Dispose(); throw new NotSupportedException("Decoder isn't giving GPU frames (no DXVA)."); }
            var tex = new ID3D11Texture2D(dxgi.GetResource(typeof(ID3D11Texture2D).GUID));
            uint slice = dxgi.SubresourceIndex;
            frames.Add(new Frame(tex, slice, outSample.SampleTime, outSample));
        }
        return frames;
    }

    public void Dispose()
    {
        try { mft.ProcessMessage(TMessageType.MessageCommandFlush, 0); } catch { }
        mft.Dispose();
        manager.Dispose();
    }
}
