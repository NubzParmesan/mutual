using System.Collections.Concurrent;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;

namespace Mutual.Stream;

// hardware h.264 thru media foundation (nvenc, amf or quicksync, whatever the gpu has) fed straight
// from gpu textures. low latency, constant bitrate, no b frames, keyframe every 2 s or when asked
public sealed class H264Encoder : IDisposable
{
    public string Name { get; }
    public bool Hardware { get; }
    readonly IMFTransform mft;
    readonly IMFMediaEventGenerator? events;
    readonly IMFDXGIDeviceManager manager;
    readonly int width, height, fps;
    readonly BlockingCollection<(ID3D11Texture2D tex, long ts)> inbox = new(2);
    readonly Thread worker;
    volatile bool running = true;
    long frameIndex;
    volatile bool keyframeWanted;
    public event Action<byte[], long, bool>? Encoded;   // data, timestamp (100ns), keyframe
    public Exception? Failure { get; private set; }

    const int METransformNeedInput = 601, METransformHaveOutput = 602;
    const uint MFT_ENUM_FLAG_SYNCMFT = 0x1, MFT_ENUM_FLAG_ASYNCMFT = 0x2, MFT_ENUM_FLAG_HARDWARE = 0x4, MFT_ENUM_FLAG_SORTANDFILTER = 0x40;

    public H264Encoder(ID3D11Device device, int width, int height, int fps, int bitrate)
    {
        this.width = width; this.height = height; this.fps = fps;
        MediaFactory.MFStartup(true).CheckError();
        manager = MediaFactory.MFCreateDXGIDeviceManager();
        manager.ResetDevice(device).CheckError();

        IMFTransform? chosen = null; string name = "?"; bool hw = false;
        foreach (var hardware in new[] { true, false })
        {
            uint flags = MFT_ENUM_FLAG_SORTANDFILTER | (hardware ? MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG_ASYNCMFT : MFT_ENUM_FLAG_SYNCMFT);
            using var list = MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoEncoder, flags,
                new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.NV12 },
                new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.H264 });
            foreach (var act in list)
            {
                try
                {
                    var t = act.ActivateObject<IMFTransform>();
                    try { name = act.GetString(TransformAttributeKeys.MftFriendlyNameAttribute); } catch { name = hardware ? "hardware encoder" : "software encoder"; }
                    chosen = t; hw = hardware;
                    break;
                }
                catch { }
            }
            if (chosen != null) break;
        }
        mft = chosen ?? throw new NotSupportedException("No H.264 encoder found on this PC.");
        Name = name; Hardware = hw;

        var attrs = mft.Attributes;
        if (hw)
        {
            attrs.Set(TransformAttributeKeys.TransformAsyncUnlock, 1u);
            events = mft.QueryInterface<IMFMediaEventGenerator>();
        }
        try { attrs.Set(CodecApi.LowLatencyMode, 1u); } catch { }
        if (hw) mft.ProcessMessage(TMessageType.MessageSetD3DManager, (nuint)manager.NativePointer);

        using (var outType = MediaFactory.MFCreateMediaType())
        {
            outType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            outType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
            outType.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)bitrate);
            outType.Set(MediaTypeAttributeKeys.FrameSize, Pack(width, height));
            outType.Set(MediaTypeAttributeKeys.FrameRate, Pack(fps, 1));
            outType.Set(MediaTypeAttributeKeys.InterlaceMode, 2u);   // progressive
            outType.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
            mft.SetOutputType(0, outType, 0);
        }
        using (var inType = MediaFactory.MFCreateMediaType())
        {
            inType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            inType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
            inType.Set(MediaTypeAttributeKeys.FrameSize, Pack(width, height));
            inType.Set(MediaTypeAttributeKeys.FrameRate, Pack(fps, 1));
            inType.Set(MediaTypeAttributeKeys.InterlaceMode, 2u);
            inType.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
            mft.SetInputType(0, inType, 0);
        }
        var p = mft.NativePointer;
        CodecApi.SetBool(p, CodecApi.LowLatencyMode, true);
        CodecApi.SetUInt(p, CodecApi.RateControlMode, 0);
        CodecApi.SetUInt(p, CodecApi.MeanBitRate, (uint)bitrate);
        CodecApi.SetUInt(p, CodecApi.GopSize, (uint)(fps * 2));
        CodecApi.SetUInt(p, CodecApi.BFrames, 0);
        // a buffer abt one frame big so every frame goes out the second its done instead of bunching up
        CodecApi.SetUInt(p, CodecApi.BufferSize, (uint)(bitrate / fps));

        mft.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, 0);
        mft.ProcessMessage(TMessageType.MessageNotifyStartOfStream, 0);
        worker = new Thread(hw ? RunAsync : RunSync) { IsBackground = true, Name = "mutual h264 encode" };
        worker.Start();
    }

    static ulong Pack(int hi, int lo) => ((ulong)(uint)hi << 32) | (uint)lo;

    // if the encoder is behind the oldest waiting frame gets dropped, its live not a recording
    public void Submit(ID3D11Texture2D nv12, long timestamp)
    {
        if (!running) return;
        while (!inbox.TryAdd((nv12, timestamp))) inbox.TryTake(out _);
    }

    public void RequestKeyFrame() => keyframeWanted = true;

    volatile int pendingBitrate;
    // applied before the next frame
    public void SetBitrate(int bitsPerSecond) => pendingBitrate = bitsPerSecond;

    IMFSample MakeSample(ID3D11Texture2D tex, long ts)
    {
        var buf = MediaFactory.MFCreateDXGISurfaceBuffer(typeof(ID3D11Texture2D).GUID, tex, 0, false);
        var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buf);
        buf.Dispose();
        sample.SampleTime = ts;
        sample.SampleDuration = 10_000_000L / fps;
        if (keyframeWanted) { keyframeWanted = false; CodecApi.SetUInt(mft.NativePointer, CodecApi.ForceKeyFrame, 1); }
        int br = pendingBitrate;
        if (br != 0)
        {
            pendingBitrate = 0;
            CodecApi.SetUInt(mft.NativePointer, CodecApi.MeanBitRate, (uint)br);
            CodecApi.SetUInt(mft.NativePointer, CodecApi.BufferSize, (uint)(br / fps));
        }
        return sample;
    }

    void RunAsync()
    {
        try
        {
            while (running)
            {
                using var ev = events!.GetEvent(0);
                int type = (int)ev.EventType;
                if (type == METransformNeedInput)
                {
                    (ID3D11Texture2D tex, long ts) item;
                    try { item = inbox.Take(); } catch (InvalidOperationException) { break; }
                    using var s = MakeSample(item.tex, item.ts);
                    mft.ProcessInput(0, s, 0);
                    frameIndex++;
                }
                else if (type == METransformHaveOutput) Drain();
            }
        }
        // anything thrown while shutting down (the mft refusing input after the flush) is expected, and
        // letting it escape this thread would take the whole app down with it
        catch (Exception e) { if (running) Failure = e; }
    }

    void RunSync()
    {
        try
        {
            foreach (var item in inbox.GetConsumingEnumerable())
            {
                using var s = MakeSample(item.tex, item.ts);
                mft.ProcessInput(0, s, 0);
                frameIndex++;
                Drain();
            }
        }
        catch (Exception e) { if (running) Failure = e; }
    }

    void Drain()
    {
        while (true)
        {
            var info = mft.GetOutputStreamInfo(0);
            bool providesSamples = ((int)info.Flags & 0x100) != 0;   // MFT_OUTPUT_STREAM_PROVIDES_SAMPLES
            IMFSample? own = null;
            if (!providesSamples)
            {
                own = MediaFactory.MFCreateSample();
                using var mb = MediaFactory.MFCreateMemoryBuffer(Math.Max(info.Size, width * height));
                own.AddBuffer(mb);
            }
            var odb = new OutputDataBuffer { StreamID = 0, Sample = own };
            var r = mft.ProcessOutput(ProcessOutputFlags.None, 1, ref odb, out _);
            if (r.Code == unchecked((int)0xC00D6D72)) { own?.Dispose(); return; }   // MF_E_TRANSFORM_NEED_MORE_INPUT
            if (r.Code == unchecked((int)0xC00D6D61))   // MF_E_TRANSFORM_STREAM_CHANGE, take the output type again
            {
                own?.Dispose();
                var t = mft.GetOutputAvailableType(0, 0);
                mft.SetOutputType(0, t, 0);
                t.Dispose();
                continue;
            }
            r.CheckError();
            var sample = odb.Sample!;
            try
            {
                using var buf = sample.ConvertToContiguousBuffer();
                buf.Lock(out var ptr, out _, out var len);
                var data = new byte[len];
                System.Runtime.InteropServices.Marshal.Copy(ptr, data, 0, len);
                buf.Unlock();
                bool key = false;
                try { key = sample.GetUInt32(SampleAttributeKeys.CleanPoint) != 0; } catch { }
                Encoded?.Invoke(data, sample.SampleTime, key);
            }
            finally { sample.Dispose(); odb.Events?.Dispose(); }
            if (Hardware) return;   // async encoders send an event for each output
        }
    }

    public void Dispose()
    {
        running = false;
        inbox.CompleteAdding();
        try { mft.ProcessMessage(TMessageType.MessageNotifyEndOfStream, 0); mft.ProcessMessage(TMessageType.MessageCommandFlush, 0); } catch { }
        worker.Join(1000);
        events?.Dispose();
        mft.Dispose();
        manager.Dispose();
    }
}
