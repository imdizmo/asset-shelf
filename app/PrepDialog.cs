using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using AssetShelf.Look;

namespace AssetShelf;

/// <summary>Prepare…: mesh prep for one asset (check, repair, lighten, LODs, collision, a target), into a folder of the
/// person's choosing outside the library, and the report of what was found and done.</summary>
public sealed class PrepDialog : Window
{
    public PrepDialog(Item item, Settings settings, Func<Task<string>> engine)
    {
        Title = $"Prepare {item.Name}";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Look.Theme.SurfacePage;

        string[] targetNames = ["Unreal (FBX, centimetres, Z up)", "Godot (glTF, metres, Y up)", "Groundwork (glTF, metres, Y up, origin at the base)", "Blender (.blend)"];
        var target = new ComboBox { ItemsSource = targetNames, SelectedIndex = 1, HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = Look.Theme.Body };
        var checkOnly = new CheckBox { Content = "Only look it over (write the report, nothing else)" };
        var fix = new CheckBox { Content = "Repair: close small holes, turn faces the right way, drop stray specks (look the report over first)" };
        var maxTris = new TextBox { PlaceholderText = "Lighten to this many triangles (empty: keep all)", FontSize = Look.Theme.Body };
        var lods = new ComboBox { ItemsSource = new[] { "No LODs", "1 lighter version", "2 lighter versions", "3 lighter versions", "4 lighter versions" }, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var collision = new CheckBox { Content = "A simple collision shape (convex hull)" };
        string start = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AssetShelf-prepared", Safe(item.Pack.Id), Safe(item.Name));
        var folder = new TextBox { Text = start, FontSize = Look.Theme.Label };
        Button choose = Chrome.TextButton("Choose…", "Pick the folder the prepared files go into (never inside the library).");
        choose.Click += async (_, _) =>
        {
            var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Folder for the prepared files", AllowMultiple = false });
            if (picked.FirstOrDefault()?.TryGetLocalPath() is { } p)
            {
                folder.Text = Path.Combine(p, Safe(item.Name));
            }
        };
        checkOnly.IsCheckedChanged += (_, _) =>
        {
            bool all = checkOnly.IsChecked != true;
            fix.IsEnabled = maxTris.IsEnabled = lods.IsEnabled = collision.IsEnabled = target.IsEnabled = all;
        };

        var report = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontSize = Look.Theme.Label, IsVisible = false, MaxHeight = 360 };
        var error = new TextBlock { Foreground = Look.Tokens.Danger, TextWrapping = TextWrapping.Wrap, FontSize = Look.Theme.Label, IsVisible = false };
        Button close = Chrome.TextButton("Close", "Close this window.");
        close.Click += (_, _) => Close();
        Button open = Chrome.TextButton("Open folder", "Show the prepared files.");
        open.IsVisible = false;
        open.Click += async (_, _) => await Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(folder.Text ?? start));
        Button run = Chrome.PrimaryButton("check", "Prepare", "Run mesh prep in Blender, headless (a large model takes a few minutes).");
        run.Click += async (_, _) =>
        {
            error.IsVisible = false;
            int? tris = null;
            if ((maxTris.Text ?? "").Trim() is { Length: > 0 } t)
            {
                if (!int.TryParse(t.Replace(",", "").Replace(" ", ""), out int n) || n < 4)
                {
                    (error.Text, error.IsVisible) = ("Write the triangle count as a whole number, at least 4.", true);
                    return;
                }

                tris = n;
            }

            run.IsEnabled = false;
            run.Content = "Working…";
            try
            {
                var o = new MeshPrep.Options(checkOnly.IsChecked == true, fix.IsChecked == true, tris, lods.SelectedIndex, collision.IsChecked == true,
                    MeshPrep.Targets[Math.Max(0, target.SelectedIndex)]);
                var r = await MeshPrep.RunAsync(item, settings, engine, folder.Text ?? start, o);
                report.Text = string.Join("\n", MeshPrep.Lines(r).Select(l => Untrusted.Text(l, 2000)));
                report.IsVisible = open.IsVisible = true;
            }
            catch (Exception e) when (e is InvalidOperationException or IOException)
            {
                (error.Text, error.IsVisible) = (e.Message, true);
            }
            finally
            {
                run.IsEnabled = true;
                run.Content = "Prepare";
            }
        };

        TextBlock Caption(string t) => new() { Text = t, FontSize = Look.Theme.Caption, Foreground = Look.Tokens.Mute, Margin = new Thickness(0, 6, 0, 0) };
        Content = new ScrollViewer
        {
            MaxHeight = 820,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 6,
                Children =
                {
                    new TextBlock { Text = "The asset itself is never changed: everything is written into the folder below, with a report of what was found and done.", TextWrapping = TextWrapping.Wrap, FontSize = Look.Theme.Label, Foreground = Look.Theme.TextSecondary },
                    Caption("For"), target,
                    Caption("What to do"), checkOnly, fix, maxTris, lods, collision,
                    Caption("Into"),
                    new Grid { ColumnDefinitions = new ColumnDefinitions("*,8,Auto"), Children = { folder, Col(choose, 2) } },
                    error, report,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0), Children = { open, close, run } },
                },
            },
        };
    }

    private static string Safe(string s) => new string([.. s.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_')]) is { Length: > 0 } t ? t[..Math.Min(t.Length, 80)] : "asset";

    private static Control Col(Control c, int col)
    {
        Grid.SetColumn(c, col);
        return c;
    }
}
