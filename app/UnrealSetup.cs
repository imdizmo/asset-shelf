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
        bool Ok(string p) => p.Length > 0 && Editor(p) is not null && Minor(p) >= Minor(Version);
        if (new[] { s.UnrealEngine, Environment.GetEnvironmentVariable("UNREAL_ENGINE") ?? "" }.FirstOrDefault(p => p.Length > 0) is { } chosen)
        {
            return Ok(chosen) ? chosen : null;
        }

        string home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "UnrealEngine");
        return Ok(home) ? home : Usable(Launcher());
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

    private static string[] ProgramFiles() =>
        [.. new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 }.Select(Environment.GetFolderPath).Where(p => p.Length > 0)];

    private static bool InProgramFiles(string path)
    {
        string full = Path.GetFullPath(path);
        return ProgramFiles().Any(pf => full.StartsWith(Path.TrimEndingDirectorySeparator(pf) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Engines in Program Files (only administrators can change those): Epic Games/UE_5.x, and the launcher's
    /// installs that lie there. macOS's /Users/Shared is writable by every user, so a Mac sets the engine by hand.</summary>
    private static IEnumerable<string> Launcher() =>
        ProgramFiles().Select(pf => Path.Combine(pf, "Epic Games")).Where(Directory.Exists)
            .SelectMany(epic => Directory.EnumerateDirectories(epic, "UE_5.*"))
            .Concat(Listed().Where(InProgramFiles));

    /// <summary>Engines the launcher installed outside Program Files (another drive, say). Its list
    /// (ProgramData/Epic/UnrealEngineLauncher/LauncherInstalled.dat) can be changed by other accounts on the computer, so
    /// these are only used once the user says so.</summary>
    private static IEnumerable<string> ListedElsewhere() => Listed().Where(p => !InProgramFiles(p));

    private static List<string> Listed()
    {
        string dat = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "UnrealEngineLauncher", "LauncherInstalled.dat");
        try
        {
            using JsonDocument d = JsonDocument.Parse(File.ReadAllBytes(dat));
            return [.. d.RootElement.GetProperty("InstallationList").EnumerateArray()
                .Where(e => e.GetProperty("AppName").GetString()?.StartsWith("UE_", StringComparison.Ordinal) == true)
                .Select(e => e.GetProperty("InstallLocation").GetString() ?? "")
                .Where(p => p.Length > 0 && Path.IsPathFullyQualified(p) && Plain(p))];
        }
        catch (Exception e) when (e is IOException or JsonException or KeyNotFoundException or UnauthorizedAccessException or InvalidOperationException)
        {
            return [];  // no launcher, or nothing installed with it
        }
    }

    /// <summary>No control, format (bidi, zero-width) or separator characters other than a space: the user is asked
    /// about these paths, and must see the one that is used.</summary>
    // ponytail: folder owner/ACL not checked; the user confirms the path, add an owner check if that proves too weak
    private static bool Plain(string path) => path.All(c => c == ' ' || char.GetUnicodeCategory(c) is not (
        System.Globalization.UnicodeCategory.Control or System.Globalization.UnicodeCategory.Format
        or System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator
        or System.Globalization.UnicodeCategory.SpaceSeparator));

    private static string? Usable(IEnumerable<string> engines) => engines.Where(p => Editor(p) is not null && Minor(p) >= Minor(Version)).MaxBy(Minor);

    /// <summary>Uses an engine the user confirmed: kept as the UnrealEngine setting, which Find then takes as it is.</summary>
    private static string Keep(Settings s, string engine)
    {
        s.UnrealEngine = engine;
        s.Save();
        return engine;
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

        if (Usable(ListedElsewhere()) is { } listed
            && await Prompt.Confirm(owner, "Use this Unreal Engine?", $"Epic's launcher lists Unreal Engine 5.{Minor(listed)} at {listed}. Use it to export assets? Say yes only if you installed it there.", "Use it"))
        {
            return Keep(s, listed);
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
            _step.Text = $"Meshes, characters and animations are exported without Unreal, but this kind of asset needs Unreal Engine {Version} on this computer to export it. It is free, and takes about 40 GB.";
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
            // installs listed outside Program Files before this one started are not offered again (the user can have said no to them)
            HashSet<string> before = new(ListedElsewhere(), StringComparer.OrdinalIgnoreCase);
            while (true)
            {
                Say($"Installing Unreal Engine {Version} in Epic Games Launcher… ({sw.Elapsed:h\\:mm} so far)",
                    $"Sign in to Epic in the launcher if it asks, and confirm the install. If it does not start by itself: Unreal Engine > Library > the + next to Engine Versions > {Version} > Install. Keep this window open: it closes when the engine is ready.");
                if (Find(s) is { } engine)
                {
                    return engine;
                }

                // a new install outside Program Files: most likely the folder the user just picked, but the list it comes
                // from can be edited by other accounts, so it is asked about too
                if (Usable(ListedElsewhere().Where(p => !before.Contains(p))) is { } installed)
                {
                    if (await Dispatcher.UIThread.InvokeAsync(() => Prompt.Confirm(this, "Use this Unreal Engine?",
                            $"Unreal Engine 5.{Minor(installed)} is now installed at {installed}. Is that the folder you chose in Epic's launcher? Use it to export assets?", "Use it")))
                    {
                        return Keep(s, installed);
                    }

                    before.Add(installed);
                }

                await Task.Delay(TimeSpan.FromSeconds(10), stop).ConfigureAwait(false);
            }
        }
    }
}
