using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using AssetShelf.Look;

namespace AssetShelf;

/// <summary>Each run writes its log to ~/.cache/asset-shelf/runs/&lt;time&gt;-&lt;pid&gt;.log beside a ".running" marker
/// that a clean exit removes. A marker left by a run that is gone means that run crashed: at the next start, with a team
/// server set, the person is asked whether to send that run's log to the team (POST /team/crashes). Nothing is sent
/// without their click.</summary>
public static class CrashReport
{
    private const long MaxLog = 1 << 20;  // per run; the dialog shows all of it
    private const int KeepRuns = 10;
    private static StreamWriter? _log;
    private static long _written;
    private static string? _marker;
    private static Timer? _memory;
    private static bool _failed;
    private static IDisposable? _term;  // kept alive: the registration ends when it is collected

    private static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "asset-shelf", "runs");

    public static string Version => typeof(CrashReport).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    /// <summary>The log of an earlier run that did not end cleanly, found by <see cref="Start"/>; null when none.</summary>
    public static string? Crashed { get; private set; }

    /// <summary>At start, before anything else: finds a crashed run, opens this run's log, catches unhandled errors.</summary>
    public static void Start()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            foreach (string m in Directory.GetFiles(Dir, "*.running").Order(StringComparer.Ordinal))
            {
                if (!Alive(m))
                {
                    Crashed = Path.ChangeExtension(m, ".log");
                }
            }

            foreach (string old in Directory.GetFiles(Dir, "*.log").Order(StringComparer.Ordinal).SkipLast(KeepRuns).Where(f => f != Crashed))
            {
                File.Delete(old);
            }

            string stem = Path.Combine(Dir, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Environment.ProcessId}");
            _marker = stem + ".running";
            File.WriteAllText(_marker, "");
            _log = new StreamWriter(stem + ".log") { AutoFlush = true };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return;  // no run log: the app works without one
        }

        _log.WriteLine($"Asset Shelf {Version} on {RuntimeInformationText()}");
        Log.Added += e =>
        {
            string line = $"{e.At:HH:mm:ss.fff} {e.Area,-10} {e.Text}{(e.Ms is { } ms ? $" ({ms:0} ms)" : "")}";
            lock (_log)
            {
                if (_written < MaxLog)
                {
                    _written += line.Length + 1;
                    _log.WriteLine(_written < MaxLog ? line : "(the log is full: later lines are not kept)");
                }
            }
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Failed("unhandled error", e.ExceptionObject as Exception);
        // asked to stop (closing the window, or SIGTERM from systemctl stop) is a clean end; a crash is not
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Stop();
        _term = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, _ => Stop());
        Dispatcher.UIThread.UnhandledException += (_, e) => Failed("unhandled error on the UI thread", e.Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => Log.Write("error", $"unobserved task error: {e.Exception.GetBaseException()}");
        _memory = new Timer(_ => Log.Write("memory", MemoryText()), null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    /// <summary>A clean exit: this run did not crash.</summary>
    public static void Stop()
    {
        _memory?.Dispose();
        if (_marker is not null && !_failed)
        {
            File.Delete(_marker);
        }
    }

    private static void Failed(string what, Exception? e)
    {
        _failed = true;
        Log.Write("crash", $"{what}: {e}");
        Log.Write("crash", MemoryText());
    }

    private static bool Alive(string marker)
    {
        string name = Path.GetFileNameWithoutExtension(marker);
        if (!int.TryParse(name[(name.LastIndexOf('-') + 1)..], out int pid))
        {
            return false;
        }

        try
        {
            using var p = Process.GetProcessById(pid);
            return p.ProcessName.StartsWith("AssetShelf", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;  // no such process
        }
    }

    private static string Os => OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : "macos";

    private static string RuntimeInformationText() =>
        $"{System.Runtime.InteropServices.RuntimeInformation.OSDescription} ({System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}), .NET {Environment.Version}";

    private static string MemoryText()
    {
        using var me = Process.GetCurrentProcess();
        return $"memory: {me.WorkingSet64 >> 20} MB now, {me.PeakWorkingSet64 >> 20} MB at most, cap {Cap() ?? "none found"}";
    }

    /// <summary>The memory cap this process runs under, when the system says (Linux cgroup v2 memory.max).</summary>
    private static string? Cap()
    {
        try
        {
            if (OperatingSystem.IsLinux() && File.ReadAllLines("/proc/self/cgroup").FirstOrDefault(l => l.StartsWith("0::", StringComparison.Ordinal)) is { } line
                && File.ReadAllText($"/sys/fs/cgroup{line[3..]}/memory.max").Trim() is { } max && max != "max" && long.TryParse(max, out long b))
            {
                return $"{b >> 20} MB";
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }

        return null;
    }

    /// <summary>After the main window opens: offers to send the crashed run's report, once. Needs a team server.</summary>
    public static async Task OfferAsync(Window owner, Settings settings)
    {
        if (Crashed is not { } log || Team.Address(settings) is not { } address)
        {
            return;
        }

        string marker = Path.ChangeExtension(log, ".running");
        Crashed = null;
        File.Delete(marker);  // asked once, whatever the answer
        string text;
        try
        {
            text = File.Exists(log) ? await File.ReadAllTextAsync(log) : "(the crashed run left no log)";
        }
        catch (IOException e)
        {
            text = $"(the log could not be read: {e.Message})";
        }

        string info = $"tool: asset-shelf\nversion: {Version}\nos: {Os} ({RuntimeInformationText()})\nlog: {Path.GetFileName(log)}\n";
        if (!await new CrashDialog(info, text).ShowDialog<bool>(owner))
        {
            Log.Write("crash", "the report of the last run was not sent");
            return;
        }

        try
        {
            using var zip = new MemoryStream();
            using (var z = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
            {
                await using (var w = new StreamWriter(z.CreateEntry("info.txt").Open()))
                {
                    await w.WriteAsync(info);
                }

                await using (var w = new StreamWriter(z.CreateEntry(Path.GetFileName(log)).Open()))
                {
                    await w.WriteAsync(text);
                }
            }

            string id = await Team.SendCrashAsync(address, "asset-shelf", Version, Os, zip.ToArray());
            Log.Write("crash", $"report sent to the team: {id}");
        }
        catch (Exception e) when (e is InvalidOperationException or HttpRequestException or TaskCanceledException or IOException)
        {
            Log.Write("crash", $"the report could not be sent: {e.Message}");
            await Prompt.Confirm(owner, "Crash report", $"The report could not be sent: {e.Message}", "OK");
        }
    }

    /// <summary>"Asset Shelf closed unexpectedly. Send the report to the team?" with exactly what is sent shown.</summary>
    private sealed class CrashDialog : Window
    {
        public CrashDialog(string info, string log)
        {
            Title = "Asset Shelf closed unexpectedly";
            Width = 640;
            Height = 520;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = Look.Theme.SurfacePage;
            var sent = new TextBox { Text = info + "\n" + log, IsReadOnly = true, AcceptsReturn = true, FontFamily = new FontFamily("monospace"), FontSize = Look.Theme.Caption, TextWrapping = TextWrapping.NoWrap, IsVisible = false };
            Button show = Chrome.TextButton("Show what is sent", "The version, the system and the crashed run's log: nothing else.");
            show.Click += (_, _) => sent.IsVisible = !sent.IsVisible;
            Button no = Chrome.TextButton("Don't send", "Close; this crash is not asked about again.");
            Button yes = Chrome.PrimaryButton("send", "Send", "Send the report to your team's server.");
            no.Click += (_, _) => Close(false);
            yes.Click += (_, _) => Close(true);
            var bottom = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { show, no, yes } };
            DockPanel.SetDock(bottom, Dock.Bottom);
            var top = new TextBlock
            {
                Text = "Asset Shelf closed unexpectedly the last time. Send the report to the team? It holds that run's log, so whoever looks after the tools can see what went wrong.",
                TextWrapping = TextWrapping.Wrap,
                FontSize = Look.Theme.Body,
                Foreground = Look.Theme.TextPrimary,
                Margin = new Thickness(0, 0, 0, 10),
            };
            DockPanel.SetDock(top, Dock.Top);
            Content = new DockPanel { Margin = new Thickness(16), Children = { top, bottom, new Border { Margin = new Thickness(0, 0, 0, 10), Child = sent } } };
        }
    }
}
