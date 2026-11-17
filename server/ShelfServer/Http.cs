// The little of a request and a response the server uses, over ASP.NET Core's Kestrel (which runs the same on Linux and
// Windows; HttpListener on Windows is http.sys, which needs an admin URL reservation to listen on a Tailscale address).
using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace ShelfServer;

public sealed class Req(HttpContext ctx)
{
    public HttpContext Context => ctx;

    public string HttpMethod => ctx.Request.Method;

    /// <summary>The Host header as sent (name:port).</summary>
    public string Host => ctx.Request.Host.Value ?? "";

    /// <summary>The request target as sent (still escaped), on this server's address: what HttpListener gave as Url.</summary>
    public Uri Url { get; } = new($"http://{ctx.Request.Host.Value}{ctx.Features.Get<IHttpRequestFeature>()?.RawTarget ?? ctx.Request.Path.Value}");

    public Values QueryString { get; } = new(k => ctx.Request.Query.TryGetValue(k, out var v) ? v.ToString() : null);

    public Values Headers { get; } = new(k => ctx.Request.Headers.TryGetValue(k, out var v) ? v.ToString() : null);

    public long ContentLength64 => ctx.Request.ContentLength ?? -1;

    public string? ContentType => ctx.Request.ContentType;

    public Stream InputStream => ctx.Request.Body;

    /// <summary>The socket's peer, never a forwarded header.</summary>
    public IPEndPoint RemoteEndPoint => new(ctx.Connection.RemoteIpAddress ?? IPAddress.None, ctx.Connection.RemotePort);

    public string[] AcceptTypes => [.. ctx.Request.Headers.Accept.SelectMany(a => (a ?? "").Split(',')).Select(a => a.Trim())];

    public sealed class Values(Func<string, string?> get)
    {
        public string? this[string key] => get(key);
    }
}

public sealed class Res(HttpContext ctx)
{
    public int StatusCode { get => ctx.Response.StatusCode; set => ctx.Response.StatusCode = value; }

    public string? ContentType { get => ctx.Response.ContentType; set => ctx.Response.ContentType = value; }

    public long ContentLength64 { set => ctx.Response.ContentLength = value; }

    public IHeaderDictionary Headers => ctx.Response.Headers;

    public Stream OutputStream => ctx.Response.Body;

    public bool HasStarted => ctx.Response.HasStarted;
}
