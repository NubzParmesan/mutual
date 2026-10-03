using System.Runtime.InteropServices;

namespace Mutual.Core;

// encrypts stuff so only this windows account can read it back (dpapi)
public static class Dpapi
{
    [StructLayout(LayoutKind.Sequential)] struct Blob { public int size; public nint data; }
    [DllImport("crypt32.dll", SetLastError = true)] static extern bool CryptProtectData(ref Blob input, string? desc, nint entropy, nint reserved, nint prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)] static extern bool CryptUnprotectData(ref Blob input, nint desc, nint entropy, nint reserved, nint prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] static extern nint LocalFree(nint p);

    public static byte[] Protect(byte[] data) => Run(data, true);
    public static byte[] Unprotect(byte[] data) => Run(data, false);

    static byte[] Run(byte[] data, bool protect)
    {
        var h = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            var input = new Blob { size = data.Length, data = h.AddrOfPinnedObject() };
            const int UI_FORBIDDEN = 0x1;
            bool ok = protect ? CryptProtectData(ref input, "Mutual pairing", 0, 0, 0, UI_FORBIDDEN, out var output)
                              : CryptUnprotectData(ref input, 0, 0, 0, 0, UI_FORBIDDEN, out output);
            if (!ok) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), protect ? "Couldn't encrypt the pairing key." : "Couldn't decrypt the pairing key (different Windows account?).");
            try { var r = new byte[output.size]; Marshal.Copy(output.data, r, 0, output.size); return r; }
            finally { LocalFree(output.data); }
        }
        finally { h.Free(); }
    }
}
