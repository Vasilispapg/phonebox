using System.Buffers;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace PhoneBox.Agent;

public enum OutboxState { Waiting, Sending, Delivered, Expired, Failed }

/// <summary>What the phone is told about a waiting file: never a path, never the contents.</summary>
public sealed record OutboxItem(string Id, string Name, long Size, string Mime, long CreatedMs);

public sealed record OutboxEntry(OutboxItem Item, OutboxState State);

public enum OutboxAddStatus { Queued, TooLarge, Full, Aborted }

public sealed record OutboxAddResult(OutboxAddStatus Status, OutboxItem? Item);

/// <summary>
/// Files on their way to the phone. Each one is kept under a random 128-bit id in one private folder (the name the caller gave is
/// only data, never a path), until the phone confirms it, or <see cref="FileLimits.OutboxTtl"/> passes. The folder is emptied when
/// the agent starts and when sending is switched off. Only metadata of finished files is remembered, and only for the last 20.
/// </summary>
internal sealed partial class Outbox
{
    private const int KeepFinished = 20;

    private readonly string _folder;
    private readonly TimeProvider _time;
    private readonly long _maxFile;
    private readonly long _total;
    private readonly Lock _lock = new();
    private readonly List<OutboxEntry> _entries = [];
    private long _bytes; // waiting and in-flight files together

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9.+_-]{0,60}/[A-Za-z0-9][A-Za-z0-9.+_-]{0,60}$")]
    private static partial Regex MimePattern();

    public Outbox(string folder, TimeProvider time, long maxFileBytes = FileLimits.MaxFileBytes, long totalBytes = FileLimits.OutboxTotalBytes)
    {
        (_folder, _time, _maxFile, _total) = (folder, time, maxFileBytes, totalBytes);
        Directory.CreateDirectory(folder);
        EmptyFolder();
    }

    /// <summary>Files that wait for the phone (not yet started).</summary>
    public int WaitingCount { get { lock (_lock) return _entries.Count(e => e.State == OutboxState.Waiting); } }

    private long Now => _time.GetUtcNow().ToUnixTimeMilliseconds();

    public async Task<OutboxAddResult> AddAsync(string? rawName, long? declaredSize, string? mime, Stream body, CancellationToken ct)
    {
        if (declaredSize > _maxFile) return new(OutboxAddStatus.TooLarge, null);
        if (declaredSize is { } d && Interlocked.Read(ref _bytes) + d > _total) return new(OutboxAddStatus.Full, null);
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var path = Path.Combine(_folder, id);
        var part = path + ".part";
        long written = 0;
        var added = 0L; // what this call has put in the byte count so far
        try
        {
            Directory.CreateDirectory(_folder);
            var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
            try
            {
                await using var output = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.Asynchronous);
                while (true)
                {
                    var n = await body.ReadAsync(buffer.AsMemory(), ct);
                    if (n == 0) break;
                    written += n;
                    if (written > _maxFile) return new(OutboxAddStatus.TooLarge, null);
                    if (declaredSize is { } expected && written > expected) return new(OutboxAddStatus.Aborted, null);
                    if (Interlocked.Add(ref _bytes, n) > _total) { added += n; return new(OutboxAddStatus.Full, null); }
                    added += n;
                    await output.WriteAsync(buffer.AsMemory(0, n), ct);
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
            if (declaredSize is { } want && written != want) return new(OutboxAddStatus.Aborted, null);
            File.Move(part, path);
            var item = new OutboxItem(id, ReceivedFileName.Sanitize(rawName), written, ValidMime(mime), Now);
            lock (_lock) _entries.Insert(0, new OutboxEntry(item, OutboxState.Waiting));
            added = 0; // kept: it now counts until it is delivered or expires
            return new(OutboxAddStatus.Queued, item);
        }
        catch (OperationCanceledException) { return new(OutboxAddStatus.Aborted, null); }
        catch (IOException) { return new(OutboxAddStatus.Aborted, null); }
        finally
        {
            if (added != 0) Interlocked.Add(ref _bytes, -added);
            try { File.Delete(part); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static string ValidMime(string? mime) =>
        !string.IsNullOrEmpty(mime) && MimePattern().IsMatch(mime) ? mime : "application/octet-stream";

    /// <summary>Newest first: the waiting files and the last 20 finished ones.</summary>
    public IReadOnlyList<OutboxEntry> List() { lock (_lock) return [.. _entries]; }

    /// <summary>The file's bytes for the phone, which marks it as being sent. Null for an id that is malformed, unknown or finished.</summary>
    public Stream? Open(string id)
    {
        if (!IdPattern().IsMatch(id)) return null;
        lock (_lock)
        {
            var index = _entries.FindIndex(e => e.Item.Id == id && e.State is OutboxState.Waiting or OutboxState.Sending);
            if (index < 0) return null;
            try
            {
                var stream = new FileStream(Path.Combine(_folder, id), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                _entries[index] = _entries[index] with { State = OutboxState.Sending };
                return stream;
            }
            catch (IOException) { return null; }
        }
    }

    /// <summary>The phone has the whole file: it is deleted. False for an id that is not waiting.</summary>
    public bool Done(string id)
    {
        if (!IdPattern().IsMatch(id)) return false;
        lock (_lock)
        {
            var index = _entries.FindIndex(e => e.Item.Id == id && e.State is OutboxState.Waiting or OutboxState.Sending);
            if (index < 0) return false;
            Finish(index, OutboxState.Delivered);
            return true;
        }
    }

    /// <summary>Deletes files older than <see cref="FileLimits.OutboxTtl"/>.</summary>
    public void Sweep()
    {
        var oldest = Now - (long)FileLimits.OutboxTtl.TotalMilliseconds;
        lock (_lock)
        {
            for (var i = 0; i < _entries.Count; i++)
                if (_entries[i].State is OutboxState.Waiting or OutboxState.Sending && _entries[i].Item.CreatedMs <= oldest)
                    Finish(i, OutboxState.Expired);
        }
    }

    /// <summary>Forgets everything and empties the folder (sending was switched off).</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
            Interlocked.Exchange(ref _bytes, 0);
            EmptyFolder();
        }
    }

    /// <summary>Called with the lock held.</summary>
    private void Finish(int index, OutboxState state)
    {
        var entry = _entries[index];
        try { File.Delete(Path.Combine(_folder, entry.Item.Id)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        Interlocked.Add(ref _bytes, -entry.Item.Size);
        _entries[index] = entry with { State = state };
        var finished = 0;
        for (var i = _entries.Count - 1; i >= 0; i--)
            if (_entries[i].State is OutboxState.Delivered or OutboxState.Expired or OutboxState.Failed && ++finished > KeepFinished)
                _entries.RemoveAt(i);
    }

    private void EmptyFolder()
    {
        try
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(_folder))
            {
                try { if (Directory.Exists(path)) Directory.Delete(path, true); else File.Delete(path); }
                catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
