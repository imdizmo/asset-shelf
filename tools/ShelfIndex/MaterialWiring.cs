// Which exported texture feeds which material input, read from the material itself: a material instance's texture
// parameters (child before parent) over its base material's graph, followed from each input (BaseColor, Normal,
// Roughness, ...) back to the texture samples that reach it. Roles are the Blender add-on's: base, normal, emit,
// opacity, rough, metal, and packed orm/rma/mra from the channels that feed roughness and metallic.
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.UObject;

static class MaterialWiring
{
    static readonly string[] Inputs = ["BaseColor", "Normal", "EmissiveColor", "Opacity", "OpacityMask", "Roughness", "Metallic", "AmbientOcclusion"];

    /// <summary>Role to exported file name; <paramref name="files"/> maps texture package paths to the files written.</summary>
    public static Dictionary<string, string> Roles(DefaultFileProvider provider, string materialPath, Dictionary<string, string> files)
    {
        // instance chain: parameter overrides, child first
        var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        UObject? m = Exporter.Load<UObject>(provider, materialPath);
        for (int depth = 0; m is UMaterialInstance mi && depth < 8; depth++)
        {
            if (mi is UMaterialInstanceConstant mic)
            {
                foreach (FTextureParameterValue p in mic.TextureParameterValues)
                {
                    if (p.ParameterValue?.ResolvedObject is { } t)
                    {
                        overrides.TryAdd(p.Name, Exporter.GamePath(t.GetPathName()).Split('.')[0]);
                    }
                }
            }

            try
            {
                m = mi.Parent?.ResolvedObject is { } parent ? Exporter.Load<UObject>(provider, Exporter.GamePath(parent.GetPathName())) : null;
            }
            catch (FileNotFoundException)
            {
                m = null; // the parent is outside the pack (an engine or plugin material)
            }
        }

        if (m is null)
        {
            return ByParameterName(overrides, files);
        }

        // the base material's inputs: on the material (UE 4) or its MaterialEditorOnlyData (UE 5)
        var exports = m.Owner!.GetExports().ToList();
        var reached = new List<(string Input, string Texture, int Channel)>();
        foreach (UObject holder in exports.Where(e => e is UMaterial || e.ExportType == "MaterialEditorOnlyData"))
        {
            foreach (string input in Inputs)
            {
                if (holder.TryGetValue(out FExpressionInput link, input) && link.Expression is { IsNull: false })
                {
                    Walk(link, input, overrides, reached, [], 0);
                }
            }
        }

        var roles = new Dictionary<string, string>();
        string? File(string texture) => files.GetValueOrDefault(texture);
        void Set(string role, string? file)
        {
            if (file is not null) roles.TryAdd(role, file);
        }

        foreach (var (input, role) in new[] { ("BaseColor", "base"), ("Normal", "normal"), ("EmissiveColor", "emit") })
        {
            var own = reached.Where(r => r.Input == input && File(r.Texture) is not null).ToList();
            // AO or a mask multiplied into the colour also reaches BaseColor: prefer what feeds no other input
            var alone = own.FirstOrDefault(r => r.Channel == 0 && !reached.Any(o => o.Texture == r.Texture && o.Input != input));
            Set(role, File((alone.Texture is not null ? alone : role == "base" ? default : own.FirstOrDefault()).Texture ?? ""));
        }

        var rough = reached.FirstOrDefault(r => r.Input == "Roughness" && File(r.Texture) is not null);
        var metal = reached.FirstOrDefault(r => r.Input == "Metallic" && File(r.Texture) is not null);
        string Ch(int c) => c switch { 1 => "r", 2 => "g", 3 => "b", _ => "" };
        string? Packed(int roughCh, int metalCh) => (roughCh, metalCh) switch
        {
            (2, 3) => "orm",
            (1, 2) => "rma",
            (2, 1) => "mra",
            ( >= 1 and <= 3, -1) => "rough_" + Ch(roughCh),
            (-1, >= 1 and <= 3) => "metal_" + Ch(metalCh),
            _ => null,
        };
        bool same = rough.Texture is not null && rough.Texture == metal.Texture;
        if (Packed(rough.Texture is null ? -1 : rough.Channel, same ? metal.Channel : -1) is { } rp)
        {
            Set(rp, File(rough.Texture!));
        }
        else if (rough.Texture is not null)
        {
            Set("rough", File(rough.Texture));
        }

        if (!same && metal.Texture is not null)
        {
            Set(Packed(-1, metal.Channel) ?? "metal", File(metal.Texture));
        }

        var opacity = reached.FirstOrDefault(r => r.Input is "Opacity" or "OpacityMask" && File(r.Texture) is not null && File(r.Texture) != roles.GetValueOrDefault("base"));
        Set("opacity", opacity.Texture is null ? null : File(opacity.Texture));
        return roles;
    }

    /// <summary>Roles from an instance's parameter names alone (its parent material is not in the pack).</summary>
    static Dictionary<string, string> ByParameterName(Dictionary<string, string> overrides, Dictionary<string, string> files)
    {
        var roles = new Dictionary<string, string>();
        foreach (var (param, texture) in overrides)
        {
            string? role = param.ToLowerInvariant() switch
            {
                var n when n.Contains("normal") => "normal",
                var n when n.Contains("orm") || n.Contains("occlusionroughnessmetal") => "orm",
                var n when n.Contains("emissi") => "emit",
                var n when n.Contains("opacity") || n.Contains("mask") => null,
                var n when n.Contains("base") || n.Contains("albedo") || n.Contains("diffuse") || n.Contains("color") || n.Contains("colour") => "base",
                _ => null,
            };
            if (role is not null && files.TryGetValue(texture, out string? file))
            {
                roles.TryAdd(role, file);
            }
        }

        return roles;
    }

    /// <summary>Follows an input back through the graph (math nodes, lerps, ...) to the texture samples that feed it.
    /// The channel is the sample's output used: 0 RGB, 1 R, 2 G, 3 B, 4 A.</summary>
    static void Walk(FExpressionInput link, string input, Dictionary<string, string> overrides, List<(string, string, int)> reached, HashSet<UObject> seen, int depth)
    {
        if (depth > 16 || link.Expression?.ResolvedObject?.Load() is not { } node || !seen.Add(node))
        {
            return;
        }

        if (node is UMaterialExpressionTextureBase sample)
        {
            string? param = node.TryGetValue(out FName pn, "ParameterName") ? pn.Text : null;
            string? texture = param is not null && overrides.TryGetValue(param, out string? o) ? o
                : sample.Texture?.ResolvedObject is { } tx ? Exporter.GamePath(tx.GetPathName()).Split('.')[0] : null;
            if (texture is not null)
            {
                reached.Add((input, texture, link.OutputIndex));
            }

            return;
        }

        foreach (var prop in node.Properties)
        {
            if (prop.Tag?.GetValue(typeof(FExpressionInput)) is FExpressionInput next && next.Expression is { IsNull: false })
            {
                Walk(next, input, overrides, reached, seen, depth + 1);
            }
        }
    }
}
