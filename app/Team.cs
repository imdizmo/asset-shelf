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

    public sealed record Me(string Login, string Name, string? Role);

    private sealed record Row(string Id, List<FileRow> Files);

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
        List<Row> rows = await GetJsonAsync<List<Row>>("/library");
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

        string rel = Path.GetRelativePath(Path.Combine(Mirror, "index"), path);
        if (rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel))
        {
            return false;
        }

        string[] parts = rel.Split(Path.DirectorySeparatorChar);
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
            foreach (PackFile f in all)
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
