using System.Security.Cryptography.X509Certificates;
using Mutual.Core;

namespace Mutual;

// pairing. you each send your code (discord, text, whatever) and paste theirs
// codes only have public keys and addresses in them
// then you read the safety code to each other so yk nobody swapped one
sealed class PairDialog : DarkForm
{
    readonly Field mine = new(multiline: true, mono: true), theirs = new(multiline: true, mono: true), name = new();
    readonly Field address = new();
    string[] addresses = Array.Empty<string>();
    readonly Label pasteNote = new() { Dock = DockStyle.Top, Height = 22, Font = Theme.Small, ForeColor = Theme.Dim, UseMnemonic = false, AutoEllipsis = true };
    readonly Label safety = new() { Dock = DockStyle.Top, Height = 44, Font = new Font("Consolas", 22f, FontStyle.Bold), ForeColor = Theme.Faint, TextAlign = ContentAlignment.MiddleLeft, Text = "····-····-····" };
    readonly Label error = new() { Dock = DockStyle.Bottom, Height = 22, Font = Theme.Small, ForeColor = Theme.Bad, UseMnemonic = false };
    readonly FlatButton save, copy;
    readonly Switch compared = new() { Text = "We read it out loud and it matches", Enabled = false, Height = 30 };
    readonly StepBadge one = new(1), two = new(2), three = new(3);
    readonly Card step3;
    readonly X509Certificate2 identity;
    readonly string folder;
    Pairing.Offer? offer;
    public Pairing? Result { get; private set; }

    public PairDialog(AppSettings settings, string folder)
    {
        this.folder = folder;
        identity = Identity.LoadOrCreate(folder, settings.MyName);
        Dialog();
        Text = "Pair with a friend";
        ClientSize = new Size(540, 736);
        mine.Box.ReadOnly = true;
        mine.Box.Text = Pairing.MakeCode(settings.MyName, identity);
        mine.Height = theirs.Height = 54;
        theirs.Box.PlaceholderText = "MUTUAL1-…";
        address.AddButton(Theme.Glyph.Down, () =>
        {
            if (addresses.Length == 0) return;
            var m = Theme.Menu();
            foreach (var a in addresses) m.Items.Add(a, null, (_, _) => address.Box.Text = a);
            m.Show(address, new Point(0, address.Height + 2));
        });

        // 1 send yours
        copy = new FlatButton { Text = "Copy my code", Glyph = Theme.Glyph.Copy, Kind = ButtonKind.Primary, Width = 150, Dock = DockStyle.Left };
        var copyNote = new Label { Text = "Safe to send anywhere, it's only your public key and addresses", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(12, 0, 0, 0), Font = Theme.Small, ForeColor = Theme.Dim };
        var copiedReset = new System.Windows.Forms.Timer { Interval = 2500 };
        copiedReset.Tick += (_, _) => { copiedReset.Stop(); copy.Text = "Copy my code"; copy.Glyph = Theme.Glyph.Copy; copy.Invalidate(); };
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(mine.Box.Text); } catch { return; }
            copy.Text = "Copied"; copy.Glyph = Theme.Glyph.Check; copy.Invalidate();
            one.Done = true; one.Invalidate();
            copy.Kind = ButtonKind.Secondary;
            copiedReset.Start();
            theirs.Box.Focus();
        };
        var copyRow = Row(copy);
        copyRow.Controls.Add(copyNote);
        copyNote.BringToFront();
        var step1 = Step(one, "Send them your code", null, mine, Gap(10), copyRow);

        // 2 paste theirs
        var paste = new FlatButton { Text = "Paste", Glyph = Theme.Glyph.Paste, Width = 100, Dock = DockStyle.Left };
        paste.Click += (_, _) => { try { if (Clipboard.ContainsText()) theirs.Box.Text = Clipboard.GetText().Trim(); } catch { } };
        theirs.Box.TextChanged += (_, _) => ReadTheirs();
        pasteNote.Text = "Paste the code your friend sent you.";
        var pasteRow = Row(paste);
        pasteNote.Dock = DockStyle.Fill; pasteNote.TextAlign = ContentAlignment.MiddleLeft; pasteNote.Padding = new Padding(12, 0, 0, 0);
        pasteRow.Controls.Add(pasteNote);
        pasteNote.BringToFront();
        var step2 = Step(two, "Paste their code", null, theirs, Gap(10), pasteRow);

        // 3 check it
        var nameBox = new Panel { Dock = DockStyle.Left, Width = 170 };
        name.Dock = DockStyle.Top;
        nameBox.Controls.Add(name);
        nameBox.Controls.Add(Theme.Caption("Call them"));
        address.Dock = DockStyle.Top;
        var addrBox = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 0, 0, 0) };
        addrBox.Controls.Add(address);
        addrBox.Controls.Add(Theme.Caption("Their address (pick a Hamachi or Tailscale one)"));
        var who = new Panel { Dock = DockStyle.Top, Height = 58 };
        who.Controls.Add(addrBox);
        who.Controls.Add(nameBox);
        step3 = Step(three, "Compare the safety code", "Read it to each other on a call. If yours and theirs don't match, someone swapped a code, so don't pair.",
            safety, compared, Gap(8), who);
        three.Live = false;

        // a code pasted from a fake account looks the same as a real one, only comparing the safety code catches it
        compared.CheckedChanged += (_, _) => { save!.Enabled = offer != null && compared.Checked; three.Done = compared.Checked; three.Invalidate(); };
        save = new FlatButton { Text = "Pair", Glyph = Theme.Glyph.People, Kind = ButtonKind.Primary, Width = 110, Enabled = false };
        save.Click += (_, _) => Save();
        var cancel = new FlatButton { Text = "Cancel", Width = 90 };
        cancel.Click += (_, _) => Close();
        CancelButton = cancel;

        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20, 0, 20, 8) };
        body.Controls.Add(error);
        foreach (var c in new Control[] { step3, Gap(10), step2, Gap(10), step1 }) body.Controls.Add(c);
        var head = new Panel { Dock = DockStyle.Top, Height = 68, Padding = new Padding(22, 14, 22, 0) };
        head.Controls.Add(new Label { Text = "You both do this once. Nothing can connect until you've both paired.", Dock = DockStyle.Top, Height = 22, ForeColor = Theme.Dim, Font = Theme.Small });
        head.Controls.Add(new Label { Text = "Pair with a friend", Dock = DockStyle.Top, Height = 32, Font = Theme.Title, ForeColor = Theme.Text });
        Controls.Add(body);
        Controls.Add(head);
        Controls.Add(ButtonBar(save, cancel));
        FormClosed += (_, _) => copiedReset.Dispose();
        ActiveControl = copy;
    }

    static Panel Gap(int h) => new() { Dock = DockStyle.Top, Height = h };

    static Panel Row(Control left)
    {
        var row = new Panel { Dock = DockStyle.Top, Height = 34 };
        row.Controls.Add(left);
        return row;
    }

    // one numbered card, sized to fit whats in it
    static Card Step(StepBadge badge, string title, string? note, params Control[] topDown)
    {
        var card = new Card { Dock = DockStyle.Top, Padding = new Padding(16, 14, 16, 16) };
        foreach (var c in topDown.Reverse()) { c.Dock = c.Dock == DockStyle.None ? DockStyle.Top : c.Dock; card.Controls.Add(c); }
        int noteH = 0;
        if (note != null)
        {
            noteH = TextRenderer.MeasureText(note, Theme.Small, new Size(420, 0), TextFormatFlags.WordBreak).Height + 8;
            card.Controls.Add(new Label { Text = note, Dock = DockStyle.Top, Height = noteH, Font = Theme.Small, ForeColor = Theme.Dim, Padding = new Padding(34, 0, 0, 6) });
        }
        var top = new Panel { Dock = DockStyle.Top, Height = 28 };
        badge.Location = new Point(0, 1);
        top.Controls.Add(badge);
        top.Controls.Add(new Label { Text = title, Font = Theme.Heading, ForeColor = Theme.Text, Location = new Point(34, 3), AutoSize = true });
        card.Controls.Add(top);
        card.Height = card.Padding.Vertical + top.Height + noteH + topDown.Sum(c => c.Height);
        return card;
    }

    void ReadTheirs()
    {
        offer = null; save.Enabled = false; compared.Checked = false; compared.Enabled = false; three.Live = false; three.Invalidate();
        two.Done = false; two.Invalidate();
        safety.Text = "····-····-····"; safety.ForeColor = Theme.Faint;
        error.Text = "";
        pasteNote.ForeColor = Theme.Dim;
        if (string.IsNullOrWhiteSpace(theirs.Box.Text)) { pasteNote.Text = "Paste the code your friend sent you."; return; }
        try
        {
            var o = Pairing.ReadCode(theirs.Box.Text);
            if (o.Cert.AsSpan().SequenceEqual(identity.RawData)) { pasteNote.Text = "That's your own code. Paste your friend's."; pasteNote.ForeColor = Theme.Warn; return; }
            offer = o;
            name.Box.Text = offer.Name;
            addresses = offer.Addresses;
            address.Box.Text = Pairing.PickAddress(offer.Addresses);
            pasteNote.Text = "Got it, that's " + offer.Name + "'s code.";
            pasteNote.ForeColor = Theme.Online;
            two.Done = true; two.Invalidate();
            safety.Text = Pairing.SafetyCodeFor(identity.RawData, offer.Cert);
            safety.ForeColor = Theme.Accent;
            three.Live = true; three.Invalidate();
            compared.Enabled = true;
        }
        catch (Exception e) { pasteNote.Text = e.Message; pasteNote.ForeColor = Theme.Bad; }
    }

    void Save()
    {
        if (offer == null) return;
        if (!Pairing.IsPlainAddress(address.Box.Text.Trim())) { error.Text = "That address doesn't look right. It should be an IP like 25.1.2.3, or a host name."; return; }
        var friend = offer with { Name = string.IsNullOrWhiteSpace(name.Box.Text) ? offer.Name : new string(name.Box.Text.Trim().Where(c => !char.IsControl(c)).Take(40).ToArray()) };
        Result = Pairing.FromOffer(friend, identity, folder, address: address.Box.Text.Trim());
        DialogResult = DialogResult.OK;
        Close();
    }
}

// the number circle on each pairing step, turns into a green tick when thats done
sealed class StepBadge : Control
{
    readonly int n;
    public bool Done { get; set; }
    // dimmed until you can do that step
    public bool Live { get; set; } = true;

    public StepBadge(int n)
    {
        this.n = n;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        Size = new Size(24, 24);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Panel);
        var r = new Rectangle(0, 0, 23, 23);
        bool live = Live;
        Theme.FillRound(g, Done ? Theme.Online : live ? Theme.AccentSoft : Theme.Raised, r, 12);
        if (Done) Theme.DrawGlyph(g, Theme.Glyph.Check, 9, Theme.Back, new Rectangle(0, 0, 24, 24));
        else TextRenderer.DrawText(g, n.ToString(), Theme.SmallBold, new Rectangle(0, 0, 24, 24), live ? Theme.Accent : Theme.Faint, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }
}

// this pcs own key, made once and kept encrypted so your code doesnt change between tries
static class Identity
{
    public static X509Certificate2 LoadOrCreate(string folder, string name)
    {
        var path = Path.Combine(folder, "identity.bin");
        try
        {
            if (File.Exists(path))
                return new X509Certificate2(Dpapi.Unprotect(File.ReadAllBytes(path)), (string?)null, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
        }
        catch { }
        var id = Pairing.CreateIdentity("Mutual " + name);
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(path, Dpapi.Protect(id.Export(X509ContentType.Pfx)));
        return id;
    }
}
