using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>
/// What the bridge's HTTP surface answers, and what Wi-Fi may reach. The endpoint lambdas in Program.cs are thin; the
/// decisions live in BridgeApi so they can be tested without starting the agent.
/// </summary>
public sealed class BridgeApiTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pb-bridge-api-" + Guid.NewGuid());
    private readonly string _config;
    private readonly ConfigStore _store;
    private readonly FakePasteboard _pb = new();
    private readonly BridgeStream _stream = new();
    private readonly ClipboardBridge _bridge;
    private int _stamp;

    public BridgeApiTests()
    {
        Directory.CreateDirectory(_root);
        _config = Path.Combine(_root, "config.json");
        _store = new ConfigStore(_config);
        SetSwitch(true);
        _bridge = new ClipboardBridge(_pb, _store, _stream, NullLogger<ClipboardBridge>.Instance);
    }

    private void SetSwitch(bool on)
    {
        File.WriteAllText(_config, "{\"bridge\":{\"clipboard\":" + (on ? "true" : "false") + "}}");
        File.SetLastWriteTimeUtc(_config, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(++_stamp));
    }

    private static int Status(IResult r) => Assert.IsAssignableFrom<IStatusCodeHttpResult>(r).StatusCode!.Value;

    private static HttpRequest Request(string body, string contentType = "application/json")
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.ContentType = contentType;
        ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        return ctx.Request;
    }

    // ---- POST /api/bridge/clipboard ----

    [Fact]
    public void AStoredClipboardIsOkAndLandsOnTheMac()
    {
        Assert.Equal(200, Status(BridgeApi.PostClipboard(_bridge, "hi", "phoneA")));
        Assert.Equal("hi", _pb.Text);
    }

    [Fact]
    public void WhileTheSwitchIsOffTheAnswerIs403AndNothingIsWritten()
    {
        SetSwitch(false);
        Assert.Equal(403, Status(BridgeApi.PostClipboard(_bridge, "hi", "phoneA")));
        Assert.Equal(0, _pb.Writes);
    }

    [Fact]
    public void TooMuchTextIs413()
    {
        Assert.Equal(413, Status(BridgeApi.PostClipboard(_bridge, new string('a', ClipboardBridge.MaxBytes + 1), "phoneA")));
        Assert.Equal(0, _pb.Writes);
    }

    [Fact]
    public void AMacThatRefusesTheWriteIs500()
    {
        _pb.Fail = true;
        Assert.Equal(500, Status(BridgeApi.PostClipboard(_bridge, "hi", "phoneA")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NoTextIs400(string? text) => Assert.Equal(400, Status(BridgeApi.PostClipboard(_bridge, text, "phoneA")));

    [Fact]
    public void OffMacOSTheAnswerIs501()
    {
        Assert.Equal(501, Status(BridgeApi.PostClipboard(null, "hi", "phoneA")));
    }

    // ---- reading the request ----

    [Fact]
    public async Task TheBodyCarriesUnicodeText()
    {
        var (text, error) = await BridgeApi.ReadClipboardBody(Request("{\"text\":\"héllo 🌍\\nline two\"}"));
        Assert.Null(error);
        Assert.Equal("héllo 🌍\nline two", text);
    }

    [Fact]
    public async Task AnOversizedRequestIsRefusedBeforeItIsParsed()
    {
        var big = "{\"text\":\"" + new string('a', BridgeApi.MaxRequestBytes) + "\"}";
        var (text, error) = await BridgeApi.ReadClipboardBody(Request(big));
        Assert.Null(text);
        Assert.Equal(BridgeApi.RequestTooLarge, error);
        Assert.Equal(413, Status(BridgeApi.BodyError(error!)));
    }

    [Fact]
    public async Task SixtyFourKilobytesOfTextEscapedAsUnicodeSequencesIsStillRead()
    {
        // 50,000 characters, each sent as a six-character escape: 300 KB of JSON, 50 KB of text
        var json = "{\"text\":\"" + string.Concat(Enumerable.Repeat("\\u0061", 50_000)) + "\"}";
        var (text, error) = await BridgeApi.ReadClipboardBody(Request(json));
        Assert.Null(error);
        Assert.Equal(new string('a', 50_000), text);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    public async Task BrokenBodiesAreARequestError(string body)
    {
        var (text, error) = await BridgeApi.ReadClipboardBody(Request(body));
        Assert.Null(text);
        Assert.NotNull(error);
        Assert.Equal(400, Status(BridgeApi.BodyError(error!)));
    }

    [Theory]
    [InlineData("{\"text\":\"\\ud800\"}")]          // a high surrogate with no low one
    [InlineData("{\"text\":\"a\\udc00b\"}")]        // a low surrogate on its own
    public async Task ATextThatIsNotValidUnicodeIsARequestErrorNotAServerError(string body)
    {
        var (text, error) = await BridgeApi.ReadClipboardBody(Request(body));
        Assert.Null(text);
        Assert.NotNull(error);
        Assert.Equal(400, Status(BridgeApi.BodyError(error!)));
    }

    [Fact]
    public async Task BytesThatAreNotUtf8AreARequestErrorNotAServerError()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Body = new MemoryStream([.. "{\"text\":\""u8, 0xff, 0xfe, .. "\"}"u8]);
        var (text, error) = await BridgeApi.ReadClipboardBody(ctx.Request);
        Assert.Null(text);
        Assert.Equal(400, Status(BridgeApi.BodyError(error!)));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"text\":5}")]
    [InlineData("[\"text\"]")]
    public async Task ABodyWithoutTextGivesNoText(string body)
    {
        var (text, error) = await BridgeApi.ReadClipboardBody(Request(body));
        Assert.Null(error);
        Assert.Null(text);
    }

    // ---- GET /api/bridge/stream ----

    [Fact]
    public void TheStreamFollowsTheSwitch()
    {
        Assert.True(BridgeApi.StreamAllowed(_store));
        SetSwitch(false);
        Assert.False(BridgeApi.StreamAllowed(_store));
    }

    [Fact]
    public void AnEventFrameKeepsNewlinesInsideTheJsonString()
    {
        // text with blank lines and a fake "event:" line must not be able to end the frame early or start another one
        var frame = BridgeApi.EventFrame(new BridgeEvent(7, "clipboard", "a\n\nevent: evil\r\ndata: x", null));
        Assert.StartsWith("event: clipboard\ndata: ", frame);
        Assert.Equal(frame.Length - 2, frame.IndexOf("\n\n", StringComparison.Ordinal));
        Assert.DoesNotContain("\r", frame);
        Assert.Contains("\"seq\":7", frame);
        Assert.Contains("a\\n\\nevent: evil\\r\\ndata: x", frame);
    }

    [Fact]
    public void ThePingIsAnSseComment() => Assert.Equal(": ping\n\n", BridgeApi.PingFrame);

    /// <summary>A response body that records what is written; after <paramref name="blockAfter"/> writes it stops taking
    /// more, like a phone that stopped reading, until the write is cancelled.</summary>
    private sealed class PhoneBody(int blockAfter = int.MaxValue) : Stream
    {
        private readonly StringBuilder _text = new();
        private int _writes;
        public string Text { get { lock (_text) return _text.ToString(); } }
        public int Writes => Volatile.Read(ref _writes);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _writes) > blockAfter) await Task.Delay(Timeout.Infinite, ct);
            lock (_text) _text.Append(Encoding.UTF8.GetString(buffer.Span));
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer, offset, count, default).GetAwaiter().GetResult();
        public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
        public override void Flush() { }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private static DefaultHttpContext StreamContext(PhoneBody body, string? device)
    {
        var ctx = new DefaultHttpContext();
        ctx.Response.Body = body;
        if (device is not null) ctx.Request.Headers["X-PhoneBox-Device"] = device;
        return ctx;
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }

    [Fact]
    public async Task TheStreamSendsWhatOthersCausedSkipsThePhonesOwnAndEndsWhenTheSwitchIsTurnedOff()
    {
        var body = new PhoneBody();
        var run = BridgeApi.StreamAsync(StreamContext(body, "phoneA"), _store, _stream, CancellationToken.None);
        await Until(() => body.Text.StartsWith(BridgeApi.PingFrame, StringComparison.Ordinal));
        _stream.Publish("clipboard", "sent by phoneA", "phoneA");
        _stream.Publish("clipboard", "copied on the Mac", null);
        await Until(() => body.Text.Contains("copied on the Mac"));
        SetSwitch(false);
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.DoesNotContain("sent by phoneA", body.Text);
    }

    [Fact]
    public async Task TheStreamEndsWhenTheAgentBeginsToStop()
    {
        // otherwise a restart, an update or Quit waits for the server's shutdown timeout while a phone has the stream open
        using var stopping = new CancellationTokenSource();
        var body = new PhoneBody();
        var run = BridgeApi.StreamAsync(StreamContext(body, "phoneA"), _store, _stream, stopping.Token);
        await Until(() => body.Writes > 0);
        stopping.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AWriteStuckOnAPhoneThatStoppedReadingEndsWhenTheSwitchIsTurnedOff()
    {
        var body = new PhoneBody(blockAfter: 1);    // the first ping goes through, then the phone takes nothing more
        var run = BridgeApi.StreamAsync(StreamContext(body, "phoneA"), _store, _stream, CancellationToken.None);
        await Until(() => body.Writes == 1);
        _stream.Publish("clipboard", "copied on the Mac", null);
        await Until(() => body.Writes == 2);        // that write is now stuck
        SetSwitch(false);
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.DoesNotContain("copied on the Mac", body.Text);
    }

    [Theory]
    [InlineData("a1B2c3", "a1B2c3")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("has space", null)]
    [InlineData("bad;id", null)]
    [InlineData("12345678901234567890123456789012", "12345678901234567890123456789012")]
    [InlineData("123456789012345678901234567890123", null)]
    public void ADeviceIdIsTakenOnlyWhenItLooksLikeOne(string header, string? expected) =>
        Assert.Equal(expected, BridgeApi.DeviceId(header));

    // ---- what Wi-Fi can reach ----

    [Theory]
    [InlineData("/api/bridge/clipboard", true)]
    [InlineData("/api/bridge/stream", true)]
    [InlineData("/api/bridge", false)]          // the namespace root and other paths under it are not opened
    [InlineData("/api/bridge/other", false)]
    [InlineData("/api/bridge/clipboard/x", false)]
    [InlineData("/api/bridgex", false)]
    [InlineData("/api/bridge-clipboard", false)]
    public void OnlyTheBridgePathsAreOpenOverWifi(string path, bool allowed) =>
        Assert.Equal(allowed, Lan.AllowedOverLan(new PathString(path)));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }
}
