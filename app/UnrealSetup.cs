using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using AssetShelf.Look;

namespace AssetShelf;

/// <summary>The Unreal Engine that exports assets (tools/export_fbx.py runs it headless). Asset Shelf works with
/// <see cref="Version"/>; when no such engine is found, Windows users are taken through installing it with Epic's own
/// launcher (installed first when it is missing), and the app picks the engine up when the launcher is done.</summary>
public static class UnrealSetup
{
    public const string Version = "5.8";
    private const string AppName = "UE_5.8";

    // Epic's official launcher installer, the one epicgames.com/download hands out
    private const string LauncherMsi = "https://launcher-public-service-prod06.ol.epicgames.com/launcher/api/installer/download/EpicGamesLauncherInstaller.msi";

    /// <summary>The engine's root folder: settings UnrealEngine or $UNREAL_ENGINE when set (a choice made by hand is
    /// used as it is, never outranked); else ~/UnrealEngine, then the newest that Epic's launcher installed
    /// (LauncherInstalled.dat, Program Files/Epic Games/UE_5.x), <see cref="Version"/> or later.</summary>
    public static string? Find(Settings s)
    {
        bool Usable(string p) => p.Length > 0 && Editor(p) is not null && Minor(p) >= Minor(Version);
        if (new[] { s.UnrealEngine, Environment.GetEnvironmentVariable("UNREAL_ENGINE") ?? "" }.FirstOrDefault(p => p.Length > 0) is { } chosen)
        {
            return Usable(chosen) ? chosen : null;
        }

        string home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "UnrealEngine");
        return Usable(home) ? home : Launcher().Where(Usable).MaxBy(Minor);
    }

    /// <summary>The headless editor in an engine folder, or null when it is not there (yet).</summary>
    public static string? Editor(string engine)
    {
        string bin = Path.Combine(engine, "Engine", "Binaries");
        string exe = OperatingSystem.IsWindows() ? Path.Combine(bin, "Win64", "UnrealEditor-Cmd.exe")
            : OperatingSystem.IsMacOS() ? Path.Combine(bin, "Mac", "UnrealEditor-Cmd") : Path.Combine(bin, "Linux", "UnrealEditor-Cmd");
        return File.Exists(exe) ? exe : null;
    }

    private static int Minor(string engineOrVersion)
    {
        if (engineOrVersion.StartsWith("5.", StringComparison.Ordinal))
        {
            return int.Parse(engineOrVersion[2..]);
        }

        try
        {
            using JsonDocument d = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(engineOrVersion, "Engine", "Build", "Build.version")));
            return d.RootElement.GetProperty("MajorVersion").GetInt32() == 5 ? d.RootElement.GetProperty("MinorVersion").GetInt32() : -1;
        }
        catch (Exception e) when (e is IOException or JsonException or KeyNotFoundException or UnauthorizedAccessException)
        {
            return -1;
        }
    }

    /// <summary>Engines Epic's launcher installed.</summary>
    private static IEnumerable<string> Launcher()
    {
        string dat = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "UnrealEngineLauncher", "LauncherInstalled.dat");
        var found = new List<string>();
        try
        {
            using JsonDocument d = JsonDocument.Parse(File.ReadAllBytes(dat));
            found.AddRange(d.RootElement.GetProperty("InstallationList").EnumerateArray()
                .Where(e => e.GetProperty("AppName").GetString()?.StartsWith("UE_", StringComparison.Ordinal) == true)
                .Select(e => e.GetProperty("InstallLocation").GetString() ?? ""));
        }
        catch (Exception e) when (e is IOException or JsonException or KeyNotFoundException or UnauthorizedAccessException or InvalidOperationException)
        {
            // no launcher, or nothing installed with it
        }

        // only Program Files (admin-writable); macOS's /Users/Shared is writable by every user, so a Mac sets the engine by hand
        foreach (string pf in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 }.Select(Environment.GetFolderPath))
        {
            string epic = Path.Combine(pf, "Epic Games");
            if (pf.Length > 0 && Directory.Exists(epic))
            {
                found.AddRange(Directory.EnumerateDirectories(epic, "UE_5.*"));
            }
        }

        return found;
    }

    private static string? LauncherExe() =>
        new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles }
            .Select(f => Environment.GetFolderPath(f))
            .Where(f => f.Length > 0)
            .SelectMany(f => new[] { "Win64", "Win32" }.Select(b => Path.Combine(f, "Epic Games", "Launcher", "Portal", "Binaries", b, "EpicGamesLauncher.exe")))
            .FirstOrDefault(File.Exists);

    /// <summary>The engine, installing it first (with the user, through the window) when it is missing. Throws when
    /// there is none and the user did not install it.</summary>
    public static async Task<string> EnsureAsync(Window owner, Settings s)
    {
        if (Find(s) is { } engine)
        {
            return engine;
        }

        if (!OperatingSystem.IsWindows())
        {
            // ponytail: Epic has no launcher for Linux (or a scriptable one on macOS): the engine is a download from
            // unrealengine.com/linux, set by hand; guide it here too if Linux members turn up
            throw new InvalidOperationException($"Sending Unreal assets needs Unreal Engine {Version}: unpack it into ~/UnrealEngine, or set UnrealEngine in settings.json.");
        }

        return await new InstallWindow(s).ShowDialog<string?>(owner)
            ?? throw new InvalidOperationException($"Sending Unreal assets needs Unreal Engine {Version}; it was not installed.");
    }

    /// <summary>Install, step by step: Epic's launcher (when missing), then Unreal Engine in it; closes with the engine
    /// folder once the launcher has finished.</summary>
    private sealed class InstallWindow : Window
    {
        private readonly TextBlock _step = new() { FontSize = Look.Theme.Body, Foreground = Look.Theme.TextPrimary, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        private readonly TextBlock _hint = new() { FontSize = Look.Theme.Label, Foreground = Look.Theme.TextSecondary, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        private readonly CancellationTokenSource _stop = new();

        public InstallWindow(Settings s)
        {
            Title = $"Install Unreal Engine {Version}";
            Width = 480;
            SizeToContent = SizeToContent.Height;
            CanResize = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = Look.Theme.SurfacePage;
            Button cancel = Chrome.TextButton("Cancel", "Stop waiting; the launcher keeps whatever it is doing.");
            Button go = Chrome.PrimaryButton("download", "Install", $"Install Unreal Engine {Version} with Epic's launcher.");
            cancel.Click += (_, _) => Close(null);
            Closed += (_, _) => _stop.Cancel();
            _step.Text = $"Sending Unreal assets to Blender, Unity or Groundwork needs Unreal Engine {Version} on this computer (it exports them). It is free, and takes about 40 GB.";
            _hint.Text = LauncherExe() is null
                ? "Install sets up Epic Games Launcher (Epic's official installer; Windows asks to allow it), then opens Unreal Engine's install in it. You sign in to Epic there and confirm the folder. Asset Shelf waits and carries on when it is done."
                : "Install opens Unreal Engine's install in Epic Games Launcher. You sign in to Epic there if asked and confirm the folder. Asset Shelf waits and carries on when it is done.";
            go.Click += async (_, _) =>
            {
                go.IsEnabled = false;
                try
                {
                    Close(await RunAsync(s, _stop.Token));
                }
                catch (OperationCanceledException)
                {
                    // the window was closed
                }
                catch (Exception e) when (e is InvalidOperationException or IOException or HttpRequestException or System.ComponentModel.Win32Exception)
                {
                    _step.Text = e.Message;
                    go.IsEnabled = true;
                }
            };
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 10,
                Children =
                {
                    _step,
                    _hint,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, go } },
                },
            };
        }

        private void Say(string step, string hint) => Dispatcher.UIThread.Post(() =>
        {
            _step.Text = step;
            _hint.Text = hint;
        });

        private async Task<string> RunAsync(Settings s, CancellationToken stop)
        {
            if (LauncherExe() is null)
            {
                Say("Downloading Epic Games Launcher…", "Epic's official installer, from epicgames.com.");
                string msi = Path.Combine(Path.GetTempPath(), "EpicGamesLauncherInstaller.msi");
                using (var http = new HttpClient())
                {
                    await File.WriteAllBytesAsync(msi, await http.GetByteArrayAsync(LauncherMsi, stop).ConfigureAwait(false), stop).ConfigureAwait(false);
                }

                Say("Installing Epic Games Launcher…", "Windows asks to allow it: say yes.");
                using Process p = Process.Start(new ProcessStartInfo("msiexec", ["/i", msi, "/passive", "/norestart"]) { UseShellExecute = true })!;
                await p.WaitForExitAsync(stop).ConfigureAwait(false);
                File.Delete(msi);
                if (LauncherExe() is null)
                {
                    throw new InvalidOperationException($"Epic Games Launcher did not install (msiexec ended with {p.ExitCode}). Press Install to try again.");
                }
            }

            // ponytail: the launcher's apps/<name>?action=install link starts the install for its own catalogue; if a
            // launcher version ignores it for the engine, the hint says where the button is
            Process.Start(new ProcessStartInfo($"com.epicgames.launcher://apps/{AppName}?action=install") { UseShellExecute = true })?.Dispose();
            var sw = Stopwatch.StartNew();
            while (true)
            {
                Say($"Installing Unreal Engine {Version} in Epic Games Launcher… ({sw.Elapsed:h\\:mm} so far)",
                    $"Sign in to Epic in the launcher if it asks, and confirm the install. If it does not start by itself: Unreal Engine > Library > the + next to Engine Versions > {Version} > Install. Keep this window open: it closes when the engine is ready.");
                if (Find(s) is { } engine)
                {
                    return engine;
                }

                await Task.Delay(TimeSpan.FromSeconds(10), stop).ConfigureAwait(false);
            }
        }
    }
}
