using System.Globalization;
using System.Text.RegularExpressions;

namespace PhoneBox.Agent;

/// <summary>
/// Master volume on Linux (PipeWire's wpctl, or PulseAudio's pactl) and the selected macOS output.
/// macOS falls back to CoreAudio channels and, for a matching display, optional DDC/CI readback.
/// </summary>
public sealed partial class AudioControl
{
    private const int MaxAgeMs = 900;
    private readonly Lock _lock = new();
    private readonly Func<AudioSnapshot?> _read;
    private AudioSnapshot? _cached;
    private long _at;          // when _cached was read; 0 = never read or invalidated
    private long _gen;         // bumped by Invalidate, so a read from before a volume change cannot land after it
    private long _seq, _written; // read tickets: a reading only lands if no later-started read has landed
    private int _refreshing;   // 1 while a background read runs

    private const int SetTimeoutMs = 8000; // a busy computer can need seconds; giving up kills the helper and nothing is set
    private readonly Func<int, bool>? _set;

    public AudioControl() : this(Read, null) { }

    /// <summary>For tests: a fake reader (and optionally setter) instead of osascript / wpctl / pactl.</summary>
    internal AudioControl(Func<AudioSnapshot?> read, Func<int, bool>? set = null) { _read = read; _set = set; }

    internal bool Refreshing => Volatile.Read(ref _refreshing) == 1;

    /// <summary>
    /// The last reading, refreshed in the background. Never waits for osascript / wpctl / pactl: the live stream
    /// calls this every second for every phone, and under heavy CPU load those helpers can take seconds each.
    /// </summary>
    public AudioSnapshot? Snapshot()
    {
        long at; AudioSnapshot? cached;
        lock (_lock) { at = _at; cached = _cached; }
        if (Environment.TickCount64 - at >= MaxAgeMs && Interlocked.CompareExchange(ref _refreshing, 1, 0) == 0)
            _ = Task.Run(() =>
            {
                try { var (ticket, gen) = BeginRead(); Store(ticket, gen, _read()); }
                catch { }
                finally { Volatile.Write(ref _refreshing, 0); }
            });
        return cached;
    }

    /// <summary>A reading for a control action: the cached one while fresh, otherwise read now (may wait for the helper).</summary>
    public AudioSnapshot? Current()
    {
        lock (_lock)
            if (_at != 0 && Environment.TickCount64 - _at < MaxAgeMs) return _cached;
        var (ticket, gen) = BeginRead();
        var s = _read();
        Store(ticket, gen, s);
        return s;
    }

    private (long Ticket, long Gen) BeginRead() { lock (_lock) return (++_seq, _gen); }

    private void Store(long ticket, long gen, AudioSnapshot? s)
    {
        bool changed;
        lock (_lock)
        {
            if (_gen != gen || ticket <= _written) return;
            // the volume moved from outside (the keyboard's volume keys, another app): the phone should see it now
            changed = _cached is not null && s is not null && (_cached.Volume != s.Volume || _cached.Muted != s.Muted);
            _written = ticket; _cached = s; _at = Environment.TickCount64;
        }
        if (changed) StateSignal.Poke();
    }

    // control actions run one at a time, each with its cache update inside: two requests at once (a dragged slider) must
    // leave the cache with the volume the device really ended on, not the one whose write happened to finish last
    private readonly object _controlGate = new();

    public bool SetVolume(int level)
    {
        if (OperatingSystem.IsMacOS() && !CanControlNow()) return false;
        level = Math.Clamp(level, 0, 100);
        lock (_controlGate)
        {
            var ok = _set?.Invoke(level) ?? SetOnThisSystem(level);
            // On success the new volume is what the next state shows: the old reading would make the slider snap back until
            // the background read (seconds, on a busy computer) catches up, which then looks like "nothing changed".
            if (ok)
            {
                AudioSnapshot? before;
                lock (_lock) before = _cached;
                Remember(new AudioSnapshot(level, MutedAfterSet(_set is not null || ClearsMute(level), before), true));
            }
            else Invalidate();
            return ok;
        }
    }

    /// <summary>
    /// The mute state a volume write leaves behind. A write that also clears mute leaves the sink unmuted; one that only sets
    /// the volume (pactl, wpctl at 0) leaves it as it was.
    /// </summary>
    internal static bool MutedAfterSet(bool clearsMute, AudioSnapshot? before) => !clearsMute && before?.Muted == true;

    /// <summary>Whether the write that SetOnThisSystem makes for this level also unmutes: macOS always does, wpctl above 0.</summary>
    private static bool ClearsMute(int level) => OperatingSystem.IsMacOS() || (Sh.Which("wpctl") is not null && level > 0);

    private static bool SetOnThisSystem(int level) => OperatingSystem.IsMacOS()
        ? MacAudioProbe.SetVolume(level) // in-process; osascript only for an output CoreAudio cannot control
          || Sh.RunWithTimeout(SetTimeoutMs, "osascript", "-e", $"set volume output volume {level} without output muted") is not null
        : Sh.Which("wpctl") is not null
            ? Sh.RunWithTimeout(SetTimeoutMs, "wpctl", "set-volume", "@DEFAULT_AUDIO_SINK@", (level / 100.0).ToString("0.00", CultureInfo.InvariantCulture)) is not null
              && (level == 0 || Sh.RunWithTimeout(SetTimeoutMs, "wpctl", "set-mute", "@DEFAULT_AUDIO_SINK@", "0") is not null)
            : Sh.RunWithTimeout(SetTimeoutMs, "pactl", "set-sink-volume", "@DEFAULT_SINK@", $"{level}%") is not null;

    /// <summary>
    /// Whether the output can be controlled, from the last reading however old: a fresh read first would put another
    /// slow helper call in front of the set. Only a missing or negative reading is read again.
    /// </summary>
    private bool CanControlNow()
    {
        AudioSnapshot? known;
        lock (_lock) known = _cached;
        return known is { CanControl: true } || Current() is { CanControl: true };
    }

    /// <summary>Takes a known volume as the current reading and drops any read that started before it.</summary>
    private void Remember(AudioSnapshot s) { lock (_lock) { _cached = s; _at = Environment.TickCount64; _gen++; } }

    public bool Step(int delta) => Current() is { } s && SetVolume(s.Volume + delta);

    public bool ToggleMute()
    {
        if (OperatingSystem.IsMacOS() && Current() is not { CanControl: true }) return false;
        lock (_controlGate)
        {
            var ok = OperatingSystem.IsMacOS()
                ? MacAudioProbe.ToggleMute()
                  || Sh.RunWithTimeout(SetTimeoutMs, "osascript", "-e", "set volume output muted (not (output muted of (get volume settings)))") is not null
                : Sh.Which("wpctl") is not null
                    ? Sh.Run("wpctl", "set-mute", "@DEFAULT_AUDIO_SINK@", "toggle") is not null
                    : Sh.Run("pactl", "set-sink-mute", "@DEFAULT_SINK@", "toggle") is not null;
            Invalidate();
            return ok;
        }
    }

    private void Invalidate() { lock (_lock) { _at = 0; _gen++; } }

    private static AudioSnapshot? Read()
    {
        if (OperatingSystem.IsMacOS())
        {
            var output = MacAudioProbe.SelectedOutput();
            // a controllable output is read in-process (microseconds); osascript is for the rest (a display's own volume...)
            if (output is { Volume: { } nativeVolume, Muted: { } nativeOutputMuted } native && MacAudioProbe.CanControl(native.Id))
                return new AudioSnapshot(nativeVolume, nativeOutputMuted, true);
            var s = Sh.Run("osascript", "-e", "set v to get volume settings\nreturn ((output volume of v) as string) & \",\" & ((output muted of v) as string)")?.Trim();
            var p = s?.Split(',');
            if (p is { Length: 2 } && int.TryParse(p[0], out var vol) && bool.TryParse(p[1], out var nativeMuted))
                return new AudioSnapshot(vol, nativeMuted, output is { } selected && MacAudioProbe.CanControl(selected.Id));
            if (output is null) return null;
            var level = output.Value.Volume ?? MacAudioProbe.DisplayVolume(output.Value.Id, output.Value.Name);
            return level is { } measured ? new AudioSnapshot(measured, output.Value.Muted ?? false, false) : null;
        }
        if (Sh.Which("wpctl") is not null)
        {
            // "Volume: 0.45 [MUTED]"
            var m = WpctlRe().Match(Sh.Run("wpctl", "get-volume", "@DEFAULT_AUDIO_SINK@") ?? "");
            if (m.Success) return new AudioSnapshot((int)Math.Round(double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 100), m.Groups[2].Success);
        }
        var vol2 = PercentRe().Match(Sh.Run("pactl", "get-sink-volume", "@DEFAULT_SINK@") ?? "");
        if (!vol2.Success) return null;
        var muted = (Sh.Run("pactl", "get-sink-mute", "@DEFAULT_SINK@") ?? "").Contains("yes");
        return new AudioSnapshot(int.Parse(vol2.Groups[1].Value), muted);
    }

    [GeneratedRegex(@"Volume:\s*([\d.]+)(\s*\[MUTED\])?")] private static partial Regex WpctlRe();
    [GeneratedRegex(@"(\d+)%")] private static partial Regex PercentRe();
}
