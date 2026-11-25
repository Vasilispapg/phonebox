using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PhoneBox.Agent;
using Xunit;

/// <summary>
/// A file sent to the phone waits in a private folder under a random id until the phone fetches it or an hour passes. The name
/// the caller gave never becomes a path, the folder never grows past its cap, and a body that breaks off leaves nothing.
/// </summary>
public sealed class OutboxTests : IDisposable
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), "pb-outbox-" + Guid.NewGuid());
    private string Folder => Path.Combine(_root, "outbox");
    private readonly Clock _clock = new();

    private Outbox Make(long maxFile = FileLimits.MaxFileBytes, long total = FileLimits.OutboxTotalBytes) => new(Folder, _clock, maxFile, total);

    private static MemoryStream Body(string text) => new(Encoding.UTF8.GetBytes(text));

    private string[] OnDisk() => Directory.Exists(Folder) ? Directory.GetFileSystemEntries(Folder) : [];

    private sealed class ResetBody : Stream
    {
        private bool _first = true;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (!_first) throw new IOException("connection reset");
            _first = false;
            buffer.Span[..3].Fill((byte)'x');
            return ValueTask.FromResult(3);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task AddStoresUnderARandomIdNotTheName()
    {
        var outbox = Make();
        var result = await outbox.AddAsync("../../x.jpg", 3, "image/jpeg", Body("abc"), default);
        Assert.Equal(OutboxAddStatus.Queued, result.Status);
        var item = result.Item!;
        Assert.Matches("^[0-9a-f]{32}$", item.Id);
        Assert.Equal("x.jpg", item.Name);
        Assert.Equal(3, item.Size);
        Assert.Equal("image/jpeg", item.Mime);
        Assert.Equal("abc", File.ReadAllText(Path.Combine(Folder, item.Id)));
        Assert.Equal([Path.Combine(Folder, item.Id)], OnDisk());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a type")]
    [InlineData("a/b/c")]
    public async Task ATypeThatIsNotATypeBecomesOctetStream(string? mime)
    {
        var result = await Make().AddAsync("a.bin", null, mime, Body("x"), default);
        Assert.Equal("application/octet-stream", result.Item!.Mime);
    }

    [Fact]
    public async Task AFileOverTheLimitIsRefusedWhetherOrNotItsSizeWasDeclared()
    {
        var outbox = Make(maxFile: 10);
        Assert.Equal(OutboxAddStatus.TooLarge, (await outbox.AddAsync("a", 11, null, Body("x"), default)).Status);
        Assert.Equal(OutboxAddStatus.TooLarge, (await outbox.AddAsync("a", null, null, Body(new string('x', 20)), default)).Status);
        Assert.Empty(OnDisk());
    }

    [Fact]
    public async Task TheTotalIsCapped()
    {
        var outbox = Make(maxFile: 100, total: 15);
        Assert.Equal(OutboxAddStatus.Queued, (await outbox.AddAsync("a", 10, null, Body(new string('a', 10)), default)).Status);
        Assert.Equal(OutboxAddStatus.Full, (await outbox.AddAsync("b", 10, null, Body(new string('b', 10)), default)).Status);
        Assert.Equal(OutboxAddStatus.Full, (await outbox.AddAsync("c", null, null, Body(new string('c', 10)), default)).Status);
        Assert.Single(OnDisk());
    }

    [Fact]
    public async Task ADeliveredFileFreesItsRoom()
    {
        var outbox = Make(maxFile: 100, total: 15);
        var first = (await outbox.AddAsync("a", 10, null, Body(new string('a', 10)), default)).Item!;
        Assert.True(outbox.Done(first.Id));
        Assert.Equal(OutboxAddStatus.Queued, (await outbox.AddAsync("b", 10, null, Body(new string('b', 10)), default)).Status);
    }

    [Fact]
    public async Task ABodyThatBreaksOffLeavesNothing()
    {
        var result = await Make().AddAsync("a.mp4", null, null, new ResetBody(), default);
        Assert.Equal(OutboxAddStatus.Aborted, result.Status);
        Assert.Empty(OnDisk());
    }

    [Fact]
    public async Task ASizeThatDoesNotMatchLeavesNothing()
    {
        Assert.Equal(OutboxAddStatus.Aborted, (await Make().AddAsync("a", 10, null, Body("abc"), default)).Status);
        Assert.Equal(OutboxAddStatus.Aborted, (await Make().AddAsync("a", 2, null, Body("abc"), default)).Status);
        Assert.Empty(OnDisk());
    }

    [Fact]
    public async Task OpenReturnsTheBytesAndMarksTheFileSending()
    {
        var outbox = Make();
        var item = (await outbox.AddAsync("a.txt", null, null, Body("hello"), default)).Item!;
        Assert.Equal(OutboxState.Waiting, Assert.Single(outbox.List()).State);
        await using var stream = outbox.Open(item.Id)!;
        using var reader = new StreamReader(stream);
        Assert.Equal("hello", await reader.ReadToEndAsync());
        Assert.Equal(OutboxState.Sending, Assert.Single(outbox.List()).State);
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("A")]
    [InlineData("")]
    [InlineData("0123456789abcdef0123456789abcdef")] // a well-formed id nobody was given
    [InlineData("0123456789abcdef0123456789abcdef0")]
    public async Task OpenOfAnUnknownOrMalformedIdIsNull(string id)
    {
        var outbox = Make();
        await outbox.AddAsync("a", null, null, Body("x"), default);
        Assert.Null(outbox.Open(id));
        Assert.False(outbox.Done(id));
    }

    [Fact]
    public async Task DoneDeletesTheFileAndCannotRunTwice()
    {
        var outbox = Make();
        var item = (await outbox.AddAsync("a", null, null, Body("x"), default)).Item!;
        Assert.True(outbox.Done(item.Id));
        Assert.Empty(OnDisk());
        Assert.Equal(OutboxState.Delivered, Assert.Single(outbox.List()).State);
        Assert.False(outbox.Done(item.Id));
        Assert.Null(outbox.Open(item.Id));
    }

    [Fact]
    public async Task AFileExpiresAfterOneHour()
    {
        var outbox = Make();
        var item = (await outbox.AddAsync("a", null, null, Body("x"), default)).Item!;
        _clock.Now += TimeSpan.FromMinutes(59);
        outbox.Sweep();
        Assert.Equal(OutboxState.Waiting, Assert.Single(outbox.List()).State);
        Assert.Single(OnDisk());
        _clock.Now += TimeSpan.FromMinutes(2);
        outbox.Sweep();
        Assert.Equal(OutboxState.Expired, Assert.Single(outbox.List()).State);
        Assert.Empty(OnDisk());
        Assert.Null(outbox.Open(item.Id));
    }

    [Fact]
    public async Task ClearEmptiesTheFolderAndTheList()
    {
        var outbox = Make();
        await outbox.AddAsync("a", null, null, Body("x"), default);
        await outbox.AddAsync("b", null, null, Body("y"), default);
        outbox.Clear();
        Assert.Empty(OnDisk());
        Assert.Empty(outbox.List());
        Assert.Equal(0, outbox.WaitingCount);
    }

    [Fact]
    public async Task TheListNeverHoldsAPathOrContents()
    {
        var outbox = Make();
        await outbox.AddAsync("secret-name.txt", null, null, Body("TOP SECRET CONTENT"), default);
        var json = JsonSerializer.Serialize(outbox.List());
        Assert.DoesNotContain(_root, json);
        Assert.DoesNotContain("TOP SECRET", json);
    }

    [Fact]
    public void ANewOutboxClearsWhatAnEarlierRunLeft()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(Path.Combine(Folder, "0123456789abcdef0123456789abcdef"), "old");
        File.WriteAllText(Path.Combine(Folder, ".abc.part"), "old");
        Make();
        Assert.Empty(OnDisk());
    }

    [Fact]
    public async Task OnlyTheLastTwentyFinishedEntriesAreKept()
    {
        var outbox = Make();
        for (var i = 0; i < 25; i++)
        {
            var item = (await outbox.AddAsync("f" + i, null, null, Body("x"), default)).Item!;
            outbox.Done(item.Id);
        }
        Assert.Equal(20, outbox.List().Count);
    }

    [Fact]
    public async Task TheListIsNewestFirst()
    {
        var outbox = Make();
        await outbox.AddAsync("first", null, null, Body("x"), default);
        _clock.Now += TimeSpan.FromSeconds(5);
        await outbox.AddAsync("second", null, null, Body("x"), default);
        Assert.Equal(["second", "first"], outbox.List().Select(e => e.Item.Name));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }
}
