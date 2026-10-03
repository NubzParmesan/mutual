using System.Runtime.InteropServices;

namespace Mutual.Stream;

// the few encoder settings that matter for live streaming (low latency, constant bitrate, keyframe spacing,
// keyframe now). vortice doesnt wrap ICodecAPI so its called thru the com vtable directly
// all best effort, an encoder that ignores one still works
static unsafe class CodecApi
{
    static readonly Guid IID_ICodecAPI = new("901db4c7-31ce-41a2-85dc-8fa0bf41b8da");
    public static readonly Guid LowLatencyMode = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    public static readonly Guid RateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");   // 0 = cbr
    public static readonly Guid MeanBitRate = new("f7222374-2144-4815-b550-a37f8e12ee52");
    public static readonly Guid GopSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");
    public static readonly Guid BFrames = new("8d390aac-dc5c-4200-b57f-814d04babab2");
    public static readonly Guid BufferSize = new("0db96574-b6a4-4c8b-8106-3773de0310cd");   // vbv size in bits
    public static readonly Guid ForceKeyFrame = new("398c1b98-8353-475a-9ef2-8f265d260345");

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    struct Variant
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public uint ui4;
        [FieldOffset(8)] public short boolVal;
    }

    const int SetValueSlot = 9;   // IUnknown(3) IsSupported IsModifiable GetParameterRange GetParameterValues GetDefaultValue GetValue SetValue

    public static bool SetUInt(nint unknown, Guid api, uint value) => Set(unknown, api, new Variant { vt = 19, ui4 = value });   // VT_UI4
    public static bool SetBool(nint unknown, Guid api, bool value) => Set(unknown, api, new Variant { vt = 11, boolVal = (short)(value ? -1 : 0) });   // VT_BOOL

    static bool Set(nint unknown, Guid api, Variant v)
    {
        var iid = IID_ICodecAPI;
        if (Marshal.QueryInterface(unknown, in iid, out var codec) != 0 || codec == 0) return false;
        try
        {
            var vtbl = *(nint**)codec;
            var setValue = (delegate* unmanaged[Stdcall]<nint, Guid*, Variant*, int>)vtbl[SetValueSlot];
            return setValue(codec, &api, &v) >= 0;
        }
        finally { Marshal.Release(codec); }
    }
}
