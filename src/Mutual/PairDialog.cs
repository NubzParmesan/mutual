using System.Security.Cryptography.X509Certificates;
using Mutual.Core;

namespace Mutual;

// pairing. you each send your code (discord, text, whatever) and paste theirs
// codes only have public keys and addresses in them
// then you read the safety code to each other so yk nobody swapped one
sealed class PairDialog : Form
{
    readonly TextBox mine = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Height = 70, Dock = DockStyle.Top };
    readonly TextBox theirs = new() { Multiline = true, ScrollBars = ScrollBars.Vertical, Height = 70, Dock = DockStyle.Top };
    readonly TextBox name = new() { Dock = DockStyle.Top };
    readonly ComboBox address = new() { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDown };
    readonly Label info = new() { Dock = DockStyle.Top, Height = 44, ForeColor = Theme.Dim, Font = Theme.Small };
    readonly Button save;
    readonly CheckBox compared = new() { Text = "we read the safety code to each other out loud and it matches", Dock = DockStyle.Top, Height = 30, Enabled = false };
    readonly X509Certificate2 identity;
    readonly string folder;
    Pairing.Offer? offer;
    public Pairing? Result { get; private set; }

    public PairDialog(AppSettings settings, string folder)
    {
        this.folder = folder;
        identity = Identity.LoadOrCreate(folder, settings.MyName);
        Text = "Pair with a friend";
        Icon = Program.AppIcon;
        BackColor = Theme.Back; ForeColor = Theme.Text; Font = Theme.Body;
        ClientSize = new Size(500, 500);
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        foreach (var t in new TextBoxBase[] { mine, theirs, name }) { t.BackColor = Theme.Panel; t.ForeColor = Theme.Text; t.BorderStyle = BorderStyle.FixedSingle; }
        address.BackColor = Theme.Panel; address.ForeColor = Theme.Text;
        mine.Text = Pairing.MakeCode(settings.MyName, identity);
        mine.Font = new Font("Consolas", 8f); theirs.Font = mine.Font;

        var copy = Theme.Button("Copy my code", (_, _) => { Clipboard.SetText(mine.Text); info.Text = "Copied. Send it to your friend, then paste theirs below."; });
        copy.Dock = DockStyle.Top; copy.Height = 30;
        theirs.TextChanged += (_, _) => ReadTheirs();
        // a code pasted from a fake account looks the same as a real one, only comparing the safety code catches it
        compared.CheckedChanged += (_, _) => save.Enabled = offer != null && compared.Checked;
        save = Theme.Button("Pair", (_, _) => Save(), primary: true);
        save.Enabled = false;
        var cancel = Theme.Button("Cancel", (_, _) => Close());
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 50, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
        save.Width = 100; cancel.Width = 90;
        bar.Controls.Add(save); bar.Controls.Add(cancel);

        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16, 12, 16, 0) };
        // docked top down so it goes in backwards
        body.Controls.Add(compared);
        body.Controls.Add(info);
        body.Controls.Add(address);
        body.Controls.Add(Caption("Their address (a Hamachi or Tailscale one if you both have it)"));
        body.Controls.Add(name);
        body.Controls.Add(Caption("What to call them"));
        body.Controls.Add(theirs);
        body.Controls.Add(Caption("2. Paste your friend's code"));
        body.Controls.Add(copy);
        body.Controls.Add(mine);
        body.Controls.Add(Caption("1. Your code (public, safe to send)"));
        Controls.Add(body);
        Controls.Add(bar);
        info.Text = "Your code only contains your public key and addresses. Nothing connects until you both pair, and after that, until you both click.";
    }

    static Label Caption(string s) => new() { Text = s, Dock = DockStyle.Top, Height = 26, Padding = new Padding(0, 8, 0, 0), ForeColor = Theme.Dim };

    void ReadTheirs()
    {
        offer = null; save.Enabled = false; compared.Checked = false; compared.Enabled = false;
        if (string.IsNullOrWhiteSpace(theirs.Text)) return;
        try
        {
            offer = Pairing.ReadCode(theirs.Text);
            if (offer.Cert.AsSpan().SequenceEqual(identity.RawData)) { info.Text = "That's your own code. Paste your friend's."; offer = null; return; }
            name.Text = offer.Name;
            address.Items.Clear();
            address.Items.AddRange(offer.Addresses);
            address.Text = Pairing.PickAddress(offer.Addresses);
            info.Text = "Safety code: " + Pairing.SafetyCodeFor(identity.RawData, offer.Cert) + "\r\nYour friend should see the same one. If not, don't pair.";
            info.ForeColor = Theme.Accent;
            compared.Enabled = true;
        }
        catch (Exception e) { info.Text = e.Message; info.ForeColor = Theme.Bad; }
    }

    void Save()
    {
        if (offer == null) return;
        if (!Pairing.IsPlainAddress(address.Text.Trim())) { info.Text = "That address doesn't look right (an IP like 25.1.2.3, or a host name)."; info.ForeColor = Theme.Bad; return; }
        var friend = offer with { Name = string.IsNullOrWhiteSpace(name.Text) ? offer.Name : new string(name.Text.Trim().Where(c => !char.IsControl(c)).Take(40).ToArray()) };
        Result = Pairing.FromOffer(friend, identity, folder, address: address.Text.Trim());
        DialogResult = DialogResult.OK;
        Close();
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
