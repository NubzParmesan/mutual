using System.Runtime.InteropServices;

namespace Mutual.Stream;

// the parts of the windows audio api sound sharing needs: the default speakers, a loopback capture
// of whatever theyre playing and a playback stream for the viewer. plain com, nothing extra
static class Wasapi
{
    public const int SampleRate = 48000, Channels = 2, BytesPerFrame = 4;   // 48 khz stereo 16 bit
    public const uint LOOPBACK = 0x00020000, AUTOCONVERTPCM = 0x80000000, SRC_DEFAULT_QUALITY = 0x08000000;
    public const uint BUFFERFLAGS_SILENT = 0x2;

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumeratorCo { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int flow, int mask, out nint devices);
        int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        int Activate(ref Guid iid, int clsCtx, nint activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint flags, long bufferDuration, long periodicity, ref WaveFormatEx format, nint session);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint frames);
        [PreserveSig] int IsFormatSupported(int shareMode, nint format, out nint closest);
        [PreserveSig] int GetMixFormat(out nint format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(nint handle);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out nint data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }

    [ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioRenderClient
    {
        [PreserveSig] int GetBuffer(uint frames, out nint data);
        [PreserveSig] int ReleaseBuffer(uint frames, uint flags);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    public struct WaveFormatEx
    {
        public ushort tag, channels; public uint samplesPerSec, avgBytesPerSec; public ushort blockAlign, bitsPerSample, size;
        public static WaveFormatEx Pcm16(int rate, int channels) => new()
        {
            tag = 1, channels = (ushort)channels, samplesPerSec = (uint)rate, bitsPerSample = 16,
            blockAlign = (ushort)(channels * 2), avgBytesPerSec = (uint)(rate * channels * 2), size = 0,
        };
    }

    // the default speakers. loopback captures what they play
    public static IAudioClient DefaultSpeakers()
    {
        var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCo();
        Check(en.GetDefaultAudioEndpoint(0, 0, out var dev), "no speakers");   // eRender, eConsole
        var iid = typeof(IAudioClient).GUID;
        Check(dev.Activate(ref iid, 1 /* CLSCTX_INPROC_SERVER */, 0, out var client), "speakers wouldn't open");
        return (IAudioClient)client;
    }

    public static T Service<T>(IAudioClient c)
    {
        var iid = typeof(T).GUID;
        Check(c.GetService(ref iid, out var s), "audio service " + typeof(T).Name);
        return (T)s;
    }

    public static void Check(int hr, string what) { if (hr < 0) throw new InvalidOperationException($"{what} (0x{hr:X8})"); }
}
