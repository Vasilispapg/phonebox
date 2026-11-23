using System.Text.Json;

namespace PhoneBox.Agent;

/// <summary>
/// What the phone bridge's endpoints decide, kept out of the Program.cs lambdas so tests can reach it. All text here is
/// inert data from the phone: it is stored on the clipboard and relayed, never run, opened or interpreted.
/// </summary>
internal static class BridgeApi
{
    /// <summary>The largest request the clipboard endpoint reads (the text limit is lower; JSON escapes make the body bigger).</summary>
    public const int MaxRequestBytes = 256 * 1024;
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
    }

    public static IResult BodyError(string error) =>
        Results.Json(new { error }, statusCode: error == RequestTooLarge ? 413 : 400);

    /// <summary>The device id header, only when it looks like the ids phones send (letters and digits, at most 32).</summary>
    public static string? DeviceId(string? header) =>
        !string.IsNullOrWhiteSpace(header) && header.Length <= 32 && header.All(char.IsAsciiLetterOrDigit) ? header : null;

    /// <summary>The bridge stream follows the clipboard switch: when it is off a new connection is refused and an open one ends.</summary>
    public static bool StreamAllowed(ConfigStore config) => config.Get().Bridge.Clipboard;

    /// <summary>One server-sent event. The text travels as a JSON string, so a newline inside it cannot end the frame.</summary>
    public static string EventFrame(BridgeEvent e) =>
        $"event: {e.Name}\ndata: {JsonSerializer.Serialize(new { seq = e.Seq, text = e.Text }, Web)}\n\n";
}
