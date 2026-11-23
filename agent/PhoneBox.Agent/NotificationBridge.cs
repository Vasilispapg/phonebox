using System.Security.Cryptography;
using System.Text;

namespace PhoneBox.Agent;

/// <summary>
/// Shows the phone's notifications on this computer while Settings › Phone bridge allows it. Slow macOS notifications never
/// block a request: <see cref="Receive"/> only queues, and one worker posts. Notification text is held in memory only and
/// never logged (the log gets counts and error types), because it can be a login code or a private message.
/// </summary>
public sealed class NotificationBridge : IHostedService, IDisposable
{
    private const int MaxTitle = 200, MaxText = 2000, MaxApp = 100;
    private const int MaxQueued = 50;
    private const int BurstLimit = 10, BurstWindowMs = 10_000;
    private const int MaxRemembered = 500;
    private const long PhoneFreshMs = 10 * 60_000;

    private sealed record Post(string Title, string Subtitle, string Body);

    private readonly IMacNotifier _notifier;
    private readonly ConfigStore _config;
    private readonly ClipboardBridge? _clipboard;
    private readonly Func<long> _clock;
    private readonly ILogger<NotificationBridge> _log;
    private readonly Func<IReadOnlyCollection<PhoneSeen>> _phones;
    private readonly Lock _gate = new();
    private readonly Queue<Post> _queue = new();
    private readonly Dictionary<string, string> _seen = [];       // device|key -> hash of what was shown
    private readonly Queue<string> _seenOrder = new();
    private readonly Dictionary<string, List<long>> _recent = []; // app -> times it was let through in the last 10 s
    private readonly Dictionary<string, int> _extra = [];         // app -> notifications held back, waiting for a summary
    private readonly SemaphoreSlim _wake = new(0);
    private readonly SemaphoreSlim _drain = new(1, 1);
    private CancellationTokenSource? _stop;
    private Task? _worker;

    internal NotificationBridge(IMacNotifier notifier, ConfigStore config, ClipboardBridge? clipboard, Func<long> clock,
        ILogger<NotificationBridge> log, Func<IReadOnlyCollection<PhoneSeen>>? phones = null)
    {
        _notifier = notifier;
        _config = config;
        _clipboard = clipboard;
        _clock = clock;
        _log = log;
        _phones = phones ?? PhoneRegistry.All;
    }

    /// <summary>True while Settings › Phone bridge shows phone notifications; the endpoints refuse before reading a body when it is off.</summary>
    public bool Enabled => _config.Get().Bridge.Notifications;

    public NotifyResult Receive(PhoneNotification n, string? deviceId)
    {
        var bridge = _config.Get().Bridge;
        if (!bridge.Notifications) return NotifyResult.Off;
        var isCall = n.Category is "call" or "missed_call";
        var app = Cut(string.IsNullOrWhiteSpace(n.App) ? n.Package ?? "" : n.App, MaxApp);
        var title = Cut(n.Title ?? "", MaxTitle);
        var text = Cut(n.Text ?? "", MaxText);
        var now = _clock();

        lock (_gate)
        {
            var seenKey = (deviceId ?? "") + "|" + n.Key;
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(title + "\n" + text)));
            if (_seen.TryGetValue(seenKey, out var old) && old == hash) return NotifyResult.Duplicate;
            if (old is null) { _seenOrder.Enqueue(seenKey); while (_seenOrder.Count > MaxRemembered) _seen.Remove(_seenOrder.Dequeue()); }
            _seen[seenKey] = hash;
        }

        // a login code is copied at once, not after the queue: it is what the owner is waiting for
        string? copied = null;
        if (!isCall && bridge.CodesActive && _clipboard is not null)
        {
            var code = OneTimeCode.Find(text) ?? OneTimeCode.Find(title);
            if (code is not null && _clipboard.WriteLocalOnly(code)) copied = code;
        }

        lock (_gate)
        {
            var times = _recent.TryGetValue(app, out var list) ? list : _recent[app] = [];
            times.RemoveAll(t => now - t >= BurstWindowMs);
            if (times.Count >= BurstLimit)
                _extra[app] = _extra.GetValueOrDefault(app) + 1; // shown as one "N more" once the rest is out
            else
            {
                times.Add(now);
                _queue.Enqueue(Format(app, title, text, n.Category, deviceId, copied is not null, now));
                while (_queue.Count > MaxQueued) _queue.Dequeue();
            }
        }
        _wake.Release();
        return NotifyResult.Queued;
    }

    private Post Format(string app, string title, string text, string category, string? deviceId, bool codeCopied, long now)
    {
        var parts = new List<string>();
        if (category == "call") parts.Add("Incoming call");
        else if (category == "missed_call") parts.Add("Missed call");
        else if (title.Length > 0) parts.Add(app);
        // which phone, only when more than one has been around lately
        var recent = _phones().Where(p => now - p.LastSeen <= PhoneFreshMs).ToList();
        if (recent.Count > 1 && recent.FirstOrDefault(p => p.Id == deviceId)?.Model is { Length: > 0 } model) parts.Add(model);
        if (codeCopied) parts.Add("Code copied");
        return new Post(title.Length > 0 ? title : app, string.Join(" · ", parts), text);
    }

    /// <summary>Posts everything queued, then one "N more from App" for each app that went over the burst limit.</summary>
    internal async Task DrainAsync()
    {
        await _drain.WaitAsync();
        try
        {
            while (true)
            {
                Post? next;
                lock (_gate) next = _queue.Count > 0 ? _queue.Dequeue() : null;
                if (next is null) break;
                await Show(next);
            }
            List<KeyValuePair<string, int>> summaries;
            lock (_gate) { summaries = [.. _extra]; _extra.Clear(); }
            foreach (var (app, count) in summaries) await Show(new Post($"{count} more from {app}", "", ""));
        }
        finally { _drain.Release(); }
    }

    private async Task Show(Post post)
    {
        try
        {
            if (!await _notifier.PostAsync(post.Title, post.Subtitle, post.Body, _stop?.Token ?? CancellationToken.None))
                _log.LogWarning("a phone notification could not be shown");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.LogWarning("showing a phone notification failed: {Error}", ex.GetType().Name); }
    }

    private static string Cut(string s, int max)
    {
        if (s.Length <= max) return s;
        var keep = max - 1;
        if (char.IsHighSurrogate(s[keep - 1])) keep--; // never leave half an emoji
        return s[..keep] + "…";
    }

    public Task StartAsync(CancellationToken ct)
    {
        _stop = new CancellationTokenSource();
        var token = _stop.Token;
        _worker = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try { await _wake.WaitAsync(token); await DrainAsync(); }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { _log.LogWarning("the notification worker failed: {Error}", ex.GetType().Name); }
            }
        }, CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _stop?.Cancel();
        if (_worker is not null) try { await _worker.WaitAsync(TimeSpan.FromSeconds(2), ct); } catch { }
    }

    public void Dispose() => _stop?.Dispose();
}
