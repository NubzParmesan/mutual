namespace Mutual;

// mutual.log for when something breaks on a pc you cant see
// errors and stream stuff only, never what anyone typed or what was on screen
static class Diag
{
    static readonly object gate = new();
    public static string PathName => Path.Combine(AppSettings.Dir, "mutual.log");

    public static void Write(string what)
    {
        try
        {
            lock (gate)
            {
                Directory.CreateDirectory(AppSettings.Dir);
                var fi = new FileInfo(PathName);
                if (fi.Exists && fi.Length > 2 * 1024 * 1024) File.Move(PathName, PathName + ".old", true);
                File.AppendAllText(PathName, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {what}{Environment.NewLine}");
            }
        }
        catch { }
    }

    public static void HookCrashes()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Crashed(e.Exception, fatal: false);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Crashed(e.ExceptionObject as Exception, fatal: e.IsTerminating);
        TaskScheduler.UnobservedTaskException += (_, e) => { Write("background task failed: " + e.Exception); e.SetObserved(); };
        Write($"start {Application.ProductVersion} on {Environment.OSVersion.VersionString}" + (AppSettings.Profile != null ? $" (profile {AppSettings.Profile})" : ""));
    }

    static void Crashed(Exception? e, bool fatal)
    {
        Write((fatal ? "CRASH " : "error ") + e);
        if (!fatal) return;
        try { MessageBox.Show("Mutual hit a problem and has to close. What happened is in\n" + PathName, "Mutual", MessageBoxButtons.OK, MessageBoxIcon.Error); } catch { }
    }
}
