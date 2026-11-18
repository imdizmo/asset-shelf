using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AssetShelf;

/// <summary>Mesh prep, an optional capability: blender/mesh_prep.py run headless (and capped) on an asset's model. It
/// looks the model over, optionally repairs and lightens it, makes LODs and a collision shape, and exports it for a
/// target, with a report. Only there when Blender is; the asset's own files are only read.</summary>
public static class MeshPrep
{
    public sealed record Options(bool CheckOnly, bool Fix, int? MaxTris, int Lods, bool Collision, string Target);

    public static readonly string[] Targets = ["unreal", "godot", "groundwork", "blender"];

    /// <summary>True when Blender can be run (the setting, or "blender" on the PATH).</summary>
    public static bool Available(Settings s) =>
        s.Blender.Length > 0 ? File.Exists(s.Blender)
            : (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Any(d => File.Exists(Path.Combine(d, OperatingSystem.IsWindows() ? "blender.exe" : "blender")));

    public static bool Takes(Item i) => i.Published is null && (i.Raw ? IsModel(i.Path) : i.Kind is "Mesh" or "Character");

    private static bool IsModel(string path) => Path.GetExtension(path).ToLowerInvariant() is ".fbx" or ".obj" or ".glb" or ".gltf" or ".blend";

    /// <summary>A loose file with the folder its textures are in (as the app hands Blender).</summary>
    public static (string File, string TexDir) RawFile(Item item)
    {
        string file = Path.Combine(item.Pack.FilesDir, "raw", item.Path);
        string[] parts = item.Path.Split('/');
        return (file, Path.Combine(item.Pack.FilesDir, "raw", Path.Combine(parts[..Math.Min(2, parts.Length - 1)])));
    }

    /// <summary>Runs mesh prep on one asset into <paramref name="outDir"/> and returns its report. Throws
    /// InvalidOperationException with a sentence for a person when it cannot.</summary>
    public static async Task<JsonObject> RunAsync(Item item, Settings settings, Func<Task<string>> engine, string outDir, Options o)
    {
        outDir = Path.GetFullPath(outDir);
        foreach (string mine in new[] { item.Pack.FilesDir, item.Pack.IndexDir })
        {
            string m = Path.TrimEndingDirectorySeparator(Path.GetFullPath(mine));
            if (outDir == m || outDir.StartsWith(m + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Prepared files never go into the library: choose a folder outside it.");
            }
        }

        if (!Available(settings))
        {
            throw new InvalidOperationException("Mesh prep needs Blender (set it in settings, or put blender on the PATH).");
        }

        List<string> files;
        string? textures;
        if (item.Raw)
        {
            var (file, tex) = RawFile(item);
            (files, textures) = ([file], tex);
        }
        else
        {
            var (dir, models) = await Sender.ExportAsync(item, settings, engine);
            (files, textures) = (models.Take(1).ToList(), dir);
        }

        string name = new([.. item.Name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_')]);
        name = name.Length > 0 ? name[..Math.Min(name.Length, 80)] : "asset";
        Directory.CreateDirectory(outDir);
        string job = Path.Combine(Path.GetTempPath(), $"asset-shelf-prep-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(job, JsonSerializer.Serialize(new
        {
            files, textures, unreal = !item.Raw, name, @out = outDir, checkOnly = o.CheckOnly, fix = o.Fix,
            maxTris = o.MaxTris, lods = Math.Clamp(o.Lods, 0, 4), collision = o.Collision, target = Targets.Contains(o.Target) ? o.Target : "godot",
        }));
        try
        {
            string blenderDir = Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(settings.Tools))!, "blender");
            // headless, never a window; capped: a model of millions of triangles took Blender to about 6 GB
            ProcessStartInfo psi = Run.Capped("12G", false, Run.Blender(settings), "-b", "--factory-startup", "--disable-autoexec",
                "-P", Path.Combine(blenderDir, "mesh_prep.py"), "--", job);
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            using Process p = Process.Start(psi) ?? throw new InvalidOperationException("Blender did not start.");
            Task<string> err = p.StandardError.ReadToEndAsync();
            string output = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            // Blender exits 0 even when the script failed: the report line is what says it worked
            string? line = output.Split('\n').FirstOrDefault(l => l.StartsWith("ASSETSHELF_PREP ", StringComparison.Ordinal));
            if (line is null || JsonNode.Parse(line["ASSETSHELF_PREP ".Length..]) is not JsonObject report)
            {
                string why = (output + await err).Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("Error", StringComparison.Ordinal) || l.Contains("Error:", StringComparison.Ordinal)) ?? "see the debug log";
                Log.Write("prep", $"{item.Name}: no report; {output[^Math.Min(output.Length, 2000)..]}");
                throw new InvalidOperationException($"Mesh prep did not finish: {why}");
            }

            Log.Write("prep", $"{item.Name}: {report["done"]?.ToJsonString()}");
            return report;
        }
        finally
        {
            File.Delete(job);
        }
    }

    /// <summary>The report in plain lines, for the app and for `shelf prep` without --json.</summary>
    public static IEnumerable<string> Lines(JsonObject r)
    {
        static string Facts(JsonNode? f) => f is null ? "" :
            $"{f["triangles"]:N0} triangles, {f["objects"]} objects, {f["parts"]} separate parts; {f["holes"]} open loops (holes or rims), "
            + $"{f["flippedFaces"]} faces turned the wrong way, {f["fragments"]} stray fragments, {f["nonManifoldEdges"]} edges shared by more than two faces, "
            + $"{f["looseVertices"]} loose vertices; size {string.Join(" × ", (f["size"] as JsonArray ?? []).Select(v => $"{v?.GetValue<double>():0.##}"))} m";
        yield return $"Found: {Facts(r["found"])}";
        if (r["repair"] is JsonObject fix)
        {
            yield return $"Repaired: {fix["verticesJoined"]} vertices joined, {fix["looseRemoved"]} loose vertices and {fix["fragmentsRemoved"]} fragments removed, "
                + $"{fix["facesTurned"]} faces turned, {fix["holesClosed"]} holes closed";
        }

        foreach (JsonNode? d in r["done"] as JsonArray ?? [])
        {
            yield return $"Done: {d}";
        }

        if (r["done"] is JsonArray { Count: > 0 })
        {
            yield return $"After: {Facts(r["after"])}";
        }

        foreach (JsonNode? l in r["lods"] as JsonArray ?? [])
        {
            yield return $"LOD {l?["level"]}: {l?["triangles"]:N0} triangles ({l?["file"]})";
        }

        if (r["collision"] is JsonObject c)
        {
            yield return $"Collision: {c["object"]}, {c["triangles"]} triangles";
        }

        if (r["axes"] is { } axes)
        {
            yield return $"Axes and units: {axes}";
        }

        yield return $"Files: {string.Join(", ", (r["files"] as JsonArray ?? []).Select(f => f?.ToString()))}";
    }
}
