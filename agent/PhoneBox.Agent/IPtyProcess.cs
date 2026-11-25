namespace PhoneBox.Agent;

/// <summary>A shell running in a pseudo-terminal. Output is raw bytes: nothing here decodes, logs or stores it.</summary>
public interface IPtyProcess : IDisposable
{
    /// <summary>Reads shell output into <paramref name="buffer"/>; returns 0 when the shell has ended.</summary>
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct);
    void Write(ReadOnlySpan<byte> data);
    void Resize(int cols, int rows);
    void Kill();
    bool HasExited { get; }
}

public delegate IPtyProcess PtyFactory(int cols, int rows);
