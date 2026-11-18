using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AssetShelf;

/// <summary>The team's library service (server/ShelfServer, on the team's own server). Tailscale says who you are, so
/// there is nothing to sign in to. The library's index is mirrored into a local folder and read from there as usual;
/// preview pictures and pack files are fetched when first needed.</summary>
public static class Team
{
    public const int Port = 8781;

    public sealed record Me(string Login, string Name, string? Role, string Service = "", string Version = "");

    /// <summary>The oldest ShelfServer this app works fully with (0.2: pictures, versions and delete of maps and props).</summary>
    public static readonly Version MinServer = new(0, 2, 0);

    /// <summary>A sentence for a person when the team server is older than this app needs (or is not ShelfServer); null when fine.</summary>
    public static string? Outdated(Me me)
    {
        if (me.Service != "shelf")
        {
            return $"the team server answers as \"{me.Service}\", not Asset Shelf's library service: check the address and port";
        }

        return System.Version.TryParse(me.Version.Split('-', '+')[0], out Version? v) && v >= MinServer ? null
            : $"the team server runs ShelfServer {me.Version}, older than the {MinServer} this app needs: ask the owner to update it (some things, like deleting a map, will not work until then)";
    }

    public static string Size(long bytes) => bytes < 1 << 20 ? $"{Math.Max(1, bytes >> 10)} KB" : $"{bytes / 1048576.0:0.#} MB";

    private sealed record Row(string Id, List<FileRow> Files);

    public sealed record Person(string Login, string Name);

    /// <summary>One version of a map (Groundwork) or prop (Prop Generator) on the team server; Kind is "maps" or "props".</summary>
    public sealed record Published(string Id, string Name, int Version, long Size, string Sha256, Person PublishedBy, string PublishedAt)
    {
        public string Kind { get; init; } = "";
    }

    /// <summary>True when Tailscale's account may delete what was published (the team's owner or an admin).</summary>
    public static bool CanDelete => Who?.Role is "owner" or "admin";

    /// <summary>The newest version of every published map and prop.</summary>
    public static async Task<List<Published>> PublishedAsync()
    {
        var all = new List<Published>();
        foreach (string kind in (string[])["maps", "props"])
        {
            all.AddRange((await GetJsonAsync<List<Published>>($"/{kind}")).Where(p => Part(p.Id)).Select(p => p with { Kind = kind }));
        }

        return all;
    }

    /// <summary>Every version the server keeps of one map or prop, oldest first.</summary>
    public static async Task<List<Published>> VersionsAsync(Published p) =>
        [.. (await GetJsonAsync<System.Text.Json.Nodes.JsonObject>($"/{p.Kind}/{Esc(p.Id)}"))["versions"]!.AsArray()
            .Select(v => v.Deserialize<Published>(Json)! with { Kind = p.Kind })];

    /// <summary>Deletes one version, or (null) all of them. Owner and admin only; the server checks.</summary>
    public static async Task DeleteAsync(Published p, int? version)
    {
        using HttpResponseMessage r = await Http.DeleteAsync($"{Base}/{p.Kind}/{Esc(p.Id)}" + (version is { } v ? $"?version={v}" : ""));
        if (!r.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(await ErrorOf(r));
        }
    }

    /// <summary>Saves the zip into <paramref name="folder"/> as &lt;name&gt; v&lt;n&gt;.zip; returns its path.</summary>
    public static async Task<string> SaveAsync(Published p, string folder)
    {
        string safe = string.Concat(p.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is '/' or '\\' or ':' ? '_' : c)).Trim('.', ' ');
        string file = Path.Combine(folder, $"{(safe.Length > 0 ? safe : p.Id)} v{p.Version}.zip");
        await DownloadAsync($"/{p.Kind}/{Esc(p.Id)}/pack?version={p.Version}", file, null, default);
        return file;
    }


    private sealed record FileRow(string Name, long Size, long Mtime);

    private sealed record PackFile(string Path, long Size, long Mtime);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>http://host:port of the connected team server; null when offline.</summary>
    public static string? Base { get; private set; }

    public static Me? Who { get; private set; }

    /// <summary>The team address: the one saved in settings (empty there: offline by choice), else JATIFY_TEAM from the
    /// Launcher; null: offline.</summary>
    public static string? Address(Settings s) => (s.Team ?? Environment.GetEnvironmentVariable("JATIFY_TEAM"))?.Trim() is { Length: > 0 } a ? a : null;

    // ponytail: host or host:port; a bare IPv6 address would need [brackets], add when someone uses one
    private static string Url(string address) => "http://" + (address.Contains(':') ? address : $"{address}:{Port}");

    /// <summary>Asks the server who Tailscale says we are. Throws <see cref="InvalidOperationException"/> with a sentence
    /// for a person when it cannot be reached.</summary>
    public static async Task<Me> WhoAmIAsync(string address)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        try
        {
            using HttpResponseMessage r = await Http.GetAsync($"{Url(address)}/team/whoami", cts.Token);
            if (!r.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(await ErrorOf(r));
            }

            return await r.Content.ReadFromJsonAsync<Me>(Json, cts.Token) ?? throw new InvalidOperationException("The team server gave an empty answer.");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            throw new InvalidOperationException($"The team server at {address} cannot be reached ({(e is TaskCanceledException ? "no answer" : e.Message)}).");
        }
    }

    /// <summary>Sends a crash report (a zip) to the team server; returns its id there.</summary>
    public static Task<string> SendCrashAsync(string address, string tool, string version, string os, byte[] zip) => SendAsync("crashes", address, tool, version, os, zip);

    /// <summary>POST /team/suggestions: a zip of suggestion.txt and screenshots (contract ~/.cache/jatify-ui/SUGGESTIONS.md).</summary>
    public static Task<string> SendSuggestionAsync(string address, string tool, string version, string os, byte[] zip) => SendAsync("suggestions", address, tool, version, os, zip);

    private static async Task<string> SendAsync(string area, string address, string tool, string version, string os, byte[] zip)
    {
        using var body = new ByteArrayContent(zip);
        body.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/zip");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using HttpResponseMessage r = await Http.PostAsync($"{Url(address)}/team/{area}?tool={Esc(tool)}&version={Esc(version)}&os={Esc(os)}", body, cts.Token);
        if (!r.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(await ErrorOf(r));
        }

        return (await r.Content.ReadFromJsonAsync<System.Text.Json.Nodes.JsonObject>(Json, cts.Token))?["id"]?.GetValue<string>() ?? "";
    }

    /// <summary>Uses the team server from now on (until <see cref="Leave"/>).</summary>
    public static void Join(string address, Me me) => (Base, Who) = (Url(address), me);

    public static void Leave() => (Base, Who) = (null, null);

    /// <summary>~/.cache/asset-shelf/team/&lt;host&gt;: index/ and packs/ like a library on disk.</summary>
    public static string Mirror => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "asset-shelf", "team",
        new Uri(Base ?? throw new InvalidOperationException("not connected")).Host);

    /// <summary>Brings the mirror's index up to date: each pack's top files (index.json, pack.json, provenance.json,
    /// cover), fetched when their size or time changed; packs gone from the server are dropped. Returns (packs, index).</summary>
    public static async Task<(string Packs, string Index)> SyncIndexAsync(IProgress<(int Done, int Total)>? progress = null)
    {
        string index = Path.Combine(Mirror, "index"), packs = Path.Combine(Mirror, "packs");
        Directory.CreateDirectory(index);
        Directory.CreateDirectory(packs);
        // names come from the server: anything that is not a plain part is dropped, never written
        List<Row> rows = [.. (await GetJsonAsync<List<Row>>("/library")).Where(r => Part(r.Id)).Select(r => r with { Files = [.. r.Files.Where(f => Part(f.Name))] })];
        var ids = rows.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        foreach (string d in Directory.GetDirectories(index).Where(d => !ids.Contains(Path.GetFileName(d))))
        {
            Directory.Delete(d, true);
        }

        int done = 0;
        progress?.Report((0, rows.Count));
        await Parallel.ForEachAsync(rows, new ParallelOptions { MaxDegreeOfParallelism = 8 }, async (row, ct) =>
        {
            foreach (FileRow f in row.Files)
            {
                string local = Path.Combine(index, row.Id, f.Name);
                if (!Same(local, f.Size, f.Mtime))
                {
                    await DownloadAsync($"/library/{Esc(row.Id)}/index/{Esc(f.Name)}", local, f.Mtime, ct);
                }
            }

            progress?.Report((Interlocked.Increment(ref done), rows.Count));
        });
        Trim(Path.Combine(packs), 20L << 30);
        return (packs, index);
    }

    /// <summary>For a picture in the mirror's index: fetches it when it is not here yet. False when it cannot be had.</summary>
    public static async Task<bool> EnsureAsync(string path)
    {
        if (Base is null || File.Exists(path))
        {
            return File.Exists(path);
        }

        string pub = Path.GetRelativePath(Path.Combine(Mirror, "published"), path);
        if (pub.Split(Path.DirectorySeparatorChar) is [var kind and ("maps" or "props"), var id, var pic] && Part(id)
            && pic.StartsWith("picture-", StringComparison.Ordinal) && int.TryParse(pic[8..^4], out int version))
        {
            try
            {
                await DownloadAsync($"/{kind}/{Esc(id)}/picture?version={version}", path, null, default);
                return true;
            }
            catch (Exception e) when (e is HttpRequestException or IOException or InvalidOperationException)
            {
                return false;  // most maps have no picture
            }
        }

        string rel = Path.GetRelativePath(Path.Combine(Mirror, "index"), path);
        if (rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel))
        {
            return false;
        }

        string[] parts = rel.Split(Path.DirectorySeparatorChar);
        if (!parts.All(Part))
        {
            return false;
        }

        try
        {
            await DownloadAsync($"/library/{Esc(parts[0])}/index/{string.Join('/', parts[1..].Select(Esc))}", path, null, default);
            return true;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidOperationException)
        {
            Log.Write("team", $"{rel}: {e.Message}");
            return false;
        }
    }

    /// <summary>Before a send: fetches the files these assets need from their packs (each Unreal package's files, a raw
    /// asset's archive folder, and the pack's top files and Unreal Config/). Already-fetched files are kept.</summary>
    public static async Task FetchAsync(IEnumerable<Item> items, IProgress<string>? status = null)
    {
        if (Base is null)
        {
            return;
        }

        foreach (var byPack in items.GroupBy(i => i.Pack))
        {
            Pack pack = byPack.Key;
            List<PackFile> all = await GetJsonAsync<List<PackFile>>($"/library/{Esc(pack.Id)}/files");
            var want = new List<PackFile>();
            foreach (PackFile f in all.Where(f => f.Path.Split('/').All(Part)))
            {
                string p = f.Path;
                bool top = !p.Contains('/') || (p.StartsWith("unreal/", StringComparison.Ordinal) && (p.Count(c => c == '/') == 1 || p.StartsWith("unreal/Config/", StringComparison.Ordinal)));
                if (top || byPack.Any(i => Wants(i, p)))
                {
                    want.Add(f);
                }
            }

            var todo = want.Where(f => !Same(Path.Combine(pack.FilesDir, f.Path), f.Size, f.Mtime)).ToList();
            long total = Math.Max(1, todo.Sum(f => f.Size)), got = 0;
            foreach (PackFile f in todo)
            {
                status?.Report($"{pack.Title}: fetching {todo.Count} files from the team library ({got * 100 / total}%)…");
                await DownloadAsync($"/library/{Esc(pack.Id)}/files/{string.Join('/', f.Path.Split('/').Select(Esc))}", Path.Combine(pack.FilesDir, f.Path), f.Mtime, default);
                got += f.Size;
            }

            Log.Write("team", $"{pack.Id}: {todo.Count} of {want.Count} needed files fetched ({got >> 20} MB)");
        }
    }

    /// <summary>True when the pack file at <paramref name="rel"/> (packs/&lt;id&gt;/rel) belongs to <paramref name="i"/>.</summary>
    private static bool Wants(Item i, string rel)
    {
        if (i.Raw)
        {
            // the asset's archive folder (raw/<format>/<archive>/), where its textures are
            string[] parts = i.Path.Split('/');
            string dir = "raw/" + string.Join('/', parts[..Math.Min(2, parts.Length - 1)]) + "/";
            return rel.StartsWith(dir, StringComparison.Ordinal) || rel == "raw/" + i.Path;
        }

        string stem = "unreal/Content/" + (i.Package.StartsWith("/Game/", StringComparison.Ordinal) ? i.Package["/Game/".Length..] : i.Package.TrimStart('/'));
        return rel.StartsWith(stem + ".", StringComparison.Ordinal) && !rel[(stem.Length + 1)..].Contains('/');
    }

    /// <summary>A name the server gave that is safe as one part of a local path (not empty, ".", "..", hidden, rooted,
    /// or holding a separator).</summary>
    public static bool Part(string s) => s.Length is > 0 and <= 255 && s[0] != '.' && s.IndexOfAny(['/', '\\', ':', '\0']) < 0;

    private static bool Same(string local, long size, long mtime)
    {
        var fi = new FileInfo(local);
        return fi.Exists && fi.Length == size && Math.Abs(new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeMilliseconds() - mtime) < 2000;
    }

    private static string Esc(string s) => Uri.EscapeDataString(s);

    private static async Task<T> GetJsonAsync<T>(string path)
    {
        using HttpResponseMessage r = await Http.GetAsync(Base + path);
        if (!r.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(await ErrorOf(r));
        }

        return await r.Content.ReadFromJsonAsync<T>(Json) ?? throw new InvalidOperationException("The team server gave an empty answer.");
    }

    /// <summary>Streams <paramref name="path"/> to <paramref name="local"/> through a .part file, then gives it the server's time.</summary>
    private static async Task DownloadAsync(string path, string local, long? mtime, CancellationToken ct)
    {
        using HttpResponseMessage r = await Http.GetAsync(Base + path, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!r.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(await ErrorOf(r));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(local)!);
        string part = local + ".part";
        await using (FileStream f = File.Create(part))
        {
            await r.Content.CopyToAsync(f, ct);
        }

        File.Move(part, local, overwrite: true);
        if (mtime is { } t)
        {
            File.SetLastWriteTimeUtc(local, DateTimeOffset.FromUnixTimeMilliseconds(t).UtcDateTime);
        }
    }

    private static async Task<string> ErrorOf(HttpResponseMessage r)
    {
        try
        {
            using JsonDocument d = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
            if (d.RootElement.TryGetProperty("error", out JsonElement e) && e.GetString() is { Length: > 0 } s)
            {
                return s;
            }
        }
        catch (JsonException)
        {
        }

        return $"The team server answered {(int)r.StatusCode} {r.ReasonPhrase}.";
    }

    /// <summary>Keeps fetched pack files under <paramref name="budget"/> bytes: the ones used longest ago go first.</summary>
    private static void Trim(string dir, long budget)
    {
        var files = new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).OrderBy(f => f.LastAccessTimeUtc).ToList();
        long size = files.Sum(f => f.Length);
        foreach (FileInfo f in files)
        {
            if (size <= budget)
            {
                break;
            }

            size -= f.Length;
            f.Delete();
        }
    }
}

public sealed partial class Library
{
    /// <summary>Adds what teammates published as two packs, "Team maps" and "Team props".</summary>
    public void AddPublished(IReadOnlyList<Team.Published> published)
    {
        foreach (var (kind, title, itemKind, cls) in new[] { ("maps", "Team maps", "Map", "Groundwork map"), ("props", "Team props", "Prop", "Prop Generator prop") })
        {
            string dir = System.IO.Path.Combine(Team.Mirror, "published", kind);
            var pack = new Pack { Id = $"team-{kind}", Title = title, Licence = "Team", Published = true, Indexed = true, FilesDir = dir, IndexDir = dir, Tags = ["team"] };
            foreach (Team.Published p in published.Where(p => p.Kind == kind))
            {
                var item = new Item
                {
                    Pack = pack,
                    Path = p.Id,
                    Package = $"team:{kind}/{p.Id}",
                    Name = p.Name,
                    Class = cls,
                    Kind = itemKind,
                    Thumb = System.IO.Path.Combine(dir, p.Id, $"picture-{p.Version}.img"),
                    Published = p,
                };
                AddTags(item);
                item.Tags.Add(p.PublishedBy.Name);
                item.SearchText += $" {p.PublishedBy.Name} {p.PublishedBy.Login}".ToLowerInvariant();
                pack.ByPackage[item.Package] = item;
                pack.AssetCount++;
                Items.Add(item);
            }

            Packs.Add(pack);
        }
    }
}
