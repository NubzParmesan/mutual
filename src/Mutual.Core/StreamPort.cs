using System.Diagnostics;
using System.Net;

namespace Mutual.Core;

// the stream port and the firewall rules mutual needs. every rule only lets in the friends address,
// only on its port, only for mutual. they all go in under one admin prompt, only the missing ones
public static class StreamPort
{
    public const int Port = 28800;

    public sealed record Rule(string Name, string Protocol, int Port);

    public const string StreamLinkName = "Mutual stream (paired friend only)";
    public const string StreamVideoName = "Mutual stream video (paired friend only)";
    public const string RequestsName = "Mutual requests (paired friend only)";

    // requests come in on both pcs. the stream port only listens on the tls server pc
    // (the other one connects out and windows lets the replies back in)
    public static IEnumerable<Rule> Needed(LinkRole role, Ports ports) =>
        role == LinkRole.Server
            ? new[] { new Rule(RequestsName, "TCP", ports.Ping), new Rule(StreamLinkName, "TCP", ports.Stream), new Rule(StreamVideoName, "UDP", ports.Stream) }
            : new[] { new Rule(RequestsName, "TCP", ports.Ping) };

    // a rule counts only if its there and points at this exe (moving mutual means the old rule lets nothing in)
    public static List<Rule> Missing(Pairing p) =>
        IsLoopback(p.PeerAddress) ? new List<Rule>() : Needed(p.Role, p.MyPorts).Where(r => !Exists(r.Name, Environment.ProcessPath)).ToList();

    static bool IsLoopback(string a) => a.StartsWith("127.") || a == "::1" || a.Equals("localhost", StringComparison.OrdinalIgnoreCase);

    static bool Exists(string name, string? program)
    {
        try
        {
            // netsh works without admin, Get-NetFirewallRule js returns nothing
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "netsh.exe"), $"advfirewall firewall show rule name=\"{name}\" verbose")
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0 || !output.Contains(name)) return false;
            if (program == null) return true;
            var line = output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("Program:", StringComparison.OrdinalIgnoreCase));
            return line != null && string.Equals(line["Program:".Length..].Trim(), program, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    // one admin prompt for all of them. false if you said no or it failed
    public static bool AddRules(IEnumerable<Rule> rules, string program, string localAddress, string peerAddress)
    {
        var list = rules.ToList();
        if (list.Count == 0) return true;
        // this ends up in an admin command line so only real ips and a plain path, nothing that could sneak a command in
        if (!IPAddress.TryParse(peerAddress, out var peer) || peer.ToString() != peerAddress) return false;
        if (!IPAddress.TryParse(localAddress, out var local) || local.ToString() != localAddress) return false;
        if (program.IndexOfAny(new[] { '"', '&', '|', '<', '>', '^', '%' }) >= 0 || !File.Exists(program)) return false;
        try
        {
            string One(Rule r) =>
                $"netsh advfirewall firewall delete rule name=\"{r.Name}\" >nul & netsh advfirewall firewall add rule name=\"{r.Name}\" dir=in action=allow " +
                $"protocol={r.Protocol} localport={r.Port} localip={(localAddress == "0.0.0.0" ? "any" : localAddress)} remoteip={peerAddress} program=\"{program}\" profile=any";
            var args = "/c " + string.Join(" && ", list.Select(One));
            using var p = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), args) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden })!;
            p.WaitForExit();
            return p.ExitCode == 0;
        }
        catch { return false; }   // said no at the admin prompt
    }
}
