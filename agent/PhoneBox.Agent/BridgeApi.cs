using System.Text.Json;

namespace PhoneBox.Agent;

/// <summary>
/// What the phone bridge's endpoints decide, kept out of the Program.cs lambdas so tests can reach it. All text here is
/// inert data from the phone: it is stored on the clipboard and relayed, never run, opened or interpreted.
/// </summary>
internal static class BridgeApi
{
    /// <summary>The largest request the clipboard endpoint reads (the text limit is lower; JSON escapes make the body bigger).</summary>
    public const int MaxRequestBytes = 512 * 1024; // 64 KiB of text can grow six times once JSON escapes it (each character as a six-character escape)
    public const string RequestTooLarge = "request too large";
    /// <summary>An SSE comment, sent now and then so a dead connection is noticed.</summary>
    public const string PingFrame = ": ping\n\n";

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static IResult PostClipboard(ClipboardBridge? bridge, string? text, string? deviceId)
    {
        if (bridge is null) return Results.Json(new { error = "not supported on this system" }, statusCode: 501);
        if (string.IsNullOrEmpty(text)) return Results.Json(new { error = "give a text" }, statusCode: 400);
        return bridge.ReceiveFromPhone(text, deviceId) switch
        {
            ClipboardResult.Stored => Results.Ok(new { ok = true }),
            ClipboardResult.Off => Results.Json(new { error = "off" }, statusCode: 403),
            ClipboardResult.TooLarge => Results.Json(new { error = "too large" }, statusCode: 413),
            _ => Results.Json(new { error = "could not write the clipboard" }, statusCode: 500),
        };
    }

    /// <summary>Reads <c>{"text": "..."}</c>. A body without a usable text gives (null, null); the caller answers 400.</summary>
    public static async Task<(string? Text, string? Error)> ReadClipboardBody(HttpRequest req)
    {
        if (req.ContentLength > MaxRequestBytes) return (null, RequestTooLarge);
        // Content-Length can be absent (chunked requests); enforce the same limit while reading
        var buf = new byte[MaxRequestBytes + 1];
        var n = 0;
        while (n < buf.Length)
        {
            var count = await req.Body.ReadAsync(buf.AsMemory(n), req.HttpContext.RequestAborted);
            if (count == 0) break;
            n += count;
        }
        if (n > MaxRequestBytes) return (null, RequestTooLarge);
        try
        {
            using var doc = JsonDocument.Parse(buf.AsMemory(0, n));
            return doc.RootElement is { ValueKind: JsonValueKind.Object } root
                   && root.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String
                ? (t.GetString(), null)
                : (null, null);
        }
        catch (JsonException) { return (null, "invalid JSON"); }
        // JSON whose text is not valid Unicode (a lone surrogate escape such as "\ud800", or bytes that are not UTF-8): GetString
        // throws this for it, and uncaught it would become a 500 instead of a request error
        catch (InvalidOperationException) { return (null, "invalid text"); }
    }

    public static IResult BodyError(string error) =>
        Results.Json(new { error }, statusCode: error == RequestTooLarge ? 413 : 400);

    /// <summary>The device id header, only when it looks like the ids phones send (letters and digits, at most 32).</summary>
    public static string? DeviceId(string? header) =>
        !string.IsNullOrWhiteSpace(header) && header.Length <= 32 && header.All(char.IsAsciiLetterOrDigit) ? header : null;

    /// <summary>The bridge stream follows the clipboard switch: when it is off a new connection is refused and an open one ends.</summary>
    public static bool StreamAllowed(ConfigStore config) => config.Get().Bridge.Clipboard;

    /// <summary>How often an open stream looks at the switch on its own, apart from every pass of its loop.</summary>
    private const int SwitchCheckMs = 500;
    private const int PingMs = 15_000;

    /// <summary>
    /// GET /api/bridge/stream once the endpoint has checked the switch: the events other devices caused, from now on, until the
    /// phone goes away, the agent begins to stop, or the switch is turned off.
    /// </summary>
    public static async Task StreamAsync(HttpContext ctx, ConfigStore cfg, BridgeStream bridge, CancellationToken stopping)
    {
        // The agent stopping ends it too: the server otherwise waits up to 30 s for this never-ending response before a restart,
        // an update or Quit can finish (as /api/stream does). The switch cancels it as well, so a write that is stuck on a phone
        // that stopped reading ends when sharing is turned off instead of when the connection finally times out.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, stopping);
        var ct = cts.Token;
        using var watch = new System.Threading.Timer(_ =>
        {
            // a timer callback that throws ends the process, so nothing may escape
            try { if (!StreamAllowed(cfg)) cts.Cancel(); }
            catch (Exception) { /* disposed after the stream ended, or the config could not be read: the loop checks again */ }
        }, null, SwitchCheckMs, SwitchCheckMs);
        var device = DeviceId(ctx.Request.Headers["X-PhoneBox-Device"]);
        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.Headers["X-Accel-Buffering"] = "no";
        using var wake = new SemaphoreSlim(0);
        void OnEmit() { try { wake.Release(); } catch (ObjectDisposedException) { } }
        bridge.Emitted += OnEmit;
        bridge.ListenerOpened(); // a phone with its stream open is how the agent knows the phone wants the automatic copy
        try
        {
            var seq = bridge.Seq; // a new connection hears only what is copied from now on
            var nextPing = Environment.TickCount64 + PingMs;
            await ctx.Response.WriteAsync(PingFrame, ct); // lets the phone see the connection is up
            await ctx.Response.Body.FlushAsync(ct);
            // the switch is checked every pass (at least once a second), so turning it off ends the stream
            while (!ct.IsCancellationRequested && StreamAllowed(cfg))
            {
                foreach (var e in bridge.Since(seq, device))
                {
                    if (!StreamAllowed(cfg)) return; // the switch was turned off during this batch: nothing more is written
                    await ctx.Response.WriteAsync(EventFrame(e), ct);
                    seq = e.Seq;
                }
                if (Environment.TickCount64 >= nextPing)
                {
                    await ctx.Response.WriteAsync(PingFrame, ct);
                    nextPing = Environment.TickCount64 + PingMs;
                }
                await ctx.Response.Body.FlushAsync(ct);
                await wake.WaitAsync(TimeSpan.FromSeconds(1), ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { } // the phone went away in the middle of a write
        finally { bridge.Emitted -= OnEmit; bridge.ListenerClosed(); }
    }

    /// <summary>One server-sent event. The text travels as a JSON string, so a newline inside it cannot end the frame.</summary>
    public static string EventFrame(BridgeEvent e) =>
        $"event: {e.Name}\ndata: {JsonSerializer.Serialize(new { seq = e.Seq, text = e.Text }, Web)}\n\n";
}
