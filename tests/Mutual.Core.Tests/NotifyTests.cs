using System.Net;
using System.Net.Sockets;
using Mutual.Core;

namespace Mutual.Core.Tests;

public class NotifyTests
{
    static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    async Task<Request?> RoundTrip(Request req, bool legacy, bool wrongCert = false)
    {
        using var a = Pairing.CreateIdentity("a");
        using var b = Pairing.CreateIdentity("b");
        using var stranger = Pairing.CreateIdentity("x");
        int port = FreePort();
        Request? got = null;
        using var stop = new CancellationTokenSource();
        var listen = Notify.ListenAsync(IPAddress.Loopback, port, null, () => a, b.RawData, r => got = r, stop.Token);
        await Task.Delay(100);
        bool acked = await Notify.SendAsync("127.0.0.1", port, wrongCert ? stranger : b, a.RawData, req, legacy);
        await Task.Delay(100);
        stop.Cancel();
        try { await listen; } catch { }
        Assert.Equal(!wrongCert, acked);
        return got;
    }

    [Fact]
    public async Task MutualRequestCarriesKindAndDetail()
    {
        var got = await RoundTrip(new Request(RequestKind.Stream, "window: RimWorld"), legacy: false);
        Assert.Equal(new Request(RequestKind.Stream, "window: RimWorld"), got);
    }

    [Fact]
    public async Task LegacyPingIsAnSshRequest()
    {
        var got = await RoundTrip(new Request(RequestKind.Ssh, null), legacy: true);
        Assert.Equal(RequestKind.Ssh, got!.Kind);
    }

    [Fact]
    public async Task StrangersCantPing()
    {
        var got = await RoundTrip(new Request(RequestKind.Play, null), legacy: false, wrongCert: true);
        Assert.Null(got);
    }

    [Fact]
    public void ActivityLogRoundTripsInTheOldFormat()
    {
        var path = Path.Combine(Path.GetTempPath(), "mutual-activity-" + Guid.NewGuid() + ".jsonl");
        var log = new ActivityLog(path);
        log.Write(ActivityResult.OK, "Both peers ready; Windows SSH enabled");
        File.AppendAllText(path, "not json\n");
        var rows = log.ReadRecent();
        Assert.Single(rows);
        Assert.Equal(ActivityResult.OK, rows[0].Result);
        var raw = File.ReadAllLines(path)[0];
        Assert.Contains("\"time\":", raw); Assert.Contains("\"machine\":", raw); Assert.Contains("\"result\":\"OK\"", raw); Assert.Contains("\"action\":", raw);
        File.Delete(path);
    }
}
