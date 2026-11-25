using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>
/// A file that arrives from the phone is streamed to a hidden part file and renamed when it is whole: a connection that
/// drops, a size that lies or a disk that fills must leave nothing visible and no part file behind.
/// </summary>
public sealed class FileReceiverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pb-files-" + Guid.NewGuid());
    private string Folder => Path.Combine(_root, "PhoneBox");
    private long _free = long.MaxValue / 2;
    private readonly List<string> _quarantined = [];

    private FileReceiver Make(long maxBytes = FileLimits.MaxFileBytes, long margin = FileLimits.SpaceMarginBytes, long checkEvery = 64L << 20) =>
        new(Folder, _ => _free, _quarantined.Add, NullLogger.Instance, maxBytes, margin, checkEvery);

    private static MemoryStream Body(string text) => new(Encoding.UTF8.GetBytes(text));

    private async Task<ReceiveResult> Receive(FileReceiver r, string? name, long? size, Stream body, CancellationToken ct = default)
    {
        Assert.Null(r.Precheck(size)); // takes the upload slot that ReceiveAsync gives back
        return await r.ReceiveAsync(name, size, body, ct);
    }

    private string[] Everything() => Directory.Exists(Folder)
        ? Directory.GetFileSystemEntries(Folder, "*", SearchOption.AllDirectories) : [];

    [Fact]
    public async Task StoresAFileAndQuarantinesIt()
    {
        var result = await Receive(Make(), "hello.txt", 5, Body("hello"));
        Assert.Equal(ReceiveStatus.Stored, result.Status);
        Assert.Equal("hello.txt", result.Name);
        Assert.Equal(5, result.Bytes);
        Assert.Equal("hello", File.ReadAllText(Path.Combine(Folder, "hello.txt")));
        Assert.Equal([Path.Combine(Folder, "hello.txt")], _quarantined);
        Assert.Equal([Path.Combine(Folder, "hello.txt")], Everything()); // no .part left
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
                File.GetUnixFileMode(Path.Combine(Folder, "hello.txt")));
    }

    [Fact]
    public async Task NeverOverwrites()
    {
        var r = Make();
        await Receive(r, "a.txt", null, Body("first"));
        var second = await Receive(r, "a.txt", null, Body("second"));
        Assert.Equal("a (1).txt", second.Name);
        Assert.Equal("first", File.ReadAllText(Path.Combine(Folder, "a.txt")));
        Assert.Equal("second", File.ReadAllText(Path.Combine(Folder, "a (1).txt")));
    }

    [Fact]
    public void RefusesOverTheLimitBeforeReadingAndTakesNoSlot()
    {
        var r = Make();
        Assert.Equal(ReceiveStatus.TooLarge, r.Precheck(FileLimits.MaxFileBytes + 1));
        Assert.Null(r.Precheck(1)); // both slots still free
        Assert.Null(r.Precheck(1));
        Assert.Equal(ReceiveStatus.Busy, r.Precheck(1));
    }

    [Fact]
    public void RefusesWhenFreeSpaceWouldDropBelowTheMargin()
    {
        var r = Make(margin: 1000);
        _free = 5000 + 1000 - 1;
        Assert.Equal(ReceiveStatus.NoSpace, r.Precheck(5000));
        _free = 5000 + 1000;
        Assert.Null(r.Precheck(5000));
    }

    [Fact]
    public void AnUnknownSizeStillNeedsTheMarginFree()
    {
        var r = Make(margin: 1000);
        _free = 999;
        Assert.Equal(ReceiveStatus.NoSpace, r.Precheck(null));
        _free = 1000;
        Assert.Null(r.Precheck(null));
    }

    [Fact]
    public async Task ATwoUploadLimitFreesASlotWhenOneFinishes()
    {
        var r = Make();
        Assert.Null(r.Precheck(1));
        Assert.Null(r.Precheck(1));
        Assert.Equal(ReceiveStatus.Busy, r.Precheck(1));
        await r.ReceiveAsync("a.txt", 1, Body("x"), default); // one of the two held slots ends
        Assert.Null(r.Precheck(1));
    }

    private sealed class DroppingStream(int bytes) : Stream
    {
        private int _sent;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            await Task.Yield();
            if (_sent >= bytes) throw new IOException("connection reset");
            var n = Math.Min(bytes - _sent, buffer.Length);
            buffer.Span[..n].Fill((byte)'x');
            _sent += n;
            return n;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A body of <paramref name="length"/> bytes that is never held in memory.</summary>
    private sealed class FillStream(long length) : Stream
    {
        private long _sent;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var n = (int)Math.Min(length - _sent, buffer.Length);
            buffer.Span[..n].Fill((byte)'y');
            _sent += n;
            return ValueTask.FromResult(n);
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task ADroppedBodyLeavesNothing()
    {
        var result = await Receive(Make(), "video.mp4", null, new DroppingStream(3));
        Assert.Equal(ReceiveStatus.Aborted, result.Status);
        Assert.Empty(Everything());
        Assert.Empty(_quarantined);
    }

    [Fact]
    public async Task ACancelledRequestLeavesNothing()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var result = await Receive(Make(), "a.txt", null, Body("data"), cts.Token);
        Assert.Equal(ReceiveStatus.Aborted, result.Status);
        Assert.Empty(Everything());
    }

    [Fact]
    public async Task ShorterThanDeclaredIsAMismatch()
    {
        var result = await Receive(Make(), "a.txt", 10, Body("abcd"));
        Assert.Equal(ReceiveStatus.SizeMismatch, result.Status);
        Assert.Empty(Everything());
    }

    [Fact]
    public async Task LongerThanDeclaredIsAMismatch()
    {
        var result = await Receive(Make(), "a.txt", 4, Body("abcdefghij"));
        Assert.Equal(ReceiveStatus.SizeMismatch, result.Status);
        Assert.Empty(Everything());
    }

    [Fact]
    public async Task AnUnknownSizeStopsAtTheLimit()
    {
        var result = await Receive(Make(maxBytes: 1000), "big.bin", null, new FillStream(5000));
        Assert.Equal(ReceiveStatus.TooLarge, result.Status);
        Assert.Empty(Everything());
    }

    [Fact]
    public async Task ADiskThatFillsMidWriteIsAborted()
    {
        _free = 10_000;
        var r = Make(margin: 100, checkEvery: 1024);
        Assert.Null(r.Precheck(null));
        _free = 50; // the disk fills up while the file is being written
        var result = await r.ReceiveAsync("a.bin", null, new FillStream(100_000), default);
        Assert.Equal(ReceiveStatus.NoSpace, result.Status);
        Assert.Empty(Everything());
    }

    [Theory]
    [InlineData("../../x", "x")]
    [InlineData("/etc/passwd", "passwd")]
    [InlineData("..", "file")]
    public async Task AHostileNameStaysInsideTheFolder(string raw, string expected)
    {
        var result = await Receive(Make(), raw, null, Body("data"));
        Assert.Equal(expected, result.Name);
        Assert.Equal([Path.Combine(Folder, expected)], Everything());
        Assert.False(File.Exists(Path.Combine(_root, "x")));
    }

    [Fact]
    public async Task AFailingQuarantineKeepsTheFile()
    {
        var r = new FileReceiver(Folder, _ => _free, _ => throw new IOException("xattr failed"), NullLogger.Instance);
        var result = await Receive(r, "a.txt", null, Body("data"));
        Assert.Equal(ReceiveStatus.Stored, result.Status);
        Assert.True(File.Exists(Path.Combine(Folder, "a.txt")));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }
}
