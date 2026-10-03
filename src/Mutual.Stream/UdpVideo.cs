using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace Mutual.Stream;

// video over udp. each frame gets split into small packets plus reed-solomon parity so a few lost ones
// get rebuilt instead of freezing everything like tcp does. packets are aes-gcm sealed with a key sent
// inside the tls link so nobody else can read or inject them
// the tls server side listens, the other side sends first so its firewall lets the replies back in
public sealed class UdpVideo : IDisposable
{
    public const int MaxShard = 1100;   // keeps packets under ~1200 bytes so they fit thru vpn tunnels
    const int HeadLen = 12, TagLen = 16, NonceLen = 12;
    const byte TypeShard = 1, TypePing = 2, TypePong = 3, TypeAudio = 4;
    // shard body: frame(4) ts(8) flags(1) frameLen(4) block(1) blocks(1) blockLen(4) k(1) m(1) shard(1)
    const int ShardMeta = 26;

    readonly Socket sock;
    readonly AesGcm aes;
    readonly byte myDir;
    EndPoint? remote;
    readonly bool listening;
    ulong sendSeq, highestSeen;
    readonly object sendLock = new();
    readonly Thread recvThread, pingThread;
    volatile bool running = true;
    long lastHeard;   // stopwatch ticks of the last good packet

    // a whole frame made it (rebuilt if it had to be): timestamp, keyframe, h264
    public event Action<long, bool, byte[]>? Frame;
    // frames got lost for good, needs a keyframe to fix the picture
    public event Action? Lost;
    // a sound packet (seq + opus) as the host sent it
    public event Action<byte[]>? Audio;

    // packet loss the viewer reported lately (0..1), parity gets sized off it
    public double ExpectedLoss { get => expectedLoss; set { expectedLoss = Math.Clamp(value, 0, 0.5); parityCache.Clear(); } }
    double expectedLoss = 0.02;
    readonly System.Collections.Concurrent.ConcurrentDictionary<int, int> parityCache = new();
    // testing only, throws away this much of the outgoing video to see recovery work
    public double DropRate { get; set; }
    readonly Random dropDice = new();
    public bool Up => lastHeard != 0 && Stopwatch.GetElapsedTime(lastHeard).TotalSeconds < 3;
    public double RttMs { get; private set; }
    public int Port => ((IPEndPoint)sock.LocalEndPoint!).Port;
    int framesLost, framesRebuilt;

    UdpVideo(Socket s, byte[] key, bool isHost, bool listening, EndPoint? remote)
    {
        sock = s; aes = new AesGcm(key, TagLen); myDir = (byte)(isHost ? 1 : 2);
        this.listening = listening; this.remote = remote;
        sock.ReceiveBufferSize = 8 << 20;
        sock.SendBufferSize = 4 << 20;
        try { sock.IOControl(-1744830452, new byte[] { 0 }, null); } catch { }   // SIO_UDP_CONNRESET off so stray icmp doesnt throw
        recvThread = new Thread(ReceiveLoop) { IsBackground = true, Name = "mutual udp receive" };
        pingThread = new Thread(PingLoop) { IsBackground = true, Name = "mutual udp ping" };
        recvThread.Start(); pingThread.Start();
    }

    // tls server side, waits on the stream port for their packets
    public static UdpVideo Listen(IPAddress bind, int port, byte[] key, bool isHost)
    {
        var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        s.Bind(new IPEndPoint(bind, port));
        return new UdpVideo(s, key, isHost, true, null);
    }

    // tls client side, sends to their stream port
    public static UdpVideo Connect(IPEndPoint peer, byte[] key, bool isHost)
    {
        var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        s.Bind(new IPEndPoint(IPAddress.Any, 0));
        return new UdpVideo(s, key, isHost, false, peer);
    }

    public static byte[] NewKey() => RandomNumberGenerator.GetBytes(32);

    // sending

    int frameId;

    // splits a frame into shards plus parity and sends them all
    public void SendFrame(long ts, bool key, byte[] data)
    {
        if (remote == null) return;
        uint id = (uint)Interlocked.Increment(ref frameId);
        // blocks of at most 200 data shards (k + m has to stay under 256)
        int shardsTotal = Math.Max(1, (data.Length + MaxShard - 1) / MaxShard);
        int blocks = (shardsTotal + 199) / 200;
        int perBlock = (data.Length + blocks - 1) / blocks;
        for (int b = 0; b < blocks; b++)
        {
            int off = b * perBlock, blen = Math.Min(perBlock, data.Length - off);
            int k = Math.Max(1, (blen + MaxShard - 1) / MaxShard);
            int shard = (blen + k - 1) / k;
            int m = Math.Min(255 - k, ParityFor(k, key));
            var dataShards = new byte[k][];
            for (int j = 0; j < k; j++)
            {
                dataShards[j] = new byte[shard];
                int so = off + j * shard, sl = Math.Clamp(blen - j * shard, 0, shard);
                if (sl > 0) Buffer.BlockCopy(data, so, dataShards[j], 0, sl);
            }
            var parity = new byte[m][];
            for (int i = 0; i < m; i++) parity[i] = new byte[shard];
            ReedSolomon.Encode(dataShards, parity, shard);
            for (int s = 0; s < k + m; s++)
            {
                var body = new byte[ShardMeta + shard];
                BinaryPrimitives.WriteUInt32LittleEndian(body, id);
                BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(4), ts);
                body[12] = (byte)(key ? 1 : 0);
                BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(13), data.Length);
                body[17] = (byte)b; body[18] = (byte)blocks;
                BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(19), blen);
                body[23] = (byte)k; body[24] = (byte)m; body[25] = (byte)s;
                Buffer.BlockCopy(s < k ? dataShards[s] : parity[s - k], 0, body, ShardMeta, shard);
                SendSealed(TypeShard, body);
            }
        }
    }

    // the least parity that makes losing a block rarer than 1 in 1000 at the loss the viewer reported
    // (assumes at least 2%). keyframes aim for 1 in 100k since everything after depends on them
    int ParityFor(int k, bool key)
    {
        int cacheKey = key ? -k : k;
        if (parityCache.TryGetValue(cacheKey, out int cached)) return cached;
        double p = Math.Max(expectedLoss * 1.5, 0.02), target = key ? 1e-5 : 1e-3;
        int m = 1;
        for (; m < 255 - k; m++)
        {
            int n = k + m;
            // p(more than m lost) = 1 - sum_{i<=m} C(n,i) p^i (1-p)^(n-i)
            double term = Math.Pow(1 - p, n), sum = term;
            for (int i = 1; i <= m; i++) { term *= (double)(n - i + 1) / i * p / (1 - p); sum += term; }
            if (1 - sum < target) break;
        }
        parityCache[cacheKey] = m;
        return m;
    }

    public void SendAudio(byte[] packed) => SendSealed(TypeAudio, packed);

    void SendSealed(byte type, ReadOnlySpan<byte> body)
    {
        var to = remote;
        if (to == null) return;
        var pkt = new byte[HeadLen + body.Length + TagLen];
        lock (sendLock)
        {
            ulong seq = ++sendSeq;
            pkt[0] = (byte)'M'; pkt[1] = 1; pkt[2] = type; pkt[3] = myDir;
            BinaryPrimitives.WriteUInt64LittleEndian(pkt.AsSpan(4), seq);
            Span<byte> nonce = stackalloc byte[NonceLen];
            nonce[0] = myDir;
            BinaryPrimitives.WriteUInt64LittleEndian(nonce[4..], seq);
            aes.Encrypt(nonce, body, pkt.AsSpan(HeadLen, body.Length), pkt.AsSpan(HeadLen + body.Length, TagLen), pkt.AsSpan(0, HeadLen));
        }
        if (type == TypeShard && DropRate > 0 && dropDice.NextDouble() < DropRate) return;
        try { sock.SendTo(pkt, to); } catch (SocketException) { } catch (ObjectDisposedException) { }
    }

    void PingLoop()
    {
        var buf = new byte[9];
        while (running)
        {
            if (remote != null)
            {
                buf[0] = 0;
                BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(1), Stopwatch.GetTimestamp());
                SendSealed(TypePing, buf);
            }
            Thread.Sleep(Up ? 1000 : 200);
        }
    }

    // receiving

    sealed class Block { public byte[]?[] Shards = Array.Empty<byte[]?>(); public int K, M, Len, Have; public bool Done; }
    sealed class Asm { public long Ts; public bool Key; public int Len; public Block?[] Blocks = Array.Empty<Block?>(); public int BlocksDone; public bool Rebuilt; }
    readonly Dictionary<uint, Asm> pending = new();
    readonly Dictionary<uint, (long ts, bool key, byte[] data)> complete = new();
    uint nextFrame = 1;   // frame ids start at 1
    bool waitingForKey = true;
    long gapSince, lastLostAt;
    // loss = gaps in the senders packet numbers
    ulong windowStart; long received;

    void ReceiveLoop()
    {
        var buf = new byte[65536];
        EndPoint from = new IPEndPoint(IPAddress.Any, 0);
        Span<byte> nonce = stackalloc byte[NonceLen];
        while (running)
        {
            int n;
            try { n = sock.ReceiveFrom(buf, ref from); }
            catch (SocketException) { continue; }
            catch (ObjectDisposedException) { return; }
            if (n < HeadLen + TagLen || buf[0] != 'M' || buf[1] != 1) continue;
            byte dir = buf[3];
            if (dir == myDir) continue;
            ulong seq = BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(4));
            var body = new byte[n - HeadLen - TagLen];
            nonce.Clear();
            nonce[0] = dir;
            BinaryPrimitives.WriteUInt64LittleEndian(nonce[4..], seq);
            try { aes.Decrypt(nonce, buf.AsSpan(HeadLen, body.Length), buf.AsSpan(HeadLen + body.Length, TagLen), body, buf.AsSpan(0, HeadLen)); }
            catch (CryptographicException) { continue; }   // not from them, or broken
            lastHeard = Stopwatch.GetTimestamp();
            Interlocked.Increment(ref received);
            if (windowStart == 0) windowStart = seq - 1;
            // the listener learns where to send from the newest real packet (an old replayed one cant move it)
            if (seq > highestSeen) { highestSeen = seq; if (listening) remote = new IPEndPoint(((IPEndPoint)from).Address, ((IPEndPoint)from).Port); }
            switch (buf[2])
            {
                case TypePing:
                    body[0] = 1;
                    SendSealed(TypePong, body);
                    break;
                case TypePong:
                    RttMs = (Stopwatch.GetTimestamp() - BinaryPrimitives.ReadInt64LittleEndian(body.AsSpan(1))) * 1000.0 / Stopwatch.Frequency;
                    break;
                case TypeShard:
                    lock (pending) { OnShard(body); Deliver(); }
                    break;
                case TypeAudio:
                    Audio?.Invoke(body);
                    break;
            }
        }
    }

    void OnShard(byte[] body)
    {
        if (body.Length < ShardMeta + 1) return;
        uint id = BinaryPrimitives.ReadUInt32LittleEndian(body);
        if (id < nextFrame || complete.ContainsKey(id)) return;   // already delivered or given up on
        if (!pending.TryGetValue(id, out var f))
        {
            f = new Asm
            {
                Ts = BinaryPrimitives.ReadInt64LittleEndian(body.AsSpan(4)), Key = body[12] != 0,
                Len = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(13)), Blocks = new Block?[body[18]],
            };
            if (f.Blocks.Length == 0 || f.Len <= 0 || f.Len > 32 << 20) return;
            pending[id] = f;
        }
        int b = body[17];
        if (b >= f.Blocks.Length) return;
        int k = body[23], m = body[24], s = body[25];
        if (k == 0) return;
        var blk = f.Blocks[b] ??= new Block { K = k, M = m, Len = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(19)), Shards = new byte[]?[k + m] };
        if (blk.Done || s >= blk.Shards.Length || blk.Shards[s] != null) return;
        blk.Shards[s] = body.AsSpan(ShardMeta).ToArray();
        if (++blk.Have < blk.K) return;

        int shardLen = blk.Shards.First(x => x != null)!.Length;
        bool hadAllData = true;
        for (int j = 0; j < blk.K; j++) if (blk.Shards[j] == null) hadAllData = false;
        if (!ReedSolomon.Reconstruct(blk.Shards, blk.K, shardLen)) return;
        if (!hadAllData) f.Rebuilt = true;
        blk.Done = true;
        if (++f.BlocksDone < f.Blocks.Length) return;

        pending.Remove(id);
        var frame = new byte[f.Len];
        int off = 0;
        foreach (var bk in f.Blocks)
        {
            int len = bk!.Shards[0]!.Length;
            for (int j = 0; j < bk.K && off < f.Len; j++)
            {
                int take = Math.Min(Math.Min(len, bk.Len - j * len), f.Len - off);
                if (take <= 0) break;
                Buffer.BlockCopy(bk.Shards[j]!, 0, frame, off, take);
                off += take;
            }
        }
        if (f.Rebuilt) Interlocked.Increment(ref framesRebuilt);
        complete[id] = (f.Ts, f.Key, frame);
    }

    // hands frames to the decoder in order. if the next one is still missing ~60 ms after a newer one showed up
    // it gets skipped, and after that only a keyframe fixes the picture so skip until one shows up and ask for it
    void Deliver()
    {
        long now = Stopwatch.GetTimestamp();
        while (true)
        {
            if (complete.Remove(nextFrame, out var f))
            {
                if (f.key) waitingForKey = false;
                if (!waitingForKey) Frame?.Invoke(f.ts, f.key, f.data);
                nextFrame++; gapSince = 0;
                continue;
            }
            uint newest = 0;
            foreach (var id in complete.Keys) newest = Math.Max(newest, id);
            foreach (var id in pending.Keys) newest = Math.Max(newest, id);
            if (newest <= nextFrame) break;
            if (waitingForKey)
            {
                // already broken, jump to a finished keyframe if theres one
                uint key = 0;
                foreach (var c in complete) if (c.Value.key && (key == 0 || c.Key < key)) key = c.Key;
                if (key != 0) { Interlocked.Add(ref framesLost, (int)(key - nextFrame)); nextFrame = key; Prune(); continue; }
            }
            if (gapSince == 0) gapSince = now;
            if (Stopwatch.GetElapsedTime(gapSince).TotalMilliseconds < 60) break;
            Interlocked.Increment(ref framesLost);
            nextFrame++;
            Prune();
            if (!waitingForKey) { waitingForKey = true; lastLostAt = now; Lost?.Invoke(); }
        }
        // still broken a second later (keyframe got lost too?), ask again
        if (waitingForKey && (lastLostAt == 0 || Stopwatch.GetElapsedTime(lastLostAt).TotalMilliseconds > 1000)) { lastLostAt = now; Lost?.Invoke(); }
    }

    void Prune()
    {
        foreach (var id in pending.Keys.Where(x => x < nextFrame).ToList()) pending.Remove(id);
        foreach (var id in complete.Keys.Where(x => x < nextFrame).ToList()) complete.Remove(id);
    }

    // how much never showed up since last time (0..1), frames lost, frames rebuilt by parity
    public (double loss, int lost, int rebuilt) TakeStats()
    {
        ulong hi = highestSeen, start = windowStart;
        long got = Interlocked.Exchange(ref received, 0);
        windowStart = hi;
        int lost = Interlocked.Exchange(ref framesLost, 0), rebuilt = Interlocked.Exchange(ref framesRebuilt, 0);
        double expected = hi > start ? hi - start : 0;
        double loss = expected > 0 ? Math.Clamp(1 - got / expected, 0, 1) : 0;
        return (loss, lost, rebuilt);
    }

    public void Dispose()
    {
        running = false;
        sock.Dispose();
        aes.Dispose();
    }
}
