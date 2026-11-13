using System.Diagnostics;
using System.Text.Json;

namespace AssetShelf;

/// <summary>Brings assets into an Unreal project (file copies) or hands them to Blender (a job file its add-on picks up).</summary>
public static class Sender
{
    private static readonly string[] PackageExtensions = [".uasset", ".umap", ".uexp", ".ubulk", ".uptnl"];

    /// <summary>The files on disk of an Unreal package, /Game/A/B -> &lt;pack&gt;/unreal/Content/A/B.*</summary>
    public static IEnumerable<string> PackageFiles(Item item)
    {
        string rel = item.Package.StartsWith("/Game/", StringComparison.Ordinal) ? item.Package["/Game/".Length..] : item.Package.TrimStart('/');
        string stem = Path.Combine(item.Pack.FilesDir, "unreal", "Content", rel);
        return PackageExtensions.Select(e => stem + e).Where(File.Exists);
    }

    /// <summary>What sending would do: the files to copy, the packages already in the project, those missing from the pack.</summary>
    public sealed record UnrealPlan(List<(string Src, string Dst, long Size)> Copy, List<string> Already, List<string> Missing)
    {
        public long Bytes => Copy.Sum(c => c.Size);
    }

    /// <summary>Works out a send without copying anything: per package, its files that are not yet in &lt;project&gt;/Content.</summary>
    public static UnrealPlan PlanUnreal(IEnumerable<Item> items, string projectDir)
    {
        var plan = new UnrealPlan([], [], []);
        string content = Path.Combine(projectDir, "Content");
        foreach (Item item in items.Where(i => !i.Raw).DistinctBy(i => i.Package))
        {
            string src = Path.Combine(item.Pack.FilesDir, "unreal", "Content");
            List<string> files = PackageFiles(item).ToList();
            if (files.Count == 0)
            {
                plan.Missing.Add(item.Package);
                continue;
            }

            var todo = files.Select(f => (f, Path.Combine(content, Path.GetRelativePath(src, f)))).Where(x => !File.Exists(x.Item2)).ToList();
            if (todo.Count == 0)
            {
                plan.Already.Add(item.Package);
            }

            plan.Copy.AddRange(todo.Select(x => (x.f, x.Item2, new FileInfo(x.f).Length)));
        }

        return plan;
    }

    /// <summary>
    /// Copies the planned files into &lt;project&gt;/Content at the same paths, so their references hold. A file that has
    /// appeared since the plan is left alone (never overwritten). Returns how many were copied.
    /// </summary>
    public static int ToUnreal(UnrealPlan plan, IProgress<double>? progress = null)
    {
        int copied = 0;
        // progress by bytes: one big texture weighs more than many small materials
        long total = Math.Max(1, plan.Bytes), done = 0;
        foreach (var (src, dst, size) in plan.Copy)
        {
            if (!File.Exists(dst))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(src, dst + ".part", overwrite: true);
                File.Move(dst + ".part", dst);
                copied++;
            }

            done += size;
            progress?.Report((double)done / total);
        }

        return copied;
    }

    /// <summary>The packages already in an Unreal project (/Game/... names from &lt;project&gt;/Content).</summary>
    public static HashSet<string> ProjectPackages(string projectDir)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string content = Path.Combine(projectDir, "Content");
        if (!Directory.Exists(content))
        {
            return set;
        }

        foreach (string f in Directory.EnumerateFiles(content, "*.*", SearchOption.AllDirectories))
        {
            if (f.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".umap", StringComparison.OrdinalIgnoreCase))
            {
                string rel = Path.GetRelativePath(content, f).Replace('\\', '/');
                set.Add("/Game/" + rel[..rel.LastIndexOf('.')]);
            }
        }

        return set;
    }

    /// <summary>A cache folder for one asset: root/&lt;pack&gt;/&lt;name&gt; with both parts reduced to safe file-name characters
    /// (names come from third-party files), checked to stay under root.</summary>
    public static string CacheDir(string root, Item item)
    {
        static string Safe(string s)
        {
            string t = System.Text.RegularExpressions.Regex.Replace(s, @"[^A-Za-z0-9_.-]", "_").Trim('.');
            return t.Length == 0 ? "_" : t.Length > 120 ? t[..120] : t;
        }

        string dir = Path.GetFullPath(Path.Combine(root, Safe(item.Pack.Id), Safe(item.Name)));
        string full = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        return dir.StartsWith(full, StringComparison.Ordinal) ? dir : throw new InvalidOperationException("unsafe asset name: " + item.Name);
    }

    /// <summary>The folder the Blender add-on watches (ASSET_SHELF_OUTBOX, or ~/.cache/asset-shelf/outbox).</summary>
    public static string Outbox() =>
        Environment.GetEnvironmentVariable("ASSET_SHELF_OUTBOX") is { Length: > 0 } o ? o
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "asset-shelf", "outbox");

    /// <summary>Drops a job for Blender: the files to import and where to look for loose textures.</summary>
    public static void ToBlender(string name, IReadOnlyList<string> files, string? textureDir, bool unreal = false)
    {
        string box = Outbox();
        Directory.CreateDirectory(box);
        string job = Path.Combine(box, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}.json");
        File.WriteAllText(job + ".part", JsonSerializer.Serialize(new { name, files, textures = textureDir, unreal }));
        File.Move(job + ".part", job);
    }

    /// <summary>Exports an Unreal asset to FBX (with its textures as PNG) through the tools' headless editor.</summary>
    /// <returns>The export folder and the FBX files in it.</returns>
    public static async Task<(string Dir, List<string> Fbx)> ExportAsync(Item item, string toolsDir)
    {
        string outDir = CacheDir(Path.Combine(Path.GetDirectoryName(Outbox())!, "exports"), item);
        // the asset and every package it needs: only these are copied out of the pack, so Unreal scans a handful of files
        List<Item> needs = Library.Needs(item);
        string jobFile = outDir + ".job.json";
        Directory.CreateDirectory(Path.GetDirectoryName(jobFile)!);
        await File.WriteAllTextAsync(jobFile, JsonSerializer.Serialize(new
        {
            pack = Path.Combine(item.Pack.FilesDir, "unreal"),
            asset = item.Path,
            @class = item.Class,
            @out = outDir,
            packages = needs.Select(i => i.Package).Prepend(item.Package).ToList(),
            textures = needs.Where(i => i.Class == "Texture2D").Select(i => i.Path).ToList(),
        })).ConfigureAwait(false);
        var psi = new ProcessStartInfo("python3")
        {
            ArgumentList = { "-I", "-u", Path.Combine(toolsDir, "export_fbx.py"), "--job", jobFile },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var step = Log.Time("export", $"{item.Name}: {needs.Count + 1} packages from {item.Pack.Id}");
        Log.Write("export", $"{item.Name} ({item.Class}) start");
        using Process p = new() { StartInfo = psi };
        var output = new System.Text.StringBuilder();
        void Line(object? _, DataReceivedEventArgs e)
        {
            if (e.Data is { Length: > 0 } l)
            {
                lock (output)
                {
                    output.AppendLine(l);
                }

                Log.Write("export", l.Length > 2000 ? l[..2000] + "…" : l, step.Ms);
            }
        }

        p.OutputDataReceived += Line;
        p.ErrorDataReceived += Line;
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        await p.WaitForExitAsync().ConfigureAwait(false);
        string log = output.ToString();
        File.Delete(jobFile);
        List<string> fbx = Directory.Exists(outDir) ? Directory.GetFiles(outDir, "*.fbx").ToList() : [];
        if (p.ExitCode != 0 || fbx.Count == 0)
        {
            throw new InvalidOperationException("export failed: " + log[Math.Max(0, log.Length - 400)..]);
        }

        return (outDir, fbx);
    }

    /// <summary>Exports an Unreal asset to FBX and hands it to the open Blender.</summary>
    public static async Task<string> UnrealToBlenderAsync(Item item, string toolsDir)
    {
        var (dir, fbx) = await ExportAsync(item, toolsDir).ConfigureAwait(false);
        ToBlender(item.Name, fbx, dir, unreal: true);
        return dir;
    }

    /// <summary>
    /// Starts a new Blender window holding only these files (blender/open_in_blender.py imports them into an empty
    /// scene). Blender runs as its own user service with a memory cap, so it outlives the app.
    /// </summary>
    public static void OpenInBlender(string name, IReadOnlyList<string> files, string? textureDir, string blenderDir, bool unreal = false)
    {
        string box = Path.Combine(Path.GetDirectoryName(Outbox())!, "open");
        Directory.CreateDirectory(box);
        string job = Path.Combine(box, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}.json");
        File.WriteAllText(job, JsonSerializer.Serialize(new { name, files, textures = textureDir, unreal }));
        var psi = new ProcessStartInfo("systemd-run")
        {
            ArgumentList =
            {
                "--user", "--collect", "-q", "-p", "MemoryMax=16G", "-p", "MemorySwapMax=0",
                "--setenv=DISPLAY", "--setenv=WAYLAND_DISPLAY", "--setenv=XDG_RUNTIME_DIR",
                "blender", "--python", Path.Combine(blenderDir, "open_in_blender.py"), "--", job,
            },
        };
        using Process? p = Process.Start(psi);
        Log.Write("blender", $"new window for {name}: {files.Count} file(s), job {Path.GetFileName(job)}");
    }
}
