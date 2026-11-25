using System.Diagnostics;
using System.Security.Cryptography;

namespace PhoneBox.Agent;

/// <summary>Output after a cursor. <see cref="Reset"/> means the cursor was older than the buffer holds, so <see cref="Data"/> starts at the oldest kept byte.</summary>
public readonly record struct TerminalChunk(ulong Cursor, bool Reset, bool Exited, byte[] Data);

/// <summary><see cref="Busy"/>: another batch is still being written (the shell is not reading); nothing was done, send it again later.</summary>
public enum InputOutcome { Applied, Duplicate, OutOfOrder, Closed, Busy }

/// <summary>
/// One shell: a ring buffer of its raw output with a byte cursor for long-poll reads, and numbered input batches
/// applied exactly once. Bytes are copied verbatim; nothing is decoded, logged or written to disk.
/// </summary>
public sealed class TerminalSession : IDisposable
{
    private readonly IPtyProcess _pty;
    private readonly Func<long> _nowMs;
    private readonly byte[] _ring;
    private readonly CancellationTokenSource _stop = new();
    // _lock guards the ring and the wake signal only. _inputLock serialises pty writes/resizes with the sequence check and
    // with disposal; a write to a full pty can block, so nothing that reads output or closes the session may wait on it,
    // and input and resizes wait for it at most InputWaitMs: a request never parks a thread behind a stuck write.
    private const int InputWaitMs = 250;
    private readonly object _lock = new();
    private readonly object _inputLock = new();
    private TaskCompletionSource _changed = NewSignal();
    private ulong _total;
    private ulong _nextInput = 1;
    private bool _exited;
    private bool _closed; // guarded by _inputLock
    private int _closing;
    private long _lastOverlayMs;
    private static readonly TimeSpan ReapEvery = TimeSpan.FromMilliseconds(50);
    private readonly TimeSpan _reapFor;

    public TerminalSession(IPtyProcess pty, Func<long> nowMs, int bufferBytes = 262144) : this(pty, nowMs, TimeSpan.FromSeconds(5), bufferBytes) { }

    internal TerminalSession(IPtyProcess pty, Func<long> nowMs, TimeSpan reapFor, int bufferBytes = 262144)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bufferBytes, 1);
        _pty = pty;
        _nowMs = nowMs;
        _ring = new byte[bufferBytes];
        _lastOverlayMs = nowMs();
        _reapFor = reapFor;
        Reading = Task.Run(ReadLoop);
    }

    /// <summary>The output reader, which ends once the output has ended and the shell is collected (tests).</summary>
    internal Task Reading { get; }

    public long LastOverlayMs => Interlocked.Read(ref _lastOverlayMs);

    /// <summary>The input batch number <see cref="ApplyInput"/> applies next (1 for a fresh shell). Read without the input
    /// lock, so an open never waits on a write to a full pty: the number moves on only once a write has returned, so a
    /// batch still being written reads as next, and its resend is answered Busy until that write is done, then Duplicate.</summary>
    public ulong NextInput => Volatile.Read(ref _nextInput);

    /// <summary>This shell's identity, random and different for every shell: input the phone numbered for another
    /// (one that ended or was replaced) is refused instead of being typed here.</summary>
    public string Id { get; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));

    public bool Exited
    {
        get { lock (_lock) return _exited; }
    }

    /// <summary>Marks overlay activity (a poll, a keystroke) so the session is not reaped as idle.</summary>
    public void Touch() => Interlocked.Exchange(ref _lastOverlayMs, _nowMs());

    public async Task<TerminalChunk> ReadAsync(ulong since, int waitMs, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(waitMs, 0));
        while (true)
        {
            Task signal;
            lock (_lock)
            {
                if (_total != since || _exited) return Snapshot(since);
                signal = _changed.Task;
            }
            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero) return Snapshot(since);
            try { await signal.WaitAsync(left, ct); }
            catch (TimeoutException) { return Snapshot(since); }
        }
    }

    /// <summary>
    /// Writes batch <paramref name="seq"/> if it is the next one. While another batch is still being written (a full pty:
    /// the program in front is not reading) it waits at most a quarter of a second and answers <see cref="InputOutcome.Busy"/>
    /// without writing, so at most one thread per shell ever waits on the pty and a resend is answered at once.
    /// </summary>
    public (InputOutcome Outcome, ulong Expected) ApplyInput(ulong seq, ReadOnlySpan<byte> data)
    {
        if (!Monitor.TryEnter(_inputLock, InputWaitMs)) return (InputOutcome.Busy, NextInput);
        try
        {
            if (_closed) return (InputOutcome.Closed, _nextInput);
            if (seq < _nextInput) return (InputOutcome.Duplicate, _nextInput);
            if (seq > _nextInput) return (InputOutcome.OutOfOrder, _nextInput);
            _pty.Write(data);
            Volatile.Write(ref _nextInput, _nextInput + 1);
            return (InputOutcome.Applied, _nextInput);
        }
        finally { Monitor.Exit(_inputLock); }
    }

    /// <summary>Sets the window size (clamped to 10–500 × 2–200). Skipped while a write is stuck: the next size change sends it.</summary>
    public void Resize(int cols, int rows) => SetSize(Math.Clamp(cols, 10, 500), Math.Clamp(rows, 2, 200), InputWaitMs, redraw: false);

    /// <summary>
    /// For a reattach: sets the size by way of one row less (or more), so the size really changes and the program in front
    /// gets its window-size signal and redraws, even at an unchanged size. Never waits: skipped while a write is stuck.
    /// </summary>
    public void Redraw(int cols, int rows) => SetSize(Math.Clamp(cols, 10, 500), Math.Clamp(rows, 2, 200), 0, redraw: true);

    private void SetSize(int cols, int rows, int waitMs, bool redraw)
    {
        if (!Monitor.TryEnter(_inputLock, waitMs)) return;
        try
        {
            if (_closed) return;
            if (redraw) _pty.Resize(cols, rows > 2 ? rows - 1 : rows + 1);
            _pty.Resize(cols, rows);
        }
        finally { Monitor.Exit(_inputLock); }
    }

    /// <summary>Kills the shell and releases the pty. Safe to call more than once.</summary>
    public void Close()
    {
        if (Interlocked.Exchange(ref _closing, 1) == 1) return;
        _stop.Cancel();
        try { _pty.Kill(); } // first and without the input lock: ending the shell unblocks a write stuck on a full pty
        finally
        {
            lock (_inputLock)
            {
                _closed = true;
                _pty.Dispose();
            }
            lock (_lock) _exited = true;
            Wake();
        }
    }

    public void Dispose() => Close();

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void Wake()
    {
        TaskCompletionSource old;
        lock (_lock)
        {
            old = _changed;
            _changed = NewSignal();
        }
        old.TrySetResult();
    }

    private async Task ReadLoop()
    {
        var buffer = new byte[8192];
        try
        {
            while (true)
            {
                var n = await _pty.ReadAsync(buffer, _stop.Token);
                if (n <= 0) break;
                Append(buffer.AsSpan(0, n));
                Wake();
            }
        }
        catch (Exception)
        {
            // The shell went away (or the session was closed): either way the output ends here.
        }
        lock (_lock) _exited = true;
        Wake();
        await Reap();
    }

    /// <summary>
    /// Asking whether the shell exited is what collects it, so a shell that ended by itself is not left a zombie (holding
    /// its pid) until Restart, End or the idle sweep. The pty can report the end a moment before the shell is collectable,
    /// so it is asked again every 50 ms until it says yes, the session closes (Close kills and collects it itself) or
    /// about 5 s have passed. Readers already know the output ended; nothing here holds a lock or is logged.
    /// </summary>
    private async Task Reap()
    {
        var since = Stopwatch.GetTimestamp();
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                if (_pty.HasExited || Stopwatch.GetElapsedTime(since) >= _reapFor) return;
                await Task.Delay(ReapEvery, _stop.Token);
            }
        }
        catch (Exception)
        {
            // closed meanwhile (OperationCanceledException), or the pty is gone (ObjectDisposedException): nothing to collect
        }
    }

    private void Append(ReadOnlySpan<byte> data)
    {
        lock (_lock)
        {
            var cap = _ring.Length;
            var skipped = 0;
            if (data.Length > cap)
            {
                skipped = data.Length - cap;
                data = data[skipped..];
            }
            var at = (int)((_total + (ulong)skipped) % (ulong)cap);
            var first = Math.Min(data.Length, cap - at);
            data[..first].CopyTo(_ring.AsSpan(at));
            data[first..].CopyTo(_ring);
            _total += (ulong)(skipped + data.Length);
        }
    }

    private TerminalChunk Snapshot(ulong since)
    {
        lock (_lock)
        {
            var cap = (ulong)_ring.Length;
            var oldest = _total > cap ? _total - cap : 0;
            var reset = since < oldest || since > _total;
            var from = reset ? oldest : since;
            var data = new byte[(int)(_total - from)];
            var at = (int)(from % cap);
            var first = Math.Min(data.Length, _ring.Length - at);
            _ring.AsSpan(at, first).CopyTo(data);
            _ring.AsSpan(0, data.Length - first).CopyTo(data.AsSpan(first));
            return new TerminalChunk(_total, reset, _exited, data);
        }
    }
}
