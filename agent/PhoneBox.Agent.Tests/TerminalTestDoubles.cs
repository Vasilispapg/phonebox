using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using PhoneBox.Agent;

// Test doubles shared by the terminal tests.

/// <summary>Keeps every log entry (category, message and structured values) so a test can search it for secrets.</summary>
[ProviderAlias("Capture")]
internal sealed class CaptureProvider : ILoggerProvider
{
    public ConcurrentQueue<string> Entries { get; } = new();
    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, Entries);
    public void Dispose() { }

    private sealed class Logger(string category, ConcurrentQueue<string> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var structured = state is IEnumerable<KeyValuePair<string, object?>> kv ? string.Join(" ", kv.Select(p => $"{p.Key}={p.Value}")) : "";
            entries.Enqueue($"{category} {formatter(state, exception)} {structured} {exception}");
        }
    }
}

/// <summary>A pty that misbehaves on purpose: writes and resizes that throw, or a kill that takes its time.</summary>
internal sealed class StubPty : IPtyProcess
{
    public const string Secret = "stub-pty-internal-detail";
    public bool ThrowOnWrite { get; init; }
    public bool ThrowOnResize { get; init; }
    public int KillDelayMs { get; init; }
    private int _kills;
    public int Kills => Volatile.Read(ref _kills);
    public bool HasExited { get; private set; }

    private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The shell ends on its own: reads return 0.</summary>
    public void End() => _ended.TrySetResult();

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
    {
        try { await _ended.Task.WaitAsync(ct); } catch (OperationCanceledException) { }
        return 0;
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        if (ThrowOnWrite) throw new IOException(Secret);
    }

    public void Resize(int cols, int rows)
    {
        if (ThrowOnResize) throw new ObjectDisposedException(Secret);
    }

    public void Kill()
    {
        if (KillDelayMs > 0) Thread.Sleep(KillDelayMs);
        HasExited = true;
        Interlocked.Increment(ref _kills);
    }

    public void Dispose() { }
}
