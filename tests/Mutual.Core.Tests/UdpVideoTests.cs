using System.Net;
using Mutual.Stream;

namespace Mutual.Core.Tests;

public class UdpVideoTests
{
    [Theory]
    [InlineData(10, 2, 1000)]
    [InlineData(200, 40, 1100)]
    [InlineData(1, 1, 50)]
    public void ReedSolomon_rebuilds_from_any_k_shards(int k, int m, int len)
    {
        var rng = new Random(k * 31 + m);
        var data = Enumerable.Range(0, k).Select(_ => { var b = new byte[len]; rng.NextBytes(b); return b; }).ToArray();
        var parity = Enumerable.Range(0, m).Select(_ => new byte[len]).ToArray();
        ReedSolomon.Encode(data, parity, len);
        for (int trial = 0; trial < 20; trial++)
        {
            var shards = data.Concat(parity).Select(x => (byte[]?)x.ToArray()).ToArray();
            // lose exactly m random shards
            foreach (var i in Enumerable.Range(0, k + m).OrderBy(_ => rng.Next()).Take(m)) shards[i] = null;
            Assert.True(ReedSolomon.Reconstruct(shards, k, len));
            for (int j = 0; j < k; j++) Assert.Equal(data[j], shards[j]);
        }
    }

    [Fact]
    public void ReedSolomon_refuses_with_too_few()
    {
        var data = new[] { new byte[] { 1 }, new byte[] { 2 }, new byte[] { 3 } };
        var parity = new[] { new byte[1] };
        ReedSolomon.Encode(data, parity, 1);
        Assert.False(ReedSolomon.Reconstruct(new byte[]?[] { null, null, data[2], parity[0] }, 3, 1));
    }

    static (UdpVideo host, UdpVideo viewer) Pair(byte[] key)
    {
        var listen = UdpVideo.Listen(IPAddress.Loopback, 0, key, isHost: false);
        var send = UdpVideo.Connect(new IPEndPoint(IPAddress.Loopback, listen.Port), key, isHost: true);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!(send.Up && listen.Up) && sw.ElapsedMilliseconds < 3000) Thread.Sleep(10);
        Assert.True(send.Up && listen.Up, "udp never came up");
        return (send, listen);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.05)]
    public void Frames_arrive_intact_in_order(double drop)
    {
        var key = UdpVideo.NewKey();
        var (host, viewer) = Pair(key);
        using var _h = host; using var _v = viewer;
        var got = new List<(long ts, byte[] data)>();
        int lostEvents = 0;
        viewer.Frame += (ts, k, d) => { lock (got) got.Add((ts, d)); };
        viewer.Lost += () => Interlocked.Increment(ref lostEvents);
        host.DropRate = drop;
        host.ExpectedLoss = drop;
        var rng = new Random(7);
        var sent = new Dictionary<long, byte[]>();
        for (int i = 0; i < 120; i++)
        {
            var d = new byte[i == 0 ? 150_000 : rng.Next(500, 40_000)];
            rng.NextBytes(d);
            sent[i] = d;
            host.SendFrame(i, i == 0, d);
            Thread.Sleep(2);
        }
        Thread.Sleep(400);
        lock (got)
        {
            // every frame that made it is exact and in order
            for (int i = 1; i < got.Count; i++) Assert.True(got[i].ts > got[i - 1].ts);
            foreach (var (ts, data) in got) Assert.Equal(sent[ts], data);
            if (drop == 0) Assert.Equal(120, got.Count);
            else Assert.True(got.Count > 100, $"only {got.Count} of 120 frames survived {drop:P0} loss");
        }
    }

    [Fact]
    public void Wrong_key_is_ignored()
    {
        var listen = UdpVideo.Listen(IPAddress.Loopback, 0, UdpVideo.NewKey(), isHost: false);
        using var send = UdpVideo.Connect(new IPEndPoint(IPAddress.Loopback, listen.Port), UdpVideo.NewKey(), isHost: true);
        using var _ = listen;
        int frames = 0;
        listen.Frame += (_, _, _) => frames++;
        Thread.Sleep(500);
        send.SendFrame(1, true, new byte[5000]);
        Thread.Sleep(300);
        Assert.False(listen.Up);
        Assert.Equal(0, frames);
    }
}
