// Git LFS for a team's projects: the Batch API with the basic transfer, and File Locking. Objects live under
// <store>/lfs/<project>/objects/aa/bb/<oid>, written once and never changed. People are the Tailscale identities of
// Program.cs: observers download and see locks, members and up also upload and lock. The server never answers 401,
// so git-lfs never asks for a password.
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ShelfServer;

public static partial class Lfs
{
    const string Mime = "application/vnd.git-lfs+json";

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex OidRule();

    static readonly Lock LockGate = new();

    public static async Task Handle(Req req, Res res, string project, string[] rest, Who who, string role)
    {
        if (!Server.ValidId(project))
        {
            await Fail(res, 404, "No such project (a project name is 1 to 64 letters, digits, '-' or '_').");
            return;
        }

        bool write = role != "observer";
        string root = Path.Combine(Server.Store, "lfs", project);
        switch (req.HttpMethod, rest)
        {
            case ("POST", ["objects", "batch"]):
                await Batch(req, res, project, root, write);
                break;
            case ("GET" or "HEAD", ["objects", var oid]) when OidRule().IsMatch(oid):
                await Server.FileOut(req, res, File.Exists(ObjectPath(root, oid)) ? ObjectPath(root, oid) : null);
                break;
            case ("PUT", ["objects", var oid]) when OidRule().IsMatch(oid):
                if (!write)
                {
                    await Fail(res, 403, "Observers can download but not push: ask the owner for the member role.");
                    return;
                }

                await Upload(req, res, root, oid);
                break;
            case ("GET", ["locks"]):
                await ListLocks(req, res, root);
                break;
            case ("POST", ["locks", "verify"]):
                await VerifyLocks(res, root, who);
                break;
            case ("POST", ["locks"]) when write:
                await CreateLock(req, res, root, who);
                break;
            case ("POST", ["locks", var id, "unlock"]) when write:
                await Unlock(req, res, root, id, who, role);
                break;
            case ("POST", ["locks", ..]):
                await Fail(res, 403, "Observers cannot lock files: ask the owner for the member role.");
                break;
            default:
                await Fail(res, 404, "There is nothing at this address.");
                break;
        }
    }

    static string ObjectPath(string root, string oid) => Path.Combine(root, "objects", oid[..2], oid[2..4], oid);

    static Task Fail(Res res, int status, string message) =>
        Out(res, status, new JsonObject { ["message"] = message, ["error"] = message });

    static Task Out(Res res, int status, JsonNode body) => Server.JsonOut(res, status, body, Mime);

    static async Task<JsonNode?> Body(Req req)
    {
        // git-lfs's own content type (or plain JSON): a web page can only send text/plain or a form without a preflight
        string type = req.ContentType ?? "";
        if (!(type.StartsWith(Mime, StringComparison.OrdinalIgnoreCase) || type.StartsWith("application/json", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        return await Server.ReadJson(req, 16 << 20);
    }

    static async Task Batch(Req req, Res res, string project, string root, bool write)
    {
        JsonNode? body = await Body(req);
        string? op = body?["operation"]?.GetValue<string>();
        if (body?["objects"] is not JsonArray objects || op is not ("download" or "upload"))
        {
            await Fail(res, 422, "The batch request needs an operation (download or upload) and objects.");
            return;
        }

        if (op == "upload" && !write)
        {
            await Fail(res, 403, "Observers can download but not push: ask the owner for the member role.");
            return;
        }

        string bas = $"http://{req.Url!.Authority}/lfs/{project}/objects/";
        var answers = new JsonArray();
        foreach (JsonNode? o in objects)
        {
            string oid = o?["oid"]?.GetValue<string>() ?? "";
            long size = o?["size"]?.GetValue<long>() ?? -1;
            var a = new JsonObject { ["oid"] = oid, ["size"] = size };
            string file = OidRule().IsMatch(oid) ? ObjectPath(root, oid) : "";
            if (file.Length == 0 || size < 0)
            {
                a["error"] = new JsonObject { ["code"] = 422, ["message"] = "Not a valid object (a sha256 oid and a size)." };
            }
            else if (op == "download")
            {
                if (File.Exists(file))
                {
                    a["authenticated"] = true;
                    a["actions"] = new JsonObject { ["download"] = new JsonObject { ["href"] = bas + oid } };
                }
                else
                {
                    a["error"] = new JsonObject { ["code"] = 404, ["message"] = "The server does not have this object." };
                }
            }
            else if (size > Server.MaxUpload)
            {
                a["error"] = new JsonObject { ["code"] = 422, ["message"] = $"The object is larger than this server takes ({Server.MaxUpload >> 20} MB)." };
            }
            else if (!File.Exists(file))
            {
                // an object already stored gets no actions: git-lfs skips it
                a["authenticated"] = true;
                a["actions"] = new JsonObject { ["upload"] = new JsonObject { ["href"] = bas + oid } };
            }

            answers.Add((JsonNode)a);
        }

        await Out(res, 200, new JsonObject { ["transfer"] = "basic", ["objects"] = answers });
    }

    /// <summary>Streams the object to a hidden file; it becomes visible only when its size and sha256 are the oid's.</summary>
    static async Task Upload(Req req, Res res, string root, string oid)
    {
        string file = ObjectPath(root, oid);
        if (File.Exists(file))
        {
            await Server.JsonOut(res, 200, new JsonObject());
            return;
        }

        long free = new DriveInfo(Server.Store).AvailableFreeSpace - (1L << 30);
        if (req.ContentLength64 > Server.MaxUpload || req.ContentLength64 > free)
        {
            await Fail(res, 413, "The object is larger than this server takes, or the server is out of space.");
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        string part = Path.Combine(Path.GetDirectoryName(file)!, $".upload-{Guid.NewGuid():N}");
        try
        {
            long size = 0;
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var f = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                byte[] buf = new byte[1 << 16];
                for (int got; (got = await req.InputStream.ReadAsync(buf)) > 0;)
                {
                    size += got;
                    if (size > Server.MaxUpload || size > free)
                    {
                        await Fail(res, 413, "The object is larger than this server takes, or the server is out of space.");
                        return;
                    }

                    sha.AppendData(buf, 0, got);
                    await f.WriteAsync(buf.AsMemory(0, got));
                }
            }

            if (Convert.ToHexStringLower(sha.GetHashAndReset()) != oid || (req.ContentLength64 >= 0 && size != req.ContentLength64))
            {
                await Fail(res, 422, "The upload does not match its oid (cut short, or a different file).");
                return;
            }

            try
            {
                File.Move(part, file);
            }
            catch (IOException) when (File.Exists(file))
            {
                // someone else stored the same object meanwhile: same content
            }

            await Server.JsonOut(res, 200, new JsonObject());
        }
        finally
        {
            File.Delete(part);
        }
    }

    // ---- locks: <root>/locks.json, a list of { id, path, locked_at, owner: { name }, login } ----

    static JsonArray ReadLocks(string root)
    {
        string f = Path.Combine(root, "locks.json");
        return File.Exists(f) && JsonNode.Parse(File.ReadAllText(f)) is JsonArray a ? a : [];
    }

    static void WriteLocks(string root, JsonArray locks)
    {
        Directory.CreateDirectory(root);
        string tmp = Path.Combine(root, ".locks.json.tmp");
        File.WriteAllText(tmp, locks.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, Path.Combine(root, "locks.json"), overwrite: true);
    }

    static JsonObject Public(JsonNode l) => new() { ["id"] = l["id"]!.GetValue<string>(), ["path"] = l["path"]!.GetValue<string>(), ["locked_at"] = l["locked_at"]!.GetValue<string>(), ["owner"] = l["owner"]!.DeepClone() };

    static async Task ListLocks(Req req, Res res, string root)
    {
        string? path = req.QueryString["path"], id = req.QueryString["id"];
        JsonArray all;
        lock (LockGate)
        {
            all = ReadLocks(root);
        }

        // ponytail: one page with every lock; a team holds a handful, add cursors when it holds thousands
        var found = new JsonArray([.. all.Where(l => (path is null || l!["path"]!.GetValue<string>() == path) && (id is null || l!["id"]!.GetValue<string>() == id)).Select(l => (JsonNode)Public(l!))]);
        await Out(res, 200, new JsonObject { ["locks"] = found });
    }

    static async Task VerifyLocks(Res res, string root, Who who)
    {
        JsonArray all;
        lock (LockGate)
        {
            all = ReadLocks(root);
        }

        bool Ours(JsonNode? l) => string.Equals(l!["login"]!.GetValue<string>(), who.Login, StringComparison.OrdinalIgnoreCase);
        await Out(res, 200, new JsonObject
        {
            ["ours"] = new JsonArray([.. all.Where(Ours).Select(l => (JsonNode)Public(l!))]),
            ["theirs"] = new JsonArray([.. all.Where(l => !Ours(l)).Select(l => (JsonNode)Public(l!))]),
        });
    }

    static async Task CreateLock(Req req, Res res, string root, Who who)
    {
        string? path = (await Body(req))?["path"]?.GetValue<string>();
        if (path is not { Length: > 0 and <= 1024 })
        {
            await Fail(res, 422, "A lock needs the file's path.");
            return;
        }

        JsonNode? held;
        JsonObject made;
        lock (LockGate)
        {
            JsonArray all = ReadLocks(root);
            held = all.FirstOrDefault(l => l!["path"]!.GetValue<string>() == path);
            made = new JsonObject { ["id"] = Guid.NewGuid().ToString("N"), ["path"] = path, ["locked_at"] = DateTime.UtcNow.ToString("O"), ["owner"] = new JsonObject { ["name"] = who.Name }, ["login"] = who.Login };
            if (held is null)
            {
                all.Add((JsonNode)made);
                WriteLocks(root, all);
            }
        }

        if (held is not null)
        {
            await Out(res, 409, new JsonObject { ["lock"] = Public(held), ["message"] = $"{path} is already locked by {held["owner"]?["name"]}." });
            return;
        }

        await Out(res, 201, new JsonObject { ["lock"] = Public(made) });
    }

    static async Task Unlock(Req req, Res res, string root, string id, Who who, string role)
    {
        bool force = (await Body(req))?["force"]?.GetValue<bool>() == true;
        JsonNode? l;
        string? refused = null;
        lock (LockGate)
        {
            JsonArray all = ReadLocks(root);
            l = all.FirstOrDefault(x => x!["id"]!.GetValue<string>() == id);
            if (l is not null)
            {
                bool mine = string.Equals(l["login"]!.GetValue<string>(), who.Login, StringComparison.OrdinalIgnoreCase);
                if (!mine && !(force && role is "owner" or "admin"))
                {
                    refused = mine ? null : $"{l["path"]} is locked by {l["owner"]?["name"]}; only they, or an admin with --force, can unlock it.";
                }
                else
                {
                    all.Remove(l);
                    WriteLocks(root, all);
                }
            }
        }

        if (l is null)
        {
            await Fail(res, 404, "No such lock.");
        }
        else if (refused is not null)
        {
            await Fail(res, 403, refused);
        }
        else
        {
            await Out(res, 200, new JsonObject { ["lock"] = Public(l) });
        }
    }
}
