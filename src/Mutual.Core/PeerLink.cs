using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace Mutual.Core;

public enum LinkRole { Server, Client }

// one tls link to the friend. both sides show a cert and each side only takes the one exact cert
// it paired with, not whatever some public ca signed. same as Open-PeerStream in the old scripts so they work together
public static class PeerLink
{
    // what the old scripts send as the target name, the pin is what actually gets checked
    public const string TargetName = "mutual-ssh";

    public static async Task<SslStream> OpenAsync(
        LinkRole role, IPAddress bindAddress, string peerAddress, int port,
        X509Certificate2 own, byte[] expectedPeerRaw, TimeSpan wait, CancellationToken ct = default)
    {
        if (!own.HasPrivateKey) throw new InvalidOperationException("This PC's pairing certificate has no private key under this Windows account.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(wait);
        TcpClient? tcp = null;
        try
        {
            tcp = role == LinkRole.Server
                ? await AcceptOneAsync(bindAddress, port, timeout.Token)
                : await ConnectWithRetryAsync(peerAddress, port, timeout.Token);
            tcp.ReceiveTimeout = 12000;
            tcp.SendTimeout = 12000;
            var ssl = await SecureAsync(tcp.GetStream(), role, own, expectedPeerRaw, timeout.Token);
            tcp = null;
            return ssl;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("Waiting expired. Nothing was switched on.");
        }
        finally { tcp?.Dispose(); }
    }

    // the pinned tls handshake over a stream thats already connected (direct or relayed, doesnt matter)
    public static async Task<SslStream> SecureAsync(Stream raw, LinkRole role, X509Certificate2 own, byte[] expectedPeerRaw, CancellationToken ct)
    {
        var ssl = new SslStream(raw, false, (_, cert, _, _) => IsPinned(cert, expectedPeerRaw));
        ssl.ReadTimeout = 12000;
        ssl.WriteTimeout = 12000;
        try
        {
            if (role == LinkRole.Server)
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = own,
                    ClientCertificateRequired = true,
                    EnabledSslProtocols = SslProtocols.Tls12,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                }, ct);
            else
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = TargetName,
                    ClientCertificates = new X509CertificateCollection { own },
                    EnabledSslProtocols = SslProtocols.Tls12,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                }, ct);
            if (!ssl.IsMutuallyAuthenticated) throw new AuthenticationException("Mutual TLS authentication failed.");
            return ssl;
        }
        catch { ssl.Dispose(); throw; }
    }

    // exact match on the raw cert and its not expired, nothing else counts
    public static bool IsPinned(X509Certificate? cert, byte[] expectedRaw)
    {
        if (cert == null) return false;
        using var c = new X509Certificate2(cert);
        var now = DateTime.UtcNow;
        return c.RawData.AsSpan().SequenceEqual(expectedRaw)
            && now >= c.NotBefore.ToUniversalTime() && now < c.NotAfter.ToUniversalTime();
    }

    static async Task<TcpClient> AcceptOneAsync(IPAddress bind, int port, CancellationToken ct)
    {
        var listener = new TcpListener(bind, port);
        listener.Start();
        try { return await listener.AcceptTcpClientAsync(ct); }
        finally { listener.Stop(); }
    }

    public static async Task<TcpClient> ConnectWithRetryAsync(string host, int port, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var c = new TcpClient();
            try
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attempt.CancelAfter(2000);
                await c.ConnectAsync(host, port, attempt.Token);
                return c;
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                c.Dispose();
                await Task.Delay(500, ct);
            }
        }
    }

    // both write the byte and both have to read the same byte back, the old scripts lockstep
    public static async Task ExchangeByteAsync(Stream s, byte value, CancellationToken ct = default)
    {
        await s.WriteAsync(new[] { value }, ct);
        await s.FlushAsync(ct);
        var buf = new byte[1];
        int n = await s.ReadAsync(buf, ct);
        if (n != 1 || buf[0] != value) throw new IOException("Peer disconnected or protocol mismatch.");
    }
}
