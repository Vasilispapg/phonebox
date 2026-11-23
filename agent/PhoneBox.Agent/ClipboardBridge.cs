using System.Security.Cryptography;
using System.Text;

namespace PhoneBox.Agent;

public enum ClipboardResult { Stored, Off, TooLarge, Failed }

/// <summary>
/// Shares the clipboard's plain text with the paired phones, both ways, while Settings › Phone bridge allows it.
/// Text is held in memory only and never logged: the log gets counts and error types, nothing a person copied.
/// </summary>
public sealed class ClipboardBridge : IHostedService, IDisposable
{
    /// <summary>The most text the bridge moves, in UTF-8 bytes (not characters).</summary>
    public const int MaxBytes = 64 * 1024;
    private const int PollMs = 400;

    private readonly ITextPasteboard _pasteboard;
    private readonly ConfigStore _config;
    private readonly BridgeStream _stream;
    private readonly ILogger<ClipboardBridge> _log;
    private readonly Lock _gate = new();
    private System.Threading.Timer? _timer;
    private bool _primed;
    private long _lastCount;
    private byte[]? _lastReceived; // hash of the text just written for a phone, until its echo is seen once
    private volatile bool _stopped;

    internal ClipboardBridge(ITextPasteboard pasteboard, ConfigStore config, BridgeStream stream, ILogger<ClipboardBridge> log)
    {
        _pasteboard = pasteboard;
        _config = config;
        _stream = stream;
        _log = log;
    }

    /// <summary>Text from a phone: put it on this computer's clipboard and hand it to the other phones.</summary>
    public ClipboardResult ReceiveFromPhone(string text, string? deviceId)
    {
        if (!_config.Get().Bridge.Clipboard) return ClipboardResult.Off;
        if (Encoding.UTF8.GetByteCount(text) > MaxBytes) return ClipboardResult.TooLarge;
        lock (_gate)
        {
            if (!_pasteboard.WriteText(text)) return ClipboardResult.Failed;
            _lastReceived = Hash(text);
        }
        _stream.Publish("clipboard", text, deviceId);
        return ClipboardResult.Stored;
    }

    /// <summary>One look at the Mac's clipboard (the timer calls this every 400 ms).</summary>
    internal void PollOnce()
    {
        lock (_gate)
        {
            if (!_config.Get().Bridge.Clipboard)
            {
                _primed = false; // when it is turned on again, what is on the clipboard then must not be sent
                _lastReceived = null;
                return;
            }
            var count = _pasteboard.ChangeCount;
            if (!_primed) { _primed = true; _lastCount = count; return; }
            if (count == _lastCount) return;
            _lastCount = count;
            var text = _pasteboard.ReadShareableText();
            if (text is null || Encoding.UTF8.GetByteCount(text) > MaxBytes) return;
            // the change may be our own write for a phone: swallow that echo once, so copying the same words again later still goes out
            if (_lastReceived is not null && Hash(text).AsSpan().SequenceEqual(_lastReceived)) { _lastReceived = null; return; }
            _stream.Publish("clipboard", text, null);
        }
    }

    internal void Tick()
    {
        try { PollOnce(); }
        catch (Exception ex) { _log.LogWarning("clipboard poll failed: {Error}", ex.GetType().Name); }
    }

    private static byte[] Hash(string text) => SHA256.HashData(Encoding.UTF8.GetBytes(text));

    public Task StartAsync(CancellationToken ct)
    {
        _timer = new System.Threading.Timer(_ =>
        {
            Tick();
            if (!_stopped) try { _timer?.Change(PollMs, Timeout.Infinite); } catch (ObjectDisposedException) { }
        }, null, PollMs, Timeout.Infinite);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _stopped = true;
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _stopped = true;
        _timer?.Dispose();
    }
}
