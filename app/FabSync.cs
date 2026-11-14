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
    public static async Task<int> RunAsync(Settings s, string script, IEnumerable<string> args, Action<string> line, string memory = "2G", string? stdin = null)
    {
        ProcessStartInfo psi = Run.Capped(memory, false, Run.Python(s), "-I", "-u", Path.Combine(s.Tools, script));
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }

        return await RunAsync(psi, line, stdin).ConfigureAwait(false);
    }

    /// <summary>True when the sync tools' Python has their packages (legendary-gl, requests). Otherwise asks to set them up
    /// in <see cref="Run.SyncVenv"/> and does it, reporting through <paramref name="line"/>.</summary>
    public static async Task<bool> ReadyAsync(Window owner, Settings s, Action<string> line)
    {
        if (await Quiet(Run.Python(s), "-I", "-c", "import legendary, requests").ConfigureAwait(true) == 0)
        {
            return true;
        }

        if (!await Prompt.Confirm(owner, "Set up sync", $"Fab and itch.io sync need two Python packages, legendary-gl and requests. Download them from PyPI into {Run.SyncVenv}? This is done once. Python 3.11 or later must be installed.", "Set up").ConfigureAwait(true))
        {
            return false;
        }

        string py = Run.VenvPython(Run.SyncVenv);
        line("setting up Python packages…");
        if (await Quiet(Run.BasePython, "-m", "venv", Run.SyncVenv).ConfigureAwait(true) != 0
            || await RunAsync(new ProcessStartInfo(py) { ArgumentList = { "-I", "-m", "pip", "install", "-q", "-r", Path.Combine(s.Tools, "requirements.txt") } }, line, null).ConfigureAwait(true) != 0
            || await Quiet(py, "-I", "-c", "import legendary, requests").ConfigureAwait(true) != 0)
        {
            line($"could not set up the Python packages (is Python 3.11+ installed as {Run.BasePython}? see the debug log)");
            return false;
        }

        line("Python packages ready");
        return true;
    }

    private static async Task<int> Quiet(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe);
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }

        return await RunAsync(psi, _ => { }, null).ConfigureAwait(false);
    }

    private static async Task<int> RunAsync(ProcessStartInfo psi, Action<string> line, string? stdin)
    {
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.RedirectStandardInput = stdin is not null;
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
        try
        {
            p.Start();
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            Log.Write("fab", $"{psi.FileName}: {e.Message}");
            return -1;
        }

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
