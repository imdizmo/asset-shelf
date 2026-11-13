using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AssetShelf.Look;

namespace AssetShelf;

/// <summary>Runs tools/fab_sync.py (Epic sign-in, library comparison, downloads) and tools/index_library.py, each line
/// going to the debug log and to <paramref name="line"/>.</summary>
public static class FabSync
{
    public static string Python(Settings s) =>
        File.Exists(Path.Combine(s.Tools, ".venv", "bin", "python")) ? Path.Combine(s.Tools, ".venv", "bin", "python") : "python3";

    public static async Task<int> RunAsync(Settings s, string script, IEnumerable<string> args, Action<string> line, string memory = "2G", string? stdin = null)
    {
        var psi = new ProcessStartInfo("systemd-run")
        {
            ArgumentList = { "--user", "--scope", "-q", "-p", $"MemoryMax={memory}", "-p", "MemorySwapMax=0", Python(s), "-I", "-u", Path.Combine(s.Tools, script) },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
        };
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using Process p = new() { StartInfo = psi };
        void Got(object? _, DataReceivedEventArgs e)
        {
            if (e.Data is { Length: > 0 } l)
            {
                Log.Write("fab", l);
                line(l);
            }
        }

        p.OutputDataReceived += Got;
        p.ErrorDataReceived += Got;
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (stdin is not null)
        {
            await p.StandardInput.WriteAsync(stdin).ConfigureAwait(false);
            p.StandardInput.Close();
        }

        await p.WaitForExitAsync().ConfigureAwait(false);
        return p.ExitCode;
    }
}

/// <summary>What a sync would download (new and updated packs), and the question whether to.</summary>
public sealed class SyncPreview : Window
{
    public static Task<bool> Ask(Window owner, string planFile, string source = "Fab") => new SyncPreview(planFile, source).ShowDialog<bool>(owner);

    private SyncPreview(string planFile, string source)
    {
        Title = $"Sync the {source} library";
        Width = 620;
        Height = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Look.Theme.SurfacePage;
        using JsonDocument d = JsonDocument.Parse(File.ReadAllBytes(planFile));
        List<(string Title, string Note)> Rows(string k) => d.RootElement.TryGetProperty(k, out JsonElement a)
            ? [.. a.EnumerateArray().Select(r => (r.GetProperty("title").GetString() ?? "", k == "updated" ? $"{r.GetProperty("had").GetString()} → {r.GetProperty("build").GetString()}" : ""))] : [];
        var fresh = Rows("new");
        var updated = Rows("updated");
        int same = d.RootElement.GetProperty("same").GetArrayLength();
        int newer = d.RootElement.GetProperty("newer_engine").GetArrayLength();
        var body = new StackPanel { Spacing = 6, Margin = new Thickness(16) };
        body.Children.Add(new TextBlock { Text = $"{fresh.Count} new, {updated.Count} updated, {same} up to date", FontSize = 16, FontWeight = FontWeight.SemiBold, Foreground = Look.Theme.TextPrimary });
        if (newer > 0)
        {
            body.Children.Add(new TextBlock { Text = $"{newer} packs also have a build for a newer Unreal; they are not downloaded by a sync.", FontSize = Look.Theme.Label, Foreground = Look.Theme.TextTertiary, TextWrapping = TextWrapping.Wrap });
        }

        var list = new StackPanel { Spacing = 2 };
        foreach (var (t, _) in fresh)
        {
            list.Children.Add(new TextBlock { Text = "new  " + t, FontSize = Look.Theme.Label, Foreground = Look.Theme.TextPrimary });
        }

        foreach (var (t, n) in updated)
        {
            list.Children.Add(new TextBlock { Text = $"updated  {t}   {n}", FontSize = Look.Theme.Label, Foreground = Look.Theme.TextSecondary, TextWrapping = TextWrapping.Wrap });
        }

        int count = fresh.Count + updated.Count;
        Button cancel = Chrome.TextButton(count == 0 ? "Close" : "Not now", "Download nothing.");
        cancel.Click += (_, _) => Close(false);
        Button go = Chrome.PrimaryButton("download", $"Download {count}", "Download these packs, check every file, then index them.");
        go.IsVisible = count > 0;
        go.Click += (_, _) => Close(true);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(16, 8, 16, 16), Children = { cancel, go } };
        var dock = new DockPanel();
        DockPanel.SetDock(body, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        dock.Children.Add(body);
        dock.Children.Add(buttons);
        dock.Children.Add(new Border { Margin = new Thickness(16, 0), BorderBrush = Look.Theme.BorderSubtle, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(Look.Theme.Radius2), Child = new ScrollViewer { Content = list, Padding = new Thickness(8) } });
        Content = dock;
    }
}
