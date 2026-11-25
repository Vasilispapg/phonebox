using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace PhoneBox.Agent;

/// <summary>
/// The terminal widget's routes. A shell is reachable only while the Terminal master switch and the asking widget's own
/// <c>control</c> option are on, for a widget on an enabled page of the asking phone (header <c>X-PhoneBox-Device</c>,
/// query <c>widget=pageId/widgetId</c>). Everything comes in the query string: the phone's transport sends no request
/// bodies and drops response headers, so output carries its cursor inside the body (see <see cref="Frame"/>). Nothing
/// typed or shown is logged, and an error never carries an exception's text.
/// </summary>
public sealed class TerminalService(ConfigStore config, TerminalSessions sessions)
{
    public const int MaxInputBytes = 4096;
    public const int MaxWaitMs = 2500;
    public const int DefaultCols = 80, DefaultRows = 24;

    /// <summary>Only the macOS pseudo-terminal exists; elsewhere no shell is ever started.</summary>
    internal bool Supported { get; init; } = OperatingSystem.IsMacOS();

    private static readonly object Closed = new { error = "closed" };
    private static readonly object Busy = new { error = "busy" };

    /// <summary>What the overlay may show before it opens anything. Never counts as overlay activity.</summary>
    public (int Status, object Body) State(string? device, string? widget)
    {
        var c = config.Get();
        if (PanelWidgets.Find(c, device, widget, "terminal") is not { } w) return NotOnPage;
        var control = w.Bool("control") == true;
        var session = Supported ? sessions.Get(device ?? "", widget!) : null;
        return (200, new { supported = Supported, enabled = c.Terminal.Enabled, control, open = session is { Exited: false } });
    }

    /// <summary>Opens the shell for this phone and widget, or reattaches to the one already running. Says which, and the
    /// input number the shell applies next, so the phone continues a reattached shell's numbering and resolves a batch
    /// whose answer it never saw.</summary>
    public (int Status, object Body) Open(string? device, string? widget, int? cols, int? rows)
    {
        if (Gate(device, widget) is { } refused) return refused;
        TerminalSession? session;
        string? error;
        bool attached;
        try { (session, error, attached) = sessions.Open(device ?? "", widget!, cols ?? DefaultCols, rows ?? DefaultRows); }
        catch (Exception) { (session, error, attached) = (null, TerminalSessions.StartError, false); } // a reattach whose pty is already gone
        if (session is null)
            return (error == TerminalSessions.BusyError ? 429 : error == TerminalSessions.OffError ? 403 : 500, new { error });
        session.Touch();
        return (200, new { ok = true, attached, nextSeq = session.NextInput, shell = session.Id });
    }

    /// <summary>Output after <paramref name="since"/>, waiting up to <see cref="MaxWaitMs"/> for some. An aborted request answers 499 and nothing else.</summary>
    public async Task<(int Status, byte[]? Frame, object? Error)> Out(string? device, string? widget, ulong since, int? waitMs, CancellationToken ct)
    {
        if (Gate(device, widget) is { } refused) return (refused.Status, null, refused.Body);
        if (sessions.Get(device ?? "", widget!) is not { } session) return (410, null, Closed);
        session.Touch();
        try { return (200, Frame(await session.ReadAsync(since, Math.Clamp(waitMs ?? 0, 0, MaxWaitMs), ct)), null); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return (499, null, null); }
        catch (Exception) { return (410, null, Closed); }
    }

    /// <summary>One numbered batch of typed text (the query-decoded <paramref name="d"/>, written as UTF-8), applied exactly once.
    /// With <paramref name="shell"/> (the id <see cref="Open"/> gave) a batch numbered for another shell is refused untyped.
    /// 503 "busy" while an earlier batch is still being written: nothing was typed, the same number goes again.</summary>
    public (int Status, object Body) In(string? device, string? widget, ulong seq, string? d, string? shell = null)
    {
        if (Gate(device, widget) is { } refused) return refused;
        if (seq == 0) return (400, new { error = "seq must be a number from 1" });
        if (d is null) return (400, new { error = "nothing to type" });
        if (Encoding.UTF8.GetByteCount(d) > MaxInputBytes) return (413, new { error = $"more than {MaxInputBytes} bytes at once" });
        if (sessions.Get(device ?? "", widget!) is not { } session) return (410, Closed);
        if (shell is not null && shell != session.Id) return (409, new { error = "other-session" });
        try
        {
            var (outcome, expected) = session.ApplyInput(seq, Encoding.UTF8.GetBytes(d));
            // Busy: an earlier batch is still being written (the program in front is not reading). The phone sends this one
            // again later with the same number; a retry is not activity, so a shell stuck like this still ends when idle.
            if (outcome == InputOutcome.Busy) return (503, Busy);
            session.Touch();
            return outcome switch
            {
                InputOutcome.Applied => (200, new { applied = true }),
                InputOutcome.Duplicate => (200, new { applied = false }),
                InputOutcome.OutOfOrder => (409, new { error = "out-of-order", expected }),
                _ => (410, Closed),
            };
        }
        catch (Exception) { return (410, Closed); } // the pty is gone (killed, or the shell ended): never the exception's text
    }

    /// <summary>The overlay's size in cells; the session clamps it to 10–500 × 2–200.</summary>
    public (int Status, object Body) Size(string? device, string? widget, int? cols, int? rows)
    {
        if (Gate(device, widget) is { } refused) return refused;
        if (cols is not { } c || rows is not { } r) return (400, new { error = "cols and rows must be numbers" });
        if (sessions.Get(device ?? "", widget!) is not { } session || session.Exited) return (410, Closed);
        session.Touch();
        try { session.Resize(c, r); }
        catch (Exception) { return (410, Closed); }
        return (200, new { ok = true });
    }

    /// <summary>Ends this phone's shell for the widget. Answers at once: killing the shell can take a few seconds.</summary>
    public (int Status, object Body) Close(string? device, string? widget)
    {
        if (PanelWidgets.Find(config.Get(), device, widget, "terminal") is null) return NotOnPage;
        sessions.Get(device ?? "", widget!)?.Touch();
        sessions.CloseInBackground(device ?? "", widget!);
        return (200, new { ok = true });
    }

    /// <summary><c>[flags:1][cursor:8, big-endian][data…]</c>; flags bit 0 = the shell has ended, bit 1 = the cursor was reset.</summary>
    public static byte[] Frame(TerminalChunk chunk)
    {
        var frame = new byte[9 + chunk.Data.Length];
        frame[0] = (byte)((chunk.Exited ? 1 : 0) | (chunk.Reset ? 2 : 0));
        BinaryPrimitives.WriteUInt64BigEndian(frame.AsSpan(1, 8), chunk.Cursor);
        chunk.Data.CopyTo(frame.AsSpan(9));
        return frame;
    }

    private static readonly (int Status, object Body) NotOnPage = (404, new { error = "this terminal widget isn't on an enabled page of this phone" });

    // 404 when the widget is not on the phone's enabled page, 501 off macOS, 403 while either switch is off.
    private (int Status, object Body)? Gate(string? device, string? widget)
    {
        var c = config.Get();
        if (PanelWidgets.Find(c, device, widget, "terminal") is not { } w) return NotOnPage;
        if (!Supported) return (501, new { error = "the terminal needs macOS" });
        if (!c.Terminal.Enabled) return (403, new { error = "the terminal is off: turn it on in Settings › General › Terminal" });
        if (w.Bool("control") != true) return (403, new { error = "this widget may not open a shell: turn on its control option" });
        return null;
    }
}

/// <summary>The terminal's HTTP routes; Program.cs maps them beside the app console's.</summary>
public static class TerminalRoutes
{
    public static void Map(IEndpointRouteBuilder app)
    {
        // every parameter is read as text and parsed here: a value the framework could not bind would be logged with its contents
        app.MapGet("/api/terminal/state", (string? widget, HttpRequest req, TerminalService t) => Json(t.State(Device(req), widget)));
        app.MapPost("/api/terminal/open", (string? widget, string? cols, string? rows, HttpRequest req, TerminalService t) =>
            Json(t.Open(Device(req), widget, Int(cols), Int(rows))));
        app.MapGet("/api/terminal/out", async (string? widget, string? since, string? wait, HttpRequest req, TerminalService t, CancellationToken ct) =>
        {
            ulong from = 0;
            if (!string.IsNullOrEmpty(since) && !ulong.TryParse(since, NumberStyles.None, CultureInfo.InvariantCulture, out from))
                return Results.Json(new { error = "since must be a number" }, statusCode: 400);
            var (status, frame, error) = await t.Out(Device(req), widget, from, Int(wait), ct);
            if (frame is not null) return Results.Bytes(frame, "application/octet-stream");
            return error is null ? Results.StatusCode(status) : Results.Json(error, statusCode: status);
        });
        app.MapPost("/api/terminal/in", (string? widget, string? seq, string? d, string? s, HttpRequest req, TerminalService t) =>
            Json(t.In(Device(req), widget, ulong.TryParse(seq, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0, d, s)));
        app.MapPost("/api/terminal/size", (string? widget, string? cols, string? rows, HttpRequest req, TerminalService t) =>
            Json(t.Size(Device(req), widget, Int(cols), Int(rows))));
        app.MapPost("/api/terminal/close", (string? widget, HttpRequest req, TerminalService t) => Json(t.Close(Device(req), widget)));
    }

    // the session key and the log get the id only when it is one the app makes (PhoneRegistry's rule), else "" (no id)
    private static string Device(HttpRequest req) => PhoneRegistry.CleanId(req.Headers["X-PhoneBox-Device"].ToString());

    private static int? Int(string? s) => int.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) ? n : null;

    private static IResult Json((int Status, object Body) r) => Results.Json(r.Body, statusCode: r.Status);
}
