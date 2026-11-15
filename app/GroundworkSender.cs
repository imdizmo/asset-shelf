using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AssetShelf;

/// <summary>Sends meshes into the open Groundwork map (Groundwork inbox v1): each asset becomes one self-contained .glb
/// (blender/to_glb.py), written with send.json into inbox/.&lt;id&gt;.partial/ and renamed to inbox/&lt;id&gt;/ when complete.
/// Groundwork answers in inbox/.results/&lt;id&gt;.json.</summary>
public static partial class GroundworkSender
{
    public const int MaxFiles = 200;

    public static string Inbox => Path.Combine(OperatingSystem.IsWindows()
        ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        : Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } x ? x
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share"), "Groundwork", "inbox");

    /// <summary>One asset to send: its files, its textures folder and whether it came out of Unreal.</summary>
    public sealed record Part(Item Item, IReadOnlyList<string> Files, string? TexDir, bool Unreal);

    /// <summary>Converts and delivers; returns the delivery id. <paramref name="step"/> gets "n of m" progress.</summary>
    public static async Task<string> DeliverAsync(IReadOnlyList<Part> parts, string blenderDir, Settings settings, Action<int>? step = null)
    {
        string id = $"assetshelf-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..4]}";
        string partial = Path.Combine(Inbox, $".{id}.partial");
        Directory.CreateDirectory(partial);
        try
        {
            var kinds = new List<object>();
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Part part in parts.Take(MaxFiles))
            {
                step?.Invoke(kinds.Count);
                string title = Title(part.Item), first = Model(part.Item), model = first;
                for (int n = 2; !used.Add(model); n++)
                {
                    model = $"{first}_{n}";
                }

                string glb = Path.Combine(partial, model + ".glb");
                await ToGlbAsync(part, glb, blenderDir, settings).ConfigureAwait(false);
                kinds.Add(new { file = model + ".glb", model, label = Label(title), category = Category(part.Item), provenance = JsonSerializer.Deserialize<JsonElement>(Provenance.Line(part.Item, part.Item.Pack.Provenance, "Groundwork")) });
            }

            await File.WriteAllTextAsync(Path.Combine(partial, "send.json"), JsonSerializer.Serialize(new { version = 1, source = "Asset Shelf", kinds })).ConfigureAwait(false);
            Directory.Move(partial, Path.Combine(Inbox, id));
            return id;
        }
        catch
        {
            Directory.Delete(partial, recursive: true);
            throw;
        }
    }

    /// <summary>Groundwork's answer, waited for up to <paramref name="wait"/>: "In Groundwork map X", its error, or the
    /// waiting note when it has not taken the delivery in yet.</summary>
    public static async Task<string> AnswerAsync(string id, TimeSpan wait)
    {
        string file = Path.Combine(Inbox, ".results", id + ".json");
        for (var sw = Stopwatch.StartNew(); sw.Elapsed < wait; await Task.Delay(250).ConfigureAwait(false))
        {
            if (!File.Exists(file))
            {
                continue;
            }

            try
            {
                using JsonDocument d = JsonDocument.Parse(await File.ReadAllTextAsync(file).ConfigureAwait(false));
                JsonElement r = d.RootElement;
                return r.TryGetProperty("ok", out JsonElement ok) && ok.ValueKind == JsonValueKind.True
                    ? $"In Groundwork map {(r.TryGetProperty("map", out JsonElement m) ? m.GetString() : "?")}"
                    : $"Groundwork refused it: {(r.TryGetProperty("error", out JsonElement e) ? e.GetString() : "no reason given")}";
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                // still being written: look again
            }
        }

        return "Waiting in Groundwork's inbox (open a map in Groundwork to take it in)";
    }

    /// <summary>Groundwork's category from the asset's name, kind and tags; meshes default to Props.</summary>
    public static string Category(Item item)
    {
        string words = string.Join(' ', [item.Name, .. item.Tags]).ToLowerInvariant();
        foreach (var (category, pattern) in Categories)
        {
            if (pattern.IsMatch(words))
            {
                return category;
            }
        }

        return item.Raw || item.Kind == "Mesh" ? "Props" : "Other";
    }

    // ponytail: word lists by eye; first match wins, so the more specific ones (bush before tree) come first
    private static readonly (string, Regex)[] Categories =
    [
        ("Bushes", new(@"bush|shrub|hedge", RegexOptions.Compiled)),
        ("Trees", new(@"tree|pine|oak|birch|spruce|conifer|willow|palm|trunk|stump", RegexOptions.Compiled)),
        ("Rocks", new(@"rock|stone|boulder|cliff|pebble", RegexOptions.Compiled)),
        ("Plants", new(@"plant|grass|flower|fern|weed|mushroom|ivy|reed|foliage", RegexOptions.Compiled)),
        ("Buildings", new(@"building|house|wall|tower|castle|ruin|hut|cabin|bridge|church|tavern|roof|gate|fence", RegexOptions.Compiled)),
        ("Markers", new(@"marker|sign|waypoint|flag", RegexOptions.Compiled)),
    ];

    /// <summary>The asset's human name: its own, or its pack's title when the name is only a content hash.</summary>
    public static string Title(Item item) => HashRx().IsMatch(item.Name) ? MainWindow.ShortTitle(item.Pack.Title) : item.Name;

    /// <summary>Groundwork's model (and file) name, the same on every send so a re-send replaces it: the plain name, or
    /// for a hash-named asset its title plus the hash's first 6 characters, so two such assets never share one.</summary>
    // ponytail: names that are not hashes go as they are; prefix the pack if two packs' meshes start to clash
    public static string Model(Item item) => HashRx().IsMatch(item.Name) ? $"{Plain(Title(item))}_{item.Name[..6].ToLowerInvariant()}" : Plain(item.Name);

    [GeneratedRegex(@"^[0-9a-fA-F]{16,}$")]
    private static partial Regex HashRx();

    /// <summary>The drawer's label: Unreal's type prefix (SM_, SK_, S_) dropped, underscores as spaces.</summary>
    public static string Label(string name) => LabelRx().Replace(name, "").Replace('_', ' ').Trim() is { Length: > 0 } l ? l : name;

    [GeneratedRegex(@"^(SM|SK|S)_", RegexOptions.IgnoreCase)]
    private static partial Regex LabelRx();

    /// <summary>A plain file name: letters, digits, '_', '-' and '.' only.</summary>
    private static string Plain(string s) => PlainRx().Replace(s, "_").Trim('_', '.') is { Length: > 0 } p ? p[..Math.Min(p.Length, 80)] : "asset";

    [GeneratedRegex(@"[^A-Za-z0-9_.-]+")]
    private static partial Regex PlainRx();

    private static async Task ToGlbAsync(Part part, string glb, string blenderDir, Settings settings)
    {
        string job = glb + ".job.json";
        await File.WriteAllTextAsync(job, JsonSerializer.Serialize(new { files = part.Files, textures = part.TexDir, unreal = part.Unreal, @out = glb })).ConfigureAwait(false);
        try
        {
            using var t = Log.Time("groundwork", $"{part.Item.Name}: to .glb");
            ProcessStartInfo psi = Run.Capped("6G", false, Run.Blender(settings), "-b", "--factory-startup", "--disable-autoexec", "-P", Path.Combine(blenderDir, "to_glb.py"), "--", job);
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            using Process p = Process.Start(psi)!;
            Task<string> err = p.StandardError.ReadToEndAsync();
            string output = await p.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            await p.WaitForExitAsync().ConfigureAwait(false);
            if (!output.Contains("ASSETSHELF_GLB", StringComparison.Ordinal) || !File.Exists(glb))
            {
                string why = (await err.ConfigureAwait(false)) + output;
                throw new InvalidOperationException($"{part.Item.Name}: the .glb export failed ({why[Math.Max(0, why.Length - 300)..].Trim()})");
            }

            if (new FileInfo(glb).Length > 512L << 20)
            {
                throw new InvalidOperationException($"{part.Item.Name}: the .glb is over Groundwork's 512 MB");
            }
        }
        finally
        {
            File.Delete(job);
        }
    }
}
