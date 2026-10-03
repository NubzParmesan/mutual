using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Mutual.Core;

namespace Mutual.Core.Tests;

// mutual against the real mutual ssh powershell (Open-PeerStream / Exchange-Byte from Common.ps1)
// so someone on the old scripts can still pair with someone on mutual. skips if the scripts arent here
public class LegacyInteropTests
{
    static readonly string Legacy = Environment.GetEnvironmentVariable("MUTUAL_LEGACY_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "MutualSSH");

    static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    [Fact]
    public async Task CSharpServerConsentsWithPowerShellClient()
    {
        if (!File.Exists(Path.Combine(Legacy, "Common.ps1"))) return;
        using var mine = Pairing.CreateIdentity("mutual-side");
        using var theirs = Pairing.CreateIdentity("legacy-side");
        var dir = Directory.CreateTempSubdirectory("mutual-interop").FullName;
        var pfx = Path.Combine(dir, "legacy.pfx");
        File.WriteAllBytes(pfx, theirs.Export(X509ContentType.Pfx, "t"));
        var myHex = Convert.ToHexString(mine.RawData);
        int port = FreePort();

        // only the two functions being tested, the rest of Common.ps1 stays out
        var script = Path.Combine(dir, "client.ps1");
        File.WriteAllText(script, $$"""
            $ErrorActionPreference = 'Stop'
            $src = Get-Content '{{Path.Combine(Legacy, "Common.ps1")}}' -Raw
            $src = $src -replace '(?m)^\. .*Activity\.ps1.*$', ''
            Invoke-Expression $src
            $own = [Security.Cryptography.X509Certificates.X509Certificate2]::new('{{pfx}}', 't')
            $s = Open-PeerStream 'client' '127.0.0.1' '127.0.0.1' {{port}} $own '{{myHex}}' 20
            Exchange-Byte $s 1
            Exchange-Byte $s 2
            1..3 | ForEach-Object { Exchange-Byte $s 3; Start-Sleep -Milliseconds 300 }
            $s.Dispose()
            'LEGACY-OK'
            """);
        var psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };

        var serverTask = PeerLink.OpenAsync(LinkRole.Server, IPAddress.Loopback, "", port, mine, theirs.RawData, TimeSpan.FromSeconds(30));
        using var ps = Process.Start(psi)!;
        using var link = await serverTask;
        bool enabled = false;
        var run = LegacyConsent.RunAsync(link, () => { enabled = true; return Task.CompletedTask; }, () => { }, CancellationToken.None);
        var outText = await ps.StandardOutput.ReadToEndAsync();
        var errText = await ps.StandardError.ReadToEndAsync();
        await ps.WaitForExitAsync();
        Assert.True(outText.Contains("LEGACY-OK"), "powershell side: " + outText + errText);
        Assert.True(enabled);
        // the old side hung up so it has to end, not spin
        await Assert.ThrowsAnyAsync<Exception>(() => run.WaitAsync(TimeSpan.FromSeconds(15)));
    }
}
