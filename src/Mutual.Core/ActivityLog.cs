using System.Text;
using System.Text.Json;

namespace Mutual.Core;

public enum ActivityResult { INFO, OK, FAILED }

public sealed record ActivityRow(DateTime Time, string Machine, ActivityResult Result, string Action);

// the activity list. one json object per line, same file, mutex and format as the old Write-FishEvent
// so the old viewer and mutual can read each others rows. short stuff only, never passwords or command output
public sealed class ActivityLog
{
    const string MutexName = @"Global\FishBlackBoxLog";
    public string Path { get; }

    public ActivityLog(string path) { Path = path; }

    // every row also goes to mutuals own text log
    public static Action<string>? Mirror { get; set; }

    public void Write(ActivityResult result, string action)
    {
        Mirror?.Invoke(result + " " + action);
        Mutex? gate = null; bool held = false;
        try
        {
            gate = new Mutex(false, MutexName);
            try { held = gate.WaitOne(1000); } catch (AbandonedMutexException) { held = true; }
            if (!held) return;
            var fi = new FileInfo(Path);
            if (fi.Exists && fi.Length > 1024 * 1024) File.Move(Path, Path + ".previous", true);
            var line = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["time"] = DateTime.UtcNow.ToString("o"),
                ["machine"] = Environment.MachineName,
                ["result"] = result.ToString(),
                ["action"] = action.Length > 240 ? action[..240] : action,
            });
            File.AppendAllText(Path, line + Environment.NewLine, new UTF8Encoding(false));
        }
        catch { /* logging never blocks the action it describes */ }
        finally { if (held) gate!.ReleaseMutex(); gate?.Dispose(); }
    }

    public List<ActivityRow> ReadRecent(int max = 200)
    {
        var rows = new List<ActivityRow>();
        if (!File.Exists(Path)) return rows;
        string[] lines;
        try
        {
            using var fs = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            lines = sr.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        }
        catch { return rows; }
        foreach (var raw in lines.Skip(Math.Max(0, lines.Length - max)))
        {
            try
            {
                using var doc = JsonDocument.Parse(raw);
                var r = doc.RootElement;
                if (!Enum.TryParse<ActivityResult>(r.GetProperty("result").GetString(), out var res)) continue;
                rows.Add(new ActivityRow(DateTime.Parse(r.GetProperty("time").GetString()!).ToUniversalTime(),
                    r.GetProperty("machine").GetString() ?? "", res, r.GetProperty("action").GetString() ?? ""));
            }
            catch { }
        }
        return rows;
    }
}
