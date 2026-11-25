using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;
using PhoneBox.Agent;
using Xunit;

/// <summary>
/// The Mac's side of sending a file to the phone: the Quick Action and the settings page's drop zone put a file in the outbox
/// and tell the bridge stream. Only the settings page (a browser's own headers) or the local key may do it, and these paths are
/// never reachable from the phone's ports.
/// </summary>
public sealed class TransferApiTests : IDisposable
{
    private static readonly string[] Origins = ["http://127.0.0.1:8765", "http://localhost:8765"];

    private sealed class BodyLimit : IHttpMaxRequestBodySizeFeature
    {
        public bool IsReadOnly => false;
        public long? MaxRequestBodySize { get; set; } = 30_000_000;
    }

    private sealed class NeverRead : Stream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => throw new InvalidOperationException("the body was read");
        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("the body was read");
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

    private readonly string _root = Path.Combine(Path.GetTempPath(), "pb-transfer-" + Guid.NewGuid());
    private readonly ConfigStore _store;
    private readonly LocalTransferKey _key;
    private readonly Outbox _outbox;
    private readonly BridgeStream _stream = new();
    private int _stamp;

    public TransferApiTests()
    {
        Directory.CreateDirectory(_root);
        _store = new ConfigStore(Path.Combine(_root, "config.json"));
        _key = new LocalTransferKey(Path.Combine(_root, "transfer.json"), 8765);
        _outbox = new Outbox(Path.Combine(_root, "outbox"), TimeProvider.System);
        SetSwitch(true);
        _key.Ensure();
    }

    private void SetSwitch(bool on)
    {
        var path = Path.Combine(_root, "config.json");
        File.WriteAllText(path, "{\"bridge\":{\"filesToPhone\":" + (on ? "true" : "false") + "}}");
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(++_stamp));
    }

    private string KeyValue() => JsonDocument.Parse(File.ReadAllText(_key.Path)).RootElement.GetProperty("key").GetString()!;

    private static DefaultHttpContext Request(string method, string query, Stream body, params (string Name, string Value)[] headers)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.QueryString = new QueryString(query);
        ctx.Request.Body = body;
        foreach (var (name, value) in headers) ctx.Request.Headers[name] = value;
        ctx.Features.Set<IHttpMaxRequestBodySizeFeature>(new BodyLimit());
        return ctx;
    }

    private static int Status(IResult r) => Assert.IsAssignableFrom<IStatusCodeHttpResult>(r).StatusCode!.Value;

    private static string Json(IResult r) => JsonSerializer.Serialize(Assert.IsAssignableFrom<IValueHttpResult>(r).Value);

    // ---- who may ask ----

    [Fact]
    public void ASettingsPageRequestIsAllowedByItsOrigin() =>
        Assert.True(TransferApi.CallerAllowed(Request("PUT", "", Stream.Null, ("Origin", Origins[0])).Request, Origins, _key));

    [Fact]
    public void ASettingsPageGetIsAllowedByTheBrowsersSameOriginMark() =>
        Assert.True(TransferApi.CallerAllowed(Request("GET", "", Stream.Null, ("Sec-Fetch-Site", "same-origin")).Request, Origins, _key));

    [Fact]
    public void ARequestWithNeitherAnOriginNorAKeyIsRefused() =>
        Assert.False(TransferApi.CallerAllowed(Request("PUT", "", Stream.Null).Request, Origins, _key));

    [Theory]
    [InlineData("http://evil.example")]
    [InlineData("http://127.0.0.1:9999")]
    [InlineData("null")]
    public void AForeignOriginIsRefused(string origin) =>
        Assert.False(TransferApi.CallerAllowed(Request("PUT", "", Stream.Null, ("Origin", origin)).Request, Origins, _key));

    [Fact]
    public void ACrossSiteBrowserRequestIsRefused() =>
        Assert.False(TransferApi.CallerAllowed(Request("GET", "", Stream.Null, ("Sec-Fetch-Site", "cross-site")).Request, Origins, _key));

    [Fact]
    public void TheRightKeyIsAllowedWithoutAnOrigin() =>
        Assert.True(TransferApi.CallerAllowed(Request("PUT", "", Stream.Null, (LocalTransferKey.Header, KeyValue())).Request, Origins, _key));

    [Fact]
    public void AWrongKeyIsRefused() =>
        Assert.False(TransferApi.CallerAllowed(Request("PUT", "", Stream.Null, (LocalTransferKey.Header, "nope")).Request, Origins, _key));

    [Fact]
    public void TheKeyOfADeletedFileIsRefused()
    {
        var old = KeyValue();
        _key.Delete();
        Assert.False(TransferApi.CallerAllowed(Request("PUT", "", Stream.Null, (LocalTransferKey.Header, old)).Request, Origins, _key));
        Assert.False(File.Exists(_key.Path));
    }

    [Fact]
    public void TheKeyFileIsPrivateAndKeepsItsKeyAndPort()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(_key.Path));
        var first = KeyValue();
        _key.Ensure();
        Assert.Equal(first, KeyValue());
        Assert.Equal(8765, JsonDocument.Parse(File.ReadAllText(_key.Path)).RootElement.GetProperty("port").GetInt32());
    }

    [Fact]
    public void ANewKeyObjectReadsTheSameKeyFromTheFile()
    {
        var again = new LocalTransferKey(_key.Path, 8765);
        again.Ensure();
        Assert.True(again.Matches(KeyValue()));
    }

    // ---- sending ----

    private Task<IResult> Send(DefaultHttpContext ctx, Outbox? outbox = null) =>
        TransferApi.SendAsync(ctx, outbox ?? _outbox, _stream, _store, _key, Origins);

    [Fact]
    public async Task ASendFromTheSettingsPageQueuesTheFileAndTellsTheStream()
    {
        var ctx = Request("PUT", "?name=photo.jpg", new MemoryStream("abc"u8.ToArray()), ("Origin", Origins[0]));
        ctx.Request.ContentLength = 3;
        var result = await Send(ctx);
        Assert.Equal(202, Status(result));
        var item = Assert.Single(_outbox.List()).Item;
        Assert.Contains(item.Id, Json(result));
        Assert.Equal("photo.jpg", item.Name);
        Assert.Equal("image/jpeg", item.Mime); // taken from the extension, never from the caller
        var e = Assert.Single(_stream.Since(0, null));
        Assert.Equal("file", e.Name);
        Assert.Equal(TransferApi.FileEventJson(item), e.Text);
        Assert.Null(e.Origin);
    }

    [Fact]
    public void TheFileEventCarriesOnlyIdNameSizeAndType()
    {
        var item = new OutboxItem("0123456789abcdef0123456789abcdef", "a \"b\".txt", 12, "text/plain", 5);
        using var doc = JsonDocument.Parse(TransferApi.FileEventJson(item));
        var root = doc.RootElement;
        Assert.Equal("0123456789abcdef0123456789abcdef", root.GetProperty("id").GetString());
        Assert.Equal("a \"b\".txt", root.GetProperty("name").GetString());
        Assert.Equal(12, root.GetProperty("size").GetInt64());
        Assert.Equal("text/plain", root.GetProperty("mime").GetString());
        Assert.Equal(4, root.EnumerateObject().Count());
    }

    [Fact]
    public async Task ASendWithTheKeyAndNoOriginIsAccepted()
    {
        var ctx = Request("PUT", "?name=a.txt", new MemoryStream("x"u8.ToArray()), (LocalTransferKey.Header, KeyValue()));
        Assert.Equal(202, Status(await Send(ctx)));
    }

    [Fact]
    public async Task ARefusalReadsNoBody()
    {
        var body = new NeverRead();
        // nobody told who they are
        Assert.Equal(403, Status(await Send(Request("PUT", "?name=a", body))));
        // the switch is off
        SetSwitch(false);
        var off = await Send(Request("PUT", "?name=a", body, ("Origin", Origins[0])));
        Assert.Equal(403, Status(off));
        Assert.Contains("off", Json(off));
        SetSwitch(true);
        // a name is needed
        Assert.Equal(400, Status(await Send(Request("PUT", "", body, ("Origin", Origins[0])))));
        // the declared size is over the limit
        var big = Request("PUT", "?name=a", body, ("Origin", Origins[0]));
        big.Request.ContentLength = FileLimits.MaxFileBytes + 1;
        Assert.Equal(413, Status(await Send(big)));
        // not macOS: no outbox
        Assert.Equal(501, Status(await TransferApi.SendAsync(Request("PUT", "?name=a", body, ("Origin", Origins[0])), null, _stream, _store, _key, Origins)));
        Assert.Empty(_outbox.List());
    }

    [Fact]
    public async Task AFullOutboxIs507()
    {
        var small = new Outbox(Path.Combine(_root, "small"), TimeProvider.System, 100, 5);
        var ctx = Request("PUT", "?name=a", new MemoryStream(new byte[10]), ("Origin", Origins[0]));
        ctx.Request.ContentLength = 10;
        Assert.Equal(507, Status(await Send(ctx, small)));
    }

    [Fact]
    public async Task TheBodyLimitIsRaisedForThisRequestOnly()
    {
        var ctx = Request("PUT", "?name=a", new MemoryStream([1]), ("Origin", Origins[0]));
        await Send(ctx);
        Assert.Equal(FileLimits.MaxFileBytes, ctx.Features.Get<IHttpMaxRequestBodySizeFeature>()!.MaxRequestBodySize);
    }

    // ---- the list ----

    [Fact]
    public async Task TheStatusListsFilesWithoutPathsOrContents()
    {
        var ctx = Request("PUT", "?name=secret.txt", new MemoryStream("TOP SECRET"u8.ToArray()), ("Origin", Origins[0]));
        await Send(ctx);
        var status = TransferApi.Status(_outbox, Request("GET", "", Stream.Null, ("Sec-Fetch-Site", "same-origin")).Request, _key, Origins);
        Assert.Equal(200, Status(status));
        var json = Json(status);
        Assert.Contains("\"state\":\"waiting\"", json);
        Assert.Contains("secret.txt", json);
        Assert.DoesNotContain("TOP SECRET", json);
        Assert.DoesNotContain(_root, json);
        Assert.Equal(403, Status(TransferApi.Status(_outbox, Request("GET", "", Stream.Null).Request, _key, Origins)));
    }

    // ---- what the phone's ports can reach ----

    [Theory]
    [InlineData("/api/transfer/send")]
    [InlineData("/api/transfer/status")]
    [InlineData("/api/transfer")]
    public void TheTransferPathsAreNotOnTheWifiAllowListOrTheUsbTunnel(string path)
    {
        Assert.False(Lan.AllowedOverLan(new PathString(path)));
        var usb = new UsbLink(18765, "pin");
        usb.SetKey("serial1", "usb-key-0123456789abcdef0123456789abcdef");
        Assert.NotEqual(UsbLink.Access.Allowed, usb.Authorize("PUT", new PathString(path), "", "Bearer usb-key-0123456789abcdef0123456789abcdef"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }
}
