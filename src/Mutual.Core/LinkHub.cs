using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;

namespace Mutual.Core;

// what a link is for, the connecting side says right after the handshake
public enum Channel : byte { Stream = 1, File = 2 }

// every link after the request ping comes thru here on the one stream port so one firewall rule
// covers streaming and files. a link only gets handed over if this pc is waiting for that kind rn
// (someone clicked or accepted), anything else gets closed after the handshake
// links can come thru the relay too, same tls on top so the relay only sees scrambled bytes
public sealed class LinkHub : IDisposable
{
    readonly Pairing p;
    readonly ConcurrentDictionary<Channel, TaskCompletionSource<SslStream>> waiting = new();
    readonly CancellationTokenSource stop = new();
    TcpListener? listener;
    readonly object startLock = new();

    // the relay, tried if connecting directly hasnt worked after a few seconds
    public Func<Channel, CancellationToken, Task<Stream>>? RelayDial { get; set; }
    public event Action<string>? Note;

    public LinkHub(Pairing pairing) { p = pairing; }

    // the listening side waits for them to connect, the other side keeps trying. gives up after wait
    public async Task<SslStream> OpenAsync(Channel ch, TimeSpan wait, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, stop.Token);
        timeout.CancelAfter(wait);
        try
        {
            return p.Role == LinkRole.Server ? await AcceptAsync(ch, timeout.Token) : await DialAsync(ch, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("Waiting expired. Nothing was switched on.");
        }
    }

    // listening side

    async Task<SslStream> AcceptAsync(Channel ch, CancellationToken ct)
    {
        EnsureListening();
        var tcs = new TaskCompletionSource<SslStream>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (waiting.TryGetValue(ch, out var old)) old.TrySetCanceled();
        waiting[ch] = tcs;
        // the relay can deliver too, whichever gets here first wins
        if (RelayDial != null) _ = RelayInAsync(ch, tcs, ct);
        using (ct.Register(() => tcs.TrySetCanceled()))
        {
            try { return await tcs.Task; }
            finally { waiting.TryRemove(new KeyValuePair<Channel, TaskCompletionSource<SslStream>>(ch, tcs)); }
        }
    }

    void EnsureListening()
    {
        lock (startLock)
        {
            if (listener != null) return;
            listener = new TcpListener(p.BindAddress, p.MyPorts.Stream);
            listener.Start(8);
            _ = AcceptLoop(listener);
        }
    }

    async Task AcceptLoop(TcpListener l)
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient tcp;
            try { tcp = await l.AcceptTcpClientAsync(stop.Token); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { await Task.Delay(500); continue; }
            _ = HandleIncoming(tcp);
        }
    }

    readonly SemaphoreSlim handshakes = new(8);

    async Task HandleIncoming(TcpClient tcp)
    {
        // a few at a time, the rest get dropped (the real friend just retries)
        if (!handshakes.Wait(0)) { tcp.Dispose(); return; }
        try
        {
            tcp.ReceiveTimeout = tcp.SendTimeout = 12000;
            using var hs = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            hs.CancelAfter(8000);
            var ssl = await PeerLink.SecureAsync(tcp.GetStream(), LinkRole.Server, p.Own, p.PeerCertRaw, hs.Token);
            Deliver(ssl, tcp);
        }
        catch (Exception) { tcp.Dispose(); }   // stranger, wrong cert or stalled, drop it
        finally { handshakes.Release(); }
    }

    async Task RelayInAsync(Channel ch, TaskCompletionSource<SslStream> tcs, CancellationToken ct)
    {
        try
        {
            await Task.Delay(1500, ct);   // give the direct way a head start
            if (tcs.Task.IsCompleted) return;
            var raw = await RelayDial!(ch, ct);
            var ssl = await PeerLink.SecureAsync(raw, LinkRole.Server, p.Own, p.PeerCertRaw, ct);
            if (!Deliver(ssl, null, expect: ch)) ssl.Dispose();
            else Note?.Invoke("connected through the relay");
        }
        catch (Exception) { }
    }

    // reads which channel they want and hands the link to whoevers waiting for it
    bool Deliver(SslStream ssl, TcpClient? tcp, Channel? expect = null)
    {
        var b = new byte[1];
        try
        {
            if (ssl.Read(b, 0, 1) != 1) throw new IOException();
            var ch = (Channel)b[0];
            if ((expect == null || expect == ch) && waiting.TryGetValue(ch, out var tcs) && tcs.TrySetResult(ssl)) return true;
        }
        catch (Exception) { }
        ssl.Dispose(); tcp?.Dispose();
        return false;
    }

    // connecting side

    async Task<SslStream> DialAsync(Channel ch, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        Task<SslStream>? relay = null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (RelayDial != null && relay == null && DateTime.UtcNow - started > TimeSpan.FromSeconds(4))
                relay = RelayOutAsync(ch, ct);
            if (relay is { IsCompletedSuccessfully: true }) { Note?.Invoke("connected through the relay"); return relay.Result; }
            var c = new TcpClient();
            try
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attempt.CancelAfter(2000);
                await c.ConnectAsync(p.PeerAddress, p.PeerPorts.Stream, attempt.Token);
                c.ReceiveTimeout = c.SendTimeout = 12000;
                var ssl = await PeerLink.SecureAsync(c.GetStream(), LinkRole.Client, p.Own, p.PeerCertRaw, ct);
                ssl.Write(new[] { (byte)ch });
                ssl.Flush();
                return ssl;
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                c.Dispose();
                if (relay != null)
                    try { var r = await relay.WaitAsync(TimeSpan.FromMilliseconds(500), ct); Note?.Invoke("connected through the relay"); return r; } catch (TimeoutException) { } catch (Exception) when (!ct.IsCancellationRequested) { relay = null; }
                else await Task.Delay(500, ct);
            }
        }
    }

    async Task<SslStream> RelayOutAsync(Channel ch, CancellationToken ct)
    {
        var raw = await RelayDial!(ch, ct);
        var ssl = await PeerLink.SecureAsync(raw, LinkRole.Client, p.Own, p.PeerCertRaw, ct);
        ssl.Write(new[] { (byte)ch });
        ssl.Flush();
        return ssl;
    }

    public void Dispose()
    {
        stop.Cancel();
        lock (startLock) { listener?.Stop(); listener = null; }
        foreach (var t in waiting.Values) t.TrySetCanceled();
    }
}
