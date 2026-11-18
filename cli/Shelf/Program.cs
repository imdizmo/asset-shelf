// shelf: Asset Shelf from the command line. Searches the library the app shows (its settings, or the team library when
// a team server is set), shows a pack or an asset, and pulls an asset into a project with its provenance and licence.
// Made for scripts and coding agents; skills/asset-shelf/SKILL.md tells an agent how to use it.
using System.Text.Json;

namespace AssetShelf;

public static class Cli
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static async Task<int> Main(string[] args)
    {
        try
        {
            return args switch
            {
                ["search", .. var rest] => await Search(rest),
                ["show", var id, .. var rest] => await Show(id, rest),
                ["pull", var id, .. var rest] => await Pull(id, rest),
                ["install-skill"] => InstallSkill(),
                _ => Usage(),
            };
        }
        catch (InvalidOperationException e)
        {
            Console.Error.WriteLine($"shelf: {e.Message}");
            return 1;
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine("""
            usage:
              shelf search <words...> [--kind Mesh,Character,...] [--pack <words>] [--licence <words>] [--limit 20] [--json]
              shelf show <pack id | asset id> [--json]
              shelf pull <asset id> --to <folder> [--format model|unreal|raw] [--force] [--json]
              shelf install-skill            (puts the asset-shelf skill in ~/.claude/skills)
            An asset id is <pack id>/<asset path>, as search prints it. Kinds: Mesh, Character, Animation, VFX, Sound,
            Blueprint, Map, Material, Texture, Other.
            """);
        return 2;
    }

    // ---- the library, as the app sees it ----

    private static Settings? _settings;

    private static async Task<Library> LoadAsync()
    {
        _settings = Settings.Load();
        string packs = _settings.Packs, index = _settings.Index;
        if (Team.Address(_settings) is { } address)
        {
            Team.Me me = await Team.WhoAmIAsync(address);
            if (me.Role is null)
            {
                throw new InvalidOperationException($"Tailscale says you are {me.Login}, not on the team at {address} yet: ask the owner to add you.");
            }

            Team.Join(address, me);
            (packs, index) = await Team.SyncIndexAsync();
        }

        if (!Directory.Exists(index))
        {
            throw new InvalidOperationException($"No library index at {index}: set Packs and Index in Asset Shelf first (or a team server).");
        }

        return Library.Load(packs, index);
    }

    private static string LibraryRoot => Path.GetDirectoryName(_settings!.Index.TrimEnd('/', '\\')) ?? _settings.Index;

    private static string Id(Item i) => $"{i.Pack.Id}/{i.Path}";

    private static Item? FindItem(Library lib, string id)
    {
        int slash = id.IndexOf('/');
        if (slash <= 0)
        {
            return null;
        }

        string pack = id[..slash], path = id[(slash + 1)..];
        return lib.Items.FirstOrDefault(i => i.Pack.Id == pack && i.Path == path);
    }

    private static string? Option(string[] args, string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;

    private static string[] Words(string? s) => s is null ? [] : [.. Library.Words(s).Select(w => w.ToLowerInvariant())];

    // ---- search ----

    private static async Task<int> Search(string[] args)
    {
        string[] flags = ["--kind", "--pack", "--licence", "--limit"];
        var free = args.Where((a, i) => !a.StartsWith("--", StringComparison.Ordinal) && (i == 0 || !flags.Contains(args[i - 1]))).ToList();
        string[] query = Words(string.Join(' ', free));
        HashSet<string>? kinds = Option(args, "--kind") is { } k ? new(k.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase) : null;
        string[] pack = Words(Option(args, "--pack")), licence = Words(Option(args, "--licence"));
        int limit = int.TryParse(Option(args, "--limit"), out int l) ? Math.Clamp(l, 1, 500) : 20;

        Library lib = await LoadAsync();
        var hits = lib.Items
            .Where(i => i.Published is null && (kinds is null ? Library.DefaultKinds.Contains(i.Kind) : kinds.Contains(i.Kind)))
            .Where(i => query.All(w => i.SearchText.Contains(w, StringComparison.Ordinal)))
            .Where(i => pack.All(w => (i.Pack.Title + " " + i.Pack.Id).Contains(w, StringComparison.OrdinalIgnoreCase)))
            .Where(i => licence.All(w => LicenceOf(i.Pack).Contains(w, StringComparison.OrdinalIgnoreCase)))
            // ponytail: words in the name count three times; no stemming or synonyms (search by meaning comes later)
            .Select(i => (Item: i, Score: query.Sum(w => i.Name.Contains(w, StringComparison.OrdinalIgnoreCase) ? 3 : 1)))
            .OrderByDescending(h => h.Score).ThenBy(h => h.Item.Name, StringComparer.OrdinalIgnoreCase)
            .Take(limit).Select(h => h.Item).ToList();

        if (args.Contains("--json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(hits.Select(i => new { id = Id(i), name = i.Name, kind = i.Kind, @class = i.Class, pack = i.Pack.Title, licence = LicenceOf(i.Pack), picture = i.Thumb }), Pretty));
            return 0;
        }

        if (hits.Count == 0)
        {
            Console.WriteLine("Nothing found: try fewer or broader words, or another --kind.");
        }

        foreach (Item i in hits)
        {
            Console.WriteLine($"{i.Name}  [{i.Kind}]  {i.Pack.Title}  ({LicenceOf(i.Pack)})\n  {Id(i)}");
        }

        return 0;
    }

    private static string LicenceOf(Pack p) =>
        p.Provenance is { Licence.Name.Length: > 0 } pr ? string.Join(" ", new[] { pr.Licence.Name, pr.Licence.Tier }.Where(x => x.Length > 0)) : p.Licence.Length > 0 ? p.Licence : "Unknown";

    // ---- show ----

    private static async Task<int> Show(string id, string[] args)
    {
        Library lib = await LoadAsync();
        bool json = args.Contains("--json");
        if (FindItem(lib, id) is { } item)
        {
            var needs = Library.Needs(item);
            Provenance? p = item.Pack.Provenance;
            var o = new
            {
                id = Id(item), name = item.Name, kind = item.Kind, @class = item.Class, raw = item.Raw, pack = item.Pack.Title, packId = item.Pack.Id,
                licence = LicenceOf(item.Pack), credit = p?.Licence.Credit ?? "", conditions = p?.Licence.Conditions ?? [],
                original = p?.Source.Originals.GetValueOrDefault(item.Path) ?? (Provenance.IsAggregator(p) ? "not recorded" : null),
                picture = item.Thumb, info = item.Info, needs = needs.Count, safety = Safety.Of(item.Pack)?.Level ?? "not scanned",
                formats = Formats(item),
            };
            Print(o, json);
            return 0;
        }

        if (lib.Packs.FirstOrDefault(pk => pk.Id == id) is { } pack)
        {
            Provenance? p = pack.Provenance;
            var o = new
            {
                id = pack.Id, title = pack.Title, licence = LicenceOf(pack), source = p?.Source.Url ?? pack.Url, creator = p?.Source.Creator ?? pack.Seller,
                aggregator = Provenance.IsAggregator(p), assets = pack.AssetCount, engine = pack.Engine, safety = Safety.Of(pack)?.Level ?? "not scanned",
                kinds = lib.Items.Where(i => i.Pack == pack).GroupBy(i => i.Kind).ToDictionary(g => g.Key, g => g.Count()),
                cover = pack.Cover, description = pack.Description,
            };
            Print(o, json);
            return 0;
        }

        throw new InvalidOperationException($"No pack or asset {id} (an asset id is <pack id>/<asset path>, as search prints it).");
    }

    private static string[] Formats(Item i) => i.Raw ? ["raw"] : i.Kind is "Mesh" or "Character" or "Animation" ? ["model", "unreal"] : ["unreal"];

    private static void Print(object o, bool json)
    {
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(o, Pretty));
            return;
        }

        foreach (var prop in o.GetType().GetProperties())
        {
            object? v = prop.GetValue(o);
            string text = v switch
            {
                null => "",
                string s => s,
                System.Collections.IDictionary d => string.Join(", ", d.Keys.Cast<object>().Select(k => $"{k} {d[k]}")),
                System.Collections.IEnumerable e => string.Join(", ", e.Cast<object>()),
                _ => v.ToString() ?? "",
            };
            if (text.Length > 0)
            {
                Console.WriteLine($"{prop.Name,-12} {text}");
            }
        }
    }

    // ---- pull ----

    private static async Task<int> Pull(string id, string[] args)
    {
        string to = Option(args, "--to") ?? throw new InvalidOperationException("pull needs --to <folder> (the project folder, or a folder in it).");
        Library lib = await LoadAsync();
        Item item = FindItem(lib, id) ?? throw new InvalidOperationException($"No asset {id}.");
        string format = Option(args, "--format") ?? Formats(item)[0];
        if (!Formats(item).Contains(format))
        {
            throw new InvalidOperationException($"{item.Name} ({item.Kind}) can be pulled as {string.Join(" or ", Formats(item))}, not {format}.");
        }

        if (Safety.Lines(item.Pack).Where(l => l.Blocking).Select(l => l.Text).ToList() is { Count: > 0 } risky && !args.Contains("--force"))
        {
            throw new InvalidOperationException($"{item.Pack.Title} holds something that can run code ({risky[0]}). Look at it in Asset Shelf; --force pulls anyway.");
        }

        to = Path.GetFullPath(to);
        Directory.CreateDirectory(to);
        var needs = Library.Needs(item);
        if (Team.Base is not null)
        {
            await Team.FetchAsync(needs.Prepend(item));
        }

        List<string> written = format switch
        {
            "raw" => CopyRaw(item, to),
            "unreal" => CopyUnreal(needs.Prepend(item), to),
            _ => await Export(item, to),
        };

        // where it came from and under which licence, next to it: a line per pull, and the licence text once per pack
        await File.AppendAllTextAsync(Path.Combine(to, "AssetShelf.provenance.jsonl"), Provenance.Line(item, item.Pack.Provenance, "shelf pull") + "\n");
        if (item.Pack.Provenance?.Licence.Copies.LastOrDefault(c => c.Kind == "licence") is { } copy && Provenance.CopyPath(copy, item.Pack.IndexDir, LibraryRoot) is { } licenceFile)
        {
            string dest = Path.Combine(to, "licences", $"{item.Pack.Id}.txt");
            if (!File.Exists(dest))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(licenceFile, dest);
                written.Add(dest);
            }
        }

        string credit = item.Pack.Provenance?.Licence.Credit ?? "";
        if (args.Contains("--json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(new { id = Id(item), format, files = written, licence = LicenceOf(item.Pack), credit, provenance = Path.Combine(to, "AssetShelf.provenance.jsonl") }, Pretty));
        }
        else
        {
            Console.WriteLine($"{item.Name}: {written.Count} files into {to} ({LicenceOf(item.Pack)}){(credit.Length > 0 ? $"\ncredit: {credit}" : "")}");
        }

        return 0;
    }

    /// <summary>A loose file (FBX, glTF, OBJ…) with the folder its textures are in, as the app hands Blender.</summary>
    private static List<string> CopyRaw(Item item, string to)
    {
        string raw = Path.Combine(item.Pack.FilesDir, "raw");
        string[] parts = item.Path.Split('/');
        string top = Path.Combine(raw, Path.Combine(parts[..Math.Min(2, parts.Length - 1)]));
        string dest = Path.Combine(to, item.Pack.Id);
        var written = new List<string>();
        foreach (string f in Directory.Exists(top) ? Directory.EnumerateFiles(top, "*", SearchOption.AllDirectories) : [Path.Combine(raw, item.Path)])
        {
            string d = Path.Combine(dest, Path.GetRelativePath(raw, f));
            if (File.Exists(d) || new FileInfo(f).LinkTarget is not null)
            {
                continue;  // never overwrite, never follow a link out of the pack
            }

            Directory.CreateDirectory(Path.GetDirectoryName(d)!);
            File.Copy(f, d);
            written.Add(d);
        }

        return written;
    }

    /// <summary>The asset and every package it needs into an Unreal project's Content, at the same paths (as Send to Unreal).</summary>
    private static List<string> CopyUnreal(IEnumerable<Item> items, string project)
    {
        if (!Directory.EnumerateFiles(project, "*.uproject").Any())
        {
            throw new InvalidOperationException($"{project} is not an Unreal project (no .uproject): --format unreal copies into a project's Content.");
        }

        Sender.UnrealPlan plan = Sender.PlanUnreal(items, project);
        Sender.ToUnreal(plan);
        return [.. plan.Copy.Select(c => c.Dst)];
    }

    /// <summary>A mesh, character or animation exported without Unreal (ShelfIndex: glTF, or FBX through Unreal when it
    /// must), with its textures and materials.json.</summary>
    private static async Task<List<string>> Export(Item item, string to)
    {
        var (dir, _) = await Sender.ExportAsync(item, _settings!, () =>
            throw new InvalidOperationException($"{item.Name} needs Unreal Engine to export: use Asset Shelf's Send buttons, or --format unreal into an Unreal project."));
        string dest = Path.Combine(to, item.Pack.Id, item.Name);
        var written = new List<string>();
        foreach (string f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Where(f => !Path.GetFileName(f).StartsWith('.')))
        {
            string d = Path.Combine(dest, Path.GetRelativePath(dir, f));
            if (File.Exists(d))
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(d)!);
            File.Copy(f, d);
            written.Add(d);
        }

        return written;
    }

    // ---- the skill ----

    private static int InstallSkill()
    {
        string dest = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "skills", "asset-shelf", "SKILL.md");
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        using Stream s = typeof(Cli).Assembly.GetManifestResourceStream("SKILL.md")!;
        using FileStream f = File.Create(dest);
        s.CopyTo(f);
        Console.WriteLine($"Skill written to {dest}");
        return 0;
    }
}
