// ShelfIndex export --job job.json: one Unreal asset to a .glb (static meshes) and its textures to PNG/JPEG, read
// straight from the editor packages, without Unreal. The job is the one tools/export_fbx.py takes:
//   {"pack": "<pack>/unreal", "asset": "/Game/X/SM_Y.SM_Y", "class": "StaticMesh", "out": "<dir>",
//    "packages": ["/Game/X/SM_Y", ...], "textures": ["/Game/T/T_A.T_A", ...]}
// Exit 0: done (<out>/.done written); 3: this kind is not handled here (the caller falls back to Unreal); 1: failed.
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Versions;

static class Exporter
{
    public static int Run(string[] args)
    {
        if (args.Length != 2 || args[0] != "--job")
        {
            Console.Error.WriteLine("usage: ShelfIndex export --job job.json");
            return 2;
        }

        if (Environment.GetEnvironmentVariable("SHELF_DEBUG") is not null)
        {
            Serilog.Log.Logger = new Serilog.LoggerConfiguration().WriteTo.Sink(new StderrSink()).CreateLogger();
        }

        using JsonDocument job = JsonDocument.Parse(File.ReadAllBytes(args[1]));
        JsonElement j = job.RootElement;
        string pack = j.GetProperty("pack").GetString()!, asset = j.GetProperty("asset").GetString()!;
        string cls = j.GetProperty("class").GetString()!, outDir = Path.GetFullPath(j.GetProperty("out").GetString()!);
        if (cls is not ("StaticMesh" or "SkeletalMesh" or "AnimSequence"))
        {
            Console.Error.WriteLine($"{cls}: not exported without Unreal yet");
            return 3;
        }

        // only the asset's own packages are copied out of the pack (it is usually on a network share), then read locally
        string src = Path.Combine(outDir + ".src", "Content");
        try
        {
            Directory.CreateDirectory(outDir);
            foreach (string p in j.GetProperty("packages").EnumerateArray().Select(e => e.GetString()!))
            {
                foreach (string ext in new[] { ".uasset", ".uexp", ".ubulk", ".upayload" })
                {
                    string from = Local(Path.Combine(pack, "Content"), p, ext), to = Local(src, p, ext);
                    if (File.Exists(from))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                        File.Copy(from, to, true);
                    }
                }
            }

            var provider = new DefaultFileProvider(new DirectoryInfo(Path.GetDirectoryName(src)!), SearchOption.AllDirectories, new VersionContainer(EGame.GAME_UE5_LATEST));
            provider.Initialize();
            provider.Mount();
            string name = asset.Split('.')[^1];
            List<(string, string)> materialPaths;
            int triangles;
            if (cls == "AnimSequence")
            {
                var anim = Load<UAnimSequence>(provider, asset) ?? throw new InvalidOperationException("no AnimSequence in " + asset);
                var skeleton = anim.Skeleton?.ResolvedObject is { } so ? Load<USkeleton>(provider, GamePath(so.GetPathName())) : null;
                (byte[] glb, triangles) = Anim.Write(name, anim, skeleton ?? throw new InvalidOperationException("the animation's skeleton is not in the pack"));
                File.WriteAllBytes(Path.Combine(outDir, Safe(name) + ".glb"), glb);
                materialPaths = [];
            }
            else if (cls == "SkeletalMesh")
            {
                var sk = Load<USkeletalMesh>(provider, asset) ?? throw new InvalidOperationException("no SkeletalMesh in " + asset);
                if (Environment.GetEnvironmentVariable("SHELF_DEBUG") is not null) Console.Error.WriteLine($"sk: lods {sk.LODModels?.Length} mats {sk.SkeletalMaterials?.Length} props [{string.Join(",", sk.Properties.Select(p => p.Name.Text))}] ver {sk.Owner?.Summary.FileVersionUE}");
                materialPaths = [.. sk.SkeletalMaterials.Select(m => m.MaterialInterface?.ResolvedObject is { } o ? (o.Name.Text, GamePath(o.GetPathName())) : ("Material", ""))];
                (byte[] glb, triangles) = Skinned.Write(name, sk, materialPaths.Select(m => m.Item1).ToArray());
                if (Environment.GetEnvironmentVariable("SHELF_DEBUG") is not null) Console.Error.WriteLine($"skeleton check: worst bone gap {Skinned.Check(sk):F5} m over {sk.ReferenceSkeleton.FinalRefBoneInfo.Length} bones");
                File.WriteAllBytes(Path.Combine(outDir, Safe(name) + ".glb"), glb);
            }
            else
            {
                var mesh = Load<UStaticMesh>(provider, asset) ?? throw new InvalidOperationException("no StaticMesh in " + asset);
                FMeshDescription md = ReadMesh(provider, asset, mesh) ?? throw new InvalidOperationException("no source mesh in " + asset);
                File.WriteAllBytes(Path.Combine(outDir, Safe(name) + ".glb"), Glb.Write(name, md, Materials(mesh, md)));
                materialPaths = [.. MaterialPaths(mesh)];
                triangles = md.Triangles.Length / 3;
            }

            var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string t in j.TryGetProperty("textures", out JsonElement te) ? te.EnumerateArray().Select(e => e.GetString()!) : [])
            {
                try
                {
                    if (Load<UTexture2D>(provider, t) is { } tex && Textures.Save(tex, Path.Combine(outDir, Safe(t.Split('.')[^1]))) is { } file)
                    {
                        files[t.Split('.')[0]] = file;
                    }
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine($"{t}: {e.Message}");
                }
            }

            int textures = files.Count;
            var wiring = new Dictionary<string, Dictionary<string, string>>();
            foreach (var (matName, matPath) in materialPaths.Where(m => m.Item2.Length > 0))
            {
                try
                {
                    if (!wiring.ContainsKey(matName) && MaterialWiring.Roles(provider, matPath, files) is { Count: > 0 } roles)
                    {
                        wiring[matName] = roles;
                    }
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine($"{matPath}: {e.Message}");
                }
            }

            if (wiring.Count > 0)
            {
                File.WriteAllText(Path.Combine(outDir, "materials.json"), JsonSerializer.Serialize(wiring));
            }

            File.WriteAllText(Path.Combine(outDir, ".done"), cls);
            Console.WriteLine($"ASSETSHELF EXPORT {asset} glb=True textures={textures} wired={wiring.Count} triangles={triangles}");
            return 0;
        }
        catch (NotSupportedException e)
        {
            Console.Error.WriteLine($"{asset}: {e.Message}");
            return 3;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"{asset}: {e.GetType().Name}: {e.Message}");
            if (Environment.GetEnvironmentVariable("SHELF_DEBUG") is not null) Console.Error.WriteLine(e.StackTrace);
            return 1;
        }
        finally
        {
            if (Directory.Exists(outDir + ".src"))
            {
                Directory.Delete(outDir + ".src", true);
            }
        }
    }

    /// <summary>/Game/A/B (or /Game/A/B.B) as a file under root, checked to stay under it (names come from the job).</summary>
    static string Local(string root, string package, string ext)
    {
        string rel = package.Split('.')[0];
        if (!rel.StartsWith("/Game/", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("not a /Game/ package: " + package);
        }

        string full = Path.GetFullPath(Path.Combine(root, rel["/Game/".Length..] + ext));
        return full.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? full : throw new InvalidOperationException("unsafe path: " + package);
    }

    internal static T? Load<T>(DefaultFileProvider provider, string objectPath) where T : UObject
    {
        string key = provider.Files.Keys.FirstOrDefault(k => k.EndsWith("Content/" + objectPath.Split('.')[0]["/Game/".Length..] + ".uasset", StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException(objectPath);
        return provider.LoadPackage(key).GetExports().OfType<T>().FirstOrDefault();
    }

    static FMeshDescription? ReadMesh(DefaultFileProvider provider, string asset, UStaticMesh mesh)
    {
        foreach (FMeshDescriptionBulkData b in mesh.SourceMeshDescriptions)
        {
            if (b.ReadMeshDescription() is { } md)
            {
                return md;
            }
        }

        // UE 5: LOD 0's mesh is its own object (StaticMeshDescriptionBulkData); HiResMeshDescription is the Nanite source
        string key = provider.Files.Keys.First(k => k.EndsWith("Content/" + asset.Split('.')[0]["/Game/".Length..] + ".uasset", StringComparison.OrdinalIgnoreCase));
        return provider.LoadPackage(key).GetExports().OfType<UMeshDescriptionBaseBulkData>()
            .OrderBy(o => o.Name == "HiResMeshDescription" ? 1 : 0)
            .Select(o => o.BulkData?.ReadMeshDescription()).FirstOrDefault(m => m is not null);
    }

    /// <summary>The material asset's name for each polygon group: by imported slot name, else in order.</summary>
    static string[] Materials(UStaticMesh mesh, FMeshDescription md)
    {
        var slots = mesh.TryGetValue(out FStructFallback[] sm, "StaticMaterials") ? sm : [];
        string Name(FStructFallback s) => s.TryGetValue(out FPackageIndex m, "MaterialInterface") && m.ResolvedObject is { } o ? o.Name.Text : "Material";
        return md.GroupMaterialSlots.Select((slot, i) =>
            slots.FirstOrDefault(s => s.TryGetValue(out FName n, "ImportedMaterialSlotName") && n.Text == slot) is { } byName ? Name(byName)
            : i < slots.Length ? Name(slots[i]) : slot).ToArray();
    }

    /// <summary>The mesh's materials: (asset name, object path), in slot order.</summary>
    static IEnumerable<(string, string)> MaterialPaths(UStaticMesh mesh)
    {
        foreach (FStructFallback slot in mesh.TryGetValue(out FStructFallback[] sm, "StaticMaterials") ? sm : [])
        {
            if (slot.TryGetValue(out FPackageIndex m, "MaterialInterface") && m.ResolvedObject is { } o)
            {
                yield return (o.Name.Text, GamePath(o.GetPathName()));
            }
        }
    }

    /// <summary>An object path as /Game/...: CUE4Parse names local copies after their folder (x.src/Content/...).</summary>
    internal static string GamePath(string path) =>
        path.IndexOf("/Content/", StringComparison.Ordinal) is var i and >= 0 ? "/Game/" + path[(i + "/Content/".Length)..] : path;

    internal static string Safe(string s) => string.Concat(s.Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_'));
}

/// <summary>A binary glTF: one node, one mesh, a primitive per polygon group, one material per material name. Unreal's
/// left-handed Z-up centimetres become glTF's right-handed Y-up metres by swapping Y and Z (winding kept: checked in
/// Blender, faces and normals agree).</summary>
static class Glb
{
    public static byte[] Write(string name, FMeshDescription md, string[] groupMaterials)
    {
        var bin = new MemoryStream();
        var views = new JsonArray();
        var accessors = new JsonArray();
        int View(ReadOnlySpan<byte> data, int target)
        {
            while (bin.Length % 4 != 0) bin.WriteByte(0);
            views.Add(new JsonObject { ["buffer"] = 0, ["byteOffset"] = bin.Length, ["byteLength"] = data.Length, ["target"] = target });
            bin.Write(data);
            return views.Count - 1;
        }

        int Accessor(float[] values, int components, bool bounds)
        {
            var a = new JsonObject
            {
                ["bufferView"] = View(System.Runtime.InteropServices.MemoryMarshal.AsBytes(values.AsSpan()), 34962),
                ["componentType"] = 5126, ["count"] = values.Length / components, ["type"] = components switch { 2 => "VEC2", 3 => "VEC3", _ => "VEC4" },
            };
            if (bounds)
            {
                a["min"] = new JsonArray(Enumerable.Range(0, components).Select(c => (JsonNode)Stride(values, components, c).Min()).ToArray());
                a["max"] = new JsonArray(Enumerable.Range(0, components).Select(c => (JsonNode)Stride(values, components, c).Max()).ToArray());
            }

            accessors.Add(a);
            return accessors.Count - 1;
        }

        int vertexCount = md.Positions.Length / 3;
        var materials = new JsonArray();
        var materialIndex = new Dictionary<string, int>();
        var primitives = new JsonArray();
        int groups = Math.Max(md.GroupMaterialSlots.Length, md.TriangleGroups.DefaultIfEmpty(-1).Max() + 1);
        for (int g = 0; g < groups; g++)
        {
            var remap = new Dictionary<int, int>();
            var indices = new List<uint>();
            for (int t = 0; t < md.TriangleGroups.Length; t++)
            {
                if (md.TriangleGroups[t] != g) continue;
                for (int k = 0; k < 3; k++)
                {
                    int vi = md.Triangles[3 * t + k];
                    if (!remap.TryGetValue(vi, out int n)) remap[vi] = n = remap.Count;
                    indices.Add((uint) n);
                }
            }

            if (indices.Count == 0) continue;
            int count = remap.Count;
            var pos = new float[3 * count];
            var nrm = md.Normals.Length > 0 ? new float[3 * count] : null;
            var uvs = md.UVs.Select(_ => new float[2 * count]).ToList();
            foreach (var (vi, n) in remap)
            {
                int v = vi < md.VertexInstanceVertex.Length ? md.VertexInstanceVertex[vi] : 0;
                if (v < 0 || v >= vertexCount) v = 0;
                pos[3 * n] = md.Positions[3 * v] / 100f;
                pos[3 * n + 1] = md.Positions[3 * v + 2] / 100f;
                pos[3 * n + 2] = md.Positions[3 * v + 1] / 100f;
                if (nrm is not null && 3 * vi + 2 < md.Normals.Length)
                {
                    float x = md.Normals[3 * vi], y = md.Normals[3 * vi + 2], z = md.Normals[3 * vi + 1];
                    float len = MathF.Sqrt(x * x + y * y + z * z);
                    (nrm[3 * n], nrm[3 * n + 1], nrm[3 * n + 2]) = len > 1e-6f ? (x / len, y / len, z / len) : (0f, 1f, 0f);
                }

                for (int c = 0; c < uvs.Count; c++)
                {
                    if (2 * vi + 1 < md.UVs[c].Length)
                    {
                        uvs[c][2 * n] = md.UVs[c][2 * vi];
                        uvs[c][2 * n + 1] = md.UVs[c][2 * vi + 1];
                    }
                }
            }

            var attributes = new JsonObject { ["POSITION"] = Accessor(pos, 3, true) };
            if (nrm is not null) attributes["NORMAL"] = Accessor(nrm, 3, false);
            for (int c = 0; c < uvs.Count; c++) attributes[$"TEXCOORD_{c}"] = Accessor(uvs[c], 2, false);
            int idx = View(System.Runtime.InteropServices.MemoryMarshal.AsBytes(indices.ToArray().AsSpan()), 34963);
            accessors.Add(new JsonObject { ["bufferView"] = idx, ["componentType"] = 5125, ["count"] = indices.Count, ["type"] = "SCALAR" });
            string matName = g < groupMaterials.Length ? groupMaterials[g] : $"Material_{g}";
            if (!materialIndex.TryGetValue(matName, out int mi))
            {
                materials.Add(new JsonObject
                {
                    ["name"] = matName,
                    ["pbrMetallicRoughness"] = new JsonObject { ["metallicFactor"] = 0, ["roughnessFactor"] = 0.8 },
                });
                materialIndex[matName] = mi = materials.Count - 1;
            }

            primitives.Add(new JsonObject { ["attributes"] = attributes, ["indices"] = accessors.Count - 1, ["material"] = mi });
        }

        while (bin.Length % 4 != 0) bin.WriteByte(0);
        var gltf = new JsonObject
        {
            ["asset"] = new JsonObject { ["version"] = "2.0", ["generator"] = "Asset Shelf" },
            ["scene"] = 0,
            ["scenes"] = new JsonArray(new JsonObject { ["nodes"] = new JsonArray(0) }),
            ["nodes"] = new JsonArray(new JsonObject { ["name"] = name, ["mesh"] = 0 }),
            ["meshes"] = new JsonArray(new JsonObject { ["name"] = name, ["primitives"] = primitives }),
            ["materials"] = materials,
            ["buffers"] = new JsonArray(new JsonObject { ["byteLength"] = bin.Length }),
            ["bufferViews"] = views,
            ["accessors"] = accessors,
        };
        byte[] json = Encoding.UTF8.GetBytes(gltf.ToJsonString());
        int jsonLen = (json.Length + 3) & ~3;
        var glb = new MemoryStream();
        var w = new BinaryWriter(glb);
        w.Write(0x46546C67u); w.Write(2u); w.Write((uint) (12 + 8 + jsonLen + 8 + bin.Length));
        w.Write((uint) jsonLen); w.Write(0x4E4F534Au); w.Write(json); for (int i = json.Length; i < jsonLen; i++) w.Write((byte) ' ');
        w.Write((uint) bin.Length); w.Write(0x004E4942u); w.Write(bin.ToArray());
        return glb.ToArray();
    }

    static IEnumerable<float> Stride(float[] v, int n, int c)
    {
        for (int i = c; i < v.Length; i += n) yield return v[i];
    }
}

/// <summary>A texture's source image (the editor package keeps the original): PNG or JPEG as stored, raw formats as PNG.</summary>
static class Textures
{
    public static string? Save(UTexture2D tex, string pathNoExt)
    {
        byte[] data = tex.EditorData?.Payload?.Decompress() is { Length: > 0 } p ? p : tex.SourceArt?.Data ?? [];
        if (data.Length == 0 || !tex.TryGetValue(out FStructFallback source, "Source"))
        {
            Console.Error.WriteLine($"{tex.Name}: no source image (data {data.Length} bytes, art {tex.SourceArt is not null}, editor {tex.EditorData is not null})");
            return null;
        }

        int w = source.GetOrDefault("SizeX", 0), h = source.GetOrDefault("SizeY", 0);
        string format = source.TryGetValue(out FName f, "Format") ? f.Text.Split(':')[^1] : "";
        string compression = source.TryGetValue(out FName cf, "CompressionFormat") ? cf.Text.Split(':')[^1] : source.GetOrDefault("bPNGCompressed", false) ? "TSCF_PNG" : "TSCF_None";
        if (data.Length > 8 && data[0] == 0x89 && data[1] == 'P' && data[2] == 'N' && data[3] == 'G')
        {
            File.WriteAllBytes(pathNoExt + ".png", data);
            return Path.GetFileName(pathNoExt) + ".png";
        }

        if (data.Length > 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
        {
            File.WriteAllBytes(pathNoExt + ".jpg", data);
            return Path.GetFileName(pathNoExt) + ".jpg";
        }

        int bpp = format switch { "TSF_G8" => 1, "TSF_BGRA8" or "TSF_BGRE8" => 4, "TSF_G16" or "TSF_R16F" => 2, "TSF_RGBA16" or "TSF_RGBA16F" => 8, _ => 0 };
        if (compression == "TSCF_UEDELTA" && bpp > 0 && w > 0 && h > 0 && data.Length >= (long) w * h * bpp)
        {
            data = UnDelta(data, w, h, bpp, bpp is 2 or 8 ? 2 : 1);
            compression = "TSCF_None";
        }

        if (compression != "TSCF_None" || w <= 0 || h <= 0 || (long) w * h > 16384L * 16384)
        {
            Console.Error.WriteLine($"{tex.Name}: source {format}/{compression} not handled");
            return null;
        }

        // mip 0 of the first slice, as 8-bit RGBA
        var rgba = new byte[w * h * 4];
        int n = w * h;
        switch (format)
        {
            case "TSF_BGRA8" when data.Length >= 4 * n:
                for (int i = 0; i < n; i++) { rgba[4 * i] = data[4 * i + 2]; rgba[4 * i + 1] = data[4 * i + 1]; rgba[4 * i + 2] = data[4 * i]; rgba[4 * i + 3] = data[4 * i + 3]; }
                break;
            case "TSF_G8" when data.Length >= n:
                for (int i = 0; i < n; i++) { rgba[4 * i] = rgba[4 * i + 1] = rgba[4 * i + 2] = data[i]; rgba[4 * i + 3] = 255; }
                break;
            case "TSF_G16" when data.Length >= 2 * n:
                for (int i = 0; i < n; i++) { rgba[4 * i] = rgba[4 * i + 1] = rgba[4 * i + 2] = data[2 * i + 1]; rgba[4 * i + 3] = 255; }
                break;
            case "TSF_RGBA16" when data.Length >= 8 * n:
                for (int i = 0; i < 4 * n; i++) rgba[i] = data[2 * i + 1];
                break;
            case "TSF_RGBA16F" when data.Length >= 8 * n:
                for (int i = 0; i < 4 * n; i++) rgba[i] = (byte) Math.Clamp((float) BitConverter.ToHalf(data, 2 * i) * 255f + 0.5f, 0f, 255f);
                break;
            default:
                Console.Error.WriteLine($"{tex.Name}: source format {format} not handled");
                return null;
        }

        File.WriteAllBytes(pathNoExt + ".png", Png(rgba, w, h));
        return Path.GetFileName(pathNoExt) + ".png";
    }

    /// <summary>Undoes TSCF_UEDELTA on mip 0 (the engine's FImageCoreDelta, reversed): the image is cut into tiles (the
    /// cut sizes are part of the format), each tile's first row is stored as is and every other row as its difference
    /// from the row above, per 8- or 16-bit sample (16-bit with a 0x8080 bias).</summary>
    static byte[] UnDelta(byte[] data, int w, int h, int bpp, int sample)
    {
        var o = new byte[data.Length];
        long stride = (long) w * bpp;
        void Tile(int x0, int tw, int y0, int th)
        {
            long rowBytes = (long) tw * bpp;
            Buffer.BlockCopy(data, (int) (y0 * stride + (long) x0 * bpp), o, (int) (y0 * stride + (long) x0 * bpp), (int) rowBytes);
            for (int y = y0 + 1; y < y0 + th; y++)
            {
                long row = y * stride + (long) x0 * bpp, up = row - stride;
                if (sample == 1)
                {
                    for (long i = 0; i < rowBytes; i++) o[row + i] = (byte) (data[row + i] + o[up + i]);
                }
                else
                {
                    for (long i = 0; i + 1 < rowBytes; i += 2)
                    {
                        ushort v = (ushort) (BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan((int) (row + i))) + BinaryPrimitives.ReadUInt16LittleEndian(o.AsSpan((int) (up + i))) - 0x8080);
                        BinaryPrimitives.WriteUInt16LittleEndian(o.AsSpan((int) (row + i)), v);
                    }
                }
            }
        }

        static long Cuts(long n)
        {
            if (n <= 32768) return 1;
            long c = n / 32768;
            while (c > 512) c >>= 1;
            return c;
        }

        void Rows(int x0, int tw)
        {
            long perCut = (h + Cuts((long) tw * h) - 1) / Cuts((long) tw * h);
            for (long y = 0; y < h; y += perCut) Tile(x0, tw, (int) y, (int) Math.Min(perCut, h - y));
        }

        if ((long) w * h <= 136 * 136)
        {
            Tile(0, w, 0, h);
        }
        else if (stride <= 4096)
        {
            Rows(0, w);
        }
        else
        {
            long parts = (stride + 4095) / 4096;
            long partBytes = ((stride + parts / 2) / parts + 63) & ~63L;
            int partPx = (int) (partBytes / bpp);
            for (int x = 0; x < w; x += partPx) Rows(x, Math.Min(partPx, w - x));
        }

        return o;
    }

    static byte[] Png(byte[] rgba, int w, int h)
    {
        var raw = new byte[(w * 4 + 1) * h];
        for (int y = 0; y < h; y++) Buffer.BlockCopy(rgba, y * w * 4, raw, y * (w * 4 + 1) + 1, w * 4);
        var z = new MemoryStream();
        using (var zs = new ZLibStream(z, CompressionLevel.Fastest, true)) zs.Write(raw);
        var png = new MemoryStream();
        png.Write([0x89, (byte) 'P', (byte) 'N', (byte) 'G', 13, 10, 26, 10]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, w);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), h);
        ihdr[8] = 8; ihdr[9] = 6;
        Chunk(png, "IHDR", ihdr);
        Chunk(png, "IDAT", z.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    static void Chunk(Stream s, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);
        byte[] td = [.. Encoding.ASCII.GetBytes(type), .. data];
        s.Write(td);
        BinaryPrimitives.WriteUInt32BigEndian(len, Crc(td));
        s.Write(len);
    }

    static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint) n;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    static uint Crc(byte[] d)
    {
        uint c = 0xFFFFFFFFu;
        foreach (byte b in d) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}

sealed class StderrSink : Serilog.Core.ILogEventSink
{
    public void Emit(Serilog.Events.LogEvent e) => Console.Error.WriteLine($"{e.Level}: {e.RenderMessage()} {e.Exception?.GetType().Name}: {e.Exception?.Message} {(Environment.GetEnvironmentVariable("SHELF_DEBUG") == "2" ? e.Exception?.ToString() : "")}");
}
