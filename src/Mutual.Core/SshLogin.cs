namespace Mutual.Core;

// how the ssh button logs in. keys only: a password prompt on the other pc is never the right answer here,
// so ssh is told not to ask and mutual says whats actually wrong instead
public static class SshLogin
{
    // older mutual shows a dead popup for the login note, so it only goes to 1.4.4+
    public static readonly Version Since = new(1, 4, 4);

    public static IReadOnlyList<string> Args(string target, string? login)
    {
        var a = new List<string> { "-o", "PasswordAuthentication=no", "-o", "KbdInteractiveAuthentication=no" };
        if (IsUsableName(login)) { a.Add("-l"); a.Add(login!); }
        // -- so nothing after it can count as an ssh option
        a.Add("--"); a.Add(target);
        return a;
    }

    // their windows name comes over the wire, so only take something that looks like one
    public static bool IsUsableName(string? n) =>
        !string.IsNullOrWhiteSpace(n) && n.Length <= 64 && !n.StartsWith('-') &&
        n.All(c => char.IsLetterOrDigit(c) || c is ' ' or '.' or '_' or '-');

    // a host in ~/.ssh/config already has the right user and key, so use it if its for this friend:
    // either its named after them or its hostname is their address
    public static string? FindAlias(IEnumerable<string> config, string peerName, string peerAddress)
    {
        string? current = null;
        foreach (var raw in config)
        {
            var parts = raw.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            if (parts[0].Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                var names = parts.Skip(1).Where(n => !n.Contains('*') && !n.Contains('?') && !n.StartsWith('!')).ToArray();
                if (names.Contains(peerName)) return peerName;
                current = names.FirstOrDefault();
            }
            else if (parts[0].Equals("HostName", StringComparison.OrdinalIgnoreCase) && current != null && parts[1] == peerAddress)
                return current;
        }
        return null;
    }
}
