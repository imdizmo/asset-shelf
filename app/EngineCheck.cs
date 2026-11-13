using System.Text.Json;
using System.Text.RegularExpressions;

namespace AssetShelf;

/// <summary>Can this pack go into that project? Engine versions (Unreal opens older assets, never newer ones), C++ code,
/// and plugins a pack's sample project turns on that the project does not.</summary>
public static partial class EngineCheck
{
    private static readonly Dictionary<string, (Version? Version, string? EngineDir)> ProjectCache = [];
    private static readonly Dictionary<string, HashSet<string>> DefaultOnCache = [];

    /// <summary>"5.4.4-35576357+++UE5+Release-5.4" -> 5.4.4</summary>
    public static Version? Parse(string? v) =>
        v is not null && VersionRx().Match(v) is { Success: true } m ? new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0) : null;

    /// <summary>The engine version of an Unreal project and where that engine is: from the .uproject's EngineAssociation,
    /// a version ("5.8") or an installation id looked up in Epic's Install.ini, then the engine's Build.version.</summary>
    public static (Version? Version, string? EngineDir) Project(string projectDir)
    {
        if (ProjectCache.TryGetValue(projectDir, out var hit))
        {
            return hit;
        }

        Version? v = null;
        string? engine = null;
        string? up = Directory.Exists(projectDir) ? Directory.EnumerateFiles(projectDir, "*.uproject").FirstOrDefault() : null;
        if (up is not null)
        {
            try
            {
                using JsonDocument d = JsonDocument.Parse(File.ReadAllBytes(up));
                string assoc = d.RootElement.TryGetProperty("EngineAssociation", out JsonElement a) ? a.GetString() ?? "" : "";
                engine = Installations().GetValueOrDefault(assoc) ?? Installations().GetValueOrDefault("UE_" + assoc);
                v = Parse(assoc) ?? (engine is null ? null : BuildVersion(engine));
            }
            catch (Exception e) when (e is JsonException or IOException)
            {
                // ponytail: an unreadable .uproject means "version unknown", not an error
            }
        }

        return ProjectCache[projectDir] = (v, engine);
    }

    private static Dictionary<string, string> Installations()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string ini = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "Epic", "UnrealEngine", "Install.ini");
        if (File.Exists(ini))
        {
            foreach (string line in File.ReadLines(ini))
            {
                int eq = line.IndexOf('=');
                if (eq > 0 && !line.StartsWith('['))
                {
                    map[line[..eq].Trim().Trim('{', '}')] = line[(eq + 1)..].Trim();
                }
            }
        }

        return map;
    }

    private static Version? BuildVersion(string engineDir)
    {
        string f = Path.Combine(engineDir, "Engine", "Build", "Build.version");
        if (!File.Exists(f))
        {
            return null;
        }

        using JsonDocument d = JsonDocument.Parse(File.ReadAllBytes(f));
        JsonElement r = d.RootElement;
        return new Version(r.GetProperty("MajorVersion").GetInt32(), r.GetProperty("MinorVersion").GetInt32(), r.GetProperty("PatchVersion").GetInt32());
    }

    /// <summary>Plain-language findings for putting this pack into that project; empty when nothing stands in the way.</summary>
    public static List<(string Text, bool Blocking)> Check(Pack pack, string projectDir)
    {
        var found = new List<(string, bool)>();
        Version? made = Parse(pack.Engine);
        var (proj, engineDir) = projectDir.Length > 0 ? Project(projectDir) : (null, null);
        string name = Path.GetFileName(projectDir.TrimEnd('/'));
        if (made is not null && proj is not null && (made.Major, made.Minor).CompareTo((proj.Major, proj.Minor)) > 0)
        {
            found.Add(($"Saved with Unreal {made.Major}.{made.Minor}; {name} is {proj.Major}.{proj.Minor}: Unreal cannot open assets from a newer version.", true));
        }

        string unreal = Path.Combine(pack.FilesDir, "unreal");
        if (Directory.Exists(Path.Combine(unreal, "Source")))
        {
            found.Add(("The pack has C++ code (Source/): only its code-based parts need a C++ project; single meshes, materials and textures do not.", false));
        }

        if (Directory.Exists(Path.Combine(unreal, "Plugins")))
        {
            string[] plugins = [.. Directory.GetDirectories(Path.Combine(unreal, "Plugins")).Select(Path.GetFileName).OfType<string>()];
            if (plugins.Length > 0)
            {
                found.Add(($"The pack ships plugins ({string.Join(", ", plugins)}): only its blueprints and levels that use them need them in the project's Plugins folder.", false));
            }
        }

        // plugins its sample project turns on, that the target project does not and the engine does not by default
        string? packProject = Directory.Exists(unreal) ? Directory.EnumerateFiles(unreal, "*.uproject").FirstOrDefault() : null;
        if (packProject is not null && projectDir.Length > 0)
        {
            HashSet<string> wanted = EnabledPlugins(packProject);
            string? target = Directory.EnumerateFiles(projectDir, "*.uproject").FirstOrDefault();
            HashSet<string> have = target is null ? [] : EnabledPlugins(target);
            HashSet<string> defaults = engineDir is null ? [] : DefaultOn(engineDir);
            string[] missing = [.. wanted.Where(p => !have.Contains(p) && !defaults.Contains(p)).Order(StringComparer.OrdinalIgnoreCase)];
            if (missing.Length > 0)
            {
                found.Add(($"The pack's sample project turns on plugins {name} does not: {string.Join(", ", missing)} (matters for its blueprints and levels).", false));
            }
        }

        return found;
    }

    private static HashSet<string> EnabledPlugins(string uproject)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using JsonDocument d = JsonDocument.Parse(File.ReadAllBytes(uproject), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (d.RootElement.TryGetProperty("Plugins", out JsonElement ps) && ps.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement p in ps.EnumerateArray())
                {
                    if (p.TryGetProperty("Enabled", out JsonElement en) && en.ValueKind == JsonValueKind.True && p.TryGetProperty("Name", out JsonElement n))
                    {
                        set.Add(n.GetString() ?? "");
                    }
                }
            }
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            // ponytail: unreadable means "no plugins known"
        }

        return set;
    }

    /// <summary>Engine plugins that are on unless a project turns them off ("EnabledByDefault": true). Read once per engine.</summary>
    private static HashSet<string> DefaultOn(string engineDir)
    {
        if (DefaultOnCache.TryGetValue(engineDir, out var hit))
        {
            return hit;
        }

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string root = Path.Combine(engineDir, "Engine", "Plugins");
        if (Directory.Exists(root))
        {
            foreach (string f in Directory.EnumerateFiles(root, "*.uplugin", SearchOption.AllDirectories))
            {
                try
                {
                    if (DefaultRx().IsMatch(File.ReadAllText(f)))
                    {
                        set.Add(Path.GetFileNameWithoutExtension(f));
                    }
                }
                catch (IOException)
                {
                }
            }
        }

        return DefaultOnCache[engineDir] = set;
    }

    [GeneratedRegex(@"(\d+)\.(\d+)(?:\.(\d+))?")]
    private static partial Regex VersionRx();

    [GeneratedRegex("\"EnabledByDefault\"\\s*:\\s*true")]
    private static partial Regex DefaultRx();
}
