using System.Diagnostics;

namespace AssetShelf;

/// <summary>How the app starts its helpers on each system. On Linux with systemd, under a memory cap (systemd-run);
/// elsewhere, directly. Blender and Python are found on the PATH, or set in settings.json.</summary>
public static class Run
{
    private static readonly bool Systemd = OperatingSystem.IsLinux() && OnPath("systemd-run");

    /// <summary>A process for <paramref name="cmd"/>, capped at <paramref name="memory"/> where the system can.
    /// <paramref name="detached"/>: it outlives the app (a Blender window).</summary>
    public static ProcessStartInfo Capped(string memory, bool detached, params string[] cmd)
    {
        var psi = new ProcessStartInfo(Systemd ? "systemd-run" : cmd[0]);
        if (Systemd)
        {
            foreach (string a in detached
                ? new[] { "--user", "--collect", "-q", "--setenv=DISPLAY", "--setenv=WAYLAND_DISPLAY", "--setenv=XDG_RUNTIME_DIR" }
                : new[] { "--user", "--scope", "-q" })
            {
                psi.ArgumentList.Add(a);
            }

            psi.ArgumentList.Add("-p");
            psi.ArgumentList.Add($"MemoryMax={memory}");
            psi.ArgumentList.Add("-p");
            psi.ArgumentList.Add("MemorySwapMax=0");
            psi.ArgumentList.Add(cmd[0]);
        }

        foreach (string a in cmd.Skip(1))
        {
            psi.ArgumentList.Add(a);
        }

        return psi;
    }

    /// <summary>The Python that runs the tools: tools/.venv (a checkout) or <see cref="SyncVenv"/> when either exists,
    /// else python3 (python on Windows).</summary>
    public static string Python(Settings s) =>
        new[] { VenvPython(Path.Combine(s.Tools, ".venv")), VenvPython(SyncVenv) }.FirstOrDefault(File.Exists) ?? BasePython;

    /// <summary>The Python shipped in a release (python/ beside the app), else the system's; also makes <see cref="SyncVenv"/>.</summary>
    public static string BasePython => Path.Combine(AppContext.BaseDirectory, "python", OperatingSystem.IsWindows() ? "python.exe" : Path.Combine("bin", "python3")) is var b && File.Exists(b)
        ? b : OperatingSystem.IsWindows() ? "python" : "python3";

    /// <summary>Where the app sets up the sync tools' Python packages: under the user's data folder, since a release's
    /// tools/ may be read-only (an AppImage).</summary>
    public static string SyncVenv => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify), "asset-shelf", "venv");

    public static string VenvPython(string venv) =>
        OperatingSystem.IsWindows() ? Path.Combine(venv, "Scripts", "python.exe") : Path.Combine(venv, "bin", "python");

    /// <summary>ShelfIndex: published beside the tools in a release, or the build output in the repo; null when not built.</summary>
    public static string? ShelfIndex(Settings s)
    {
        string exe = OperatingSystem.IsWindows() ? "ShelfIndex.exe" : "ShelfIndex";
        return new[] { Path.Combine(s.Tools, "ShelfIndex", exe), Path.Combine(s.Tools, "ShelfIndex", "bin", "Release", "net10.0", exe) }.FirstOrDefault(File.Exists);
    }

    public static string Blender(Settings s) => s.Blender.Length > 0 ? s.Blender : "blender";

    private static bool OnPath(string exe) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Any(d => d.Length > 0 && File.Exists(Path.Combine(d, exe)));
}
