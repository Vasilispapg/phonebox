using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PhoneBox.Agent;

/// <summary>
/// The key the Finder Quick Action sends to the agent's local trigger, kept in a file only the owner can read
/// (<c>{"port":N,"key":"…"}</c>, mode 0600) while sending files to the phone is switched on. A browser page cannot know it, and a
/// process of another user or without access to the owner's files cannot read it. A process running as the owner can, which is
/// the same bar as the rest of the agent's local API: this is not a defence against such a process.
/// </summary>
internal sealed class LocalTransferKey(string path, int port)
{
    public const string Header = "X-PhoneBox-Transfer-Key";

    private readonly Lock _lock = new();
    private string? _key;

    public string Path => path;

    /// <summary>Writes the key file when there is none (a valid one is kept, so a running Quick Action keeps working).</summary>
    public void Ensure()
    {
        lock (_lock)
        {
            if (Read() is { } existing) { _key = existing; return; }
            try { File.Delete(path); } catch (IOException) { }
            var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite; // private from the first byte
            using (var stream = new FileStream(path, options))
                stream.Write(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { port, key })));
            _key = key;
        }
    }

    /// <summary>The key of the file as it is now, or null when the file is missing or does not hold this port's key.</summary>
    private string? Read()
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            return root.TryGetProperty("port", out var p) && p.TryGetInt32(out var value) && value == port
                   && root.TryGetProperty("key", out var k) && k.GetString() is { Length: >= 20 } key ? key : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException) { return null; }
    }

    public void Delete()
    {
        lock (_lock)
        {
            _key = null;
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>A fixed-time comparison; false when there is no key (sending is off).</summary>
    public bool Matches(string? given)
    {
        string? key;
        lock (_lock) key = _key;
        if (key is null || string.IsNullOrEmpty(given)) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(key));
    }
}
