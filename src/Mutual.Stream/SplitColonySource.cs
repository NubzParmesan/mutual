using System.Diagnostics;
using System.Drawing;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace Mutual.Stream;

// a source that takes the viewers input itself instead of moving this pcs real mouse
public interface IInputSink
{
    // x and y are screen pixels inside whats shared
    void Handle(InputEvent e, int x, int y);
}

// rimworld with the splitcolony mod. shares the right half the mod reports and sends their mouse and keys
// straight to the mod over localhost udp so you keep your own mouse
// same lines duocursor used: M x y / D b / U b / W delta / K vk 1|0 / R
public sealed class SplitColonySource : StreamSource, IInputSink, IDisposable
{
    const int ModPort = 28794, ListenPort = 28795;
    readonly UdpClient udp;
    readonly IPEndPoint mod = new(IPAddress.Loopback, ModPort);
    readonly Thread reader;
    volatile bool running = true;
    Rectangle split;
    long lastSplit;
    readonly HashSet<int> buttons = new();

    // throws if something else (duocursor) already has the port
    public SplitColonySource()
    {
        udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, ListenPort));
        reader = new Thread(Read) { IsBackground = true, Name = "mutual splitcolony" };
        reader.Start();
    }

    // the mod says SPLIT x y w h every quarter second while the split is up, SPLIT 0 when its not
    void Read()
    {
        var from = new IPEndPoint(IPAddress.Any, 0);
        while (running)
        {
            try
            {
                var p = Encoding.ASCII.GetString(udp.Receive(ref from)).Trim().Split(' ');
                if (p[0] != "SPLIT") continue;
                if (p.Length >= 5) { split = new Rectangle(int.Parse(p[1]), int.Parse(p[2]), int.Parse(p[3]), int.Parse(p[4])); lastSplit = Stopwatch.GetTimestamp(); }
                else lastSplit = 0;
            }
            catch (SocketException) { }
            catch (ObjectDisposedException) { return; }
            catch (FormatException) { }
        }
    }

    public bool Active => lastSplit != 0 && Stopwatch.GetElapsedTime(lastSplit).TotalSeconds < 1.5;

    public override string Describe() => Active ? "rimworld split (right half)" : "rimworld split (waiting for the split, press pause/break in game)";

    // right half while the split is up, otherwise the right half of the game window so theres still a picture
    public override Rectangle Current()
    {
        if (Active) return split;
        var w = GameWindow();
        if (w.Width > 0) return new Rectangle(w.X + w.Width / 2, w.Y, w.Width - w.Width / 2, w.Height);
        var s = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
        return new Rectangle(s.X + s.Width / 2, s.Y, s.Width / 2, s.Height);
    }

    // same as hitting the split key in game
    public void Toggle() => Send("T");

    public static bool GameRunning() => Process.GetProcessesByName("RimWorldWin64").Length > 0;

    static Rectangle GameWindow()
    {
        var p = Process.GetProcessesByName("RimWorldWin64").FirstOrDefault();
        if (p == null || p.MainWindowHandle == 0) return Rectangle.Empty;
        if (!GetClientRect(p.MainWindowHandle, out var rc)) return Rectangle.Empty;
        var o = new POINT();
        ClientToScreen(p.MainWindowHandle, ref o);
        return new Rectangle(o.X, o.Y, rc.R - rc.L, rc.B - rc.T);
    }

    public void Handle(InputEvent e, int x, int y)
    {
        switch (e.Kind)
        {
            case InputKind.Move: Send("M " + x + " " + y); break;
            case InputKind.Down: buttons.Add(e.Value); Send("M " + x + " " + y); Send("D " + e.Value); break;
            case InputKind.Up: buttons.Remove(e.Value); Send("U " + e.Value); break;
            case InputKind.Wheel: Send("W " + e.Value); break;
            case InputKind.KeyDown: Send("K " + (e.Value & 0xFFFF) + " 1"); break;
            case InputKind.KeyUp: Send("K " + (e.Value & 0xFFFF) + " 0"); break;
        }
    }

    void Send(string line)
    {
        var b = Encoding.ASCII.GetBytes(line);
        try { udp.Send(b, b.Length, mod); } catch { }
    }

    public void Dispose()
    {
        // tell the mod theyre gone so nothing stays held down
        foreach (var b in buttons) Send("U " + b);
        Send("R");
        running = false;
        udp.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [DllImport("user32.dll")] static extern bool GetClientRect(nint h, out RECT r);
    [DllImport("user32.dll")] static extern bool ClientToScreen(nint h, ref POINT p);
}
