using System.Text.Json;
using System.Text.RegularExpressions;

namespace AssetShelf;

/// <summary>One pack: a folder <c>packs/&lt;id&gt;</c> with its files and <c>index/&lt;id&gt;</c> with its index.</summary>
public sealed class Pack
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public string Category { get; init; } = "";
    public string Licence { get; init; } = "";
    public string Url { get; init; } = "";
    public string Seller { get; init; } = "";
    public string Description { get; init; } = "";
    public string Acquired { get; init; } = "";
    public string? Cover { get; init; }
    public IReadOnlyList<string> Gallery { get; init; } = [];
    public bool Indexed { get; set; }

    /// <summary>The newest Unreal version that saved any of the pack's files (ShelfIndex), or null.</summary>
    public string? Engine { get; set; }
    public int AssetCount { get; set; }
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>True for "Team maps" and "Team props": what teammates published to the team server, not a store pack.</summary>
    public bool Published { get; init; }
    public required string FilesDir { get; init; }
    public required string IndexDir { get; init; }

    /// <summary>Where the pack came from and its licence (index/&lt;pack&gt;/provenance.json); null when not recorded yet.</summary>
    public Provenance? Provenance { get; set; }
    public Dictionary<string, Item> ByPackage { get; } = new(StringComparer.Ordinal);
}

/// <summary>One asset in the grid.</summary>
public sealed class Item
{
    public required Pack Pack { get; init; }
    public required string Path { get; init; }
    public required string Package { get; init; }
    public required string Name { get; init; }
    public required string Class { get; init; }
    public required string Kind { get; init; }
    public bool Raw { get; init; }
    public string? Thumb { get; init; }
    public IReadOnlyDictionary<string, string> Info { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<string> Deps { get; init; } = [];
    public IReadOnlyList<string> Refs { get; init; } = [];
    public HashSet<string> Tags { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string SearchText { get; set; } = "";

    /// <summary>A map or prop on the team server (see <see cref="Team"/>); null for a pack's asset.</summary>
    public Team.Published? Published { get; init; }

    /// <summary>The same asset in other packs (same kind, name, triangles, vertices and size); empty for most.</summary>
    public List<Item> Twins { get; } = [];
}

/// <summary>Loads every pack's index and works out what an asset brings along.</summary>
public sealed partial class Library
{
    /// <summary>Kinds in grid order. The first ones are shown by default; materials, textures and the rest on demand.</summary>
    public static readonly string[] Kinds = ["Mesh", "Character", "Animation", "VFX", "Sound", "Blueprint", "Map", "Prop", "Material", "Texture", "Other"];
    public static readonly HashSet<string> DefaultKinds = ["Mesh", "Character", "Animation", "VFX", "Sound", "Blueprint", "Map", "Prop"];

    public List<Pack> Packs { get; } = [];
    public List<Item> Items { get; } = [];

    public static string KindOf(string cls) => cls switch
    {
        "StaticMesh" => "Mesh",
        "SkeletalMesh" => "Character",
        "AnimSequence" or "AnimMontage" or "AnimComposite" or "BlendSpace" or "BlendSpace1D" or "AimOffsetBlendSpace" or "AnimBlueprint" or "PoseAsset" => "Animation",
        "NiagaraSystem" or "ParticleSystem" => "VFX",
        "SoundWave" or "SoundCue" or "MetaSoundSource" or "DialogueWave" => "Sound",
        "Blueprint" or "WidgetBlueprint" => "Blueprint",
        "World" => "Map",
        "Material" or "MaterialInstanceConstant" or "MaterialFunction" or "MaterialParameterCollection" => "Material",
        "Texture2D" or "TextureCube" or "TextureRenderTarget2D" or "Texture2DArray" => "Texture",
        _ => "Other",
    };

    /// <summary>Reads <paramref name="indexRoot"/>/*/index.json; pack files are in <paramref name="packsRoot"/>/&lt;id&gt;.
    /// Packs are read several at a time (the index is usually on a network share, where each file costs a round trip);
    /// <paramref name="progress"/> gets (packs read, packs in all).</summary>
    public static Library Load(string packsRoot, string indexRoot, IProgress<(int Done, int Total)>? progress = null)
    {
        var lib = new Library();
        if (!Directory.Exists(indexRoot))
        {
            return lib;
        }

        var all = Log.Time("library", "read");
        string[] dirs = [.. Directory.GetDirectories(indexRoot).Order(StringComparer.Ordinal)];
        Log.Write("library", $"{dirs.Length} pack folders in {indexRoot}", all.Ms);
        var read = new (Pack Pack, List<Item> Items)?[dirs.Length];
        int done = 0;
        progress?.Report((0, dirs.Length));
        Parallel.For(0, dirs.Length, new ParallelOptions { MaxDegreeOfParallelism = 12 }, i =>
        {
            var t = System.Diagnostics.Stopwatch.StartNew();
            read[i] = ReadDir(dirs[i], packsRoot);
            if (t.ElapsedMilliseconds >= 500)
            {
                Log.Write("library", $"slow pack: {System.IO.Path.GetFileName(dirs[i])} ({read[i]?.Item2.Count ?? 0} assets)", t.Elapsed.TotalMilliseconds);
            }
            progress?.Report((Interlocked.Increment(ref done), dirs.Length));
        });
        foreach (var r in read)
        {
            if (r is { } x)
            {
                lib.Packs.Add(x.Pack);
                lib.Items.AddRange(x.Items);
            }
        }

        int twins = MarkTwins(lib.Items);
        all.Then($"{lib.Packs.Count} packs, {lib.Items.Count:N0} assets read ({lib.Packs.Count(p => p.Indexed)} indexed), {twins:N0} in other packs too");
        all.Dispose();
        return lib;
    }

    private static (Pack, List<Item>)? ReadDir(string dir, string packsRoot)
    {
        string file = System.IO.Path.Combine(dir, "index.json");
        bool indexed = File.Exists(file);
        if (!indexed && !File.Exists(System.IO.Path.Combine(dir, "pack.json")))
        {
            return null;
        }

        string id = System.IO.Path.GetFileName(dir);
        Pack pack = ReadPack(dir, id, System.IO.Path.Combine(packsRoot, id));
        pack.Provenance = Provenance.Load(dir);
        pack.Indexed = indexed;
        var items = new List<Item>();
        if (!indexed)
        {
            return (pack, items);  // a pack known from its pack.json only: shown in the Packs view, no assets yet
        }

        using JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(file));
        bool raw = doc.RootElement.TryGetProperty("source", out JsonElement src) && src.GetString() == "raw";
        // only ShelfIndex records the pack's own version; the Unreal indexer wrote the version of the engine it ran in
        if (doc.RootElement.TryGetProperty("reader", out JsonElement rd) && rd.GetString() == "ShelfIndex" && doc.RootElement.TryGetProperty("engine", out JsonElement en))
        {
            pack.Engine = en.GetString();
        }
        foreach (JsonElement a in doc.RootElement.GetProperty("assets").EnumerateArray())
        {
            if (a.TryGetProperty("error", out _))
            {
                continue;
            }

            string cls = a.GetProperty("class").GetString()!;
            var item = new Item
            {
                Pack = pack,
                Path = a.GetProperty("path").GetString()!,
                Package = a.GetProperty("package").GetString()!,
                Name = a.GetProperty("name").GetString()!,
                Class = cls,
                Kind = KindOf(cls),
                Raw = raw,
                Thumb = a.TryGetProperty("thumb", out JsonElement t) ? System.IO.Path.Combine(dir, t.GetString()!) : null,
                Info = a.GetProperty("tags").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? ""),
                Deps = Strings(a, "deps"),
                Refs = Strings(a, "refs"),
            };
            AddTags(item);
            pack.ByPackage[item.Package] = item;
            pack.AssetCount++;
            items.Add(item);
        }

        return (pack, items);
    }

    /// <summary>Links meshes that several packs ship (the same kind, name, triangle and vertex counts and size) and tags
    /// them "duplicate". Returns how many assets have a twin.</summary>
    // ponytail: matched on the asset-registry numbers; file contents differ anyway (each pack saves its own path inside)
    private static int MarkTwins(List<Item> items)
    {
        int n = 0;
        foreach (var g in items.Where(i => i.Kind is "Mesh" or "Character" && i.Info.ContainsKey("Triangles"))
                     .GroupBy(i => (i.Class, i.Name.ToLowerInvariant(), i.Info["Triangles"], i.Info.GetValueOrDefault("Vertices"), i.Info.GetValueOrDefault("ApproxSize"))))
        {
            List<Item> same = [.. g];
            if (same.Select(i => i.Pack).Distinct().Count() < 2)
            {
                continue;
            }

            foreach (Item i in same)
            {
                i.Twins.AddRange(same.Where(o => o.Pack != i.Pack));
                i.Tags.Add("duplicate");
                i.SearchText += " duplicate";
                n++;
            }
        }

        return n;
    }

    private static Pack ReadPack(string indexDir, string id, string filesDir)
    {
        string meta = System.IO.Path.Combine(indexDir, "pack.json");
        if (!File.Exists(meta))
        {
            return new Pack { Id = id, Title = id, FilesDir = filesDir, IndexDir = indexDir };
        }

        using JsonDocument d = JsonDocument.Parse(File.ReadAllBytes(meta));
        JsonElement r = d.RootElement;
        string S(string k) => r.TryGetProperty(k, out JsonElement v) ? v.GetString() ?? "" : "";
        return new Pack
        {
            Id = id,
            Title = S("title") is { Length: > 0 } title ? title : id,
            Category = S("category"),
            Licence = S("licence"),
            Url = S("url"),
            Seller = S("seller"),
            Description = S("description"),
            Acquired = S("acquired"),
            Cover = S("cover") is { Length: > 0 } c ? System.IO.Path.Combine(indexDir, c) : null,
            Gallery = r.TryGetProperty("gallery", out JsonElement gl) && gl.ValueKind == JsonValueKind.Array
                ? gl.EnumerateArray().Select(x => System.IO.Path.Combine(indexDir, x.GetString()!)).ToList() : [],
            Tags = r.TryGetProperty("tags", out JsonElement tg) ? tg.EnumerateArray().Select(x => x.GetString()!).ToList() : [],
            FilesDir = filesDir,
            IndexDir = indexDir,
        };
    }

    private static List<string> Strings(JsonElement a, string key) =>
        a.TryGetProperty(key, out JsonElement v) ? v.EnumerateArray().Select(x => x.GetString()!).ToList() : [];

    private static readonly HashSet<string> Noise = new(StringComparer.OrdinalIgnoreCase)
    {
        "game", "content", "meshes", "mesh", "textures", "texture", "materials", "material", "instances", "maps", "map", "blueprints",
        "animations", "anims", "anim", "fbx", "source", "raw", "the", "and", "for", "lod", "inst", "master", "static", "skeletal",
    };

    // ponytail: words from names and folders by CamelCase/underscore splitting; a curated synonym list if search misses
    private static void AddTags(Item item)
    {
        foreach (string t in item.Pack.Tags)
        {
            item.Tags.Add(t);
        }

        foreach (string part in item.Pack.Category.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            item.Tags.Add(part);
        }

        item.Tags.Add(item.Kind);
        string folders = item.Raw ? System.IO.Path.GetDirectoryName(item.Path) ?? "" : item.Package[..Math.Max(0, item.Package.LastIndexOf('/'))];
        foreach (string w in Words(item.Name + " " + folders.Replace('/', ' ')))
        {
            item.Tags.Add(w);
        }

        item.SearchText = string.Join(' ', [item.Name, item.Pack.Title, item.Class, .. item.Tags]).ToLowerInvariant();
    }

    public static IEnumerable<string> Words(string s)
    {
        foreach (Match m in WordRx().Matches(s))
        {
            string w = m.Value.ToLowerInvariant();
            if (w.Length >= 3 && !Noise.Contains(w) && !w.All(char.IsDigit))
            {
                yield return w;
            }
        }
    }

    [GeneratedRegex("[A-Z]?[a-z]+|[A-Z]+(?![a-z])|[0-9]+")]
    private static partial Regex WordRx();

    /// <summary>Every package of the same pack that <paramref name="item"/> needs, directly or not (hard references).</summary>
    public static List<Item> Needs(Item item)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { item.Package };
        var order = new List<Item>();
        var todo = new Stack<string>(item.Deps);
        while (todo.Count > 0)
        {
            string p = todo.Pop();
            if (!seen.Add(p) || !item.Pack.ByPackage.TryGetValue(p, out Item? dep))
            {
                continue;
            }

            order.Add(dep);
            foreach (string d in dep.Deps)
            {
                todo.Push(d);
            }
        }

        return order;
    }

    /// <summary>Assets that work with <paramref name="item"/> without being needed by it, by group name.</summary>
    public static Dictionary<string, List<Item>> WorksWith(Item item)
    {
        var groups = new Dictionary<string, List<Item>>();
        string? skeleton = item.Class == "Skeleton" ? item.Package : SkeletonPackage(item);
        if (skeleton is not null && item.Kind is "Character" or "Other")
        {
            groups["Animations for this skeleton"] = item.Pack.ByPackage.Values
                .Where(i => i.Kind == "Animation" && SkeletonPackage(i) == skeleton).OrderBy(i => i.Name, StringComparer.Ordinal).ToList();
            groups["Other meshes on this skeleton"] = item.Pack.ByPackage.Values
                .Where(i => i.Kind == "Character" && i != item && SkeletonPackage(i) == skeleton).ToList();
        }

        groups["Blueprints using it"] = item.Refs.Select(r => item.Pack.ByPackage.GetValueOrDefault(r))
            .OfType<Item>().Where(i => i.Kind == "Blueprint").ToList();
        return groups.Where(g => g.Value.Count > 0).ToDictionary(g => g.Key, g => g.Value);
    }

    private static string? SkeletonPackage(Item i)
    {
        // registry value looks like /Script/Engine.Skeleton'/Game/X/SK_Y.SK_Y'
        if (!i.Info.TryGetValue("Skeleton", out string? s) || s.Length == 0)
        {
            return null;
        }

        int q = s.IndexOf('\'', StringComparison.Ordinal);
        string path = q >= 0 ? s[(q + 1)..].TrimEnd('\'') : s;
        int dot = path.LastIndexOf('.');
        return dot > 0 ? path[..dot] : path;
    }
}
