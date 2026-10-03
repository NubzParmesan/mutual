// mutual rendezvous server. helps two paired pcs find each other over the internet and relays their link
// when theres no direct way. its dumb on purpose and barely learns anything:
//   - a pair id (hash of the two certs, cant be turned back into anything)
//   - the addresses each side reports and the public address it connects from
//   - signed requests it passes along but cant read the meaning of or fake
//   - relayed bytes, which are tls between the two pcs
// nothing gets saved. run it anywhere with a public address:
//   dotnet Mutual.Rendezvous.dll [port]        (default 28810, tcp)
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

int port = args.Length > 0 ? int.Parse(args[0]) : 28810;
var server = new RendezvousServer(port);
Console.CancelKeyPress += (_, e) => { e.Cancel = true; server.Stop(); };
Console.WriteLine($"mutual rendezvous listening on tcp {port}");
await server.Run();

sealed class RendezvousServer
{
    const int MaxPairs = 10_000, MaxLine = 8192;
    readonly TcpListener listener;
    readonly CancellationTokenSource stop = new();
    // pair id -> (side id -> that sides connection)
    readonly ConcurrentDictionary<string, ConcurrentDictionary<string, Side>> pairs = new();
    // pair|channel -> first relay half waiting for the second
    readonly ConcurrentDictionary<string, (string me, TcpClient c, TaskCompletionSource<TcpClient> partner)> relays = new();

    sealed class Side
    {
        public required TcpClient Client; public required StreamWriter Writer; public required string Public;
        public JsonArray Local = new(); public string? Upnp;
        public readonly object WriteLock = new();
        public void Send(JsonNode m) { lock (WriteLock) { try { Writer.WriteLine(m.ToJsonString()); } catch { } } }
    }

    public RendezvousServer(int port) { listener = new TcpListener(IPAddress.Any, port); }
    public void Stop() { stop.Cancel(); listener.Stop(); }

    public async Task Run()
    {
        listener.Start(64);
        while (!stop.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await listener.AcceptTcpClientAsync(stop.Token); } catch { break; }
            _ = Handle(c);
        }
    }

    async Task Handle(TcpClient c)
    {
        var ip = ((IPEndPoint)c.Client.RemoteEndPoint!).Address.MapToIPv4().ToString();
        try
        {
            var s = c.GetStream();
            var first = await ReadLine(s, TimeSpan.FromSeconds(10));
            var m = first == null ? null : JsonNode.Parse(first);
            string? op = (string?)m?["op"], pair = (string?)m?["pair"], me = (string?)m?["me"];
            if (pair is not { Length: 64 } || me is not { Length: 16 }) { c.Dispose(); return; }
            if (op == "hello") await Control(c, s, ip, pair, me, m!);
            else if (op == "relay") await Relay(c, s, pair, me, (int?)m!["chan"] ?? 0);
            else c.Dispose();
        }
        catch { c.Dispose(); }
    }

    // the connection each side keeps open

    async Task Control(TcpClient c, NetworkStream s, string ip, string pair, string me, JsonNode hello)
    {
        if (pairs.Count >= MaxPairs && !pairs.ContainsKey(pair)) { c.Dispose(); return; }
        var sides = pairs.GetOrAdd(pair, _ => new());
        var side = new Side
        {
            Client = c, Public = ip, Writer = new StreamWriter(s, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" },
            Local = hello["local"] as JsonArray ?? new JsonArray(), Upnp = (string?)hello["upnp"],
        };
        side.Local = JsonNode.Parse(side.Local.ToJsonString())!.AsArray();
        if (sides.TryRemove(me, out var old)) old.Client.Dispose();
        // two sides per pair, no more
        if (sides.Count >= 2) { c.Dispose(); return; }
        sides[me] = side;
        Log($"{Short(pair)} {me[..6]} online from {ip} ({sides.Count}/2)");
        Introduce(sides);
        try
        {
            string? line;
            while ((line = await ReadLine(s, TimeSpan.FromMinutes(10))) != null)
            {
                var m = JsonNode.Parse(line);
                if ((string?)m?["op"] == "signal")
                    foreach (var (id, other) in sides) if (id != me) other.Send(new JsonObject { ["op"] = "signal", ["data"] = (string?)m!["data"], ["sig"] = (string?)m["sig"] });
            }
        }
        catch { }
        finally
        {
            sides.TryRemove(new KeyValuePair<string, Side>(me, side));
            if (sides.IsEmpty) pairs.TryRemove(pair, out _);
            foreach (var other in sides.Values) other.Send(new JsonObject { ["op"] = "gone" });
            c.Dispose();
            Log($"{Short(pair)} {me[..6]} gone");
        }
    }

    // once both are here tell each where the other one is
    static void Introduce(ConcurrentDictionary<string, Side> sides)
    {
        if (sides.Count != 2) return;
        var both = sides.Values.ToArray();
        for (int i = 0; i < 2; i++)
        {
            var other = both[1 - i];
            both[i].Send(new JsonObject
            {
                ["op"] = "peer", ["public"] = other.Public, ["yourPublic"] = both[i].Public,
                ["local"] = JsonNode.Parse(other.Local.ToJsonString()), ["upnp"] = other.Upnp,
            });
        }
    }

    // relaying one link

    async Task Relay(TcpClient c, NetworkStream s, string pair, string me, int chan)
    {
        // only pairs that are online can relay so it cant be used as an open proxy
        if (!pairs.TryGetValue(pair, out var sides) || !sides.ContainsKey(me)) { c.Dispose(); return; }
        var key = pair + "|" + chan;
        TcpClient partner;
        if (relays.TryRemove(key, out var waiting) && waiting.me != me)
        {
            waiting.partner.TrySetResult(c);
            partner = waiting.c;
        }
        else
        {
            if (waiting.c != null) waiting.c.Dispose();
            var tcs = new TaskCompletionSource<TcpClient>(TaskCreationOptions.RunContinuationsAsynchronously);
            relays[key] = (me, c, tcs);
            var done = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(60)));
            if (done != tcs.Task) { relays.TryRemove(new KeyValuePair<string, (string, TcpClient, TaskCompletionSource<TcpClient>)>(key, (me, c, tcs))); c.Dispose(); return; }
            return;   // the second half runs the pipe
        }
        Log($"{Short(pair)} relaying channel {chan}");
        var ok = "ok\n"u8.ToArray();
        var ps = partner.GetStream();
        try
        {
            await s.WriteAsync(ok); await ps.WriteAsync(ok);
            await Task.WhenAny(s.CopyToAsync(ps), ps.CopyToAsync(s));
        }
        catch { }
        finally { c.Dispose(); partner.Dispose(); Log($"{Short(pair)} relay {chan} closed"); }
    }

    static async Task<string?> ReadLine(NetworkStream s, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var buf = new List<byte>();
        var one = new byte[1];
        while (buf.Count < MaxLine)
        {
            int n = await s.ReadAsync(one, cts.Token);
            if (n == 0) return null;
            if (one[0] == '\n') return Encoding.UTF8.GetString(buf.ToArray());
            buf.Add(one[0]);
        }
        return null;
    }

    static string Short(string pair) => pair[..8];
    static void Log(string s) => Console.WriteLine($"{DateTime.UtcNow:HH:mm:ss} {s}");
}
