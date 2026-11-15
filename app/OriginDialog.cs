using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using AssetShelf.Look;

namespace AssetShelf;

/// <summary>Records where a pack came from: source page, licence (with a saved, dated copy of its text), how and when
/// it was acquired, and its receipt. Writes index/&lt;pack&gt;/provenance.json when saved.</summary>
public sealed class OriginDialog : Window
{
    public static Task<bool> Ask(Window owner, Pack pack, string libraryRoot) => new OriginDialog(pack, libraryRoot).ShowDialog<bool>(owner);

    private static readonly string[] Ways = ["unknown", "bought", "claimed free", "free download", "subscription"];

    private OriginDialog(Pack pack, string libraryRoot)
    {
        Title = $"Origin: {pack.Title}";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Look.Theme.SurfacePage;

        Provenance p = Provenance.Load(pack.IndexDir) ?? new Provenance
        {
            Source = { Url = pack.Url, Creator = pack.Seller, Site = pack.Category },
            Licence = { Name = pack.Licence },
            Acquired = { Date = pack.Acquired.Length >= 10 ? pack.Acquired[..10] : pack.Acquired },
        };

        TextBox Box(string text, string hint) => new() { Text = text, PlaceholderText = hint, FontSize = Look.Theme.Body };
        Label Caption(string t) => new() { Content = t, FontSize = Look.Theme.Label, Foreground = Look.Theme.TextSecondary, Padding = new Thickness(0) };
        TextBlock Note(string t) => new() { Text = t, FontSize = Look.Theme.Caption, Foreground = Look.Tokens.Mute, TextWrapping = TextWrapping.Wrap };

        TextBox site = Box(p.Source.Site, "Fab, itch.io, Gumroad, the creator's site…"), url = Box(p.Source.Url, "https://…"), creator = Box(p.Source.Creator, "Creator or seller");
        TextBox licName = Box(p.Licence.Name, "Licence name (e.g. CC0, Fab Standard License)"), licTier = Box(p.Licence.Tier, "Tier (Personal, Professional…)"), licUrl = Box(p.Licence.Url, "https:// address of the terms (optional)");
        var how = new ComboBox { ItemsSource = Ways, SelectedItem = Ways.Contains(p.Acquired.How) ? p.Acquired.How : "unknown", FontSize = Look.Theme.Body, HorizontalAlignment = HorizontalAlignment.Stretch };
        TextBox date = Box(p.Acquired.Date, "YYYY-MM-DD"), account = Box(p.Acquired.Account, "Store account (kept in the library, not shown elsewhere)");
        var pasted = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 90, PlaceholderText = "Paste the licence text here to save a dated copy…", FontSize = Look.Theme.Label };
        var copies = new TextBlock { FontSize = Look.Theme.Caption, Foreground = Look.Tokens.Mute, TextWrapping = TextWrapping.Wrap };
        var receipts = new TextBlock { FontSize = Look.Theme.Caption, Foreground = Look.Tokens.Mute, TextWrapping = TextWrapping.Wrap };
        var error = new TextBlock { FontSize = Look.Theme.Caption, Foreground = Look.Tokens.Danger, TextWrapping = TextWrapping.Wrap, IsVisible = false };
        void Show()
        {
            copies.Text = p.Licence.Copies.Count == 0 ? "No licence text saved yet." : "Saved copies: " + string.Join(", ", p.Licence.Copies.Select(c => c.Saved));
            receipts.Text = p.Receipts.Count == 0 ? "No receipt on file." : "Receipts on file: " + string.Join(", ", p.Receipts.Select(r => r.Date.Length > 0 ? r.Date : "undated"));
        }

        Show();

        Button fromFile = Chrome.TextButton("Save a licence file…", "Pick a licence file (text, at most 256 KB): a dated copy is kept in the library.");
        fromFile.Click += async (_, _) =>
        {
            var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Licence file", AllowMultiple = false });
            if (picked.FirstOrDefault()?.TryGetLocalPath() is { } f)
            {
                if (new FileInfo(f).Length > Provenance.MaxText)
                {
                    Fail("That file is over 256 KB: not a licence text.");
                    return;
                }

                p.SaveLicenceText(pack.IndexDir, await File.ReadAllTextAsync(f), Path.GetFileName(f), licName.Text ?? Path.GetFileNameWithoutExtension(f));
                Persist();
                Show();
            }
        };

        var inbox = Provenance.Inbox(libraryRoot);
        var inboxPick = new ComboBox
        {
            ItemsSource = inbox.Select(r => $"{r.Receipt.Date} · {r.Store} · {r.Items}").ToList(),
            PlaceholderText = inbox.Count == 0 ? "The receipts inbox is empty" : $"From the receipts inbox ({inbox.Count})…",
            IsEnabled = inbox.Count > 0,
            FontSize = Look.Theme.Label,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        Button attach = Chrome.TextButton("Attach", "Copy this receipt into the pack (it stays in the inbox: one order can cover several packs).");
        attach.Click += (_, _) =>
        {
            if (inboxPick.SelectedIndex is int i and >= 0 && i < inbox.Count)
            {
                p.AttachFromInbox(pack.IndexDir, inbox[i].Sidecar, inbox[i].Receipt);
                Persist();
                Show();
            }
        };
        Button receiptFile = Chrome.TextButton("Attach a receipt file…", "Pick a saved receipt (an email, a PDF): it is copied into the pack's provenance folder.");
        receiptFile.Click += async (_, _) =>
        {
            var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Receipt", AllowMultiple = false });
            if (picked.FirstOrDefault()?.TryGetLocalPath() is { } f)
            {
                p.AttachFile(pack.IndexDir, f, new Provenance.Receipt { Date = date.Text ?? "" });
                Persist();
                Show();
            }
        };

        // files already moved or copied into the library are recorded at once, so Cancel never leaves them unlisted
        void Persist()
        {
            p.Save(pack.IndexDir);
            pack.Provenance = p;
        }

        void Fail(string message)
        {
            error.Text = message;
            error.IsVisible = true;
        }

        Button cancel = Chrome.TextButton("Cancel", "Close without saving (files already copied stay in the library).");
        Button ok = Chrome.PrimaryButton("check", "Save", "Save the pack's origin.");
        cancel.Click += (_, _) => Close(false);
        ok.Click += (_, _) =>
        {
            string u = (url.Text ?? "").Trim();
            if (u.Length > 0 && !Provenance.IsWebLink(u))
            {
                Fail("The source page must be an https:// address.");
                return;
            }

            string d = (date.Text ?? "").Trim();
            if (d.Length > 0 && !DateTime.TryParseExact(d, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out _))
            {
                Fail("Write the date as YYYY-MM-DD.");
                return;
            }

            p.Source = new Provenance.SourceInfo { Site = (site.Text ?? "").Trim(), Url = u, Creator = (creator.Text ?? "").Trim() };
            p.Licence.Name = (licName.Text ?? "").Trim();
            p.Licence.Tier = (licTier.Text ?? "").Trim();
            p.Licence.Url = Provenance.IsWebLink((licUrl.Text ?? "").Trim()) ? licUrl.Text!.Trim() : "";
            if ((pasted.Text ?? "").Trim().Length > 0)
            {
                p.SaveLicenceText(pack.IndexDir, pasted.Text!, "pasted", p.Licence.Name.Length > 0 ? p.Licence.Name : "pasted");
            }

            p.Acquired = new Provenance.AcquiredInfo { How = how.SelectedItem as string ?? "unknown", Date = d, Account = (account.Text ?? "").Trim() };
            try
            {
                p.Save(pack.IndexDir);
                pack.Provenance = p;
                Close(true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Fail($"Could not save: {e.Message}");
            }
        };

        var form = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 6,
            Children =
            {
                Note("Where this pack came from and the right to use it. Kept in the library next to its pack.json; receipts and the account are never shown or logged."),
                Caption("Source page"), url, site, creator,
                Caption("Licence"), licName, licTier, licUrl, pasted, fromFile, copies,
                Caption("Acquired"), how, date, account,
                Caption("Receipt"),
                new Grid { ColumnDefinitions = new ColumnDefinitions("*,8,Auto"), Children = { inboxPick, Col(attach, 2) } },
                receiptFile, receipts,
                error,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0), Children = { cancel, ok } },
            },
        };
        Content = new ScrollViewer { Content = form, MaxHeight = 760 };
    }

    private static Control Col(Control c, int col)
    {
        Grid.SetColumn(c, col);
        return c;
    }
}
