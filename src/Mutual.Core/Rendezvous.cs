using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mutual.Core;

// for when youre not on the same vpn. both pcs stay connected to a small server (tools/Mutual.Rendezvous)
// under a pair id. it swaps addresses, passes requests along (signed so it cant fake one) and relays the
// link if theres no direct way. tls is still end to end so all it sees is the id and scrambled bytes
public sealed class Rendezvous : IDisposable
{
    readonly string host; readonly int port;
    readonly Pairing p;
    readonly string me;
    TcpClient? control;
    StreamWriter? writer;
    readonly object writeLock = new();
    Upnp.Mapping[] mappings = Array.Empty<Upnp.Mapping>();
    long lastSentTs, lastSeenTs;

    // somewhere you can reach them directly, their upnp port or their lan address if youre on the same network
    public event Action<string>? Found;
    // a request that came thru the server, signature checked
    public event Action<Request>? Request;
    public bool Connected => control?.Connected == true;
    public bool PeerOnline { get; private set; }

    public Rendezvous(string server, Pairing pairing)
    {
        var parts = server.Trim().Split(':');
        host = parts[0]; port = parts.Length > 1 ? int.Parse(parts[1]) : 28810;
        p = pairing;
        me = Convert.ToHexString(SHA256.HashData(p.Own.RawData))[..16];
    }

    // stays connected and reconnects until cancelled
    public async Task AnnounceLoop(bool useUpnp, CancellationToken ct)
    {
        if (useUpnp && p.Role == LinkRole.Server)
        {
            // the router forgets after an hour so renew before that
            var ports = new[] { (p.MyPorts.Stream, "TCP"), (p.MyPorts.Stream, "UDP"), (p.MyPorts.Ping, "TCP") };
            mappings = await Task.Run(() => Upnp.Map(ports, "Mutual"), ct);
            _ = Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested)
                {
                    try { await Task.Delay(TimeSpan.FromMinutes(45), ct); } catch { return; }
                    if (mappings.Length > 0) mappings = Upnp.Map(ports, "Mutual");
                }
            }, ct);
        }
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var c = new TcpClient();
                await c.ConnectAsync(host, port, ct);
                control = c;
                var s = c.GetStream();
                using var reader = new StreamReader(s, Encoding.UTF8);
                writer = new StreamWriter(s, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
                var local = Pairing.LocalAddresses();
                var external = mappings.FirstOrDefault()?.ExternalIp;
                Send(new JsonObject { ["op"] = "hello", ["pair"] = p.PairId, ["me"] = me, ["local"] = new JsonArray(local.Select(a => (JsonNode)a!).ToArray()), ["upnp"] = external });
                string? line;
                while ((line = await reader.ReadLineAsync(ct)) != null)
                {
                    var m = JsonNode.Parse(line);
                    switch ((string?)m?["op"])
                    {
                        case "peer": PeerOnline = true; OnPeer(m!); break;
                        case "gone": PeerOnline = false; break;
                        case "signal": OnSignal(m!); break;
                    }
                }
            }
            catch (Exception) when (!ct.IsCancellationRequested) { }
            finally { control = null; writer = null; PeerOnline = false; }
            try { await Task.Delay(10_000, ct); } catch { break; }
        }
    }

    void OnPeer(JsonNode m)
    {
        // same public address as us means same network, use their lan address
        var theirPublic = (string?)m["public"]; var ourPublic = (string?)m["yourPublic"];
        var theirLocal = m["local"]?.AsArray().Select(x => (string?)x).Where(x => x != null).ToArray() ?? Array.Empty<string?>();
        // the server could send anything here so only plain ip addresses get thru
        static bool Ip(string? a) => a != null && IPAddress.TryParse(a, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork && ip.ToString() == a;
        var lan = theirLocal.FirstOrDefault(Ip);
        if (theirPublic != null && theirPublic == ourPublic && lan != null) { Found?.Invoke(lan); return; }
        var upnp = (string?)m["upnp"];
        if (Ip(upnp)) Found?.Invoke(upnp!);
    }

    void Send(JsonNode msg) { lock (writeLock) writer?.WriteLine(msg.ToJsonString()); }

    // requests thru the server

    public bool SendRequest(Request req)
    {
        if (writer == null) return false;
        long ts = Math.Max(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), lastSentTs + 1);
        lastSentTs = ts;
        var body = Encoding.UTF8.GetBytes($"{(int)req.Kind}|{ts}|{req.Detail}");
        var sig = Sign(p.Own, body);
        Send(new JsonObject { ["op"] = "signal", ["pair"] = p.PairId, ["data"] = Convert.ToBase64String(body), ["sig"] = Convert.ToBase64String(sig) });
        return true;
    }

    void OnSignal(JsonNode m)
    {
        try
        {
            var body = Convert.FromBase64String((string)m["data"]!);
            var sig = Convert.FromBase64String((string)m["sig"]!);
            using var peer = new X509Certificate2(p.PeerCertRaw);
            if (!Verify(peer, body, sig)) return;   // not from them, ignore
            var parts = Encoding.UTF8.GetString(body).Split('|', 3);
            long ts = long.Parse(parts[1]);
            // too old or not newer than the last one, a replay
            if (Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - ts) > 120_000 || ts <= lastSeenTs) return;
            lastSeenTs = ts;
            int kind = int.Parse(parts[0]);
            if (!Enum.IsDefined(typeof(RequestKind), (byte)kind) || parts[2].Length > 200) return;
            Request?.Invoke(new Request((RequestKind)kind, parts[2].Length > 0 ? parts[2] : null));
        }
        catch { }
    }

    static byte[] Sign(X509Certificate2 own, byte[] data)
    {
        using var ec = own.GetECDsaPrivateKey();
        if (ec != null) return ec.SignData(data, HashAlgorithmName.SHA256);
        using var rsa = own.GetRSAPrivateKey() ?? throw new InvalidOperationException("No usable private key.");
        return rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }

    static bool Verify(X509Certificate2 peer, byte[] data, byte[] sig)
    {
        using var ec = peer.GetECDsaPublicKey();
        if (ec != null) return ec.VerifyData(data, sig, HashAlgorithmName.SHA256);
        using var rsa = peer.GetRSAPublicKey();
        return rsa != null && rsa.VerifyData(data, sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }

    // relay

    // a raw pipe to them thru the server for one channel
    // both sides ask for the same pair and channel and the server joins them once both are there
    public async Task<Stream> RelayAsync(Channel ch, CancellationToken ct)
    {
        var c = new TcpClient();
        try
        {
            await c.ConnectAsync(host, port, ct);
            var s = c.GetStream();
            var hello = Encoding.UTF8.GetBytes(new JsonObject { ["op"] = "relay", ["pair"] = p.PairId, ["me"] = me, ["chan"] = (int)ch }.ToJsonString() + "\n");
            await s.WriteAsync(hello, ct);
            // server says ok once their half shows up
            var ok = new byte[3];
            await s.ReadExactlyAsync(ok, ct);
            if (ok[0] != 'o' || ok[1] != 'k' || ok[2] != '\n') throw new IOException("The relay refused.");
            c.ReceiveTimeout = c.SendTimeout = 12000;
            return new OwningStream(s, c);
        }
        catch { c.Dispose(); throw; }
    }

    // closes its socket when its done
    sealed class OwningStream : Stream
    {
        readonly NetworkStream s; readonly TcpClient c;
        public OwningStream(NetworkStream s, TcpClient c) { this.s = s; this.c = c; }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int ReadTimeout { get => s.ReadTimeout; set => s.ReadTimeout = value; }
        public override int WriteTimeout { get => s.WriteTimeout; set => s.WriteTimeout = value; }
        public override bool CanTimeout => true;
        public override void Flush() => s.Flush();
        public override int Read(byte[] buffer, int offset, int count) => s.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => s.ReadAsync(buffer, ct);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => s.ReadAsync(buffer, offset, count, ct);
        public override void Write(byte[] buffer, int offset, int count) => s.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => s.WriteAsync(buffer, ct);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => s.WriteAsync(buffer, offset, count, ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) { s.Dispose(); c.Dispose(); } base.Dispose(disposing); }
    }

    public void Dispose()
    {
        control?.Dispose();
        foreach (var m in mappings) Upnp.Unmap(m);
    }
}
