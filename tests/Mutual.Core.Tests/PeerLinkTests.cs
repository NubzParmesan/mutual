using System.Net;
using System.Net.Sockets;
using Mutual.Core;

namespace Mutual.Core.Tests;

public class PeerLinkTests
{
    static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    [Fact]
    public async Task PairedPeersConnectAndExchange()
    {
        using var a = Pairing.CreateIdentity("alice-test");
        using var b = Pairing.CreateIdentity("bob-test");
        int port = FreePort();
        var server = PeerLink.OpenAsync(LinkRole.Server, IPAddress.Loopback, "", port, a, b.RawData, TimeSpan.FromSeconds(10));
        var client = PeerLink.OpenAsync(LinkRole.Client, IPAddress.Loopback, "127.0.0.1", port, b, a.RawData, TimeSpan.FromSeconds(10));
        using var s = await server;
        using var c = await client;
        await Task.WhenAll(PeerLink.ExchangeByteAsync(s, 1), PeerLink.ExchangeByteAsync(c, 1));
    }

    [Fact]
    public async Task WrongCertificateIsRejected()
    {
        using var a = Pairing.CreateIdentity("alice-test");
        using var b = Pairing.CreateIdentity("bob-test");
        using var stranger = Pairing.CreateIdentity("stranger");
        int port = FreePort();
        var server = PeerLink.OpenAsync(LinkRole.Server, IPAddress.Loopback, "", port, a, b.RawData, TimeSpan.FromSeconds(10));
        var client = PeerLink.OpenAsync(LinkRole.Client, IPAddress.Loopback, "127.0.0.1", port, stranger, a.RawData, TimeSpan.FromSeconds(10));
        await Assert.ThrowsAnyAsync<Exception>(async () => { using var _ = await server; });
        try { using var _ = await client; } catch { }
    }

    [Fact]
    public void ExpiredCertificateIsNotPinned()
    {
        using var old = Pairing.CreateIdentity("old", TimeSpan.FromDays(1), DateTimeOffset.UtcNow.AddDays(-10));
        Assert.False(PeerLink.IsPinned(old, old.RawData));
    }

    [Fact]
    public void OnlyTheExactCertificateIsPinned()
    {
        using var a = Pairing.CreateIdentity("a");
        using var b = Pairing.CreateIdentity("b");
        Assert.True(PeerLink.IsPinned(a, a.RawData));
        Assert.False(PeerLink.IsPinned(a, b.RawData));
        Assert.False(PeerLink.IsPinned(null, a.RawData));
    }

    [Fact]
    public async Task NobodyShowsUpTimesOut()
    {
        using var a = Pairing.CreateIdentity("a");
        using var b = Pairing.CreateIdentity("b");
        await Assert.ThrowsAsync<TimeoutException>(() =>
            PeerLink.OpenAsync(LinkRole.Server, IPAddress.Loopback, "", FreePort(), a, b.RawData, TimeSpan.FromMilliseconds(500)));
    }

    [Fact]
    public async Task LegacyConsentRunsAndEndsWhenOneSideStops()
    {
        using var a = Pairing.CreateIdentity("a");
        using var b = Pairing.CreateIdentity("b");
        int port = FreePort();
        var server = PeerLink.OpenAsync(LinkRole.Server, IPAddress.Loopback, "", port, a, b.RawData, TimeSpan.FromSeconds(10));
        var client = PeerLink.OpenAsync(LinkRole.Client, IPAddress.Loopback, "127.0.0.1", port, b, a.RawData, TimeSpan.FromSeconds(10));
        using var s = await server;
        using var c = await client;
        int enabled = 0, ready = 0;
        using var stopA = new CancellationTokenSource();
        var runA = LegacyConsent.RunAsync(s, () => { Interlocked.Increment(ref enabled); return Task.CompletedTask; }, () => Interlocked.Increment(ref ready), stopA.Token);
        var runB = LegacyConsent.RunAsync(c, () => { Interlocked.Increment(ref enabled); return Task.CompletedTask; }, () => Interlocked.Increment(ref ready), CancellationToken.None);
        await Task.Delay(2500);
        Assert.Equal(2, enabled);
        Assert.Equal(2, ready);
        stopA.Cancel();
        await runA;
        s.Dispose();   // a leaving closes the link
        await Assert.ThrowsAnyAsync<Exception>(() => runB.WaitAsync(TimeSpan.FromSeconds(15)));
    }
}
