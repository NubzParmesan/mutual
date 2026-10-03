using System.Net;
using Mutual.Stream;

namespace Mutual.Core.Tests;

// throws random junk at everything that reads data from someone else. none of it should crash
// anything except with the normal "thats not valid" exceptions
public class FuzzTests
{
    static byte[] Junk(Random r, int max) { var b = new byte[r.Next(0, max)]; r.NextBytes(b); return b; }

    [Fact]
    public void Pairing_codes_from_junk_only_fail_cleanly()
    {
        var r = new Random(1);
        for (int i = 0; i < 2000; i++)
        {
            string code = i % 2 == 0
                ? "MUTUAL1-" + Convert.ToBase64String(Junk(r, 900)).Replace('+', '-').Replace('/', '_')
                : "MUTUAL1-" + new string(Enumerable.Range(0, r.Next(0, 300)).Select(_ => (char)r.Next(32, 127)).ToArray());
            try { Pairing.ReadCode(code); }
            catch (Exception e)
            {
                // a null reference or index error would mean a real bug in the parsing
                Assert.True(e is FormatException or InvalidDataException or System.Text.Json.JsonException
                    or System.Security.Cryptography.CryptographicException or ArgumentException, e.GetType().Name + ": " + e.Message);
            }
        }
    }

    [Fact]
    public void File_offer_details_from_junk_never_escape_downloads()
    {
        var r = new Random(2);
        for (int i = 0; i < 5000; i++)
        {
            var detail = new string(Enumerable.Range(0, r.Next(0, 80)).Select(_ => (char)r.Next(1, 0x2FFF)).ToArray()) + "|" + r.Next(-5, int.MaxValue);
            if (!FileTransfer.TryParse(detail, out var name, out var size)) continue;
            Assert.DoesNotContain('/', name);
            Assert.DoesNotContain('\\', name);
            Assert.DoesNotContain(':', name);
            Assert.True(size >= 0 && size <= FileTransfer.MaxSize);
            Assert.Equal(name, Path.GetFileName(name));
        }
    }

    [Fact]
    public void Stream_messages_from_junk_only_fail_cleanly()
    {
        var r = new Random(3);
        for (int i = 0; i < 3000; i++)
        {
            var wire = new Wire(new MemoryStream(Junk(r, 64)));
            try { while (wire.Read() is { } m) { if (m.kind == Msg.Input && m.payload.Length >= 9) Wire.ReadInput(m.payload); } }
            catch (InvalidDataException) { }
        }
    }

    [Fact]
    public void Udp_junk_and_wrong_key_packets_are_ignored()
    {
        var listen = UdpVideo.Listen(IPAddress.Loopback, 0, UdpVideo.NewKey(), isHost: false);
        using var _ = listen;
        int frames = 0;
        listen.Frame += (_, _, _) => frames++;
        using var sock = new System.Net.Sockets.UdpClient();
        var r = new Random(4);
        for (int i = 0; i < 3000; i++)
        {
            var b = Junk(r, 1400);
            if (b.Length > 3 && i % 3 == 0) { b[0] = (byte)'M'; b[1] = 1; }   // looks like ours at a glance
            sock.Send(b, b.Length, new IPEndPoint(IPAddress.Loopback, listen.Port));
        }
        Thread.Sleep(300);
        Assert.Equal(0, frames);
        Assert.False(listen.Up);
    }
}

public class Round2Tests
{
    [Fact]
    public void A_code_that_unpacks_to_gigabytes_gets_refused_fast()
    {
        // a few kb of compressed zeros that would unpack to 200 mb
        using var ms = new MemoryStream();
        using (var z = new System.IO.Compression.DeflateStream(ms, System.IO.Compression.CompressionLevel.SmallestSize, leaveOpen: true))
        {
            var zeros = new byte[1 << 20];
            for (int i = 0; i < 200; i++) z.Write(zeros);
        }
        var code = "MUTUAL1-" + Convert.ToBase64String(ms.ToArray()).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.Throws<FormatException>(() => Pairing.ReadCode(code));
        Assert.True(sw.ElapsedMilliseconds < 2000);
    }

    [Fact]
    public void Pointer_pixels_round_trip_and_junk_is_refused()
    {
        using var bmp = new System.Drawing.Bitmap(32, 20, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        bmp.SetPixel(3, 4, System.Drawing.Color.FromArgb(200, 10, 20, 30));
        var packed = Mutual.Stream.CursorPixels.Pack(bmp);
        using var back = Mutual.Stream.CursorPixels.Unpack(packed)!;
        Assert.Equal(32, back.Width);
        Assert.Equal(System.Drawing.Color.FromArgb(200, 10, 20, 30), back.GetPixel(3, 4));
        var r = new Random(5);
        for (int i = 0; i < 2000; i++)
        {
            var junk = new byte[r.Next(0, 3000)]; r.NextBytes(junk);
            using var b = Mutual.Stream.CursorPixels.Unpack(junk);   // either null or exactly sized, never a crash
        }
        Assert.Null(Mutual.Stream.CursorPixels.Unpack(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }));   // 65535 x 65535
    }
}
