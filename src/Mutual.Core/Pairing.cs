using System.IO.Compression;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace Mutual.Core;

// ports one side listens on, defaults match the old mutual ssh scripts
public sealed record Ports(int Ping = 28792, int Stream = 28800, int Consent = 28790)
{
    public static readonly Ports Default = new();
}

// who youre paired with and how to reach them. either picked up from the old mutual ssh folder
// or made by swapping pairing codes (saved as pairing.json)
public sealed class Pairing
{
    public required string PeerName { get; init; }
    public required string PeerAddress { get; set; }
    public required IPAddress BindAddress { get; init; }
    public required LinkRole Role { get; init; }
    public required X509Certificate2 Own { get; init; }
    public required byte[] PeerCertRaw { get; init; }
    public Ports MyPorts { get; init; } = Ports.Default;
    public Ports PeerPorts { get; init; } = Ports.Default;
    // where the activity log and per pairing stuff lives
    public required string Folder { get; init; }
    // came from the old mutual ssh folder so its scripts and notifier are around
    public bool IsLegacy { get; init; }

    // an id both sides get the same, its all the rendezvous server ever learns
    public string PairId => Convert.ToHexString(SHA256.HashData(Sorted(Own.RawData, PeerCertRaw))).ToLowerInvariant();

    // short code you read to each other to make sure nobody got in the middle
    public string SafetyCode => SafetyCodeFor(Own.RawData, PeerCertRaw);

    public static string SafetyCodeFor(byte[] a, byte[] b)
    {
        var h = Convert.ToHexString(SHA256.HashData(Sorted(a, b)));
        return h[..4] + "-" + h[4..8] + "-" + h[8..12];
    }

    static byte[] Sorted(byte[] a, byte[] b)
    {
        bool aFirst = a.AsSpan().SequenceCompareTo(b) <= 0;
        return (aFirst ? a : b).Concat(aFirst ? b : a).ToArray();
    }

    // old mutual ssh folder

    sealed record LegacyConfig(int Port, string PeerAddress, string PeerAlias, string Role, string Thumbprint, string BindAddress);

    public static bool LegacyFolderExists(string folder) => File.Exists(Path.Combine(folder, "config.json")) && File.Exists(Path.Combine(folder, "peer-public.cer"));

    public static Pairing FromLegacyFolder(string folder)
    {
        var cfg = JsonSerializer.Deserialize<LegacyConfig>(File.ReadAllText(Path.Combine(folder, "config.json")))
                  ?? throw new InvalidDataException("config.json is empty.");
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);
        var own = store.Certificates.Find(X509FindType.FindByThumbprint, cfg.Thumbprint, false).OfType<X509Certificate2>().FirstOrDefault()
                  ?? throw new InvalidOperationException("The pairing certificate from config.json isn't in this Windows account's certificate store.");
        var peer = File.ReadAllBytes(Path.Combine(folder, "peer-public.cer"));
        using (var check = new X509Certificate2(peer)) { }   // fail early if the file is broken
        return new Pairing
        {
            PeerName = cfg.PeerAlias,
            PeerAddress = cfg.PeerAddress,
            BindAddress = IPAddress.Parse(cfg.BindAddress),
            MyPorts = Ports.Default with { Consent = cfg.Port },
            PeerPorts = Ports.Default with { Consent = cfg.Port },
            Role = cfg.Role.Equals("server", StringComparison.OrdinalIgnoreCase) ? LinkRole.Server : LinkRole.Client,
            Own = own,
            PeerCertRaw = peer,
            Folder = folder,
            IsLegacy = true,
        };
    }

    // mutuals own pairing

    sealed record Saved(int Version, string PeerName, string PeerAddress, string PeerCert, string OwnPfx, string BindAddress, Ports MyPorts, Ports PeerPorts);

    public static string SavedPath(string folder) => Path.Combine(folder, "pairing.json");
    public static bool SavedExists(string folder) => File.Exists(SavedPath(folder));

    // the private key is encrypted to this windows account
    public static Pairing Load(string folder)
    {
        var s = JsonSerializer.Deserialize<Saved>(File.ReadAllText(SavedPath(folder))) ?? throw new InvalidDataException("pairing.json is empty.");
        var pfx = Dpapi.Unprotect(Convert.FromBase64String(s.OwnPfx));
        var own = new X509Certificate2(pfx, (string?)null, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
        var peer = Convert.FromBase64String(s.PeerCert);
        return new Pairing
        {
            PeerName = s.PeerName, PeerAddress = s.PeerAddress, BindAddress = IPAddress.Parse(s.BindAddress),
            Role = RoleFor(own.RawData, peer), Own = own, PeerCertRaw = peer, MyPorts = s.MyPorts, PeerPorts = s.PeerPorts, Folder = folder,
        };
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        var pfx = Dpapi.Protect(Own.Export(X509ContentType.Pfx));
        var s = new Saved(1, PeerName, PeerAddress, Convert.ToBase64String(PeerCertRaw), Convert.ToBase64String(pfx), BindAddress.ToString(), MyPorts, PeerPorts);
        File.WriteAllText(SavedPath(Folder), JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
    }

    // nobody picks who listens, whoever's cert hash sorts first does
    // both sides work it out the same
    public static LinkRole RoleFor(byte[] own, byte[] peer) =>
        SHA256.HashData(own).AsSpan().SequenceCompareTo(SHA256.HashData(peer)) < 0 ? LinkRole.Server : LinkRole.Client;

    // pairing codes

    public sealed record Offer(string Name, byte[] Cert, string[] Addresses, Ports Ports);

    const string CodePrefix = "MUTUAL1-";

    // public stuff only, no private key in it
    public static string MakeCode(string myName, X509Certificate2 own, Ports? ports = null, string[]? addresses = null)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new Offer(myName, own.RawData, addresses ?? LocalAddresses(), ports ?? Ports.Default));
        using var ms = new MemoryStream();
        using (var z = new DeflateStream(ms, CompressionLevel.SmallestSize)) z.Write(json);
        return CodePrefix + Convert.ToBase64String(ms.ToArray()).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    public static Offer ReadCode(string code)
    {
        code = new string(code.Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (!code.StartsWith(CodePrefix)) throw new FormatException("That isn't a Mutual pairing code (they start with " + CodePrefix + ").");
        var b64 = code[CodePrefix.Length..].Replace('-', '+').Replace('_', '/');
        b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
        using var z = new DeflateStream(new MemoryStream(Convert.FromBase64String(b64)), CompressionMode.Decompress);
        var offer = JsonSerializer.Deserialize<Offer>(z) ?? throw new FormatException("The pairing code is empty.");
        using (var check = new X509Certificate2(offer.Cert)) { }
        // everything in a code comes from the other person so only plain addresses, a short name and real ports get thru
        var name = new string((offer.Name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (name.Length == 0) name = "friend";
        if (name.Length > 40) name = name[..40];
        var addrs = (offer.Addresses ?? Array.Empty<string>()).Where(IsPlainAddress).Take(16).ToArray();
        var p = offer.Ports ?? Ports.Default;
        bool ok(int x) => x is > 0 and < 65536;
        if (!ok(p.Ping) || !ok(p.Stream) || !ok(p.Consent)) throw new FormatException("The pairing code has bad ports in it.");
        return offer with { Name = name, Addresses = addrs, Ports = p };
    }

    public static Pairing FromOffer(Offer friend, X509Certificate2 own, string folder, Ports? myPorts = null, string? address = null)
    {
        var p = new Pairing
        {
            PeerName = friend.Name, PeerAddress = address ?? PickAddress(friend.Addresses), BindAddress = IPAddress.Any,
            Role = RoleFor(own.RawData, friend.Cert), Own = own, PeerCertRaw = friend.Cert,
            MyPorts = myPorts ?? Ports.Default, PeerPorts = friend.Ports, Folder = folder,
        };
        p.Save();
        return p;
    }

    // an ip or a plain host name, never anything that could pass for a command line option
    public static bool IsPlainAddress(string? a) =>
        !string.IsNullOrEmpty(a) && a.Length <= 253 && !a.StartsWith('-') &&
        (IPAddress.TryParse(a, out _) || System.Text.RegularExpressions.Regex.IsMatch(a, @"^[A-Za-z0-9]([A-Za-z0-9\-\.]*[A-Za-z0-9])?$"));

    // a vpn address (hamachi 25.x or tailscale 100.64/10) if you both have one, otherwise their first address
    public static string PickAddress(string[] addresses)
    {
        bool mine(Func<string, bool> f) => LocalAddresses().Any(f);
        static bool hamachi(string a) => a.StartsWith("25.");
        static bool tailscale(string a) => a.StartsWith("100.") && int.TryParse(a.Split('.')[1], out var b) && b is >= 64 and < 128;
        return addresses.FirstOrDefault(a => hamachi(a) && mine(hamachi))
            ?? addresses.FirstOrDefault(a => tailscale(a) && mine(tailscale))
            ?? addresses.FirstOrDefault() ?? "127.0.0.1";
    }

    // this pcs ipv4 addresses, vpn ones first
    public static string[] LocalAddresses()
    {
        var list = new List<string>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork && !ua.Address.ToString().StartsWith("169.254.")) list.Add(ua.Address.ToString());
        }
        return list.OrderBy(a => a.StartsWith("25.") || a.StartsWith("100.") ? 0 : 1).Distinct().ToArray();
    }

    // ecdsa keeps the cert and the pairing code short
    public static X509Certificate2 CreateIdentity(string name, TimeSpan? lifetime = null, DateTimeOffset? notBefore = null, bool rsa = false)
    {
        CertificateRequest req;
        AsymmetricAlgorithm key = rsa ? RSA.Create(2048) : ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using (key)
        {
            req = key is RSA r
                ? new CertificateRequest("CN=" + name, r, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
                : new CertificateRequest("CN=" + name, (ECDsa)key, HashAlgorithmName.SHA256);
            req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | (rsa ? X509KeyUsageFlags.KeyEncipherment : 0), true));
            req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.1"), new Oid("1.3.6.1.5.5.7.3.2") }, false));   // server + client auth
            var start = notBefore ?? DateTimeOffset.UtcNow.AddMinutes(-5);
            using var cert = req.CreateSelfSigned(start, start + (lifetime ?? TimeSpan.FromDays(3650)));
            // thru pfx so schannel gets a private key it can actually use
            return new X509Certificate2(cert.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.Exportable);
        }
    }
}
