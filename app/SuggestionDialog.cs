using System.IO.Compression;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using AssetShelf.Look;

namespace AssetShelf;

/// <summary>The Suggestion button's dialog (team members only): written words plus screenshots (picked files, a pasted
/// picture, or a picture of the Asset Shelf window), sent as one zip to POST /team/suggestions. The dialog stays open
/// with the words kept when sending fails.</summary>
public sealed class SuggestionDialog : Window
{
    private const int MaxShots = 10;
    private const long MaxShot = 10L << 20, MaxAll = 19L << 20;  // the server takes 20 MB per zip
    private readonly List<(string Name, byte[] Data)> _shots = [];
    private readonly StackPanel _list = new() { Spacing = 4 };
    private readonly TextBlock _note = new() { FontSize = Look.Theme.Caption, Foreground = Look.Tokens.Mute, TextWrapping = TextWrapping.Wrap };

    public static Task Ask(Window owner, string address) => new SuggestionDialog(owner, address).ShowDialog(owner);

    private SuggestionDialog(Window owner, string address)
    {
        Title = "Suggestion for Asset Shelf";
        Width = 620;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Look.Theme.SurfacePage;
        byte[]? window = Png(owner);  // taken now, before this dialog covers it

        var words = new TextBox
        {
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxLength = Suggestion.MaxText, FontSize = Look.Theme.Body,
            PlaceholderText = "What would make Asset Shelf better for you? What were you trying to do?",
        };
        Button pick = Chrome.IconTextButton("folder-open", "Add screenshots…", "Pick PNG or JPEG pictures from this computer.");
        Button paste = Chrome.IconTextButton("clipboard-copy", "Paste picture", "Add the picture on the clipboard (Ctrl+V works too).");
        Button shot = Chrome.IconTextButton("camera", "Picture of the window", "Add a picture of the Asset Shelf window as it was when you clicked Suggestion.");
        shot.IsEnabled = window is not null;
        Button cancel = Chrome.TextButton("Cancel", "Close without sending.");
        Button send = Chrome.PrimaryButton("send", "Send", "Send your words and the pictures to your team's server.");
        send.IsEnabled = false;
        words.TextChanged += (_, _) => send.IsEnabled = !string.IsNullOrWhiteSpace(words.Text);

        pick.Click += async (_, _) =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Screenshots", AllowMultiple = true,
                FileTypeFilter = [new FilePickerFileType("Pictures") { Patterns = ["*.png", "*.jpg", "*.jpeg"] }],
            });
            foreach (IStorageFile f in files)
            {
                if (f.TryGetLocalPath() is not { } path)
                {
                    continue;
                }

                long size = new FileInfo(path).Length;
                if (size > MaxShot)
                {
                    _note.Text = $"{Path.GetFileName(path)} is over {MaxShot >> 20} MB: not added.";
                    continue;
                }

                byte[] data = await File.ReadAllBytesAsync(path);
                string? ext = Kind(data);
                if (ext is null)
                {
                    _note.Text = $"{Path.GetFileName(path)} is not a PNG or JPEG picture: not added.";
                    continue;
                }

                Add(ext, data);
            }
        };
        paste.Click += async (_, _) => await PasteAsync();
        KeyDown += async (_, e) =>
        {
            if (e.Key == Key.V && e.KeyModifiers.HasFlag(KeyModifiers.Control) && await PasteAsync(quiet: true))
            {
                e.Handled = true;
            }
        };
        shot.Click += (_, _) =>
        {
            Add("png", window!);
            shot.IsEnabled = false;
        };
        cancel.Click += (_, _) => Close();
        send.Click += async (_, _) =>
        {
            send.IsEnabled = false;
            _note.Text = "Sending…";
            try
            {
                string id = await Team.SendSuggestionAsync(address, "asset-shelf", CrashReport.Version, CrashReport.Os, Zip(words.Text ?? "", _shots));
                Log.Write("team", $"suggestion sent: {id}");
                Close();
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException or IOException)
            {
                _note.Text = $"Not sent: {ex.Message} Your words are kept: try again, or copy them somewhere.";
                send.IsEnabled = true;
            }
        };

        var intro = new TextBlock
        {
            Text = "Tell the team what to improve, fix or add. It goes to your team's server with your name, the version of Asset Shelf and the pictures below: nothing else.",
            TextWrapping = TextWrapping.Wrap, FontSize = Look.Theme.Body, Foreground = Look.Theme.TextPrimary, Margin = new Thickness(0, 0, 0, 8),
        };
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 8, 0, 4), Children = { pick, paste, shot } };
        var bottom = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0), Children = { cancel, send } };
        var under = new StackPanel { Children = { tools, new ScrollViewer { MaxHeight = 130, Content = _list }, _note, bottom } };
        DockPanel.SetDock(intro, Dock.Top);
        DockPanel.SetDock(under, Dock.Bottom);
        Content = new DockPanel { Margin = new Thickness(16), Children = { intro, under, words } };
        Opened += (_, _) => words.Focus();
    }

    private async Task<bool> PasteAsync(bool quiet = false)
    {
        Bitmap? b = Clipboard is { } c ? await c.TryGetBitmapAsync() : null;
        if (b is null)
        {
            if (!quiet)
            {
                _note.Text = "There is no picture on the clipboard.";
            }

            return false;
        }

        using (b)
        {
            using var ms = new MemoryStream();
            b.Save(ms, new PngBitmapEncoderOptions());
            Add("png", ms.ToArray());
        }

        return true;
    }

    private void Add(string ext, byte[] data)
    {
        if (_shots.Count == MaxShots || data.Length > MaxShot || _shots.Sum(s => s.Data.Length) + data.Length > MaxAll)
        {
            _note.Text = $"At most {MaxShots} pictures, {MaxShot >> 20} MB each and {MaxAll >> 20} MB together: not added.";
            return;
        }

        string name = $"screenshot-{_shots.Count + 1}.{ext}";
        var entry = (name, data);
        _shots.Add(entry);
        _note.Text = "";
        Button remove = Chrome.IconButton("x", "Leave this picture out.");
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            Children = { remove, new TextBlock { Text = $"{name} · {Math.Max(1, data.Length >> 10):N0} KB", FontSize = Look.Theme.Label, Foreground = Look.Theme.TextPrimary, VerticalAlignment = VerticalAlignment.Center } },
        };
        remove.Click += (_, _) =>
        {
            _shots.Remove(entry);
            _list.Children.Remove(row);
        };
        _list.Children.Add(row);
    }

    private static string? Kind(byte[] d) => d is [0x89, (byte)'P', (byte)'N', (byte)'G', ..] ? "png" : d is [0xFF, 0xD8, 0xFF, ..] ? "jpg" : null;

    private static byte[]? Png(Window w)
    {
        try
        {
            PixelSize size = PixelSize.FromSize(w.ClientSize, w.RenderScaling);
            using var rtb = new RenderTargetBitmap(size, new Vector(96 * w.RenderScaling, 96 * w.RenderScaling));
            rtb.Render(w);
            using var ms = new MemoryStream();
            rtb.Save(ms, new PngBitmapEncoderOptions());
            return ms.ToArray();
        }
        catch (Exception e)  // ponytail: a window that cannot be drawn just means no window picture
        {
            Log.Write("team", $"no window picture: {e.Message}");
            return null;
        }
    }

    /// <summary>The zip the server reads: suggestion.txt and the pictures, under names it accepts.</summary>
    internal static byte[] Zip(string words, IEnumerable<(string Name, byte[] Data)> shots)
    {
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var w = new StreamWriter(z.CreateEntry("suggestion.txt").Open(), new System.Text.UTF8Encoding(false)))
            {
                w.Write(words.Trim());
            }

            foreach (var (name, data) in shots)
            {
                using Stream s = z.CreateEntry(name, CompressionLevel.NoCompression).Open();  // pictures are packed already
                s.Write(data);
            }
        }

        return ms.ToArray();
    }
}

/// <summary>What the server takes (server/ShelfServer/Inbox.cs Suggestion).</summary>
internal static class Suggestion
{
    public const int MaxText = 20000;
}
