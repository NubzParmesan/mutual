namespace Mutual.Core;

// the old mutual ssh handshake byte for byte. 1 = im here, 2 = my side is on, then 3 every second
// either side stopping or a byte going missing ends it. kept so mutual works with the old scripts
public static class LegacyConsent
{
    public const byte Present = 1, Enabled = 2, Alive = 3;

    // enable only runs once the other side showed up too. returns when either side ends it
    public static async Task RunAsync(Stream link, Func<Task> enable, Action onReady, CancellationToken stop)
    {
        await PeerLink.ExchangeByteAsync(link, Present, stop);
        await enable();
        await PeerLink.ExchangeByteAsync(link, Enabled, stop);
        onReady();
        while (!stop.IsCancellationRequested)
        {
            await PeerLink.ExchangeByteAsync(link, Alive, CancellationToken.None);
            try { await Task.Delay(1000, stop); } catch (OperationCanceledException) { break; }
        }
    }
}
