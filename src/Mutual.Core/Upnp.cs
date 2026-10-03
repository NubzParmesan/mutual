using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Mutual.Core;

// asks the router to forward a port (upnp) so the friend can reach this pc without messing with router
// settings. only when a rendezvous server is set. lasts an hour, renewed while mutual runs, removed on quit
// lots of routers have it off, then this does nothing and the relay handles it
public static class Upnp
{
    public sealed record Mapping(string ControlUrl, string Service, int Port, string Protocol, string ExternalIp);
    public sealed record Gateway(string ControlUrl, string Service, IPAddress LocalIp);

    // finds the routers port forwarding service, or null if theres no upnp
    public static Gateway? Discover(int timeoutMs = 2500)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Client.ReceiveTimeout = timeoutMs;
        foreach (var st in new[] { "urn:schemas-upnp-org:service:WANIPConnection:1", "urn:schemas-upnp-org:service:WANPPPConnection:1", "urn:schemas-upnp-org:service:WANIPConnection:2" })
        {
            var req = Encoding.ASCII.GetBytes($"M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\nST: {st}\r\n\r\n");
            udp.Send(req, req.Length, new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900));
        }
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < until)
        {
            try
            {
                IPEndPoint? from = null;
                var resp = Encoding.ASCII.GetString(udp.Receive(ref from));
                var loc = Regex.Match(resp, @"(?im)^location:\s*(\S+)").Groups[1].Value;
                if (loc.Length == 0) continue;
                // only your actual router (the default gateway) gets listened to, over plain http, at its own address
                if (!Gateways().Contains(from!.Address)) continue;
                if (!Uri.TryCreate(loc, UriKind.Absolute, out var u) || u.Scheme != "http" || !IPAddress.TryParse(u.Host, out var host) || !host.Equals(from.Address)) continue;
                var g = ReadDescription(loc);
                if (g != null) return g;
            }
            catch (SocketException) { break; }
            catch (Exception) { }
        }
        return null;
    }

    static HashSet<IPAddress> Gateways() =>
        System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().GatewayAddresses.Select(g => g.Address))
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any))
            .ToHashSet();

    static Gateway? ReadDescription(string location)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var xml = XDocument.Parse(http.GetStringAsync(location).GetAwaiter().GetResult());
        XNamespace ns = xml.Root!.GetDefaultNamespace();
        foreach (var svc in xml.Descendants(ns + "service"))
        {
            var type = (string?)svc.Element(ns + "serviceType") ?? "";
            if (!type.Contains("WANIPConnection") && !type.Contains("WANPPPConnection")) continue;
            var ctl = (string?)svc.Element(ns + "controlURL");
            if (ctl == null) continue;
            var baseUri = new Uri(location);
            var control = new Uri(baseUri, ctl).ToString();
            // the address of ours the router sees
            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            probe.Connect(baseUri.Host, baseUri.Port);
            return new Gateway(control, type, ((IPEndPoint)probe.LocalEndPoint!).Address);
        }
        return null;
    }

    static string Soap(Gateway g, string action, string args)
    {
        var body = $"<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body><u:{action} xmlns:u=\"{g.Service}\">{args}</u:{action}></s:Body></s:Envelope>";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
        using var msg = new HttpRequestMessage(HttpMethod.Post, g.ControlUrl) { Content = new StringContent(body, Encoding.UTF8, "text/xml") };
        msg.Headers.Add("SOAPAction", $"\"{g.Service}#{action}\"");
        using var resp = http.Send(msg);
        var text = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException(action + " refused: " + (int)resp.StatusCode);
        return text;
    }

    // the routers public address, only reads
    public static string? ExternalIp(Gateway g)
    {
        try { return Regex.Match(Soap(g, "GetExternalIPAddress", ""), @"<NewExternalIPAddress>([^<]+)<").Groups[1].Value; }
        catch { return null; }
    }

    // opens each port (same number outside and in) for an hour, returns what worked
    public static Mapping[] Map(IEnumerable<(int port, string proto)> ports, string description)
    {
        var g = Discover();
        if (g == null) return Array.Empty<Mapping>();
        var ip = ExternalIp(g);
        if (string.IsNullOrEmpty(ip)) return Array.Empty<Mapping>();
        var done = new List<Mapping>();
        foreach (var (port, proto) in ports)
        {
            try
            {
                Soap(g, "AddPortMapping",
                    $"<NewRemoteHost></NewRemoteHost><NewExternalPort>{port}</NewExternalPort><NewProtocol>{proto}</NewProtocol><NewInternalPort>{port}</NewInternalPort>" +
                    $"<NewInternalClient>{g.LocalIp}</NewInternalClient><NewEnabled>1</NewEnabled><NewPortMappingDescription>{description}</NewPortMappingDescription><NewLeaseDuration>3600</NewLeaseDuration>");
                done.Add(new Mapping(g.ControlUrl, g.Service, port, proto, ip));
            }
            catch { }
        }
        return done.ToArray();
    }

    public static void Unmap(Mapping m)
    {
        try { Soap(new Gateway(m.ControlUrl, m.Service, IPAddress.Any), "DeletePortMapping", $"<NewRemoteHost></NewRemoteHost><NewExternalPort>{m.Port}</NewExternalPort><NewProtocol>{m.Protocol}</NewProtocol>"); }
        catch { }
    }
}
