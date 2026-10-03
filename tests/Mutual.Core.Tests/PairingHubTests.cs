using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Mutual.Core;
using System.Security.Cryptography.X509Certificates;

namespace Mutual.Core.Tests;

public class PairingHubTests
{
    static int FreePort() { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); int p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p; }
    static string TempDir() { var d = Path.Combine(Path.GetTempPath(), "mutual-test-" + Guid.NewGuid().ToString("N")[..8]); Directory.CreateDirectory(d); return d; }

    // two pcs paired by swapping codes, on loopback with their own ports
    static (Pairing a, Pairing b) Pair(string? peerAddressForA = null)
    {
        var ia = Pairing.CreateIdentity("a"); var ib = Pairing.CreateIdentity("b");
        var pa = new Ports(FreePort(), FreePort(), FreePort()); var pb = new Ports(FreePort(), FreePort(), FreePort());
        var codeA = Pairing.MakeCode("alice", ia, pa, new[] { "127.0.0.1" });
        var codeB = Pairing.MakeCode("bob", ib, pb, new[] { "127.0.0.1" });
        var a = Pairing.FromOffer(Pairing.ReadCode(codeB), ia, TempDir(), pa, peerAddressForA ?? "127.0.0.1");
        var b = Pairing.FromOffer(Pairing.ReadCode(codeA), ib, TempDir(), pb, "127.0.0.1");
        return (a, b);
    }

    [Fact]
    public void Codes_round_trip_and_agree_on_roles_and_safety_code()
    {
        var (a, b) = Pair();
        Assert.Equal("bob", a.PeerName);
        Assert.NotEqual(a.Role, b.Role);
        Assert.Equal(a.SafetyCode, b.SafetyCode);
        Assert.Equal(a.PairId, b.PairId);
        Assert.True(Pairing.MakeCode("x", a.Own).Length < 1200, "pairing code should stay short enough to paste");
        Assert.Throws<FormatException>(() => Pairing.ReadCode("hello"));
    }

    [Theory]
    [InlineData("25.1.2.3", true)]
    [InlineData("my-pc.local", true)]
    [InlineData("-oProxyCommand=calc", false)]
    [InlineData("host; calc", false)]
    [InlineData("", false)]
    public void Only_plain_addresses_count(string a, bool ok) => Assert.Equal(ok, Pairing.IsPlainAddress(a));

    [Fact]
    public void A_code_with_nasty_addresses_loses_them()
    {
        using var id = Pairing.CreateIdentity("x");
        var code = Pairing.MakeCode("evil\nname", id, null, new[] { "-oProxyCommand=calc", "10.0.0.5" });
        var offer = Pairing.ReadCode(code);
        Assert.Equal(new[] { "10.0.0.5" }, offer.Addresses);
        Assert.DoesNotContain('\n', offer.Name);
    }

    // only bad input here, a good one would pop a real admin prompt
    [Theory]
    [InlineData("1.2.3.4 & calc", "0.0.0.0")]
    [InlineData("1.2.3.4", "0.0.0.0 | calc")]
    [InlineData("evil.example.com", "0.0.0.0")]
    public void Firewall_command_refuses_anything_but_ips(string peer, string local) =>
        Assert.False(StreamPort.AddRules(new[] { new StreamPort.Rule("x", "TCP", 1) }, Environment.ProcessPath!, local, peer));

    [Fact]
    public void Saved_pairing_loads_back_with_a_usable_key()
    {
        var (a, _) = Pair();
        var again = Pairing.Load(a.Folder);
        Assert.Equal(a.PeerCertRaw, again.PeerCertRaw);
        Assert.True(again.Own.HasPrivateKey);
        Assert.Equal(a.MyPorts, again.MyPorts);
        // the key on disk is encrypted, not a plain pfx
        Assert.DoesNotContain("BEGIN", File.ReadAllText(Pairing.SavedPath(a.Folder)));
    }

    [Fact]
    public async Task Hub_links_both_channels_and_refuses_what_nobody_asked_for()
    {
        var (a, b) = Pair();
        using var ha = new LinkHub(a); using var hb = new LinkHub(b);
        var t1 = ha.OpenAsync(Channel.Stream, TimeSpan.FromSeconds(10));
        var t2 = hb.OpenAsync(Channel.Stream, TimeSpan.FromSeconds(10));
        using var la = await t1; using var lb = await t2;
        la.Write(new byte[] { 42 }); la.Flush();
        var one = new byte[1]; await lb.ReadExactlyAsync(one);
        Assert.Equal(42, one[0]);

        // a file link nobody asked for gets dropped so the dialer keeps waiting and times out
        var server = a.Role == LinkRole.Server ? ha : hb; var client = a.Role == LinkRole.Server ? hb : ha;
        _ = server.OpenAsync(Channel.Stream, TimeSpan.FromSeconds(1));   // make sure its listening
        var unwanted = client.OpenAsync(Channel.File, TimeSpan.FromSeconds(3));
        try { using var x = await unwanted; var buf = new byte[1]; x.ReadTimeout = 2000; Assert.ThrowsAny<IOException>(() => x.ReadExactly(buf)); }
        catch (TimeoutException) { }
        catch (IOException) { }
    }

    [Fact]
    public async Task File_goes_across_intact_and_lands_in_the_folder()
    {
        var (a, b) = Pair();
        using var ha = new LinkHub(a); using var hb = new LinkHub(b);
        var src = Path.Combine(TempDir(), "colony save.rws");
        var data = new byte[3_500_000]; new Random(1).NextBytes(data);
        File.WriteAllBytes(src, data);
        Assert.True(FileTransfer.TryParse(FileTransfer.Describe(src), out var name, out var size));
        var dest = TempDir();
        var send = Task.Run(async () => { using var l = await ha.OpenAsync(Channel.File, TimeSpan.FromSeconds(10)); await FileTransfer.SendAsync(l, src, null, default); });
        var recv = Task.Run(async () => { using var l = await hb.OpenAsync(Channel.File, TimeSpan.FromSeconds(10)); return await FileTransfer.ReceiveAsync(l, dest, name, size, null, default); });
        await send;
        var saved = await recv;
        Assert.Equal(data, File.ReadAllBytes(saved));
        Assert.Empty(Directory.GetFiles(dest, "*.part"));
    }

    [Theory]
    [InlineData("../../evil.exe", "evil.exe")]
    [InlineData("C:\\Windows\\x.dll", "x.dll")]
    [InlineData("CON.txt", "_CON.txt")]
    [InlineData("a:b?.txt", "a_b_.txt")]
    public void File_names_cant_escape_downloads(string offered, string expected) => Assert.Equal(expected, FileTransfer.SafeName(offered));

    // the rendezvous server, running for real

    static Process StartServer(int port)
    {
        var dll = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tools", "Mutual.Rendezvous", "bin", "Debug", "net8.0", "Mutual.Rendezvous.dll"));
        Assert.True(File.Exists(dll), "build the rendezvous server first: " + dll);
        var dotnet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "dotnet", "dotnet.exe");
        var p = Process.Start(new ProcessStartInfo(File.Exists(dotnet) ? dotnet : "dotnet", $"\"{dll}\" {port}") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true })!;
        p.StandardOutput.ReadLine();   // "listening"
        return p;
    }

    [Fact]
    public async Task Rendezvous_passes_signed_requests_and_relays_when_direct_fails()
    {
        int port = FreePort();
        using var server = StartServer(port);
        try
        {
            // the address a has for b is dead so only the relay can join them
            var (a, b) = Pair(peerAddressForA: "127.0.0.1");
            a.PeerAddress = "127.0.0.2"; b.PeerAddress = "127.0.0.2";
            using var ra = new Rendezvous("127.0.0.1:" + port, a); using var rb = new Rendezvous("127.0.0.1:" + port, b);
            using var stop = new CancellationTokenSource();
            _ = ra.AnnounceLoop(false, stop.Token); _ = rb.AnnounceLoop(false, stop.Token);
            var sw = Stopwatch.StartNew();
            while (!(ra.Connected && rb.Connected) && sw.ElapsedMilliseconds < 5000) await Task.Delay(50);
            Assert.True(ra.Connected && rb.Connected);
            await Task.Delay(300);

            var got = new TaskCompletionSource<Request>();
            rb.Request += r => got.TrySetResult(r);
            Assert.True(ra.SendRequest(new Request(RequestKind.Stream, "custom box (800x600)")));
            var req = await got.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(RequestKind.Stream, req.Kind);
            Assert.Equal("custom box (800x600)", req.Detail);

            using var ha = new LinkHub(a) { RelayDial = ra.RelayAsync }; using var hb = new LinkHub(b) { RelayDial = rb.RelayAsync };
            var t1 = ha.OpenAsync(Channel.Stream, TimeSpan.FromSeconds(20));
            var t2 = hb.OpenAsync(Channel.Stream, TimeSpan.FromSeconds(20));
            using var la = await t1; using var lb = await t2;
            la.Write(new byte[] { 7, 8, 9 }); la.Flush();
            var buf = new byte[3]; await lb.ReadExactlyAsync(buf);
            Assert.Equal(new byte[] { 7, 8, 9 }, buf);
            stop.Cancel();
        }
        finally { server.Kill(); }
    }

    [Fact]
    public async Task Rendezvous_drops_requests_not_signed_by_the_friend()
    {
        int port = FreePort();
        using var server = StartServer(port);
        try
        {
            var (a, b) = Pair();
            // an impostor with the same pair id but its own key, what a bad server could try
            var impostor = new Pairing
            {
                PeerName = "x", PeerAddress = "127.0.0.1", BindAddress = IPAddress.Any, Role = a.Role, Own = Pairing.CreateIdentity("evil"),
                PeerCertRaw = a.PeerCertRaw, Folder = TempDir(),
            };
            using var rb = new Rendezvous("127.0.0.1:" + port, b);
            using var stop = new CancellationTokenSource();
            _ = rb.AnnounceLoop(false, stop.Token);
            int seen = 0;
            rb.Request += _ => seen++;
            // the impostor joins under the real pair id by talking the protocol directly
            using var c = new TcpClient(); await c.ConnectAsync("127.0.0.1", port);
            var w = new StreamWriter(c.GetStream()) { AutoFlush = true, NewLine = "\n" };
            await Task.Delay(500);
            await w.WriteLineAsync($"{{\"op\":\"hello\",\"pair\":\"{b.PairId}\",\"me\":\"0123456789abcdef\"}}");
            var body = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"2|{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}|fake"));
            using var key = impostor.Own.GetECDsaPrivateKey()!;
            var sig = Convert.ToBase64String(key.SignData(Convert.FromBase64String(body), System.Security.Cryptography.HashAlgorithmName.SHA256));
            await w.WriteLineAsync($"{{\"op\":\"signal\",\"pair\":\"{b.PairId}\",\"data\":\"{body}\",\"sig\":\"{sig}\"}}");
            await Task.Delay(800);
            Assert.Equal(0, seen);
            stop.Cancel();
        }
        finally { server.Kill(); }
    }
}
