using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Mutual.Stream;

// bgra to nv12 (what encoders want) and back, on the gpu with the d3d11 video processor
// no pixels touch the cpu
public sealed class GpuColorConverter : IDisposable
{
    readonly ID3D11VideoDevice vdev;
    readonly ID3D11VideoContext vctx;
    readonly ID3D11VideoProcessorEnumerator vpEnum;
    readonly ID3D11VideoProcessor vp;
    readonly ID3D11Device device;
    public uint Width { get; }
    public uint Height { get; }
    public Format InFormat { get; }
    public Format OutFormat { get; }
    // a ring of outputs so a frame the encoder is still reading never gets overwritten
    public ID3D11Texture2D Output => ring[(next + ring.Length - 1) % ring.Length];
    readonly ID3D11Texture2D[] ring;
    readonly ID3D11VideoProcessorOutputView[] outViews;
    int next;
    readonly Dictionary<nint, ID3D11VideoProcessorInputView> inViews = new();

    public GpuColorConverter(ID3D11Device device, uint width, uint height, Format inFormat, Format outFormat, BindFlags outBind = BindFlags.RenderTarget, int ringSize = 6)
        : this(device, width, height, width, height, inFormat, outFormat, outBind, ringSize) { }

    // input can be bigger than the picture (decoders pad to 16 px), only the top left part gets converted
    public GpuColorConverter(ID3D11Device device, uint inWidth, uint inHeight, uint width, uint height, Format inFormat, Format outFormat, BindFlags outBind = BindFlags.RenderTarget, int ringSize = 6)
    {
        this.device = device; Width = width; Height = height; InFormat = inFormat; OutFormat = outFormat;
        vdev = device.QueryInterface<ID3D11VideoDevice>();
        vctx = device.ImmediateContext.QueryInterface<ID3D11VideoContext>();
        var desc = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputWidth = inWidth, InputHeight = inHeight,
            OutputWidth = width, OutputHeight = height,
            InputFrameRate = new Rational(60, 1), OutputFrameRate = new Rational(60, 1),
            Usage = VideoUsage.OptimalSpeed,
        };
        vpEnum = vdev.CreateVideoProcessorEnumerator(desc);
        vp = vdev.CreateVideoProcessor(vpEnum, 0);
        ring = new ID3D11Texture2D[ringSize];
        outViews = new ID3D11VideoProcessorOutputView[ringSize];
        for (int i = 0; i < ringSize; i++)
        {
            ring[i] = device.CreateTexture2D(new Texture2DDescription
            {
                Width = width, Height = height, MipLevels = 1, ArraySize = 1, Format = outFormat,
                SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default,
                BindFlags = outBind,
            });
            outViews[i] = vdev.CreateVideoProcessorOutputView(ring[i], vpEnum, new VideoProcessorOutputViewDescription { ViewDimension = VideoProcessorOutputViewDimension.Texture2D });
        }
        // full range in (desktop), studio range out (what h.264 decoders expect) and the other way back
        vctx.VideoProcessorSetStreamFrameFormat(vp, 0, VideoFrameFormat.Progressive);
        vctx.VideoProcessorSetStreamAutoProcessingMode(vp, 0, false);
        var crop = new Vortice.RawRect(0, 0, (int)width, (int)height);
        vctx.VideoProcessorSetStreamSourceRect(vp, 0, true, crop);
        vctx.VideoProcessorSetStreamDestRect(vp, 0, true, crop);
        vctx.VideoProcessorSetOutputTargetRect(vp, true, crop);
    }

    public ID3D11Texture2D Convert(ID3D11Texture2D input, uint arraySlice = 0)
    {
        if (!inViews.TryGetValue(input.NativePointer + (nint)arraySlice, out var view))
        {
            view = vdev.CreateVideoProcessorInputView(input, vpEnum, new VideoProcessorInputViewDescription
            {
                ViewDimension = VideoProcessorInputViewDimension.Texture2D,
                Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = arraySlice },
            });
            inViews[input.NativePointer + (nint)arraySlice] = view;
        }
        var stream = new VideoProcessorStream { Enable = true, InputSurface = view };
        int slot = next;
        next = (next + 1) % ring.Length;
        vctx.VideoProcessorBlt(vp, outViews[slot], 0, 1, new[] { stream }).CheckError();
        return ring[slot];
    }

    public void Dispose()
    {
        foreach (var v in inViews.Values) v.Dispose();
        foreach (var v in outViews) v.Dispose();
        foreach (var t in ring) t.Dispose();
        vp.Dispose(); vpEnum.Dispose(); vctx.Dispose(); vdev.Dispose();
    }
}
