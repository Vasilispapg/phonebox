using System.Buffers;
using System.Diagnostics;
using System.Globalization;

namespace PhoneBox.Agent;

public enum ReceiveStatus { Stored, TooLarge, NoSpace, Busy, SizeMismatch, Aborted, Failed }

/// <param name="Name">The name the file was stored under (null unless stored).</param>
public sealed record ReceiveResult(ReceiveStatus Status, string? Name, long Bytes);

/// <summary>
/// Saves a file streamed from the phone. It is written to a hidden <c>.part</c> file in the same folder and renamed when the
/// body ended and the byte count is right, so a half file is never visible; every other ending deletes it. The name is
/// sanitized, an existing file is never overwritten, and nothing is opened or run. Names and contents are never logged.
/// </summary>
internal sealed class FileReceiver(string folder, Func<string, long> freeSpace, Action<string> quarantine, ILogger log,
    long maxBytes = FileLimits.MaxFileBytes, long marginBytes = FileLimits.SpaceMarginBytes, long spaceCheckBytes = 64L << 20)
{
    private int _slots;

    public string Folder => folder;

    /// <summary>
    /// Answers before the body is read: null means go on, and an upload slot is now held until <see cref="ReceiveAsync"/> (or
    /// <see cref="Release"/>) gives it back. A refusal takes no slot.
    /// </summary>
    public ReceiveStatus? Precheck(long? declaredSize)
    {
        if (declaredSize > maxBytes) return ReceiveStatus.TooLarge;
        long free;
        try { free = freeSpace(folder); } catch (Exception) { free = long.MaxValue; } // not knowing is not a reason to refuse
        if (free < (declaredSize ?? 0) + marginBytes) return ReceiveStatus.NoSpace;
        if (Interlocked.Increment(ref _slots) > FileLimits.MaxConcurrentUploads)
        {
            Interlocked.Decrement(ref _slots);
            return ReceiveStatus.Busy;
        }
        return null;
    }

    public void Release()
    {
        if (Interlocked.Decrement(ref _slots) < 0) Interlocked.Increment(ref _slots);
    }

    public async Task<ReceiveResult> ReceiveAsync(string? rawName, long? declaredSize, Stream body, CancellationToken ct)
    {
        string? part = null;
        long total = 0;
        try
        {
            Directory.CreateDirectory(folder);
            part = Path.Combine(folder, "." + Guid.NewGuid().ToString("N") + ".part");
            var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
            try
            {
                await using var output = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.Asynchronous);
                var nextSpaceCheck = spaceCheckBytes;
                while (true)
                {
                    var n = await body.ReadAsync(buffer.AsMemory(), ct);
                    if (n == 0) break;
                    total += n;
                    if (total > maxBytes) return new(ReceiveStatus.TooLarge, null, total);
                    if (declaredSize is { } d && total > d) return new(ReceiveStatus.SizeMismatch, null, total);
                    await output.WriteAsync(buffer.AsMemory(0, n), ct);
                    if (total >= nextSpaceCheck)
                    {
                        nextSpaceCheck += spaceCheckBytes;
                        if (freeSpace(folder) < marginBytes) return new(ReceiveStatus.NoSpace, null, total);
                    }
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
            if (declaredSize is { } expected && total != expected) return new(ReceiveStatus.SizeMismatch, null, total);

            var (name, destination) = Place(part, ReceivedFileName.Sanitize(rawName));
            part = null; // renamed: nothing to delete any more
            try { quarantine(destination); }
            catch (Exception e) { log.LogWarning("Could not mark a received file as downloaded: {Error}", e.GetType().Name); }
            log.LogInformation("Received a file from the phone ({Bytes} bytes)", total);
            return new(ReceiveStatus.Stored, name, total);
        }
        catch (OperationCanceledException) { return new(ReceiveStatus.Aborted, null, total); }
        catch (IOException) when (SafeFree() < marginBytes) { return new(ReceiveStatus.NoSpace, null, total); }
        catch (IOException) { return new(ReceiveStatus.Aborted, null, total); } // the phone went away, or the disk refused
        catch (Exception e)
        {
            log.LogWarning("Receiving a file failed: {Error}", e.GetType().Name);
            return new(ReceiveStatus.Failed, null, total);
        }
        finally
        {
            if (part is not null) { try { File.Delete(part); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            Release();
        }
    }

    private long SafeFree()
    {
        try { return freeSpace(folder); } catch (Exception) { return long.MaxValue; }
    }

    /// <summary>Moves the part file to a name nobody has; never replaces a file (a name taken in the meantime gets the next number).</summary>
    private (string Name, string Path) Place(string part, string wanted)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var name = ReceivedFileName.Unique(folder, wanted, n => File.Exists(Path.Combine(folder, n)));
            var destination = Path.Combine(folder, name);
            try
            {
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(part, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
                File.Move(part, destination, overwrite: false);
                return (name, destination);
            }
            catch (IOException) when (File.Exists(destination)) { /* taken by someone else just now: the next number */ }
        }
        throw new IOException("no free name");
    }

    /// <summary>The Mac's folder: <c>~/Downloads/PhoneBox</c>, free space of the volume it is on, and the quarantine flag AirDrop and browsers set.</summary>
    public static FileReceiver ForMac(ILogger log)
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "PhoneBox");
        return new FileReceiver(folder, FreeSpaceOf, MarkQuarantined, log);
    }

    /// <summary>Free bytes on the volume that holds <paramref name="path"/> (the mount point with the longest matching prefix).</summary>
    internal static long FreeSpaceOf(string path)
    {
        var full = Path.GetFullPath(path);
        var drive = DriveInfo.GetDrives()
            .Where(d => d.IsReady && full.StartsWith(d.RootDirectory.FullName, StringComparison.Ordinal))
            .OrderByDescending(d => d.RootDirectory.FullName.Length).FirstOrDefault();
        return drive?.AvailableFreeSpace ?? long.MaxValue;
    }

    private static void MarkQuarantined(string path)
    {
        if (!OperatingSystem.IsMacOS()) return;
        var info = new ProcessStartInfo("/usr/bin/xattr") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        info.ArgumentList.Add("-w");
        info.ArgumentList.Add("com.apple.quarantine");
        info.ArgumentList.Add($"0081;{DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString("x", CultureInfo.InvariantCulture)};PhoneBox;");
        info.ArgumentList.Add(path);
        using var process = Process.Start(info) ?? throw new IOException("xattr did not start");
        if (!process.WaitForExit(5000)) { try { process.Kill(); } catch (InvalidOperationException) { } throw new IOException("xattr timed out"); }
        if (process.ExitCode != 0) throw new IOException("xattr failed");
    }
}
