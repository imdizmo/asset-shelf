// ShelfServer: the team's library service. It serves the shared library (index/ and packs/) and keeps the maps and
// props teammates publish from Groundwork and Prop Generator. Nobody signs in: the server asks the machine's Tailscale
// daemon who is behind each connection and looks them up in the team list (team.json, written by Questlog).
// See README.md beside this file for the API and how to run it.
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ShelfServer;

public sealed record Who(string Login, string Name);

public static partial class Server
{
    static readonly string Version = typeof(Server).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";
    static readonly string[] Kinds = ["maps", "props"];
    static readonly HashSet<string> Roles = ["owner", "admin", "member", "observer"];
    const int KeepVersions = 5;

    static string _library = "", _store = "", _teamFile = "", _socket = "/var/run/tailscale/tailscaled.sock";
    static long _maxUpload = 4L << 30;
    static Who? _dev;  // only on a loopback address, for tests: every local caller is this person

    public static async Task<int> Main(string[] args)
    {
        string listen = "";
        if (args is ["--version"])
        {
            Console.WriteLine(Version);
            return 0;
        }

        for (int i = 0; i + 1 < args.Length; i += 2)
        {
            switch (args[i])
            {
                case "--listen": listen = args[i + 1]; break;
                case "--library": _library = Path.GetFullPath(args[i + 1]); break;
                case "--store": _store = Path.GetFullPath(args[i + 1]); break;
                case "--team": _teamFile = args[i + 1]; break;
                case "--max-upload": _maxUpload = long.Parse(args[i + 1]); break;
                case "--tailscale-socket": _socket = args[i + 1]; break;
                default: return Usage($"unknown option {args[i]}");
            }
        }

        _teamFile = _teamFile.Length > 0 ? _teamFile : Environment.GetEnvironmentVariable("SHELF_TEAM_FILE") ?? "";
        if (listen.Length == 0 || _store.Length == 0 || _teamFile.Length == 0)
        {
            return Usage("--listen, --store and --team (or SHELF_TEAM_FILE) are needed");
        }

        if (!IPEndPoint.TryParse(listen, out IPEndPoint? at) || at.AddressFamily != AddressFamily.InterNetwork)
        {
            return Usage($"--listen takes an IPv4 address and port (got {listen})");
        }

        at.Port = at.Port == 0 ? 8781 : at.Port;
        // Tailscale recognition is only sound where nothing but the tailnet can connect
        if (!Tailnet(at.Address) && !IPAddress.IsLoopback(at.Address))
        {
            return Usage($"{at.Address} is not a Tailscale address: the server listens only on the tailnet (or loopback for tests)");
        }

        if (IPAddress.IsLoopback(at.Address) && Environment.GetEnvironmentVariable("SHELF_DEV_LOGIN") is { Length: > 0 } dl)
        {
            _dev = new Who(dl, Environment.GetEnvironmentVariable("SHELF_DEV_NAME") ?? dl);
        }

        if (_library.Length > 0 && !Directory.Exists(Path.Combine(_library, "index")))
        {
            return Usage($"{_library} has no index/ folder");
        }

        foreach (string k in Kinds)
        {
            Directory.CreateDirectory(Path.Combine(_store, k));
        }

        if (!File.Exists(_teamFile))
        {
            Console.WriteLine($"{DateTime.UtcNow:O} no team list at {_teamFile} yet: everyone is refused until Questlog writes it");
        }

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://{at}/");
        listener.Start();
        Console.WriteLine($"{DateTime.UtcNow:O} ShelfServer {Version} on http://{at}/ library={(_library.Length > 0 ? _library : "(none)")} store={_store} team={_teamFile}");
        using var gate = new SemaphoreSlim(32);
        while (true)
        {
            HttpListenerContext ctx = await listener.GetContextAsync();
            await gate.WaitAsync();
            _ = Task.Run(async () =>
            {
                try
                {
                    await Handle(ctx);
                }
                finally
                {
                    gate.Release();
                }
            });
        }
    }

    static int Usage(string why)
    {
        Console.Error.WriteLine($"ShelfServer: {why}\nusage: ShelfServer --listen <tailscale-ip>:8781 --store <dir> --team <team.json> [--library <dir with index/ and packs/>] [--max-upload <bytes>] [--tailscale-socket <path>]");
        return 2;
    }

    // ---- requests ----

    static async Task Handle(HttpListenerContext ctx)
    {
        HttpListenerRequest req = ctx.Request;
        HttpListenerResponse res = ctx.Response;
        Who? who = null;
        try
        {
            who = await Identify(req.RemoteEndPoint);
            string? role = who is null ? null : Team.RoleOf(who.Login);
            string[] seg = [.. (req.Url!.AbsolutePath).Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString)];
            bool get = req.HttpMethod is "GET" or "HEAD";
            if (who is null)
            {
                await Error(res, 403, "This server only answers people Tailscale recognises (connect over the tailnet, from a device signed in to your own account).");
            }
            else if (get && seg is ["team", "whoami"])
            {
                await Json(res, 200, new JsonObject { ["login"] = who.Login, ["name"] = who.Name, ["role"] = role, ["service"] = "shelf", ["version"] = Version });
            }
            else if (role is null)
            {
                await NotMember(req, res, who);
            }
            else if (get && seg is ["team", "projects"])
            {
                await Json(res, 200, new JsonObject { ["schema"] = 1, ["projects"] = Projects.List($"http://{req.Url!.Authority}") });
            }
            else if (get && seg is ["library"])
            {
                await LibraryList(res);
            }
            else if (get && seg is ["library", var pack, "index", ..])
            {
                await SendFile(req, res, LibFile("index", pack, seg[3..]));
            }
            else if (get && seg is ["library", var pack2, "files"])
            {
                await PackListing(res, pack2);
            }
            else if (get && seg is ["library", var pack3, "files", _, ..])
            {
                await SendFile(req, res, LibFile("packs", pack3, seg[3..]));
            }
            else if (get && seg is [var k] && Kinds.Contains(k))
            {
                await Json(res, 200, new JsonArray([.. Published(k).Select(m => m.Newest)]));
            }
            else if (req.HttpMethod == "PUT" && seg is [var k2, var id] && Kinds.Contains(k2))
            {
                if (role == "observer")
                {
                    await Error(res, 403, "Observers can look but not publish: ask the owner for the member role.");
                }
                else
                {
                    await Publish(req, res, k2, id, who);
                }
            }
            else if (get && seg is [var k4, var id4] && Kinds.Contains(k4))
            {
                Entry? e = IdRule().IsMatch(id4) ? Load(k4, id4) : null;
                await (e is null ? Error(res, 404, "Nothing has been published under this id.")
                    : Json(res, 200, new JsonObject { ["id"] = id4, ["versions"] = new JsonArray([.. e.Versions.Select(v => (JsonNode)e.Row(v!))]) }));
            }
            else if (get && seg is [var k5, var id5, "picture"] && Kinds.Contains(k5))
            {
                await SendPicture(req, res, k5, id5);
            }
            else if (req.HttpMethod == "DELETE" && seg is [var k6, var id6] && Kinds.Contains(k6))
            {
                if (role is not ("owner" or "admin"))
                {
                    await Error(res, 403, "Only the team's owner or an admin can delete what was published.");
                }
                else
                {
                    await Delete(req, res, k6, id6, who);
                }
            }
            else if (get && seg is [var k3, var id3, "pack"] && Kinds.Contains(k3))
            {
                await SendPublished(req, res, k3, id3);
            }
            else if (seg is ["lfs", var project, .. var rest])
            {
                await Lfs.Handle(req, res, project, rest, who, role);
            }
            else
            {
                await Error(res, 404, "There is nothing at this address.");
            }
        }
        catch (HttpListenerException)
        {
            // the caller went away
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"{DateTime.UtcNow:O} {req.HttpMethod} {req.Url?.AbsolutePath}: {e}");
            try
            {
                await Error(res, 500, "The server failed on this request; its log has the details.");
            }
            catch (Exception)
            {
                // the response had already started
            }
        }
        finally
        {
            Console.WriteLine($"{DateTime.UtcNow:O} {who?.Login ?? req.RemoteEndPoint.Address.ToString()} {req.HttpMethod} {req.Url?.PathAndQuery} {res.StatusCode}");
            try
            {
                res.Close();
            }
            catch (Exception)
            {
                // already gone
            }
        }
    }

    static async Task NotMember(HttpListenerRequest req, HttpListenerResponse res, Who who)
    {
        string msg = $"You are signed in to Tailscale as {who.Login}, but you are not on this team yet. Ask the owner to add you in Questlog (Members > Waiting to join).";
        if (req.AcceptTypes?.Any(t => t.StartsWith("text/html", StringComparison.Ordinal)) == true)
        {
            byte[] page = Encoding.UTF8.GetBytes($"<!doctype html><meta charset=utf-8><title>Asset Shelf</title><p style=\"font:16px sans-serif;margin:3em\">{WebUtility.HtmlEncode(msg)}</p>");
            res.StatusCode = 403;
            res.ContentType = "text/html; charset=utf-8";
            res.ContentLength64 = page.Length;
            await res.OutputStream.WriteAsync(page);
            return;
        }

        await Error(res, 403, msg);
    }

    static async Task Json(HttpListenerResponse res, int status, JsonNode body, string mime = "application/json; charset=utf-8")
    {
        byte[] b = Encoding.UTF8.GetBytes(body.ToJsonString());
        res.StatusCode = status;
        res.ContentType = mime;
        res.ContentLength64 = b.Length;
        await res.OutputStream.WriteAsync(b);
    }

    static Task Error(HttpListenerResponse res, int status, string message) => Json(res, status, new JsonObject { ["error"] = message, ["message"] = message });  // "message": what git-lfs shows

    // ---- who is calling ----

    static readonly ConcurrentDictionary<IPAddress, (Who? Who, DateTime Until)> Known = new();
    static readonly HttpClient Daemon = new(new SocketsHttpHandler
    {
        ConnectCallback = async (_, ct) =>
        {
            var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await s.ConnectAsync(new UnixDomainSocketEndPoint(_socket), ct);
            return new NetworkStream(s, ownsSocket: true);
        },
    })
    { Timeout = TimeSpan.FromSeconds(5) };

    /// <summary>The person behind a connection, from the socket's peer address only (never a header), or null: not a
    /// tailnet address, a tagged device, or the daemon could not say.</summary>
    static async Task<Who?> Identify(IPEndPoint peer)
    {
        IPAddress ip = peer.Address.IsIPv4MappedToIPv6 ? peer.Address.MapToIPv4() : peer.Address;
        if (_dev is not null && IPAddress.IsLoopback(ip))
        {
            return _dev;
        }

        if (!Tailnet(ip))
        {
            return null;
        }

        if (Known.TryGetValue(ip, out var k) && k.Until > DateTime.UtcNow)
        {
            return k.Who;
        }

        Who? who = null;
        try
        {
            string addr = ip.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{ip}]:{peer.Port}" : $"{ip}:{peer.Port}";
            using var r = await Daemon.GetAsync($"http://local-tailscaled.sock/localapi/v0/whois?addr={Uri.EscapeDataString(addr)}");
            if (r.IsSuccessStatusCode)
            {
                JsonNode? j = JsonNode.Parse(await r.Content.ReadAsStringAsync());
                bool tagged = j?["Node"]?["Tags"] is JsonArray { Count: > 0 };
                string? login = j?["UserProfile"]?["LoginName"]?.GetValue<string>();
                if (!tagged && login is { Length: > 0 } && login != "tagged-devices")
                {
                    who = new Who(login, j?["UserProfile"]?["DisplayName"]?.GetValue<string>() ?? login);
                }
            }
        }
        catch (Exception e) when (e is HttpRequestException or SocketException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            Console.Error.WriteLine($"{DateTime.UtcNow:O} whois {ip}: {e.Message}");
            return null;  // not cached: the daemon may be back on the next request
        }

        if (Known.Count > 512)
        {
            Known.Clear();  // ponytail: a tailnet has a handful of devices; clearing beats an LRU here
        }

        Known[ip] = (who, DateTime.UtcNow.AddMinutes(1));
        return who;
    }

    /// <summary>Tailscale's ranges: 100.64.0.0/10 and fd7a:115c:a1e0::/48.</summary>
    public static bool Tailnet(IPAddress ip)
    {
        byte[] b = ip.GetAddressBytes();
        return ip.AddressFamily == AddressFamily.InterNetwork
            ? b[0] == 100 && (b[1] & 0xC0) == 64
            : b.Length == 16 && b[0] == 0xfd && b[1] == 0x7a && b[2] == 0x11 && b[3] == 0x5c && b[4] == 0xa1 && b[5] == 0xe0;
    }

    /// <summary>team.json (version 1, written by Questlog): read whole, again when its time changes.</summary>
    static class Team
    {
        static DateTime _stamp;
        static Dictionary<string, string> _roles = new(StringComparer.OrdinalIgnoreCase);
        static readonly Lock Gate = new();

        public static string? RoleOf(string login)
        {
            lock (Gate)
            {
                DateTime t = File.Exists(_teamFile) ? File.GetLastWriteTimeUtc(_teamFile) : default;
                if (t != _stamp)
                {
                    _stamp = t;
                    _roles = Read();
                }

                return _roles.GetValueOrDefault(login);
            }
        }

        static Dictionary<string, string> Read()
        {
            var roles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (JsonNode? m in JsonNode.Parse(File.ReadAllText(_teamFile))?["members"]?.AsArray() ?? [])
                {
                    if (m?["login"]?.GetValue<string>() is { Length: > 0 } login && m["role"]?.GetValue<string>() is { } role && Roles.Contains(role))
                    {
                        roles[login] = role;  // a role this server does not know gives no access
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
            {
                Console.Error.WriteLine($"{DateTime.UtcNow:O} team list {_teamFile} not read ({e.Message}): nobody is a member until it is");
            }

            return roles;
        }
    }

    /// <summary>projects.json beside team.json (the owner edits it): the team's projects, for the Launcher's project
    /// cards. Each: id, name, description, git (what to clone), lfs (a project name here), setup (a script's path inside the
    /// clone: never a command, so this file cannot make a teammate's machine run anything the repository does not hold),
    /// and optionally branch, folder and memoryMax. Anything that does not check out is dropped.</summary>
    static class Projects
    {
        static DateTime _stamp;
        static List<(string Id, JsonObject Row, string? Lfs)> _rows = [];
        static readonly Lock Gate = new();

        static string File => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_teamFile))!, "projects.json");

        public static JsonArray List(string server)
        {
            lock (Gate)
            {
                DateTime t = System.IO.File.Exists(File) ? System.IO.File.GetLastWriteTimeUtc(File) : default;
                if (t != _stamp)
                {
                    _stamp = t;
                    _rows = t == default ? [] : Read();
                }

                return new JsonArray([.. _rows.Select(r =>
                {
                    var o = (JsonObject)r.Row.DeepClone();
                    o["lfs"] = r.Lfs is null ? null : $"{server}/lfs/{r.Lfs}";
                    return (JsonNode)o;
                })]);
            }
        }

        static string Cap(JsonNode? n, int max) => n is JsonValue v && v.TryGetValue(out string? s) ? s.Trim()[..Math.Min(s.Trim().Length, max)] : "";

        static List<(string, JsonObject, string?)> Read()
        {
            var rows = new List<(string, JsonObject, string?)>();
            try
            {
                foreach (JsonNode? p in JsonNode.Parse(System.IO.File.ReadAllText(File))?["projects"]?.AsArray() ?? [])
                {
                    string id = Cap(p?["id"], 64), git = Cap(p?["git"], 500), lfs = Cap(p?["lfs"], 64), setup = Cap(p?["setup"], 200);
                    if (!IdRule().IsMatch(id) || !GitUrl().IsMatch(git) || !RepoPath().IsMatch(setup) || setup.Split('/').Contains(".."))
                    {
                        Console.Error.WriteLine($"{DateTime.UtcNow:O} projects.json: entry {id} left out (it needs an id, a git URL that is https://… or git@host:…, and setup as a path inside the repository)");
                        continue;
                    }

                    var row = new JsonObject { ["id"] = id, ["name"] = Cap(p?["name"], 60) is { Length: > 0 } n ? n : id, ["description"] = Cap(p?["description"], 1000), ["git"] = git, ["setup"] = setup };
                    // optional: a value that does not check out is left out, and the client's default applies
                    if (Cap(p?["branch"], 100) is var branch && Branch().IsMatch(branch) && !branch.Contains(".."))
                    {
                        row["branch"] = branch;
                    }

                    if (Cap(p?["folder"], 100) is var folder && Folder().IsMatch(folder))
                    {
                        row["folder"] = folder;
                    }

                    if (Cap(p?["memoryMax"], 10) is var mem && Memory().IsMatch(mem))
                    {
                        row["memoryMax"] = mem;
                    }

                    rows.Add((id, row, IdRule().IsMatch(lfs) ? lfs : null));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
            {
                Console.Error.WriteLine($"{DateTime.UtcNow:O} projects.json not read ({e.Message}): no projects until it is");
            }

            return rows;
        }
    }

    [GeneratedRegex(@"^(https://[A-Za-z0-9.-]+(:\d+)?/[^\s""'\\]+|git@[A-Za-z0-9.-]+:[A-Za-z0-9._/-]+)$")]
    private static partial Regex GitUrl();

    [GeneratedRegex("^[A-Za-z0-9_][A-Za-z0-9._-]*(/[A-Za-z0-9_.-]+)*$")]
    private static partial Regex RepoPath();

    [GeneratedRegex("^[A-Za-z0-9_][A-Za-z0-9._/-]*$")]
    private static partial Regex Branch();

    [GeneratedRegex("^[A-Za-z0-9_][A-Za-z0-9 ._-]*$")]
    private static partial Regex Folder();

    [GeneratedRegex("^[1-9][0-9]{0,5}[KMG]$")]
    private static partial Regex Memory();

    // ---- the library ----

    static async Task LibraryList(HttpListenerResponse res)
    {
        if (_library.Length == 0)
        {
            await Json(res, 200, new JsonArray());
            return;
        }

        var all = new JsonArray();
        foreach (DirectoryInfo d in new DirectoryInfo(Path.Combine(_library, "index")).EnumerateDirectories("*", NoLinks(false)).OrderBy(d => d.Name, StringComparer.Ordinal))
        {
            var files = new JsonArray();
            foreach (FileInfo f in d.EnumerateFiles("*", NoLinks(false)).OrderBy(f => f.Name, StringComparer.Ordinal))
            {
                files.Add((JsonNode)new JsonObject { ["name"] = f.Name, ["size"] = f.Length, ["mtime"] = f.LastWriteTimeUtc.Ticks / TimeSpan.TicksPerMillisecond - 62135596800000L });
            }

            all.Add((JsonNode)new JsonObject { ["id"] = d.Name, ["files"] = files });
        }

        await Json(res, 200, all);
    }

    static async Task PackListing(HttpListenerResponse res, string pack)
    {
        string? dir = LibDir("packs", pack);
        if (dir is null)
        {
            await Error(res, 404, "No such pack.");
            return;
        }

        res.StatusCode = 200;
        res.ContentType = "application/json; charset=utf-8";
        res.SendChunked = true;
        // written as it is walked: a big pack lists tens of thousands of files
        await using var w = new Utf8JsonWriter(res.OutputStream);
        w.WriteStartArray();
        foreach (FileInfo f in new DirectoryInfo(dir).EnumerateFiles("*", NoLinks(true)))
        {
            w.WriteStartObject();
            w.WriteString("path", Path.GetRelativePath(dir, f.FullName).Replace('\\', '/'));
            w.WriteNumber("size", f.Length);
            w.WriteNumber("mtime", f.LastWriteTimeUtc.Ticks / TimeSpan.TicksPerMillisecond - 62135596800000L);
            w.WriteEndObject();
            if (w.BytesPending > 64 << 10)
            {
                await w.FlushAsync();
            }
        }

        w.WriteEndArray();
        await w.FlushAsync();
    }

    static EnumerationOptions NoLinks(bool recurse) => new()
    {
        RecurseSubdirectories = recurse,
        AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System,
        IgnoreInaccessible = true,
    };

    static string? LibDir(string top, string pack) =>
        _library.Length > 0 && Safe(Path.Combine(_library, top), [pack]) is { } d && Directory.Exists(d) ? d : null;

    static string? LibFile(string top, string pack, string[] rel) =>
        _library.Length > 0 && Safe(Path.Combine(_library, top), [pack, .. rel]) is { } f && File.Exists(f) ? f : null;

    /// <summary><paramref name="root"/>/a/b/c, or null when a part is empty, ".", "..", hidden, holds a separator, or
    /// is a link: nothing outside the root, and no link out of it, can be named.</summary>
    public static string? Safe(string root, string[] parts)
    {
        string cur = root;
        foreach (string p in parts)
        {
            if (p.Length == 0 || p[0] == '.' || p.IndexOfAny(['/', '\\', '\0', ':']) >= 0)
            {
                return null;
            }

            cur = Path.Combine(cur, p);
            FileSystemInfo fi = Directory.Exists(cur) ? new DirectoryInfo(cur) : new FileInfo(cur);
            if (fi.LinkTarget is not null)
            {
                return null;
            }
        }

        return cur;
    }

    internal static string Store => _store;

    internal static long MaxUpload => _maxUpload;

    internal static bool ValidId(string id) => IdRule().IsMatch(id);

    internal static Task JsonOut(HttpListenerResponse res, int status, JsonNode body, string mime = "application/json; charset=utf-8") => Json(res, status, body, mime);

    internal static Task ErrorOut(HttpListenerResponse res, int status, string message) => Error(res, status, message);

    internal static Task FileOut(HttpListenerRequest req, HttpListenerResponse res, string? file) => SendFile(req, res, file);

    static async Task SendFile(HttpListenerRequest req, HttpListenerResponse res, string? file, string? etag = null, string? download = null)
    {
        if (file is null)
        {
            await Error(res, 404, "No such file.");
            return;
        }

        if (etag is not null)
        {
            res.Headers["ETag"] = etag;
            if (req.Headers["If-None-Match"] == etag)
            {
                res.StatusCode = 304;
                return;
            }
        }

        await using FileStream f = new(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        res.StatusCode = 200;
        res.ContentType = Path.GetExtension(file).ToLowerInvariant() switch
        {
            ".json" => "application/json",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".zip" => "application/zip",
            _ => "application/octet-stream",
        };
        res.ContentLength64 = f.Length;
        if (download is not null)
        {
            res.Headers["Content-Disposition"] = $"attachment; filename=\"{download}\"";
        }

        if (req.HttpMethod != "HEAD")
        {
            await f.CopyToAsync(res.OutputStream, 1 << 16);
        }
    }

    // ---- maps and props ----

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex IdRule();

    static readonly Lock PublishGate = new();

    sealed record Entry(JsonObject Meta)
    {
        public JsonArray Versions => Meta["versions"]!.AsArray();
        public int Latest => Versions.Count == 0 ? 0 : Versions[^1]!["version"]!.GetValue<int>();
        public JsonObject Newest => Row(Versions[^1]!);

        public JsonObject Row(JsonNode v) => new() { ["id"] = Meta["id"]!.GetValue<string>(), ["name"] = v["name"]!.GetValue<string>(), ["version"] = v["version"]!.GetValue<int>(), ["size"] = v["size"]!.GetValue<long>(), ["sha256"] = v["sha256"]!.GetValue<string>(), ["publishedBy"] = v["publishedBy"]!.DeepClone(), ["publishedAt"] = v["publishedAt"]!.GetValue<string>() };
    }

    static string Folder(string kind, string id) => Path.Combine(_store, kind, id);

    static Entry? Load(string kind, string id)
    {
        string meta = Path.Combine(Folder(kind, id), "meta.json");
        return File.Exists(meta) && JsonNode.Parse(File.ReadAllText(meta)) is JsonObject o && o["versions"] is JsonArray { Count: > 0 } ? new Entry(o) : null;
    }

    static IEnumerable<Entry> Published(string kind) =>
        new DirectoryInfo(Path.Combine(_store, kind)).EnumerateDirectories("*", NoLinks(false))
            .Where(d => IdRule().IsMatch(d.Name)).Select(d => Load(kind, d.Name)).OfType<Entry>()
            .OrderByDescending(e => e.Newest["publishedAt"]!.GetValue<string>(), StringComparer.Ordinal);

    static async Task Publish(HttpListenerRequest req, HttpListenerResponse res, string kind, string id, Who who)
    {
        if (!IdRule().IsMatch(id))
        {
            await Error(res, 400, "The id must be 1 to 64 letters, digits, '-' or '_'.");
            return;
        }

        string name = new([.. (req.QueryString["name"] ?? id).Where(c => !char.IsControl(c))]);
        name = name.Trim() is { Length: > 0 } n ? n[..Math.Min(n.Length, 200)] : id;
        long declared = req.ContentLength64;
        long free = new DriveInfo(_store).AvailableFreeSpace - (1L << 30);  // keep a GiB for everything else
        if (declared > _maxUpload || declared > free)
        {
            await Error(res, 413, declared > _maxUpload ? $"The upload is larger than this server takes ({_maxUpload >> 20} MB)." : "The server does not have the disk space for this upload.");
            return;
        }

        string dir = Folder(kind, id);
        Directory.CreateDirectory(dir);
        string part = Path.Combine(dir, $".upload-{Guid.NewGuid():N}");
        try
        {
            long size = 0;
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] head = new byte[4];
            await using (var f = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                byte[] buf = new byte[1 << 16];
                for (int got; (got = await req.InputStream.ReadAsync(buf)) > 0;)
                {
                    if (size < 4)
                    {
                        Array.Copy(buf, 0, head, (int)size, (int)Math.Min(4 - size, got));
                    }

                    size += got;
                    if (size > _maxUpload || size > free)
                    {
                        await Error(res, 413, size > _maxUpload ? $"The upload is larger than this server takes ({_maxUpload >> 20} MB)." : "The server does not have the disk space for this upload.");
                        return;
                    }

                    sha.AppendData(buf, 0, got);
                    await f.WriteAsync(buf.AsMemory(0, got));
                }
            }

            if (declared >= 0 && size != declared)
            {
                await Error(res, 400, "The upload ended before all of it arrived.");
                return;
            }

            if (head is not [(byte)'P', (byte)'K', 3, 4] || !WholeZip(part))
            {
                await Error(res, 400, head is [(byte)'P', (byte)'K', 3, 4] ? "The upload is cut short: the zip has no directory at its end." : "The upload is not a zip file.");
                return;
            }

            string hash = Convert.ToHexStringLower(sha.GetHashAndReset());
            JsonObject row;
            bool created;
            string? conflict = null;
            lock (PublishGate)
            {
                Entry e = Load(kind, id) ?? new Entry(new JsonObject { ["id"] = id, ["versions"] = new JsonArray() });
                created = e.Latest == 0;
                string? ifMatch = req.Headers["If-Match"]?.Trim().Trim('"');
                if (ifMatch is { Length: > 0 } && ifMatch != "*" && ifMatch != e.Latest.ToString(System.Globalization.CultureInfo.InvariantCulture))
                {
                    conflict = $"Someone published a newer version ({e.Latest}, by {e.Versions[^1]?["publishedBy"]?["name"]}) since you last had it ({ifMatch}).";
                    row = e.Latest > 0 ? e.Newest : new JsonObject();
                }
                else
                {
                    int v = e.Latest + 1;
                    File.Move(part, Path.Combine(dir, $"{v}.zip"));
                    e.Versions.Add((JsonNode)new JsonObject { ["version"] = v, ["name"] = name, ["size"] = size, ["sha256"] = hash, ["publishedBy"] = new JsonObject { ["login"] = who.Login, ["name"] = who.Name }, ["publishedAt"] = DateTime.UtcNow.ToString("O") });
                    // ponytail: keeps the newest KeepVersions zips; older ones are dropped with their rows
                    while (e.Versions.Count > KeepVersions)
                    {
                        File.Delete(Path.Combine(dir, $"{e.Versions[0]!["version"]}.zip"));
                        e.Versions.RemoveAt(0);
                    }

                    string tmp = Path.Combine(dir, ".meta.json.tmp");
                    File.WriteAllText(tmp, e.Meta.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                    File.Move(tmp, Path.Combine(dir, "meta.json"), overwrite: true);
                    row = e.Newest;
                }
            }

            if (conflict is not null)
            {
                await Error(res, 409, conflict);
                return;
            }

            await Json(res, created ? 201 : 200, row);
        }
        finally
        {
            File.Delete(part);  // gone already when it was kept
        }
    }

    /// <summary>True when the zip's directory (at its end) reads: a cut-off upload has none, and is never published.</summary>
    static bool WholeZip(string file)
    {
        try
        {
            using var z = System.IO.Compression.ZipFile.OpenRead(file);
            return z.Entries.Count >= 0;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    static readonly string[] PictureNames = ["picture.png", "picture.jpg", "thumbnail.png", "thumbnail.jpg", "preview.png", "preview.jpg", "cover.png", "cover.jpg"];

    /// <summary>The picture inside a published zip (picture.png and the like, at most one folder deep, up to 8 MB).</summary>
    static async Task SendPicture(HttpListenerRequest req, HttpListenerResponse res, string kind, string id)
    {
        Entry? e = IdRule().IsMatch(id) ? Load(kind, id) : null;
        string? want = req.QueryString["version"];
        JsonNode? v = e?.Versions.LastOrDefault(x => want is null || x!["version"]!.ToString() == want);
        string? zip = v is null ? null : Path.Combine(Folder(kind, id), $"{v["version"]}.zip");
        using var z = zip is not null && File.Exists(zip) ? System.IO.Compression.ZipFile.OpenRead(zip) : null;
        var entry = z?.Entries.Where(x => x.FullName.Count(c => c == '/') <= 1 && x.Length <= 8 << 20 && PictureNames.Contains(x.Name.ToLowerInvariant()))
            .OrderBy(x => Array.IndexOf(PictureNames, x.Name.ToLowerInvariant())).FirstOrDefault();
        if (entry is null)
        {
            await Error(res, 404, "This has no picture.");
            return;
        }

        res.StatusCode = 200;
        res.ContentType = entry.Name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";
        res.ContentLength64 = entry.Length;
        res.Headers["ETag"] = $"\"{v!["sha256"]}\"";
        await using Stream s = entry.Open();
        await s.CopyToAsync(res.OutputStream);
    }

    /// <summary>DELETE /{kind}/{id}: all of it; ?version=n: that version only (the last one going takes the id with it).</summary>
    static async Task Delete(HttpListenerRequest req, HttpListenerResponse res, string kind, string id, Who who)
    {
        string? want = req.QueryString["version"];
        string? error = null;
        int left = 0;
        lock (PublishGate)
        {
            Entry? e = IdRule().IsMatch(id) ? Load(kind, id) : null;
            JsonNode? v = e?.Versions.FirstOrDefault(x => x!["version"]!.ToString() == want);
            string dir = Folder(kind, id);
            if (e is null || (want is not null && v is null))
            {
                error = e is null ? "Nothing has been published under this id." : "This version is not kept on the server.";
            }
            else if (want is null || e.Versions.Count == 1)
            {
                Directory.Delete(dir, recursive: true);
            }
            else
            {
                e.Versions.Remove(v);
                string tmp = Path.Combine(dir, ".meta.json.tmp");
                File.WriteAllText(tmp, e.Meta.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                File.Move(tmp, Path.Combine(dir, "meta.json"), overwrite: true);
                File.Delete(Path.Combine(dir, $"{want}.zip"));
                left = e.Versions.Count;
            }
        }

        if (error is not null)
        {
            await Error(res, 404, error);
            return;
        }

        Console.WriteLine($"{DateTime.UtcNow:O} {who.Login} deleted {kind}/{id}{(want is null ? "" : $" version {want}")}");
        await Json(res, 200, new JsonObject { ["id"] = id, ["deleted"] = want is null ? "all" : want, ["versionsLeft"] = left });
    }

    static async Task SendPublished(HttpListenerRequest req, HttpListenerResponse res, string kind, string id)
    {
        Entry? e = IdRule().IsMatch(id) ? Load(kind, id) : null;
        string? want = req.QueryString["version"];
        JsonNode? v = e?.Versions.LastOrDefault(x => want is null || x!["version"]!.ToString() == want);
        if (e is null || v is null)
        {
            await Error(res, 404, want is null ? "Nothing has been published under this id." : "This version is not kept on the server.");
            return;
        }

        await SendFile(req, res, Path.Combine(Folder(kind, id), $"{v["version"]}.zip"), $"\"{v["sha256"]}\"", $"{id}-{v["version"]}.zip");
    }
}
