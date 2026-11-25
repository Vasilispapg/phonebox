using System.Security.Cryptography;
using System.Text;

namespace PhoneBox.Agent;

/// <summary>
/// The shells that are open, keyed by phone and widget ("pageId/widgetId"). At most <see cref="MaxSessions"/> exist
/// agent-wide; a shell exists only while the Terminal master switch and that widget's own <c>control</c> option are on.
/// Only the overlay counts as activity (<see cref="TerminalSession.Touch"/>): an idle shell is closed, and so is one
/// whose switch went off or whose phone key (Lan.Token) was replaced. The log says that a session opened or closed and for which widget, never what was typed or shown.
/// </summary>
public sealed class TerminalSessions(ConfigStore config, PtyFactory factory, Func<long> nowMs, ILogger<TerminalSessions> log) : IDisposable
{
    public const int MaxSessions = 2;
    /// <summary>A session whose overlay has been quiet this long may be evicted by a request for a new one.</summary>
    public const long DetachedAfterMs = 60_000;
    /// <summary>With IdleMinutes at 0, a shell is closed this long after the overlay stops.</summary>
    public const long ZeroIdleMs = 30_000;
    public const string OffError = "The terminal is switched off for this widget.";
    public const string BusyError = "Two terminals are already open on this computer. Close one of them first.";
    public const string StartError = "The shell could not be started.";

    // _lock guards only the dictionary. Closing a session can wait on its pty, so it always happens after the lock is released.
    private readonly object _lock = new();
    private readonly Dictionary<(string Device, string Widget), Entry> _sessions = [];

    /// <summary>
    /// A session with the fingerprint (SHA-256, hex) of the phone key it was opened under; empty when no key existed then.
    /// The key itself is never kept, logged or shown. "New key" (unpair phones) saves a new Lan.Token, so a session whose
    /// fingerprint is non-empty and no longer matches is stale and ends within a sweep. This is deliberately conservative:
    /// the agent cannot tell whether a shell came in over Wi-Fi or USB, so a USB phone's shell ends on a new key too.
    /// A session opened before any key existed is never ended because a key appears later.
    /// </summary>
    private sealed record Entry(TerminalSession Session, string KeyPrint)
    {
        public bool Stale(string currentPrint) => KeyPrint.Length > 0 && KeyPrint != currentPrint;
    }

    private static string Fingerprint(string? token) =>
        string.IsNullOrWhiteSpace(token) ? "" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>Opens the shell for this phone and widget, or reattaches to the one that is already there (<c>Attached</c>).</summary>
    public (TerminalSession? Session, string? Error, bool Attached) Open(string deviceId, string widgetKey, int cols, int rows)
    {
        var cfg = config.Get();
        if (!Allowed(cfg, deviceId, widgetKey)) return (null, OffError, false);
        cols = Math.Clamp(cols, 10, 500);
        rows = Math.Clamp(rows, 2, 200);
        var key = (deviceId, widgetKey);
        var print = Fingerprint(cfg.Lan.Token);
        var closing = new List<((string Device, string Widget) Key, TerminalSession Session)>();
        TerminalSession session;
        bool attached;
        try
        {
            lock (_lock)
            {
                if (_sessions.TryGetValue(key, out var existing) && !existing.Session.Exited && !existing.Stale(print))
                {
                    existing.Session.Touch(); // reattaching keeps the key fingerprint the shell was opened under
                    session = existing.Session;
                    attached = true;
                }
                else
                {
                    attached = false;
                    if (existing is not null) Detach(key, existing, closing); // its shell has ended, or the phone key changed since: a fresh one takes the key
                    if (_sessions.Count >= MaxSessions)
                    {
                        var now = nowMs();
                        // an ended shell goes first, then the one whose overlay has been quiet longest
                        var victims = _sessions.Where(kv => kv.Value.Session.Exited || now - kv.Value.Session.LastOverlayMs >= DetachedAfterMs)
                            .OrderBy(kv => kv.Value.Session.Exited ? long.MinValue : kv.Value.Session.LastOverlayMs).ToList();
                        if (victims.Count == 0) return (null, BusyError, false);
                        Detach(victims[0].Key, victims[0].Value, closing);
                    }
                    try { session = new TerminalSession(factory(cols, rows), nowMs); }
                    catch (Exception ex)
                    {
                        log.LogWarning("terminal session could not start for device {Device}, widget {Widget}: {Error}", deviceId, widgetKey, ex.GetType().Name);
                        return (null, StartError, false);
                    }
                    _sessions[key] = new Entry(session, print);
                }
            }
        }
        finally
        {
            // Open runs on a request thread and a kill can wait seconds for the shell: the sessions are already out of the
            // dictionary (unreachable, and not counted against MaxSessions), so they are killed on the pool. A victim's pty
            // may outlive the new one's start by those seconds.
            foreach (var (k, s) in closing) _ = Task.Run(() => CloseSession(k, s));
        }
        if (attached) session.Redraw(cols, rows); // never waits for a stuck write; the size change makes the program in front redraw
        else log.LogInformation("terminal session opened for device {Device}, widget {Widget}", deviceId, widgetKey);
        return (session, null, attached);
    }

    public TerminalSession? Get(string deviceId, string widgetKey)
    {
        lock (_lock) return _sessions.TryGetValue((deviceId, widgetKey), out var e) ? e.Session : null;
    }

    /// <summary>Closes the sessions that are idle, or whose master switch, widget control, page or widget is now off, or whose phone key was replaced.</summary>
    public void Sweep()
    {
        List<KeyValuePair<(string Device, string Widget), Entry>> all;
        lock (_lock) all = [.. _sessions];
        if (all.Count == 0) return;
        var cfg = config.Get();
        var idleMinutes = cfg.Terminal.EffectiveIdleMinutes();
        var idleMs = idleMinutes == 0 ? ZeroIdleMs : idleMinutes * 60_000L;
        var now = nowMs();
        var print = Fingerprint(cfg.Lan.Token);
        var closing = new List<((string Device, string Widget) Key, TerminalSession Session)>();
        foreach (var (key, entry) in all)
        {
            if (Allowed(cfg, key.Device, key.Widget) && !entry.Stale(print) && now - entry.Session.LastOverlayMs < idleMs) continue;
            lock (_lock)
            {
                if (_sessions.TryGetValue(key, out var current) && ReferenceEquals(current, entry)) Detach(key, entry, closing);
            }
        }
        foreach (var (k, s) in closing) CloseSession(k, s);
    }

    /// <summary>
    /// Takes this phone's session for the widget out at once (a new open starts a fresh shell) and kills it on a pool thread:
    /// the kill can wait seconds for the shell, which a request must not. Returns that work, or null when there was none.
    /// </summary>
    public Task? CloseInBackground(string deviceId, string widgetKey)
    {
        var key = (deviceId, widgetKey);
        Entry? entry;
        lock (_lock)
        {
            if (!_sessions.Remove(key, out entry)) return null;
        }
        return Task.Run(() => CloseSession(key, entry.Session)); // CloseSession contains its own exceptions
    }

    public void CloseAll()
    {
        List<KeyValuePair<(string Device, string Widget), Entry>> all;
        lock (_lock)
        {
            all = [.. _sessions];
            _sessions.Clear();
        }
        foreach (var (k, e) in all) CloseSession(k, e.Session);
    }

    public void Dispose() => CloseAll();

    private static bool Allowed(AgentConfig c, string deviceId, string widgetKey) =>
        c.Terminal.Enabled && PanelWidgets.Find(c, deviceId, widgetKey, "terminal")?.Bool("control") == true;

    // Call under _lock: takes the session out of the dictionary and queues it to be closed once the lock is free.
    private void Detach((string Device, string Widget) key, Entry entry, List<((string Device, string Widget), TerminalSession)> closing)
    {
        _sessions.Remove(key);
        closing.Add((key, entry.Session));
    }

    private void CloseSession((string Device, string Widget) key, TerminalSession session)
    {
        try { session.Close(); }
        catch (Exception ex) { log.LogWarning("terminal session close failed for device {Device}, widget {Widget}: {Error}", key.Device, key.Widget, ex.GetType().Name); }
        log.LogInformation("terminal session closed for device {Device}, widget {Widget}", key.Device, key.Widget);
    }
}

/// <summary>Runs <see cref="TerminalSessions.Sweep"/> every second, and closes every shell when the agent stops.</summary>
public sealed class TerminalSweeper(TerminalSessions sessions, ILogger<TerminalSweeper> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                try { sessions.Sweep(); }
                catch (Exception ex) { log.LogWarning("terminal sweep failed: {Error}", ex.GetType().Name); }
            }
        }
        catch (OperationCanceledException) { }
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        try { await base.StopAsync(ct); }
        finally { sessions.CloseAll(); }
    }
}
