namespace PhoneBox.Agent;

/// <param name="Origin">The device id (X-PhoneBox-Device) of the phone that sent it; null when it came from this computer.</param>
public sealed record BridgeEvent(long Seq, string Name, string Text, string? Origin);

/// <summary>
/// The last few bridge events, in memory only, for the phones' bridge streams to read. It is separate from the panel's
/// PushStore because bridge text must never be kept in data.json, and because the panel's stream may stay paused while a
/// phone's bridge connection stays open.
/// </summary>
public sealed class BridgeStream
{
    private const int MaxEvents = 20;

    private readonly Lock _lock = new();
    private readonly LinkedList<BridgeEvent> _events = new();
    private long _seq;

    public long Seq { get { lock (_lock) return _seq; } }

    /// <summary>Raised after every <see cref="Publish"/>, so a stream handler can wake up and send.</summary>
    public event Action? Emitted;

    public BridgeEvent Publish(string name, string text, string? origin)
    {
        BridgeEvent e;
        lock (_lock)
        {
            e = new BridgeEvent(++_seq, name, text, origin);
            _events.AddLast(e);
            while (_events.Count > MaxEvents) _events.RemoveFirst();
        }
        Emitted?.Invoke();
        return e;
    }

    /// <summary>Events after <paramref name="seq"/>, oldest first, leaving out the ones <paramref name="forDevice"/> sent itself.</summary>
    public IReadOnlyList<BridgeEvent> Since(long seq, string? forDevice)
    {
        lock (_lock) return _events.Where(e => e.Seq > seq && (forDevice is null || e.Origin != forDevice)).ToList();
    }
}
