// Playtest logs from the team's games (format: one JSON line per event, the first a header; gzipped), sent only after
// the player said yes twice. Read once on upload: the header is checked and the events counted, and the facts are kept
// in a .json beside the file, so a listing never opens a log. Kept in
// <store>/playlogs/<game>/<received>-<player>-<content hash>.playlog.gz (the server's own clock and the file's hash, never
// a time the client wrote); the oldest received go past 2,000 logs or 2 GB. Members list and download; the owner, admins and whoever sent one delete it.
// The sender's login is kept beside the file for that, and never listed: the logs only name a random player id.
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ShelfServer;

public static partial class Playlogs
{
    const long MaxUpload = 20L << 20, MaxUnpacked = 200L << 20, MaxTotal = 2L << 30;
    const int MaxCount = 2000, MaxLine = 64 << 10;
    static readonly string[] Counted = ["death", "kill", "quest", "craft", "build", "gather", "respawn", "teleport"];
    static readonly Lock Gate = new();

    [GeneratedRegex("^[a-z0-9-]{1,32}$")]
    private static partial Regex GameRule();

    [GeneratedRegex("^[0-9A-Za-z.+-]{1,40}$")]
    private static partial Regex BuildRule();

    [GeneratedRegex("^[0-9a-f]{8}$")]
    private static partial Regex PlayerRule();

    [GeneratedRegex("^[a-z0-9-]{1,32}/[0-9]{8}-[0-9]{6}-[0-9a-f]{8}-[0-9a-f]{8}\\.playlog\\.gz$")]
    private static partial Regex IdRule();

    static string Root => Path.Combine(Server.Store, "playlogs");

    public static async Task Handle(Req req, Res res, string[] rest, Who who, string role)
    {
        switch (req.HttpMethod, rest)
        {
            case ("POST", []) when !Server.UploadType(req):
                await Server.ErrorOut(res, 415, "Send the log as application/gzip.");
                break;
            case ("POST", []):
                await Receive(req, res, who);
                break;
            case ("GET", []):
                await Server.JsonOut(res, 200, List(req.QueryString["game"]));
                break;
            case ("GET", [var game, var file]) when IdRule().IsMatch($"{game}/{file}"):
                string path = Path.Combine(Root, game, file);
                await Server.FileOut(req, res, File.Exists(path) ? path : null);
                break;
            case ("DELETE", [var game, var file]) when IdRule().IsMatch($"{game}/{file}"):
                await Delete(req, res, game, file, who, role);
                break;
            default:
                await Server.ErrorOut(res, 404, "There is nothing at this address.");
                break;
        }
    }

    static async Task Receive(Req req, Res res, Who who)
    {
        string game = req.QueryString["game"] ?? "", build = req.QueryString["build"] ?? "";
        if (!GameRule().IsMatch(game) || !BuildRule().IsMatch(build))
        {
            await Server.ErrorOut(res, 400, "A playtest log needs game (a-z, 0-9, -) and build.");
            return;
        }

        if (req.ContentLength64 > MaxUpload)
        {
            await Server.ErrorOut(res, 413, $"A playtest log can be at most {MaxUpload >> 20} MB (gzipped).");
            return;
        }

        string dir = Path.Combine(Root, game);
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
                    if ((size += got) > MaxUpload)
                    {
                        await Server.ErrorOut(res, 413, $"A playtest log can be at most {MaxUpload >> 20} MB (gzipped).");
                        return;
                    }

                    await f.WriteAsync(buf.AsMemory(0, got));
                }
            }

            if (req.ContentLength64 >= 0 && size != req.ContentLength64 || Read(part, game) is not { } facts)
            {
                await Server.ErrorOut(res, 400, "Not a playlog: send one gzipped .playlog whose first line is its header, for this game.");
                return;
            }

            // the same bytes sent again (a retry) are kept once; anything else is a new log, named by the server
            string player = facts["player"]!.GetValue<string>();
            string hash;
            await using (FileStream check = File.OpenRead(part))
            {
                hash = Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(check))[..8];
            }

            string? same = Directory.EnumerateFiles(dir, $"*-{player}-{hash}.playlog.gz").Select(Path.GetFileName).FirstOrDefault();
            string file = same ?? $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{player}-{hash}.playlog.gz", id = $"{game}/{file}";
            facts["id"] = id;
            facts["build"] = build;
            facts["size"] = size;
            int status = 201;
            lock (Gate)
            {
                string dest = Path.Combine(dir, file);
                if (File.Exists(dest))
                {
                    status = 200;  // the same session sent again (a retry): kept once
                }
                else
                {
                    File.Move(part, dest);
                    var stored = (JsonObject)facts.DeepClone();
                    stored["sentBy"] = who.Login;  // for "delete what you sent"; never listed
                    File.WriteAllText(dest + ".json", stored.ToJsonString());
                    Trim();
                }
            }

            Console.WriteLine($"{DateTime.UtcNow:O} playlog {id} ({size} bytes){(status == 200 ? ", already kept" : "")}");
            await Server.JsonOut(res, status, status == 201 ? facts : Facts(Path.Combine(dir, file)) ?? facts);
        }
        finally
        {
            File.Delete(part);
        }
    }

    /// <summary>The facts of a gzipped playlog, read once: its header (which must say playlog 1 and this game, with an
    /// 8-hex player and a start time), the length (the last t), how it ended, and the known events counted. Null when it
    /// is not one. Unpacking stops counting past 200 MB; a line longer than 64 KB is skipped.</summary>
    static JsonObject? Read(string gz, string game)
    {
        try
        {
            using var file = File.OpenRead(gz);
            using var unzip = new GZipStream(file, CompressionMode.Decompress);
            using var bounded = new Bounded(unzip, MaxUnpacked);
            var lines = Lines(bounded).GetEnumerator();
            if (!lines.MoveNext() || JsonNode.Parse(lines.Current) is not JsonObject h
                || h["playlog"]?.GetValue<int>() != 1 || h["game"]?.GetValue<string>() != game
                || h["player"]?.GetValue<string>() is not { } player || !PlayerRule().IsMatch(player)
                || !DateTime.TryParse(h["started"]?.GetValue<string>(), out _))
            {
                return null;
            }

            var counts = new JsonObject();
            foreach (string c in Counted)
            {
                counts[c] = 0;
            }

            double length = 0;
            string? ended = null;
            while (lines.MoveNext())
            {
                try
                {
                    if (JsonNode.Parse(lines.Current) is not JsonObject e)
                    {
                        continue;
                    }

                    if (e["t"] is JsonValue tv && tv.TryGetValue(out double t))
                    {
                        length = Math.Max(length, t);
                    }

                    string? kind = e["e"] is JsonValue ev && ev.TryGetValue(out string? k) ? k : null;
                    if (kind is not null && counts.ContainsKey(kind))
                    {
                        counts[kind] = counts[kind]!.GetValue<int>() + 1;
                    }
                    else if (kind == "end")
                    {
                        ended = e["why"] is JsonValue w && w.TryGetValue(out string? why) ? Clip(why) : "end";
                    }
                }
                catch (JsonException)
                {
                    // a torn last line (the game crashed while writing it), or a line from a newer writer
                }
            }

            JsonNode? map = h["map"] is JsonObject m
                ? new JsonObject { ["name"] = Clip(m["name"]), ["team"] = Clip(m["team"]), ["version"] = m["version"] is JsonValue v && v.TryGetValue(out int n) ? n : null }
                : null;
            return new JsonObject
            {
                ["game"] = game, ["map"] = map, ["player"] = player, ["started"] = h["started"]!.GetValue<string>(),
                ["length"] = Math.Round(length, 1), ["ended"] = ended, ["counts"] = counts, ["partial"] = bounded.Stopped,
            };
        }
        catch (Exception e) when (e is InvalidDataException or JsonException or InvalidOperationException or IOException or FormatException)
        {
            return null;
        }
    }

    static string? Clip(JsonNode? n) => n is JsonValue v && v.TryGetValue(out string? s) ? Clip(s) : null;

    static string Clip(string s) => new([.. s.Where(c => !char.IsControl(c)).Take(100)]);

    /// <summary>Lines of UTF-8 text; a line longer than MaxLine is dropped whole.</summary>
    static IEnumerable<string> Lines(Stream s)
    {
        var line = new MemoryStream();
        bool tooLong = false;
        int b;
        while ((b = s.ReadByte()) >= 0)
        {
            if (b == '\n')
            {
                if (!tooLong && line.Length > 0)
                {
                    yield return Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length);
                }

                line.SetLength(0);
                tooLong = false;
            }
            else if (!tooLong)
            {
                line.WriteByte((byte)b);
                tooLong = line.Length > MaxLine;
            }
        }

        if (!tooLong && line.Length > 0)
        {
            yield return Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length);
        }
    }

    /// <summary>A stream that ends (and says so) after <c>max</c> bytes: a small gzip can unpack into a great deal.</summary>
    sealed class Bounded(Stream inner, long max) : Stream
    {
        long _read;

        public bool Stopped { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_read >= max)
            {
                Stopped = true;
                return 0;
            }

            int got = inner.Read(buffer, offset, (int)Math.Min(count, max - _read));
            _read += got;
            return got;
        }

        public override int ReadByte()
        {
            var one = new byte[1];
            return Read(one, 0, 1) == 1 ? one[0] : -1;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    static IEnumerable<FileInfo> All() => Directory.Exists(Root)
        ? new DirectoryInfo(Root).EnumerateFiles("*.playlog.gz", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 1, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden })
        : [];

    /// <summary>The kept facts of a log, without who sent it.</summary>
    static JsonObject? Facts(string file)
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(file + ".json")) is JsonObject o)
            {
                o.Remove("sentBy");
                return o;
            }
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
        }

        return null;
    }

    static JsonArray List(string? game) => new([.. All()
        .Where(f => game is null || f.Directory!.Name == game)
        .OrderByDescending(f => f.Name, StringComparer.Ordinal)
        .Select(f => Facts(f.FullName)).OfType<JsonObject>().Select(o => (JsonNode)o)]);

    /// <summary>Drops the oldest logs (by when they arrived) until at most MaxCount and MaxTotal bytes are kept.</summary>
    static void Trim()
    {
        List<FileInfo> all = [.. All().OrderBy(f => f.Name, StringComparer.Ordinal)];
        long total = all.Sum(f => f.Length);
        for (int i = 0; i < all.Count && (all.Count - i > MaxCount || total > MaxTotal); i++)
        {
            total -= all[i].Length;
            all[i].Delete();
            File.Delete(all[i].FullName + ".json");
        }
    }

    static async Task Delete(Req req, Res res, string game, string file, Who who, string role)
    {
        string path = Path.Combine(Root, game, file);
        if (!File.Exists(path))
        {
            await Server.ErrorOut(res, 404, "No such playtest log.");
            return;
        }

        string? sentBy = null;
        try
        {
            sentBy = JsonNode.Parse(File.ReadAllText(path + ".json"))?["sentBy"]?.GetValue<string>();
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidOperationException)
        {
        }

        if (role is not ("owner" or "admin") && !string.Equals(sentBy, who.Login, StringComparison.OrdinalIgnoreCase))
        {
            await Server.ErrorOut(res, 403, "Only the team's owner, an admin, or whoever sent it can delete a playtest log.");
            return;
        }

        if (req.Headers["X-Jatify"] != "1")
        {
            await Server.ErrorOut(res, 400, "Changes need the header X-Jatify: 1.");
            return;
        }

        lock (Gate)
        {
            File.Delete(path);
            File.Delete(path + ".json");
        }

        Console.WriteLine($"{DateTime.UtcNow:O} {who.Login} deleted playlog {game}/{file}");
        await Server.JsonOut(res, 200, new JsonObject { ["deleted"] = $"{game}/{file}" });
    }
}
