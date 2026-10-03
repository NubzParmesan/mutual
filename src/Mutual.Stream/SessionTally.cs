using System.Diagnostics;

namespace Mutual.Stream;

// adds up the viewer reports over a whole stream (reconnects included) for the line in the
// activity list when it ends: how long, fps, ping, loss, bitrate
public sealed class SessionTally
{
    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly object gate = new();
    int reports, rttReports, lost, rebuilt;
    double fps, rtt, rttWorst, loss, bitrate;
    readonly HashSet<string> transports = new();

    public int Reports { get { lock (gate) return reports; } }

    public void Add(ViewerStats st, int bitsPerSecond = 0)
    {
        lock (gate)
        {
            reports++;
            fps += st.Fps;
            loss += st.Loss;
            lost += st.FramesLost;
            rebuilt += st.FramesRebuilt;
            bitrate += bitsPerSecond;
            transports.Add(st.Transport);
            if (st.NetworkMs > 0) { rttReports++; rtt += st.NetworkMs; rttWorst = Math.Max(rttWorst, st.NetworkMs); }
        }
    }

    // "1h 12m, 58 fps, ping 23 ms (worst 61), 0.4% loss, 2 frames lost, 14 fixed, 9.8 mbit/s, udp"
    public string Describe()
    {
        lock (gate)
        {
            var t = clock.Elapsed;
            var parts = new List<string> { t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" : t.TotalMinutes >= 1 ? $"{t.Minutes}m {t.Seconds}s" : $"{t.Seconds}s" };
            if (reports == 0) return parts[0];
            parts.Add($"{fps / reports:F0} fps");
            static string Ms(double v) => v < 1 ? "<1" : v.ToString("F0");
            if (rttReports > 0) parts.Add($"ping {Ms(rtt / rttReports)} ms (worst {Ms(rttWorst)})");
            parts.Add($"{loss / reports:P1} loss");
            if (lost > 0) parts.Add($"{lost} frames lost");
            if (rebuilt > 0) parts.Add($"{rebuilt} fixed by parity");
            if (bitrate > 0) parts.Add($"{bitrate / reports / 1e6:F1} mbit/s");
            parts.Add(string.Join("+", transports));
            return string.Join(", ", parts);
        }
    }
}
