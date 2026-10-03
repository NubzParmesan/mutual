using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace Mutual.Core;

public enum RequestKind : byte { Ssh = 1, Stream = 2, File = 3, Play = 4 }

public sealed record Request(RequestKind Kind, string? Detail);

// "wanna connect?" pings on port 28792, same pinned tls as everything else
// a ping only asks, it never turns anything on. old mutual ssh sends MSN1 (means ssh), mutual sends
// MUT1 + kind + detail which the old notifier ignores
public static class Notify
{
    public const int Port = 28792;
    static readonly byte[] LegacyMagic = "MSN1"u8.ToArray();
    static readonly byte[] MutualMagic = "MUT1"u8.ToArray();

    public static async Task<bool> SendAsync(string host, int port, X509Certificate2 own, byte[] peerRaw, Request req, bool legacy = false, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(5000);
        using var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(host, port, timeout.Token);
            using var ssl = new SslStream(tcp.GetStream(), false, (_, c, _, _) => PeerLink.IsPinned(c, peerRaw));
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = PeerLink.TargetName,
                ClientCertificates = new X509CertificateCollection { own },
                EnabledSslProtocols = SslProtocols.Tls12,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            }, timeout.Token);
            if (!ssl.IsMutuallyAuthenticated) return false;
            if (legacy)
                await ssl.WriteAsync(LegacyMagic, timeout.Token);
            else
            {
                var detail = System.Text.Encoding.UTF8.GetBytes(req.Detail ?? "");
                if (detail.Length > 200) Array.Resize(ref detail, 200);
                var msg = new byte[4 + 1 + 1 + detail.Length];
                MutualMagic.CopyTo(msg, 0);
                msg[4] = (byte)req.Kind;
                msg[5] = (byte)detail.Length;
                detail.CopyTo(msg, 6);
                await ssl.WriteAsync(msg, timeout.Token);
            }
            await ssl.FlushAsync(timeout.Token);
            var ack = new byte[1];
            return await ssl.ReadAsync(ack, timeout.Token) == 1 && ack[0] == 1;
        }
        catch (Exception) when (!ct.IsCancellationRequested) { return false; }
    }

    // only answers the paired friend. wrong address, wrong cert or stalling all get dropped
    public static async Task ListenAsync(IPAddress bind, int port, string? peerAddress, Func<X509Certificate2> own, byte[] peerRaw,
        Action<Request> onRequest, CancellationToken stop)
    {
        var listener = new TcpListener(bind, port);
        listener.Start(4);
        try
        {
            while (!stop.IsCancellationRequested)
            {
                TcpClient tcp;
                try { tcp = await listener.AcceptTcpClientAsync(stop); }
                catch (OperationCanceledException) { break; }
                catch (SocketException) { await Task.Delay(1000, stop); continue; }
                using (tcp)
                {
                    if (peerAddress != null && ((IPEndPoint)tcp.Client.RemoteEndPoint!).Address.ToString() != peerAddress) continue;
                    var req = await ReceiveOneAsync(tcp, own(), peerRaw, stop);
                    if (req != null) onRequest(req);
                }
            }
        }
        finally { listener.Stop(); }
    }

    public static async Task<Request?> ReceiveOneAsync(TcpClient tcp, X509Certificate2 own, byte[] peerRaw, CancellationToken stop)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
        timeout.CancelAfter(4000);
        try
        {
            using var ssl = new SslStream(tcp.GetStream(), false, (_, c, _, _) => PeerLink.IsPinned(c, peerRaw));
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = own,
                ClientCertificateRequired = true,
                EnabledSslProtocols = SslProtocols.Tls12,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            }, timeout.Token);
            if (!ssl.IsMutuallyAuthenticated) return null;
            var head = new byte[4];
            await ssl.ReadExactlyAsync(head, timeout.Token);
            Request req;
            if (head.AsSpan().SequenceEqual(LegacyMagic)) req = new Request(RequestKind.Ssh, null);
            else if (head.AsSpan().SequenceEqual(MutualMagic))
            {
                var kd = new byte[2];
                await ssl.ReadExactlyAsync(kd, timeout.Token);
                var detail = new byte[kd[1]];
                if (detail.Length > 0) await ssl.ReadExactlyAsync(detail, timeout.Token);
                if (!Enum.IsDefined(typeof(RequestKind), kd[0])) return null;
                req = new Request((RequestKind)kd[0], detail.Length > 0 ? System.Text.Encoding.UTF8.GetString(detail) : null);
            }
            else return null;
            await ssl.WriteAsync(new byte[] { 1 }, timeout.Token);
            await ssl.FlushAsync(timeout.Token);
            return req;
        }
        catch (Exception) { return null; }
    }
}
