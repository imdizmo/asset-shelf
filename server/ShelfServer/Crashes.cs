// Crash reports from the team's tools: one zip per crash (the crashed run's logs and a little about the machine), sent
// only after the person clicks Send. Kept in <store>/crashes/<tool>/<UTC time>-<8 hex of the sender>.zip with a .json
// beside it (who sent it, tool, version, os), the oldest dropped past 500 reports or 1 GB. Any team member sends; the
// owner and admins list, download and delete.
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ShelfServer;

public static partial class Crashes
{
    const long MaxReport = 20L << 20, MaxTotal = 1L << 30;
    const int MaxCount = 500;
    static readonly Lock Gate = new();

    [GeneratedRegex("^[a-z0-9-]{1,32}$")]
    private static partial Regex ToolRule();

    [GeneratedRegex("^[0-9A-Za-z.+-]{1,40}$")]
    private static partial Regex VersionRule();

    [GeneratedRegex("^[a-z0-9-]{1,32}/[0-9]{8}-[0-9]{6}(-[0-9]+)?-[A-Za-z0-9._@-]{1,100}\\.zip$")]
    private static partial Regex IdRule();

    static string Root => Path.Combine(Server.Store, "crashes");

    public static async Task Handle(HttpListenerRequest req, HttpListenerResponse res, string[] rest, Who who, string role)
    {
        bool manager = role is "owner" or "admin";
        switch (req.HttpMethod, rest)
        {
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
                    await Server.ErrorOut(res, 404, "No such crash report.");
                    break;
                }

                lock (Gate)
                {
                    File.Delete(doomed);
                    File.Delete(doomed + ".json");
                }

                Console.WriteLine($"{DateTime.UtcNow:O} {who.Login} deleted crash report {tool}/{file}");
                await Server.JsonOut(res, 200, new JsonObject { ["deleted"] = $"{tool}/{file}" });
                break;
            case ("DELETE", _) when !manager:
                await Server.ErrorOut(res, 403, "Only the team's owner or an admin can delete crash reports.");
                break;
            case ("GET", _):
                await Server.ErrorOut(res, manager ? 404 : 403, manager ? "No such crash report." : "Only the team's owner or an admin can read crash reports.");
                break;
            default:
                await Server.ErrorOut(res, 404, "There is nothing at this address.");
                break;
        }
    }

    static async Task Receive(HttpListenerRequest req, HttpListenerResponse res, Who who)
    {
        string tool = req.QueryString["tool"] ?? "", version = req.QueryString["version"] ?? "", os = req.QueryString["os"] ?? "";
        if (!ToolRule().IsMatch(tool) || !VersionRule().IsMatch(version) || os is not ("windows" or "linux" or "macos"))
        {
            await Server.ErrorOut(res, 400, "A crash report needs tool (a-z, 0-9, -), version and os (windows, linux or macos).");
            return;
        }

        if (req.ContentLength64 > MaxReport)
        {
            await Server.ErrorOut(res, 413, $"A crash report can be at most {MaxReport >> 20} MB.");
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
                        await Server.ErrorOut(res, 413, $"A crash report can be at most {MaxReport >> 20} MB.");
                        return;
                    }

                    await f.WriteAsync(buf.AsMemory(0, got));
                }
            }

            if ((req.ContentLength64 >= 0 && size != req.ContentLength64) || !Server.IsWholeZip(part))
            {
                await Server.ErrorOut(res, 400, "A crash report is one whole zip file.");
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
                File.WriteAllText(Path.Combine(dir, name + ".json"), new JsonObject
                {
                    ["tool"] = tool, ["version"] = version, ["os"] = os, ["login"] = who.Login, ["name"] = who.Name,
                    ["at"] = DateTime.UtcNow.ToString("O"), ["size"] = size,
                }.ToJsonString());
                id = $"{tool}/{name}";
                Trim();
            }

            Console.WriteLine($"{DateTime.UtcNow:O} crash report {id} ({size} bytes)");
            await Server.JsonOut(res, 201, new JsonObject { ["id"] = id });
        }
        finally
        {
            File.Delete(part);
        }
    }

    static IEnumerable<FileInfo> Reports() => Directory.Exists(Root)
        ? new DirectoryInfo(Root).EnumerateFiles("*.zip", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 1, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden })
        : [];

    /// <summary>Drops the oldest reports (and their .json) until at most MaxCount and MaxTotal bytes are kept.</summary>
    static void Trim()
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

    static JsonArray List() => new([.. Reports().OrderByDescending(f => f.Name, StringComparer.Ordinal).Select(f =>
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
