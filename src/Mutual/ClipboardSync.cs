using System.Runtime.InteropServices;

namespace Mutual;

// copy on one pc paste on the other during a stream. text only
// opt in since it sends whatever you copy, passwords too
sealed class ClipboardSync : NativeWindow, IDisposable
{
    readonly Action<string> send;
    readonly Control ui;
    string? lastApplied;
    const int MaxChars = 200_000;

    public ClipboardSync(Control uiThread, Action<string> send)
    {
        this.send = send; ui = uiThread;
        ui.Invoke(() => { CreateHandle(new CreateParams { Parent = new IntPtr(-3) }); AddClipboardFormatListener(Handle); });   // message only window
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_CLIPBOARDUPDATE = 0x031D;
        if (m.Msg == WM_CLIPBOARDUPDATE)
        {
            try
            {
                if (Clipboard.ContainsText())
                {
                    var t = Clipboard.GetText();
                    // dont send back what they just sent us
                    if (t != lastApplied && t.Length <= MaxChars) send(t);
                }
            }
            catch (ExternalException) { }   // something else has the clipboard open, skip it
        }
        base.WndProc(ref m);
    }

    public void Apply(string text)
    {
        if (text.Length > MaxChars) return;
        try { ui.BeginInvoke(() => { try { lastApplied = text; Clipboard.SetText(text); } catch (ExternalException) { } }); } catch { }
    }

    public void Dispose()
    {
        try { ui.Invoke(() => { RemoveClipboardFormatListener(Handle); DestroyHandle(); }); } catch { }
    }

    [DllImport("user32.dll")] static extern bool AddClipboardFormatListener(nint h);
    [DllImport("user32.dll")] static extern bool RemoveClipboardFormatListener(nint h);
}
