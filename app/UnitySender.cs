using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace AssetShelf;

/// <summary>Puts an asset into a Unity project: the model and the textures its materials use, Unity materials for the
/// project's render pipeline (URP, HDRP or Built-in) wired by role, and the .meta files that make Unity import them
/// that way (normal maps as normal maps, data maps linear, the model's materials mapped onto ours). Never overwrites.</summary>
public static class UnitySender
{
    private static readonly string[] ModelExtensions = [".fbx", ".obj", ".blend", ".dae", ".3ds"];
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".tga", ".exr", ".tif", ".tiff", ".bmp", ".psd", ".hdr"];

    /// <summary>Only image files inside these folders may be copied: a .blend or FBX can point its images anywhere
    /// on disk (~/.ssh/...), and the Unity project may be shared. Symbolic links are resolved first.</summary>
    private static bool Allowed(string path, IEnumerable<string> roots)
    {
        try
        {
            string full = Path.GetFullPath(path);
            if (new FileInfo(full).ResolveLinkTarget(true) is { } target)
            {
                full = Path.GetFullPath(target.FullName);
            }

            return ImageExtensions.Contains(Path.GetExtension(full).ToLowerInvariant())
                && roots.Any(r => full.StartsWith(r, StringComparison.Ordinal));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    public static string Pipeline(string project)
    {
        string manifest = Path.Combine(project, "Packages", "manifest.json");
        string text = File.Exists(manifest) ? File.ReadAllText(manifest) : "";
        return text.Contains("render-pipelines.high-definition", StringComparison.Ordinal) ? "hdrp"
            : text.Contains("render-pipelines.universal", StringComparison.Ordinal) ? "urp" : "builtin";
    }

    /// <summary>Where an asset goes: &lt;folder&gt;/&lt;pack&gt;/&lt;asset&gt;/, or the pack's own folders under &lt;folder&gt;.</summary>
    public static string Destination(Item item, Settings s)
    {
        string root = Path.GetFullPath(Path.Combine(s.UnityProject, s.UnityFolder));
        if (!root.StartsWith(Path.GetFullPath(Path.Combine(s.UnityProject, "Assets")), StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"the Unity folder must be inside Assets/ (is {s.UnityFolder})");
        }

        if (!s.UnityMirrorPack)
        {
            return Sender.CacheDir(root, item);
        }

        // the pack's own folders: /Game/A/B/SM_X -> <root>/A/B/SM_X/, raw files -> <root>/<pack>/<folders of the file>/
        string rel = item.Raw ? Path.Combine(item.Pack.Id, Path.GetDirectoryName(item.Path) ?? "", Path.GetFileNameWithoutExtension(item.Path))
            : item.Package.StartsWith("/Game/", StringComparison.Ordinal) ? item.Package["/Game/".Length..] : item.Name;
        string dir = Path.GetFullPath(Path.Combine(root, string.Join('/', rel.Replace('\\', '/').Split('/').Select(Safe))));
        return dir.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? dir : throw new InvalidOperationException("unsafe path: " + rel);
    }

    private static string Safe(string s)
    {
        string t = System.Text.RegularExpressions.Regex.Replace(s, @"[^A-Za-z0-9_.-]", "_").Trim('.');
        return t.Length == 0 ? "_" : t;
    }

    /// <summary>Sends one asset; returns a line for the status bar.</summary>
    public static async Task<string> SendAsync(Item item, IReadOnlyList<string> files, string? texDir, bool unreal, Settings settings, string blenderDir)
    {
        string dest = Destination(item, settings);
        if (Directory.Exists(dest) && Directory.EnumerateFileSystemEntries(dest).Any())
        {
            return $"{item.Name}: already in Unity ({Path.GetRelativePath(settings.UnityProject, dest)}), left as it is";
        }

        string[] models = [.. files.Where(f => ModelExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))];
        if (models.Length == 0)
        {
            return $"{item.Name}: Unity cannot import {Path.GetExtension(files.FirstOrDefault() ?? "")} files without a package (glTF needs glTFast)";
        }

        string pipeline = Pipeline(settings.UnityProject);
        string prep = Path.Combine(Path.GetDirectoryName(Sender.Outbox())!, "unity-prep", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(prep);
        try
        {
            using var step = Log.Time("unity", $"{item.Name}: {pipeline}, into {Path.GetRelativePath(settings.UnityProject, dest)}");
            string job = Path.Combine(prep, "job.json");
            await File.WriteAllTextAsync(job, JsonSerializer.Serialize(new { files = models, textures = texDir, unreal, @out = prep, pipeline })).ConfigureAwait(false);
            var psi = new ProcessStartInfo("systemd-run")
            {
                ArgumentList = { "--user", "--scope", "-q", "-p", "MemoryMax=6G", "-p", "MemorySwapMax=0",
                    "blender", "-b", "--factory-startup", "-P", Path.Combine(blenderDir, "unity_prep.py"), "--", job },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using (Process p = Process.Start(psi)!)
            {
                string err = await p.StandardError.ReadToEndAsync().ConfigureAwait(false);
                await p.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
                await p.WaitForExitAsync().ConfigureAwait(false);
                if (!File.Exists(Path.Combine(prep, "unity_materials.json")))
                {
                    throw new InvalidOperationException("material step failed: " + err[Math.Max(0, err.Length - 300)..]);
                }
            }

            string[] roots = [.. new[] { texDir, item.Pack.FilesDir, prep }.OfType<string>()
                .Select(r => Path.GetFullPath(r).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar)];
            using JsonDocument d = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(prep, "unity_materials.json")).ConfigureAwait(false));
            Directory.CreateDirectory(Path.Combine(dest, "Textures"));
            Directory.CreateDirectory(Path.Combine(dest, "Materials"));
            var copied = new Dictionary<string, string>(StringComparer.Ordinal);  // source texture -> its guid in Unity
            string Texture(string src, bool normal, bool linear)
            {
                if (copied.TryGetValue(src, out string? g))
                {
                    return g;
                }

                string to = Path.Combine(dest, "Textures", Path.GetFileName(src));
                File.Copy(src, to);
                g = NewGuid();
                File.WriteAllText(to + ".meta", TextureMeta(g, normal, linear && !normal, flipGreen: normal && unreal));
                return copied[src] = g;
            }

            var remap = new List<(string Name, string Guid)>();
            int n = 0;
            foreach (JsonElement m in d.RootElement.GetProperty("materials").EnumerateArray())
            {
                string name = m.GetProperty("name").GetString() ?? $"Material{n}";
                string? S(string k)
                {
                    if (!m.TryGetProperty(k, out JsonElement v) || v.ValueKind != JsonValueKind.String || v.GetString() is not { } p || !File.Exists(p))
                    {
                        return null;
                    }

                    if (Allowed(p, roots))
                    {
                        return p;
                    }

                    Log.Write("unity", $"{item.Name}: skipped {p}: not an image inside the pack or its exports");
                    return null;
                }
                string? baseTex = S("base") is { } b ? Texture(b, false, false) : null;
                string? normalTex = S("normal") is { } nm ? Texture(nm, true, true) : null;
                string? maskTex = S("mask") is { } mk ? Texture(mk, false, true) : null;
                string? emitTex = S("emission") is { } em ? Texture(em, false, false) : null;
                bool clip = m.TryGetProperty("alpha_clip", out JsonElement ac) && ac.ValueKind == JsonValueKind.True;
                string matGuid = NewGuid();
                string file = Path.Combine(dest, "Materials", Safe(name) + ".mat");
                File.WriteAllText(file, Material(name, pipeline, baseTex, normalTex, maskTex, emitTex, clip));
                File.WriteAllText(file + ".meta", $"fileFormatVersion: 2\nguid: {matGuid}\nNativeFormatImporter:\n  externalObjects: {{}}\n  mainObjectFileID: 2100000\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n");
                remap.Add((name, matGuid));
                n++;
            }

            foreach (string model in models)
            {
                string to = Path.Combine(dest, Path.GetFileName(model));
                File.Copy(model, to);
                File.WriteAllText(to + ".meta", ModelMeta(NewGuid(), remap));
            }

            step.Then($"{item.Name}: {models.Length} model(s), {copied.Count} textures, {n} materials ({pipeline}) into {Path.GetRelativePath(settings.UnityProject, dest)}");
            return $"{item.Name}: in Unity at {Path.GetRelativePath(settings.UnityProject, dest)} ({n} materials, {pipeline.ToUpperInvariant()})";
        }
        finally
        {
            Directory.Delete(prep, recursive: true);
        }
    }

    private static string NewGuid() => Guid.NewGuid().ToString("N");

    private static string TextureMeta(string guid, bool normal, bool linear, bool flipGreen) =>
        $"fileFormatVersion: 2\nguid: {guid}\nTextureImporter:\n  serializedVersion: 13\n  mipmaps:\n    sRGBTexture: {(linear || normal ? 0 : 1)}\n" +
        $"  bumpmap:\n    flipGreenChannel: {(flipGreen ? 1 : 0)}\n  textureType: {(normal ? 1 : 0)}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n";

    private static string ModelMeta(string guid, List<(string Name, string Guid)> remap)
    {
        var sb = new StringBuilder($"fileFormatVersion: 2\nguid: {guid}\nModelImporter:\n  serializedVersion: 22200\n");
        if (remap.Count == 0)
        {
            sb.Append("  externalObjects: {}\n");
        }
        else
        {
            sb.Append("  externalObjects:\n");
            foreach (var (name, g) in remap)
            {
                sb.Append($"  - first:\n      type: UnityEngine:Material\n      assembly: UnityEngine.CoreModule\n      name: {Yaml(name)}\n    second: {{fileID: 2100000, guid: {g}, type: 2}}\n");
            }
        }

        return sb.Append("  materials:\n    materialImportMode: 2\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n").ToString();
    }

    private static string Yaml(string s) => "'" + s.Replace("'", "''") + "'";

    private static string TexEnv(string prop, string? guid) =>
        $"    - {prop}:\n        m_Texture: {(guid is null ? "{fileID: 0}" : $"{{fileID: 2800000, guid: {guid}, type: 3}}")}\n        m_Scale: {{x: 1, y: 1}}\n        m_Offset: {{x: 0, y: 0}}\n";

    /// <summary>A material for the pipeline's Lit shader (Built-in: Standard), its maps and the keywords they need.</summary>
    private static string Material(string name, string pipeline, string? baseTex, string? normal, string? mask, string? emit, bool clip)
    {
        var keywords = new List<string>();
        var tex = new StringBuilder();
        var floats = new List<(string, float)>();
        string shader;
        string colorProp;
        switch (pipeline)
        {
            case "hdrp":
                shader = "{fileID: 4800000, guid: 6e4ae4064600d784cac1e41a9e6f2e59, type: 3}";
                colorProp = "_BaseColor";
                tex.Append(TexEnv("_BaseColorMap", baseTex)).Append(TexEnv("_NormalMap", normal)).Append(TexEnv("_MaskMap", mask)).Append(TexEnv("_EmissiveColorMap", emit));
                if (normal is not null) { keywords.AddRange(["_NORMALMAP", "_NORMALMAP_TANGENT_SPACE"]); }
                if (mask is not null) { keywords.Add("_MASKMAP"); floats.Add(("_Metallic", 1)); floats.Add(("_SmoothnessRemapMax", 1)); }
                if (emit is not null) { keywords.Add("_EMISSIVE_COLOR_MAP"); }
                if (clip) { keywords.Add("_ALPHATEST_ON"); floats.Add(("_AlphaCutoffEnable", 1)); floats.Add(("_AlphaCutoff", 0.5f)); }
                break;
            case "urp":
                shader = "{fileID: 4800000, guid: 933532a4fcc9baf4fa0491de14d08ed7, type: 3}";
                colorProp = "_BaseColor";
                tex.Append(TexEnv("_BaseMap", baseTex)).Append(TexEnv("_MainTex", baseTex)).Append(TexEnv("_BumpMap", normal)).Append(TexEnv("_MetallicGlossMap", mask)).Append(TexEnv("_EmissionMap", emit));
                if (normal is not null) { keywords.Add("_NORMALMAP"); }
                if (mask is not null) { keywords.Add("_METALLICSPECGLOSSMAP"); floats.Add(("_Smoothness", 1)); floats.Add(("_Metallic", 1)); }
                if (emit is not null) { keywords.Add("_EMISSION"); }
                if (clip) { keywords.Add("_ALPHATEST_ON"); floats.Add(("_AlphaClip", 1)); floats.Add(("_Cutoff", 0.5f)); }
                break;
            default:
                shader = "{fileID: 46, guid: 0000000000000000f000000000000000, type: 0}";
                colorProp = "_Color";
                tex.Append(TexEnv("_MainTex", baseTex)).Append(TexEnv("_BumpMap", normal)).Append(TexEnv("_MetallicGlossMap", mask)).Append(TexEnv("_EmissionMap", emit));
                if (normal is not null) { keywords.Add("_NORMALMAP"); }
                if (mask is not null) { keywords.Add("_METALLICGLOSSMAP"); floats.Add(("_GlossMapScale", 1)); }
                if (emit is not null) { keywords.Add("_EMISSION"); }
                if (clip) { keywords.Add("_ALPHATEST_ON"); floats.Add(("_Mode", 1)); floats.Add(("_Cutoff", 0.5f)); }
                break;
        }

        var sb = new StringBuilder("%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n--- !u!21 &2100000\nMaterial:\n  serializedVersion: 8\n");
        sb.Append($"  m_ObjectHideFlags: 0\n  m_CorrespondingSourceObject: {{fileID: 0}}\n  m_PrefabInstance: {{fileID: 0}}\n  m_PrefabAsset: {{fileID: 0}}\n  m_Name: {Yaml(name)}\n  m_Shader: {shader}\n");
        sb.Append(keywords.Count == 0 ? "  m_ValidKeywords: []\n" : "  m_ValidKeywords:\n" + string.Concat(keywords.Select(k => $"  - {k}\n")));
        sb.Append("  m_InvalidKeywords: []\n  m_LightmapFlags: 4\n  m_EnableInstancingVariants: 0\n  m_DoubleSidedGI: 0\n  m_CustomRenderQueue: ").Append(clip ? "2450" : "-1").Append('\n');
        sb.Append("  stringTagMap: {}\n  disabledShaderPasses: []\n  m_SavedProperties:\n    serializedVersion: 3\n    m_TexEnvs:\n").Append(tex);
        sb.Append(floats.Count == 0 ? "    m_Floats: []\n" : "    m_Floats:\n" + string.Concat(floats.Select(f => $"    - {f.Item1}: {f.Item2.ToString(System.Globalization.CultureInfo.InvariantCulture)}\n")));
        sb.Append($"    m_Colors:\n    - {colorProp}: {{r: 1, g: 1, b: 1, a: 1}}\n    - _EmissionColor: {{r: {(emit is null ? 0 : 1)}, g: {(emit is null ? 0 : 1)}, b: {(emit is null ? 0 : 1)}, a: 1}}\n");
        return sb.Append("  m_BuildTextureStacks: []\n  m_AllowLocking: 1\n").ToString();
    }
}
