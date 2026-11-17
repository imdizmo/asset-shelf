// ShelfServer: the team's library service. It serves the shared library (index/ and packs/) and keeps the maps and
// props teammates publish from Groundwork and Prop Generator. Nobody signs in: the server asks the machine's Tailscale
// daemon who is behind each connection and looks them up in the team list (team.json, written by Questlog).
// See README.md beside this file for the API and how to run it.
using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
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

    /// <summary>The team server release this runs from (JATIFY_TEAM_SERVER, set by its manifest and compose file); null otherwise.</summary>
    static readonly string? TeamServer = Environment.GetEnvironmentVariable("JATIFY_TEAM_SERVER") is { Length: > 0 } t ? t : null;
    static readonly HashSet<string> Roles = ["owner", "admin", "member", "observer"];
    const int KeepVersions = 5;

    static string _library = "", _store = "", _teamFile = "", _socket = "/var/run/tailscale/tailscaled.sock";
    static long _maxUpload = 4L << 30;
    static Who? _dev;  // only on a loopback address, for tests: every local caller is this person

    public static async Task<int> Main(string[] args)
    {
        string listen = "", hosts = Environment.GetEnvironmentVariable("SHELF_HOSTS") ?? "";
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
                case "--hosts": hosts = args[i + 1]; break;
                default: return Usage($"unknown option {args[i]}");
            }
        }

        _teamFile = _teamFile.Length > 0 ? _teamFile : Environment.GetEnvironmentVariable("SHELF_TEAM_FILE") ?? "";
        if (listen.Length == 0 || _store.Length == 0 || _teamFile.Length == 0)
        {
            return Usage("--listen, --store and --team (or SHELF_TEAM_FILE) are needed");
        }

        if (listen.StartsWith("tailscale", StringComparison.OrdinalIgnoreCase))
        {
            // this machine's own tailnet address, from the Tailscale daemon
            if (await TailscaleIp() is not { } ip)
            {
                return Usage("--listen tailscale: Tailscale did not give this machine's address (is it running and signed in?)");
            }

            listen = ip + listen["tailscale".Length..];
        }

        if (!IPEndPoint.TryParse(listen, out IPEndPoint? at) || at.AddressFamily != AddressFamily.InterNetwork)
        {
            return Usage($"--listen takes an IPv4 address and port, or tailscale[:port] (got {listen})");
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

        await KnowHosts(at, hosts);
        Status.Begin(at.Port);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Listen(at);
            k.Limits.MaxRequestBodySize = null;  // each route caps its own uploads (zips, LFS objects, crash reports)
            k.Limits.MaxConcurrentConnections = 64;
        });
        var app = builder.Build();
        app.Run(ctx => Handle(new Req(ctx), new Res(ctx)));
        app.Lifetime.ApplicationStarted.Register(() => Console.WriteLine($"SHELF_LISTENING {at.Port}"));
        Console.WriteLine($"{DateTime.UtcNow:O} ShelfServer {Version} on http://{at}/ library={(_library.Length > 0 ? _library : "(none)")} store={_store} team={_teamFile} hosts={string.Join(",", Hosts.Order())}");
        await app.RunAsync();
        return 0;
    }

    static int Usage(string why)
    {
        Console.Error.WriteLine($"ShelfServer: {why}\nusage: ShelfServer --listen <tailscale-ip>:8781 --store <dir> --team <team.json> [--library <dir with index/ and packs/>] [--max-upload <bytes>] [--tailscale-socket <path>] [--hosts <name[:port],...>]");
        return 2;
    }

    // ---- requests ----

    static async Task Handle(Req req, Res res)
    {
        Who? who = null;
        try
        {
            if (req.HttpMethod is "GET" or "HEAD" && req.Url.AbsolutePath == "/healthz")
            {
                // up or not, for a launcher or a container's health check: says nothing else, needs no name or person
                await Text(res, 200, "ok");
                return;
            }

            if (!Hosts.Contains(req.Host))
            {
                await Error(res, 421, $"This server answers only at its own address ({string.Join(", ", Hosts.Order())}).");
                return;
            }

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
                await Json(res, 200, new JsonObject { ["login"] = who.Login, ["name"] = who.Name, ["role"] = role, ["service"] = "shelf", ["version"] = Version, ["teamServer"] = TeamServer });
            }
            else if (role is null)
            {
                await NotMember(req, res, who);
            }
            else if (get && seg is ["team", "projects"])
            {
                await Json(res, 200, new JsonObject { ["schema"] = 1, ["projects"] = Projects.List($"http://{req.Url!.Authority}") });
            }
            else if (get && seg is ["team", "server"])
            {
                await Json(res, 200, Status.Now());
            }
            else if (req.HttpMethod is "PUT" or "DELETE" && seg is ["team", "projects", var pid])
            {
                if (role is not ("owner" or "admin"))
                {
                    await Error(res, 403, "Only the team's owner or an admin can change the team's projects.");
                }
                else if (!AppWrite(req, req.HttpMethod == "PUT"))
                {
                    await Error(res, 400, "Changes need the header X-Jatify: 1 (and a JSON body).");
                }
                else
                {
                    try
                    {
                        var (status, answer) = req.HttpMethod == "PUT" ? Projects.Put(pid, await JsonBody(req), $"http://{req.Url!.Authority}", who) : Projects.Delete(pid, who);
                        await Json(res, status, answer);
                    }
                    catch (InvalidOperationException e)
                    {
                        await Error(res, 409, e.Message);
                    }
                }
            }
            else if (seg is ["team", "crashes", .. var crashRest])
            {
                await Crashes.Handle(req, res, crashRest, who, role);
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
        catch (Exception) when (req.Context.RequestAborted.IsCancellationRequested)
        {
            // the caller went away
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"{DateTime.UtcNow:O} {req.HttpMethod} {req.Url?.AbsolutePath}: {e}");
            if (!res.HasStarted)
            {
                await Error(res, 500, "The server failed on this request; its log has the details.");
            }
        }
        finally
        {
            Console.WriteLine($"{DateTime.UtcNow:O} {who?.Login ?? req.RemoteEndPoint.Address.ToString()} {req.HttpMethod} {req.Url?.PathAndQuery} {res.StatusCode}");
        }
    }

    static async Task NotMember(Req req, Res res, Who who)
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

    static async Task Json(Res res, int status, JsonNode body, string mime = "application/json; charset=utf-8")
    {
        byte[] b = Encoding.UTF8.GetBytes(body.ToJsonString());
        res.StatusCode = status;
        res.ContentType = mime;
        res.ContentLength64 = b.Length;
        await res.OutputStream.WriteAsync(b);
    }

    static async Task Text(Res res, int status, string message)
    {
        byte[] b = Encoding.UTF8.GetBytes(message + "\n");
        res.StatusCode = status;
        res.ContentType = "text/plain; charset=utf-8";
        res.ContentLength64 = b.Length;
        await res.OutputStream.WriteAsync(b);
    }

    static Task Error(Res res, int status, string message) => Json(res, status, new JsonObject { ["error"] = message, ["message"] = message });  // "message": what git-lfs shows

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
            string? json = null;
            if (File.Exists(_socket))
            {
                using var r = await Daemon.GetAsync($"http://local-tailscaled.sock/localapi/v0/whois?addr={Uri.EscapeDataString(addr)}");
                json = r.IsSuccessStatusCode ? await r.Content.ReadAsStringAsync() : null;
            }
            else
            {
                // no daemon socket here (Windows, macOS): the same answer from the tailscale command
                json = await WhoisCommand(addr);
            }

            if (json is not null)
            {
                JsonNode? j = JsonNode.Parse(json);
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

    /// <summary>`tailscale whois --json addr`: null when the command is missing or does not know the address.</summary>
    static async Task<string?> WhoisCommand(string addr)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("tailscale") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (string a in (string[])["whois", "--json", addr])
            {
                psi.ArgumentList.Add(a);
            }

            using var p = System.Diagnostics.Process.Start(psi)!;
            Task<string> err = p.StandardError.ReadToEndAsync();
            string output = await p.StandardOutput.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await p.WaitForExitAsync(timeout.Token);
            _ = await err;
            return p.ExitCode == 0 ? output : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            Console.Error.WriteLine($"{DateTime.UtcNow:O} tailscale whois: {e.Message}");
            return null;
        }
    }

    /// <summary>This machine's IPv4 tailnet address: the daemon's local API, else `tailscale ip -4`.</summary>
    static async Task<string?> TailscaleIp() =>
        (await TailscaleSelf())?["TailscaleIPs"] is JsonArray ips
            ? ips.Select(i => i?.GetValue<string>()).FirstOrDefault(i => i is not null && IPAddress.TryParse(i, out IPAddress? a) && a.AddressFamily == AddressFamily.InterNetwork)
            : null;

    /// <summary>This machine as Tailscale sees it ("Self" of the status): the daemon's local API, else `tailscale status --json`.</summary>
    static async Task<JsonNode?> TailscaleSelf()
    {
        try
        {
            if (File.Exists(_socket))
            {
                return JsonNode.Parse(await Daemon.GetStringAsync("http://local-tailscaled.sock/localapi/v0/status"))?["Self"];
            }

            var psi = new System.Diagnostics.ProcessStartInfo("tailscale") { RedirectStandardOutput = true, UseShellExecute = false };
            psi.ArgumentList.Add("status");
            psi.ArgumentList.Add("--json");
            using var p = System.Diagnostics.Process.Start(psi)!;
            string json = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            return p.ExitCode == 0 ? JsonNode.Parse(json)?["Self"] : null;
        }
        catch (Exception e) when (e is HttpRequestException or SocketException or TaskCanceledException or JsonException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>The names this server answers to (Host header): its listen address, its Tailscale names, --hosts. A
    /// request naming any other host is refused: a web page whose own domain was pointed at this address (DNS
    /// rebinding) would otherwise count as same-origin in the browser, and could send what a launcher sends.</summary>
    static readonly HashSet<string> Hosts = new(StringComparer.OrdinalIgnoreCase);

    static async Task KnowHosts(IPEndPoint at, string extra)
    {
        Hosts.Add(at.ToString());
        if (IPAddress.IsLoopback(at.Address))
        {
            Hosts.Add($"localhost:{at.Port}");
        }

        if (await TailscaleSelf() is { } self)
        {
            if (self["DNSName"]?.GetValue<string>()?.TrimEnd('.') is { Length: > 0 } dns)
            {
                Hosts.Add($"{dns}:{at.Port}");
                Hosts.Add($"{dns.Split('.')[0]}:{at.Port}");  // the short MagicDNS name
            }
        }

        foreach (string h in extra.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            Hosts.Add(h.Contains(':') ? h : $"{h}:{at.Port}");
        }
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
                Refresh();
                return new JsonArray([.. _rows.Select(r => (JsonNode)Shown(r.Row, r.Lfs, server))]);
            }
        }

        static JsonObject Shown(JsonObject row, string? lfs, string server)
        {
            var o = (JsonObject)row.DeepClone();
            o["lfs"] = lfs is null ? null : $"{server}/lfs/{lfs}";
            return o;
        }

        static void Refresh()
        {
            DateTime t = System.IO.File.Exists(File) ? System.IO.File.GetLastWriteTimeUtc(File) : default;
            if (t != _stamp)
            {
                _stamp = t;
                _rows = t == default ? [] : Read();
            }
        }

        static string Cap(JsonNode? n, int max) => n is JsonValue v && v.TryGetValue(out string? s) ? s.Trim()[..Math.Min(s.Trim().Length, max)] : "";

        /// <summary>One project checked: (row as served, its LFS project name) or the reason it fails. Reading the file,
        /// a bad optional field is left out; writing (<paramref name="strict"/>), it is refused.</summary>
        static ((JsonObject Row, string? Lfs)? Ok, string? Error) Check(JsonNode? p, bool strict)
        {
            string id = Cap(p?["id"], 64), git = Cap(p?["git"], 500), lfs = Cap(p?["lfs"], 64), setup = Cap(p?["setup"], 200);
            if (!IdRule().IsMatch(id))
            {
                return (null, "id must be 1 to 64 letters, digits, '-' or '_'");
            }

            if (!GitUrl().IsMatch(git))
            {
                return (null, "git must be an https://… or git@host:… address");
            }

            if (!RepoPath().IsMatch(setup) || setup.Split('/').Contains(".."))
            {
                return (null, "setup must be the path of a script inside the repository, like tools/setup.sh");
            }

            if (strict && lfs.Length > 0 && !IdRule().IsMatch(lfs))
            {
                return (null, "lfs must be a project name on this server (letters, digits, '-' or '_')");
            }

            var row = new JsonObject { ["id"] = id, ["name"] = Cap(p?["name"], 60) is { Length: > 0 } n ? n : id, ["description"] = Cap(p?["description"], 1000), ["git"] = git, ["setup"] = setup };
            foreach (var (key, max, ok, what) in new (string, int, Func<string, bool>, string)[]
            {
                ("branch", 100, b => Branch().IsMatch(b) && !b.Contains(".."), "a branch name"),
                ("folder", 100, f => Folder().IsMatch(f), "one folder name"),
                ("memoryMax", 10, m => Memory().IsMatch(m), "a size like 16G"),
            })
            {
                string v = Cap(p?[key], max);
                if (v.Length == 0)
                {
                    continue;
                }

                if (ok(v))
                {
                    row[key] = v;
                }
                else if (strict)
                {
                    return (null, $"{key} must be {what}");
                }
            }

            return ((row, IdRule().IsMatch(lfs) ? lfs : null), null);
        }

        static List<(string, JsonObject, string?)> Read()
        {
            var rows = new List<(string, JsonObject, string?)>();
            try
            {
                foreach (JsonNode? p in JsonNode.Parse(System.IO.File.ReadAllText(File))?["projects"]?.AsArray() ?? [])
                {
                    if (Check(p, strict: false) is ({ } ok, _))
                    {
                        rows.Add((ok.Row["id"]!.GetValue<string>(), ok.Row, ok.Lfs));
                    }
                    else
                    {
                        Console.Error.WriteLine($"{DateTime.UtcNow:O} projects.json: entry {Cap(p?["id"], 64)} left out ({Check(p, false).Error})");
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
            {
                Console.Error.WriteLine($"{DateTime.UtcNow:O} projects.json not read ({e.Message}): no projects until it is");
            }

            return rows;
        }

        /// <summary>Adds or replaces one project (the id in the path wins). Returns (status, answer).</summary>
        public static (int, JsonObject) Put(string id, JsonNode? body, string server, Who who)
        {
            if (body is not JsonObject o)
            {
                return (400, Problem("The body must be one project as a JSON object."));
            }

            o = (JsonObject)o.DeepClone();
            o["id"] = id;
            if (Check(o, strict: true) is not ({ } ok, _))
            {
                return (400, Problem($"Not saved: {Check(o, true).Error}."));
            }

            lock (Gate)
            {
                Refresh();
                bool had = _rows.Any(r => r.Id == id);
                var stored = (JsonObject)ok.Row.DeepClone();
                if (ok.Lfs is not null)
                {
                    stored["lfs"] = ok.Lfs;
                }

                Write([.. Raw().Where(p => Cap(p?["id"], 64) != id), stored]);
                Console.WriteLine($"{DateTime.UtcNow:O} {who.Login} {(had ? "changed" : "added")} project {id}");
                return (had ? 200 : 201, Shown(ok.Row, ok.Lfs, server));
            }
        }

        public static (int, JsonObject) Delete(string id, Who who)
        {
            lock (Gate)
            {
                Refresh();
                if (!_rows.Any(r => r.Id == id))
                {
                    return (404, Problem("No such project."));
                }

                Write([.. Raw().Where(p => Cap(p?["id"], 64) != id)]);
                Console.WriteLine($"{DateTime.UtcNow:O} {who.Login} deleted project {id}");
                return (200, new JsonObject { ["deleted"] = id });
            }
        }

        /// <summary>The file's entries as written, the ones that do not check out included: a save from the launcher
        /// changes only the project it names, and never drops someone's hand-written entry.</summary>
        static List<JsonNode> Raw()
        {
            try
            {
                return System.IO.File.Exists(File) && JsonNode.Parse(System.IO.File.ReadAllText(File))?["projects"] is JsonArray a
                    ? [.. a.Where(p => p is not null).Select(p => p!.DeepClone())] : [];
            }
            catch (JsonException)
            {
                throw new InvalidOperationException("projects.json is not valid JSON: fix it by hand before changing projects here.");
            }
        }

        /// <summary>projects.json rewritten whole (a temp file, then a rename), then read back as any change is.</summary>
        static void Write(List<JsonNode> projects)
        {
            string tmp = File + ".tmp";
            System.IO.File.WriteAllText(tmp, new JsonObject { ["version"] = 1, ["projects"] = new JsonArray([.. projects]) }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            System.IO.File.Move(tmp, File, overwrite: true);
            _stamp = default;
            Refresh();
        }
    }

    /// <summary>GET /team/server: this service, when it started, free disk, and how much each area holds. Sizes are
    /// measured in the background (the store every 10 minutes, the library every 6 hours: walking it is slow on a NAS).</summary>
    static class Status
    {
        static readonly DateTime Started = DateTime.UtcNow;
        static readonly Lock Gate = new();
        static readonly Dictionary<string, long> Sizes = [];
        static DateTime? _measured;
        static int _port;

        public static void Begin(int port)
        {
            _port = port;
            var t = new Thread(() =>
            {
                for (int round = 0; ; round++)
                {
                    foreach (string area in new[] { "lfs", "maps", "props", "crashes" })
                    {
                        Measure(area, Path.Combine(_store, area));
                    }

                    if (round % 36 == 0)
                    {
                        Measure("packs", _library.Length > 0 ? Path.Combine(_library, "packs") : null);
                        Measure("index", _library.Length > 0 ? Path.Combine(_library, "index") : null);
                    }

                    lock (Gate)
                    {
                        _measured = DateTime.UtcNow;
                    }

                    Thread.Sleep(TimeSpan.FromMinutes(10));
                }
            })
            { IsBackground = true, Priority = ThreadPriority.Lowest, Name = "storage sizes" };
            t.Start();
        }

        static void Measure(string area, string? dir)
        {
            long bytes = 0;
            if (dir is not null && Directory.Exists(dir))
            {
                foreach (FileInfo f in new DirectoryInfo(dir).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                {
                    bytes += f.Length;
                }
            }

            lock (Gate)
            {
                Sizes[area] = bytes;
            }
        }

        public static JsonObject Now()
        {
            var storage = new JsonObject();
            lock (Gate)
            {
                foreach (string area in new[] { "packs", "index", "lfs", "maps", "props", "crashes" })
                {
                    storage[area] = Sizes.TryGetValue(area, out long b) ? b : null;
                }

                storage["measuredAt"] = _measured?.ToString("O");
            }

            return new JsonObject
            {
                ["service"] = "shelf", ["version"] = Version, ["teamServer"] = TeamServer, ["port"] = _port, ["started"] = Started.ToString("O"),
                ["diskFree"] = new DriveInfo(_store).AvailableFreeSpace, ["storage"] = storage,
            };
        }
    }

    static JsonObject Problem(string message) => new() { ["error"] = message, ["message"] = message };

    /// <summary>Writes from the launcher's Server tab: the X-Jatify: 1 header (and JSON for a body), which a web page
    /// on the tailnet cannot send without a preflight this server never answers.</summary>
    static bool AppWrite(Req req, bool body) =>
        req.Headers["X-Jatify"] == "1" && (!body || (req.ContentType ?? "").StartsWith("application/json", StringComparison.OrdinalIgnoreCase));

    static Task<JsonNode?> JsonBody(Req req) => ReadJson(req, 1 << 20);

    /// <summary>A JSON body read into memory, at most <paramref name="max"/> bytes counted as they arrive (a body sent
    /// without a length is capped too); null when larger or not JSON.</summary>
    internal static async Task<JsonNode?> ReadJson(Req req, int max)
    {
        if (req.ContentLength64 > max)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        byte[] chunk = new byte[16 << 10];
        for (int got; (got = await req.InputStream.ReadAsync(chunk)) > 0;)
        {
            if (buffer.Length + got > max)
            {
                return null;
            }

            buffer.Write(chunk, 0, got);
        }

        try
        {
            return JsonNode.Parse(buffer.ToArray());
        }
        catch (JsonException)
        {
            return null;
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

    static async Task LibraryList(Res res)
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

    static async Task PackListing(Res res, string pack)
    {
        string? dir = LibDir("packs", pack);
        if (dir is null)
        {
            await Error(res, 404, "No such pack.");
            return;
        }

        res.StatusCode = 200;
        res.ContentType = "application/json; charset=utf-8";
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

    internal static bool IsWholeZip(string file) => WholeZip(file);

    internal static long MaxUpload => _maxUpload;

    internal static bool ValidId(string id) => IdRule().IsMatch(id);

    internal static Task JsonOut(Res res, int status, JsonNode body, string mime = "application/json; charset=utf-8") => Json(res, status, body, mime);

    internal static Task ErrorOut(Res res, int status, string message) => Error(res, status, message);

    internal static Task FileOut(Req req, Res res, string? file) => SendFile(req, res, file);

    static async Task SendFile(Req req, Res res, string? file, string? etag = null, string? download = null)
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

    static async Task Publish(Req req, Res res, string kind, string id, Who who)
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
    static async Task SendPicture(Req req, Res res, string kind, string id)
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
    static async Task Delete(Req req, Res res, string kind, string id, Who who)
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

    static async Task SendPublished(Req req, Res res, string kind, string id)
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
