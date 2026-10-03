using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Mutual.Stream;

// the orange frame around the custom box. drag the top strip to move it, any edge to resize, even while
// its streaming. the inside is see thru and click thru and the whole thing is hidden from capture so it never ends up in the stream
public sealed class BoxOverlay : Form
{
    const int Edge = 6, Strip = 24;
    static readonly Color Key = Color.FromArgb(255, 1, 0, 1);
    static readonly Color Frame = Color.FromArgb(255, 255, 140, 26);
    readonly BoxSource box;
    readonly Label caption = new() { ForeColor = Color.Black, BackColor = Frame, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, Font = new Font("Segoe UI Semibold", 9f) };

    public BoxOverlay(BoxSource box)
    {
        this.box = box;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Key;
        TransparencyKey = Key;
        caption.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) { ReleaseCapture(); SendMessage(Handle, 0xA1, 2, 0); } };   // drag by the strip
        Controls.Add(caption);
        SetFromBox();
    }

    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams { get { var p = base.CreateParams; p.ExStyle |= 0x80 | 0x08000000; return p; } }   // tool window, no activate

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        SetWindowDisplayAffinity(Handle, 0x11);   // never in the stream
    }

    // window = the box plus the frame around it
    void SetFromBox()
    {
        var b = box.Box;
        Bounds = new Rectangle(b.X - Edge, b.Y - Strip, b.Width + Edge * 2, b.Height + Strip + Edge);
        LayoutCaption();
    }

    void LayoutCaption()
    {
        caption.SetBounds(0, 0, Width, Strip);
        caption.Text = "  sharing this box  " + (Width - Edge * 2) + " x " + (Height - Strip - Edge) + "   (drag here to move, edges to resize)";
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        using var pen = new Pen(Frame, Edge);
        e.Graphics.FillRectangle(new SolidBrush(Frame), 0, 0, Width, Strip);
        e.Graphics.DrawRectangle(pen, Edge / 2, Edge / 2, Width - Edge, Height - Edge);
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); Invalidate(); if (IsHandleCreated) { LayoutCaption(); Push(); } }
    protected override void OnMove(EventArgs e) { base.OnMove(e); if (IsHandleCreated) Push(); }

    // moving or resizing the frame changes whats shared
    void Push() => box.Box = new Rectangle(Left + Edge, Top + Strip, Math.Max(32, Width - Edge * 2), Math.Max(32, Height - Strip - Edge));

    protected override void WndProc(ref Message m)
    {
        const int WM_NCHITTEST = 0x84;
        if (m.Msg == WM_NCHITTEST)
        {
            var p = PointToClient(new Point((short)(m.LParam.ToInt64() & 0xffff), (short)((m.LParam.ToInt64() >> 16) & 0xffff)));
            bool l = p.X < Edge, r = p.X >= Width - Edge, t = p.Y < Edge, b = p.Y >= Height - Edge;
            int hit = (t && l) ? 13 : (t && r) ? 14 : (b && l) ? 16 : (b && r) ? 17 : l ? 10 : r ? 11 : t ? 12 : b ? 15 : 0;
            if (hit != 0) { m.Result = hit; return; }
        }
        base.WndProc(ref m);
    }

    [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(nint h, uint a);
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern nint SendMessage(nint h, int msg, nint w, nint l);
}
