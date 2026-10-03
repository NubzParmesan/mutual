using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Mutual.Core;

// one file over a file link. the header has to match what the popup offered or it hangs up
// saves as name.part and only renames it once the sha-256 at the end matches
// wire: name length (2) + name + size (8), the bytes, 32 bytes of sha-256. reply 1 = saved and checked
public static class FileTransfer
{
    public const long MaxSize = 64L << 30;   // 64 gb, js a sanity limit

    // "name|size", what the popup shows and what the receiver checks against
    public static string Describe(string path) => Path.GetFileName(path) + "|" + new FileInfo(path).Length;

    public static bool TryParse(string? detail, out string name, out long size)
    {
        name = ""; size = 0;
        if (detail == null) return false;
        int bar = detail.LastIndexOf('|');
        if (bar <= 0 || !long.TryParse(detail[(bar + 1)..], out size) || size < 0 || size > MaxSize) return false;
        name = SafeName(detail[..bar]);
        return name.Length > 0;
    }

    // just a file name, no folders or device names or anything that gets out of downloads
    public static string SafeName(string name)
    {
        name = name.Replace('\\', '/').Split('/').Last();
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        name = name.Trim().TrimEnd('.');
        var stem = Path.GetFileNameWithoutExtension(name).ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" || (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsDigit(stem[3]))) name = "_" + name;
        return name.Length > 200 ? name[..200] : name;
    }

    public static async Task SendAsync(Stream link, string path, IProgress<long>? progress, CancellationToken ct)
    {
        var name = Encoding.UTF8.GetBytes(Path.GetFileName(path));
        await using var file = File.OpenRead(path);
        var head = new byte[2 + name.Length + 8];
        BinaryPrimitives.WriteUInt16LittleEndian(head, (ushort)name.Length);
        name.CopyTo(head, 2);
        BinaryPrimitives.WriteInt64LittleEndian(head.AsSpan(2 + name.Length), file.Length);
        await link.WriteAsync(head, ct);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buf = new byte[256 * 1024];
        long sent = 0;
        int n;
        while ((n = await file.ReadAsync(buf, ct)) > 0)
        {
            sha.AppendData(buf, 0, n);
            await link.WriteAsync(buf.AsMemory(0, n), ct);
            sent += n;
            progress?.Report(sent);
        }
        await link.WriteAsync(sha.GetHashAndReset(), ct);
        await link.FlushAsync(ct);
        var ack = new byte[1];
        if (await link.ReadAsync(ack, ct) != 1 || ack[0] != 1) throw new IOException("They didn't confirm the file arrived intact.");
    }

    // never overwrites, makes "name (2).ext" instead
    public static async Task<string> ReceiveAsync(Stream link, string folder, string expectName, long expectSize, IProgress<long>? progress, CancellationToken ct)
    {
        var lenBuf = new byte[2];
        await link.ReadExactlyAsync(lenBuf, ct);
        var nameBuf = new byte[BinaryPrimitives.ReadUInt16LittleEndian(lenBuf)];
        await link.ReadExactlyAsync(nameBuf, ct);
        var sizeBuf = new byte[8];
        await link.ReadExactlyAsync(sizeBuf, ct);
        string name = SafeName(Encoding.UTF8.GetString(nameBuf));
        long size = BinaryPrimitives.ReadInt64LittleEndian(sizeBuf);
        if (name != expectName || size != expectSize) throw new InvalidDataException("The file isn't the one that was offered; nothing was saved.");

        Directory.CreateDirectory(folder);
        var final = Unique(Path.Combine(folder, name));
        var part = final + ".part";
        try
        {
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var file = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buf = new byte[256 * 1024];
                long got = 0;
                while (got < size)
                {
                    int n = await link.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, size - got)), ct);
                    if (n <= 0) throw new IOException("The transfer was cut off.");
                    sha.AppendData(buf, 0, n);
                    await file.WriteAsync(buf.AsMemory(0, n), ct);
                    got += n;
                    progress?.Report(got);
                }
            }
            var theirs = new byte[32];
            await link.ReadExactlyAsync(theirs, ct);
            if (!sha.GetHashAndReset().AsSpan().SequenceEqual(theirs)) throw new InvalidDataException("The file arrived damaged (checksum mismatch); it was thrown away.");
            File.Move(part, final);
            await link.WriteAsync(new byte[] { 1 }, ct);
            await link.FlushAsync(ct);
            return final;
        }
        catch { try { File.Delete(part); } catch { } throw; }
    }

    static string Unique(string path)
    {
        if (!File.Exists(path) && !File.Exists(path + ".part")) return path;
        var dir = Path.GetDirectoryName(path)!; var stem = Path.GetFileNameWithoutExtension(path); var ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            var p = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(p) && !File.Exists(p + ".part")) return p;
        }
    }

    // the real downloads folder, wherever onedrive put it
    public static string DownloadsFolder()
    {
        try
        {
            var id = new Guid("374DE290-123F-4565-9164-39C4925E467B");
            if (SHGetKnownFolderPath(ref id, 0, 0, out var p) == 0) { var s = System.Runtime.InteropServices.Marshal.PtrToStringUni(p)!; System.Runtime.InteropServices.Marshal.FreeCoTaskMem(p); return s; }
        }
        catch { }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll")] static extern int SHGetKnownFolderPath(ref Guid id, uint flags, nint token, out nint path);
}
