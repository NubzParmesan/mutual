using System.Net.Sockets;

namespace Mutual.Core;

// is their pc up running mutual (or the old notifier). js a tcp knock, no data
public static class Presence
{
    public static async Task<bool> IsOnlineAsync(string host, int port, int timeoutMs = 2000)
    {
        using var tcp = new TcpClient();
        using var cts = new CancellationTokenSource(timeoutMs);
        try { await tcp.ConnectAsync(host, port, cts.Token); return true; }
        catch { return false; }
    }
}
