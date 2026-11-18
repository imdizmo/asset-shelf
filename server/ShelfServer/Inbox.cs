// What the team's tools send in, one zip each, only after the person clicks Send: crash reports (the crashed run's logs
// and a little about the machine) and suggestions (written text and screenshots, checked by Suggestion below). Kept in
// <store>/<area>/<tool>/<UTC time>-<8 hex of the sender>.zip with a .json beside it (who sent it, tool, version, os,
// and for a suggestion its text), the oldest dropped past 500 or 1 GB per area. Any team member sends; the owner and
// admins list, download and delete.
using System.IO.Compression;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ShelfServer;

/// <param name="area">The store folder and the address after /team/.</param>
/// <param name="noun">"crash report" or "suggestion", for the messages.</param>
/// <param name="check">Reads the stored zip: the fields it adds to the .json, or throws InvalidDataException with what is wrong.</param>
public sealed partial class Inbox(string area, string noun, Func<string, JsonObject>? check = null)
{
    public static readonly Inbox Crashes = new("crashes", "crash report");
    public static readonly Inbox Suggestions = new("suggestions", "suggestion", Suggestion.Check);

    const long MaxReport = 20L << 20, MaxTotal = 1L << 30;
    const int MaxCount = 500;
    readonly Lock Gate = new();

    [GeneratedRegex("^[a-z0-9-]{1,32}$")]
    private static partial Regex ToolRule();

    [GeneratedRegex("^[0-9A-Za-z.+-]{1,40}$")]
    private static partial Regex VersionRule();

    [GeneratedRegex("^[a-z0-9-]{1,32}/[0-9]{8}-[0-9]{6}(-[0-9]+)?-[A-Za-z0-9._@-]{1,100}\\.zip$")]
    private static partial Regex IdRule();

    string Root => Path.Combine(Server.Store, area);

    public async Task Handle(Req req, Res res, string[] rest, Who who, string role)
    {
        bool manager = role is "owner" or "admin";
        switch (req.HttpMethod, rest)
        {
            case ("POST", []) when !Server.UploadType(req):
                await Server.ErrorOut(res, 415, $"Send the {noun} as application/zip.");
                break;
            case ("POST", []):
                await Receive(req, res, who);
                break;
            case ("GET", []) when manager:
                await Server.JsonOut(res, 200, List());
                break;
            case ("GET", [var tool, var file]) when manager && IdRule().IsMatch($"{tool}/{file}"):
                string path = Path.Combine(Root, tool, file);
                await Server.FileOut(req, res, File.Exists(path) ? path : null);
                break;
            case ("DELETE", _) when manager && req.Headers["X-Jatify"] != "1":
                await Server.ErrorOut(res, 400, "Changes need the header X-Jatify: 1.");
                break;
            case ("DELETE", [var tool, var file]) when manager && IdRule().IsMatch($"{tool}/{file}"):
                string doomed = Path.Combine(Root, tool, file);
                if (!File.Exists(doomed))
                {
                    await Server.ErrorOut(res, 404, $"No such {noun}.");
                    break;
                }

                lock (Gate)
                {
                    File.Delete(doomed);
                    File.Delete(doomed + ".json");
                }

                Console.WriteLine($"{DateTime.UtcNow:O} {who.Login} deleted {noun} {tool}/{file}");
                await Server.JsonOut(res, 200, new JsonObject { ["deleted"] = $"{tool}/{file}" });
                break;
            case ("DELETE", _) when !manager:
                await Server.ErrorOut(res, 403, $"Only the team's owner or an admin can delete {noun}s.");
                break;
            case ("GET", _):
                await Server.ErrorOut(res, manager ? 404 : 403, manager ? $"No such {noun}." : $"Only the team's owner or an admin can read {noun}s.");
                break;
            default:
                await Server.ErrorOut(res, 404, "There is nothing at this address.");
                break;
        }
    }

    async Task Receive(Req req, Res res, Who who)
    {
        string tool = req.QueryString["tool"] ?? "", version = req.QueryString["version"] ?? "", os = req.QueryString["os"] ?? "";
        if (!ToolRule().IsMatch(tool) || !VersionRule().IsMatch(version) || os is not ("windows" or "linux" or "macos" or "web"))
        {
            await Server.ErrorOut(res, 400, $"A {noun} needs tool (a-z, 0-9, -), version and os (windows, linux, macos or web).");
            return;
        }

        if (req.ContentLength64 > MaxReport)
        {
            await Server.ErrorOut(res, 413, $"A {noun} can be at most {MaxReport >> 20} MB.");
            return;
        }

        string dir = Path.Combine(Root, tool);
        Directory.CreateDirectory(dir);
        string part = Path.Combine(dir, $".upload-{Guid.NewGuid():N}");
        try
        {
            long size = 0;
            await using (var f = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                byte[] buf = new byte[1 << 16];
                for (int got; (got = await req.InputStream.ReadAsync(buf)) > 0;)
                {
                    if ((size += got) > MaxReport)
                    {
                        await Server.ErrorOut(res, 413, $"A {noun} can be at most {MaxReport >> 20} MB.");
                        return;
                    }

                    await f.WriteAsync(buf.AsMemory(0, got));
                }
            }

            if ((req.ContentLength64 >= 0 && size != req.ContentLength64) || !Server.IsWholeZip(part))
            {
                await Server.ErrorOut(res, 400, $"A {noun} is one whole zip file.");
                return;
            }

            JsonObject extra;
            try
            {
                extra = check?.Invoke(part) ?? [];
            }
            catch (InvalidDataException e)
            {
                await Server.ErrorOut(res, 400, e.Message);
                return;
            }

            // the file name carries a short hash of the sender, not their login (an email address); the .json has the login
            string login = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(who.Login.ToLowerInvariant())))[..8];
            string id;
            lock (Gate)
            {
                string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture), name = $"{stamp}-{login}.zip";
                for (int n = 2; File.Exists(Path.Combine(dir, name)); n++)
                {
                    name = $"{stamp}-{n}-{login}.zip";
                }

                File.Move(part, Path.Combine(dir, name));
                var row = new JsonObject
                {
                    ["tool"] = tool, ["version"] = version, ["os"] = os, ["login"] = who.Login, ["name"] = who.Name,
                    ["at"] = DateTime.UtcNow.ToString("O"), ["size"] = size,
                };
                foreach ((string k, JsonNode? v) in extra.ToList())
                {
                    extra.Remove(k);
                    row[k] = v;
                }

                File.WriteAllText(Path.Combine(dir, name + ".json"), row.ToJsonString());
                id = $"{tool}/{name}";
                Trim();
            }

            Console.WriteLine($"{DateTime.UtcNow:O} {noun} {id} ({size} bytes)");
            await Server.JsonOut(res, 201, new JsonObject { ["id"] = id });
        }
        finally
        {
            File.Delete(part);
        }
    }

    IEnumerable<FileInfo> Reports() => Directory.Exists(Root)
        ? new DirectoryInfo(Root).EnumerateFiles("*.zip", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 1, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden })
        : [];

    /// <summary>Drops the oldest reports (and their .json) until at most MaxCount and MaxTotal bytes are kept.</summary>
    void Trim()
    {
        List<FileInfo> all = [.. Reports().OrderBy(f => f.Name[..Math.Min(f.Name.Length, 15)], StringComparer.Ordinal).ThenBy(f => f.LastWriteTimeUtc)];
        long total = all.Sum(f => f.Length);
        for (int i = 0; i < all.Count && (all.Count - i > MaxCount || total > MaxTotal); i++)
        {
            total -= all[i].Length;
            all[i].Delete();
            File.Delete(all[i].FullName + ".json");
        }
    }

    JsonArray List() => new([.. Reports().OrderByDescending(f => f.Name, StringComparer.Ordinal).Select(f =>
    {
        JsonObject row;
        try
        {
            row = JsonNode.Parse(File.ReadAllText(f.FullName + ".json")) as JsonObject ?? [];
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            row = [];
        }

        row["id"] = $"{f.Directory!.Name}/{f.Name}";
        row["size"] = f.Length;
        return (JsonNode)row;
    })]);
}

/// <summary>A suggestion's zip: suggestion.txt (UTF-8, the person's words, required) and at most 10 screenshots (.png,
/// .jpg or .jpeg, each at most 10 MB unpacked, checked by their first bytes), flat names, nothing else.</summary>
public static class Suggestion
{
    public const int MaxText = 20000, MaxShots = 10;
    const long MaxShot = 10L << 20;

    public static JsonObject Check(string zip)
    {
        using ZipArchive z = ZipFile.OpenRead(zip);
        string? text = null;
        var shots = new JsonArray();
        foreach (ZipArchiveEntry e in z.Entries)
        {
            if (e.FullName == "suggestion.txt")
            {
                if (text is not null || e.Length > MaxText * 4)
                {
                    throw new InvalidDataException($"suggestion.txt is there twice or longer than {MaxText} characters.");
                }

                using var r = new StreamReader(e.Open(), new System.Text.UTF8Encoding(false, throwOnInvalidBytes: true));
                try
                {
                    text = r.ReadToEnd().Trim();
                }
                catch (System.Text.DecoderFallbackException)
                {
                    throw new InvalidDataException("suggestion.txt is not UTF-8 text.");
                }

                if (text.Length is 0 or > MaxText)
                {
                    throw new InvalidDataException($"suggestion.txt must hold between 1 and {MaxText} characters.");
                }

                continue;
            }

            string lower = e.FullName.ToLowerInvariant();
            if (!Regex.IsMatch(e.FullName, "^[A-Za-z0-9._-]{1,80}$") || e.FullName.StartsWith('.') || !(lower.EndsWith(".png") || lower.EndsWith(".jpg") || lower.EndsWith(".jpeg")))
            {
                throw new InvalidDataException($"Only suggestion.txt and screenshots (.png, .jpg) may be in a suggestion, with plain names: not {Untrusted(e.FullName)}.");
            }

            if (shots.Count == MaxShots || e.Length > MaxShot)
            {
                throw new InvalidDataException($"A suggestion holds at most {MaxShots} screenshots of at most {MaxShot >> 20} MB each.");
            }

            byte[] head = new byte[4];
            using (Stream s = e.Open())
            {
                s.ReadAtLeast(head, 4, throwOnEndOfStream: false);
            }

            bool png = head is [0x89, (byte)'P', (byte)'N', (byte)'G'], jpg = head is [0xFF, 0xD8, 0xFF, _];
            if (!(lower.EndsWith(".png") ? png : jpg))
            {
                throw new InvalidDataException($"{e.FullName} is not a {(lower.EndsWith(".png") ? "PNG" : "JPEG")} picture.");
            }

            shots.Add(e.FullName);
        }

        return text is null ? throw new InvalidDataException("A suggestion needs suggestion.txt with the person's words.") : new JsonObject { ["text"] = text, ["screenshots"] = shots };
    }

    static string Untrusted(string s) => s.Length > 80 ? s[..80] + "…" : s;
}
