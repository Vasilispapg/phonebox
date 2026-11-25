using System.Threading.Channels;
using PhoneBox.Agent;

/// <summary>A scripted pty for the terminal tests: pushed bytes come out of ReadAsync, writes and kills are recorded.</summary>
internal sealed class FakePty : IPtyProcess
{
    private readonly Channel<byte[]> _output = Channel.CreateUnbounded<byte[]>();
    private byte[] _rest = [];
    public List<byte[]> Writes { get; } = [];
    public List<(int Cols, int Rows)> Resizes { get; } = [];
    public int Kills { get; private set; }
    public bool Disposed { get; private set; }
    private volatile bool _exited;
    private int _exitChecks;
    /// <summary>How often <see cref="HasExited"/> was asked (on a real pty that is what reaps the shell).</summary>
    public int ExitChecks => Volatile.Read(ref _exitChecks);

    /// <summary>The first this many asks answer "still running" (the shell is not a zombie yet).</summary>
    public int RunningChecks { get; set; }
    private volatile bool _holdExit;
    /// <summary>While held, every ask answers "still running" (a child that never becomes collectable).</summary>
    public void HoldExit() => _holdExit = true;
    public void ReleaseExit() => _holdExit = false;

    public bool HasExited
    {
        get
        {
            var n = Interlocked.Increment(ref _exitChecks);
            return _exited && !_holdExit && n > RunningChecks;
        }
    }

    public void Push(params byte[] data) => _output.Writer.TryWrite(data);

    public void End()
    {
        _exited = true;
        _output.Writer.TryComplete();
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
    {
        if (_rest.Length == 0)
        {
            if (!await _output.Reader.WaitToReadAsync(ct)) return 0;
            _rest = await _output.Reader.ReadAsync(ct);
        }
        var n = Math.Min(buffer.Length, _rest.Length);
        _rest.AsSpan(0, n).CopyTo(buffer.Span);
        _rest = _rest[n..];
        return n;
    }

    private readonly ManualResetEventSlim _writeGate = new(true);
    public SemaphoreSlim WriteEntered { get; } = new(0);
    public int CallsAfterDispose { get; private set; }

    /// <summary>Makes Write block (as on a full pty) until <see cref="ReleaseWrites"/> or Kill.</summary>
    public void BlockWrites() => _writeGate.Reset();
    public void ReleaseWrites() => _writeGate.Set();

    public void Write(ReadOnlySpan<byte> data)
    {
        var copy = data.ToArray();
        if (Disposed) CallsAfterDispose++;
        if (!_writeGate.IsSet)
        {
            WriteEntered.Release();
            _writeGate.Wait(TimeSpan.FromSeconds(10));
        }
        lock (Writes) Writes.Add(copy);
    }

    public void Resize(int cols, int rows)
    {
        if (Disposed) CallsAfterDispose++;
        lock (Resizes) Resizes.Add((cols, rows));
    }

    public void Kill()
    {
        Kills++;
        _writeGate.Set();
        End();
    }

    public void Dispose() => Disposed = true;
}
