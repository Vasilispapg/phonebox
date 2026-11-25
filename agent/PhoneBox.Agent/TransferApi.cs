using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.StaticFiles;

namespace PhoneBox.Agent;

/// <summary>
/// The Mac's side of sending a file to the phone, on the local port only (not under /api/bridge, never on the Wi-Fi allow-list
/// and not served through the USB tunnel): <c>PUT /api/transfer/send?name=</c> with the raw file as the body, and
/// <c>GET /api/transfer/status</c>. The caller is the settings page (a same-origin browser request) or the Finder Quick Action
/// (the local key). The body is the file's bytes: the agent never reads a path the caller names.
/// </summary>
internal static class TransferApi
{
    private static readonly FileExtensionContentTypeProvider Types = new();

    public static void Map(IEndpointRouteBuilder app, string[] sameOrigins)
    {
        // These take the request, not the HttpContext (see BridgeApi.Map).
        app.MapPut("/api/transfer/send", (HttpRequest req) =>
        {
            var services = req.HttpContext.RequestServices;
            return SendAsync(req.HttpContext, services.GetService<Outbox>(), services.GetRequiredService<BridgeStream>(),
                services.GetRequiredService<ConfigStore>(), services.GetRequiredService<LocalTransferKey>(), sameOrigins);
        });
        app.MapGet("/api/transfer/status", (HttpRequest req) =>
        {
            var services = req.HttpContext.RequestServices;
            return Status(services.GetService<Outbox>(), req, services.GetRequiredService<LocalTransferKey>(), sameOrigins);
        });
    }

    /// <summary>
    /// A browser request that says it comes from the settings page itself (its Origin is this agent's own, or the browser marks it
    /// same-origin), or the local key. A request with neither is refused. A foreign Origin is refused even with a key.
    /// </summary>
    public static bool CallerAllowed(HttpRequest req, string[] sameOrigins, LocalTransferKey key)
    {
        var origin = req.Headers.Origin.ToString();
        if (origin.Length > 0) return sameOrigins.Contains(origin);
        if (string.Equals(req.Headers["Sec-Fetch-Site"].ToString(), "same-origin", StringComparison.OrdinalIgnoreCase)) return true;
        return key.Matches(req.Headers[LocalTransferKey.Header].ToString());
    }

    /// <summary>The bridge stream's "file" event: the metadata the phone needs to fetch the file, never a path.</summary>
    public static string FileEventJson(OutboxItem item) =>
        JsonSerializer.Serialize(new { id = item.Id, name = item.Name, size = item.Size, mime = item.Mime });

    private static IResult Refuse(int status, string error) => Results.Json(new { error }, statusCode: status);

    /// <summary>In the cheapest order: the platform (501), who is asking (403), the switch (403), the name (400), the size (413), then the body.</summary>
    public static async Task<IResult> SendAsync(HttpContext ctx, Outbox? outbox, BridgeStream stream, ConfigStore cfg, LocalTransferKey key, string[] sameOrigins)
    {
        if (outbox is null) return Refuse(501, "not supported on this system");
        if (!CallerAllowed(ctx.Request, sameOrigins, key)) return Refuse(403, "not allowed here");
        if (!cfg.Get().Bridge.FilesToPhone) return Refuse(403, "off");
        var name = ctx.Request.Query["name"].ToString();
        if (name.Length is 0 or > 1024) return Refuse(400, "give a name");
        var declared = ctx.Request.ContentLength;
        if (declared > FileLimits.MaxFileBytes) return Refuse(413, "too large");
        // this request only: the server's own limit (about 30 MB) stays for every other endpoint
        if (ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = FileLimits.MaxFileBytes;
        var mime = Types.TryGetContentType(name, out var found) ? found : null; // from the extension, never from what the caller says
        var added = await outbox.AddAsync(name, declared, mime, ctx.Request.Body, ctx.RequestAborted);
        switch (added.Status)
        {
            case OutboxAddStatus.Queued:
                stream.Publish("file", FileEventJson(added.Item!), null);
                return Results.Json(new { id = added.Item!.Id }, statusCode: 202);
            case OutboxAddStatus.TooLarge: return Refuse(413, "too large");
            case OutboxAddStatus.Full: return Refuse(507, "the outbox is full");
            default: return Results.StatusCode(499); // the caller went away or the size did not match: nobody reads the answer
        }
    }

    public static IResult Status(Outbox? outbox, HttpRequest req, LocalTransferKey key, string[] sameOrigins)
    {
        if (outbox is null) return Refuse(501, "not supported on this system");
        if (!CallerAllowed(req, sameOrigins, key)) return Refuse(403, "not allowed here");
        var files = outbox.List().Select(e => new
        {
            id = e.Item.Id,
            name = e.Item.Name,
            size = e.Item.Size,
            state = e.State.ToString().ToLowerInvariant(),
        });
        return Results.Ok(new { files });
    }
}
