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

    /// <summary>The Python that runs the tools: tools/.venv when it exists, else python3 (python on Windows).</summary>
    public static string Python(Settings s)
    {
        string venv = OperatingSystem.IsWindows() ? Path.Combine(s.Tools, ".venv", "Scripts", "python.exe") : Path.Combine(s.Tools, ".venv", "bin", "python");
        return File.Exists(venv) ? venv : OperatingSystem.IsWindows() ? "python" : "python3";
    }

    public static string Blender(Settings s) => s.Blender.Length > 0 ? s.Blender : "blender";

    private static bool OnPath(string exe) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Any(d => d.Length > 0 && File.Exists(Path.Combine(d, exe)));
}
