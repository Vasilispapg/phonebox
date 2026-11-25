using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>
/// The bridge endpoints behind the agent's real request gate, on real sockets: a local HTTP port, a USB tunnel port and a
/// Wi-Fi port, both TLS, the way Program.cs sets them up. The ports are free loopback ports picked for the test, never the
/// agent's own; the pasteboard is a fake, so no test touches a clipboard.
/// </summary>
public sealed class BridgeGateTests : IAsyncLifetime
{
    private const string UsbKey = "usb-key-0123456789abcdef0123456789abcdef";
    private const string WifiKey = "wifi-key-0123456789abcdef0123456789abcdef";
    private const string WebhookKey = "hook-key-0123456789abcdef0123456789abcdef";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "pb-bridge-gate-" + Guid.NewGuid());
    private readonly string _config;
    private readonly ConfigStore _store;
    private readonly FakePasteboard _pb = new();
    private readonly BridgeStream _stream = new();
    private readonly Outbox _outbox;
    private readonly List<(string Title, string Subtitle, string Body)> _shown = [];
    private NotificationBridge? _notifications;
    private long _now = 1_000_000;
    private int _stamp;
    private WebApplication? _app;
    private X509Certificate2? _cert;
    private HttpClient? _http;
    private UsbLink? _usb;
    private int _local, _usbPort, _wifi;

    public BridgeGateTests()
    {
        Directory.CreateDirectory(_root);
        _config = Path.Combine(_root, "config.json");
        _store = new ConfigStore(_config);
        _outbox = new Outbox(Path.Combine(_root, "outbox"), TimeProvider.System);
        WriteConfig(wifiOn: true, wifiKey: WifiKey, clipboard: true);
    }

    private sealed class RecordingNotifier(List<(string, string, string)> shown) : IMacNotifier
    {
        public Task<bool> PostAsync(string title, string subtitle, string body, byte[]? iconPng, string? replaceId, CancellationToken ct)
        {
            lock (shown) shown.Add((title, subtitle, body));
            return Task.FromResult(true);
        }
    }

    private void WriteConfig(bool wifiOn, string wifiKey, bool clipboard, bool notifications = true, bool filesToPhone = false)
    {
        static string Bool(bool b) => b ? "true" : "false";
        File.WriteAllText(_config, "{\"lan\":{\"enabled\":" + Bool(wifiOn) + ",\"token\":\"" + wifiKey + "\",\"webhookToken\":\"" + WebhookKey
            + "\"},\"bridge\":{\"clipboard\":" + Bool(clipboard) + ",\"notifications\":" + Bool(notifications)
            + ",\"filesToPhone\":" + Bool(filesToPhone) + "}}");
        File.SetLastWriteTimeUtc(_config, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(++_stamp));
    }

    public async Task InitializeAsync()
    {
        _cert = Lan.Certificate(_root);
        for (var attempt = 0; ; attempt++)
        {
            (_local, _usbPort, _wifi) = (UsbLink.FreeLoopbackPort(), UsbLink.FreeLoopbackPort(), UsbLink.FreeLoopbackPort());
            if (_local == _usbPort || _local == _wifi || _usbPort == _wifi) continue;
            try { _app = await StartAsync(); break; }
            catch (IOException) when (attempt < 5) { /* another process took a port between picking and binding it */ }
        }
        var thumbprint = _cert.Thumbprint;
        _http = new HttpClient(new SocketsHttpHandler
        {
            UseProxy = false,
            SslOptions = { RemoteCertificateValidationCallback = (_, cert, _, _) => cert is not null && cert.GetCertHashString() == thumbprint },
        }) { Timeout = TimeSpan.FromSeconds(20) };
    }

    private async Task<WebApplication> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrelHttpsConfiguration();
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Listen(IPAddress.Loopback, _local);
            k.Listen(IPAddress.Loopback, _usbPort, l => l.UseHttps(_cert!));
            k.Listen(IPAddress.Loopback, _wifi, l => l.UseHttps(_cert!)); // the agent's is on every address; loopback is enough here
        });
        builder.Services.AddSingleton(_store);
        builder.Services.AddSingleton(_stream);
        builder.Services.AddSingleton(_outbox);
        builder.Services.AddSingleton(_ => new ClipboardBridge(_pb, _store, _stream, NullLogger<ClipboardBridge>.Instance));
        _notifications = new NotificationBridge(new RecordingNotifier(_shown), _store, null, () => Interlocked.Add(ref _now, 60_000),
            NullLogger<NotificationBridge>.Instance, () => []);
        builder.Services.AddSingleton(_notifications);
        var app = builder.Build();
        _usb = new UsbLink(_usbPort, "pin");
        _usb.SetKey("serial1", UsbKey);
        var gate = new RequestGate(_local, [$"http://127.0.0.1:{_local}", $"http://localhost:{_local}"], _usb, _wifi, _store,
            _ => false, NullLogger.Instance);
        app.UseRouting();
        app.Use(gate.InvokeAsync);
        BridgeApi.Map(app, gate);
        // an endpoint the gate alone must keep from the local port, whatever the bridge endpoints check themselves
        app.MapGet("/api/bridge/probe", () => Results.Ok());
        // a path outside the bridge, like the local trigger for sending files: the local port serves it, the phone's ports do not
        app.MapGet("/api/transfer/probe", () => Results.Ok());
        await app.StartAsync();
        return app;
    }

    public async Task DisposeAsync()
    {
        _http?.Dispose();
        if (_app is not null) { await _app.StopAsync(); await _app.DisposeAsync(); }
        _cert?.Dispose();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private string Url(string via, string path) => via switch
    {
        "local" => $"http://127.0.0.1:{_local}{path}",
        "usb" => $"https://127.0.0.1:{_usbPort}{path}",
        _ => $"https://127.0.0.1:{_wifi}{path}",
    };

    private static HttpRequestMessage Message(HttpMethod method, string url, string? key, string? text = null)
    {
        var m = new HttpRequestMessage(method, url);
        if (key is not null) m.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        if (text is not null) m.Content = new StringContent("{\"text\":\"" + text + "\"}", Encoding.UTF8, "application/json");
        return m;
    }

    private async Task<HttpStatusCode> Post(string via, string? key, string text = "hello")
    {
        using var res = await _http!.SendAsync(Message(HttpMethod.Post, Url(via, "/api/bridge/clipboard"), key, text));
        return res.StatusCode;
    }

    private async Task<HttpResponseMessage> OpenStream(string via, string? key, string path = "/api/bridge/stream") =>
        await _http!.SendAsync(Message(HttpMethod.Get, Url(via, path), key), HttpCompletionOption.ResponseHeadersRead);

    // ---- the local port ----

    [Theory]
    [InlineData("/api/bridge/clipboard")]
    [InlineData("/api/bridge/stream")]
    [InlineData("/api/bridge/stream/")]
    [InlineData("/API/BRIDGE/STREAM")]
    [InlineData("/api/bridge/probe")]
    [InlineData("/api/bridge/files/in")]
    public async Task TheLocalPortRefusesTheBridgeEvenWithAPhonesKey(string path)
    {
        foreach (var key in new[] { null, UsbKey, WifiKey })
        {
            using var get = await _http!.SendAsync(Message(HttpMethod.Get, Url("local", path), key));
            Assert.Equal(HttpStatusCode.Forbidden, get.StatusCode);
            using var post = await _http.SendAsync(Message(HttpMethod.Post, Url("local", path), key, "from a local process"));
            Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);
        }
        Assert.Equal(0, _pb.Writes);
    }

    [Fact]
    public async Task TheLocalTriggerPathsAreServedLocallyButNeverToThePhonesPorts()
    {
        using (var local = await _http!.SendAsync(Message(HttpMethod.Get, Url("local", "/api/transfer/probe"), null)))
            Assert.Equal(HttpStatusCode.OK, local.StatusCode);
        foreach (var via in new[] { "usb", "wifi" })
        {
            var key = via == "usb" ? UsbKey : WifiKey;
            using var res = await _http!.SendAsync(Message(HttpMethod.Get, Url(via, "/api/transfer/probe"), key));
            Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        }
    }

    [Fact]
    public async Task ALocalProcessHearsNothingCopiedOnTheMac()
    {
        using var res = await OpenStream("local", null);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.NotEqual("text/event-stream", res.Content.Headers.ContentType?.MediaType);
    }

    // ---- the phone's ports ----

    [Fact]
    public async Task TheUsbPortWithThePhonesKeyIsAccepted()
    {
        Assert.Equal(HttpStatusCode.OK, await Post("usb", UsbKey, "over USB"));
        Assert.Equal("over USB", _pb.Text);
        using var res = await OpenStream("usb", UsbKey);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("text/event-stream", res.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task TheWifiPortWithThePairedKeyIsAccepted()
    {
        Assert.Equal(HttpStatusCode.OK, await Post("wifi", WifiKey, "over Wi-Fi"));
        Assert.Equal("over Wi-Fi", _pb.Text);
        using var res = await OpenStream("wifi", WifiKey);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("text/event-stream", res.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData("usb", null, HttpStatusCode.Unauthorized)]
    [InlineData("usb", "wrong", HttpStatusCode.Unauthorized)]
    [InlineData("usb", WifiKey, HttpStatusCode.Unauthorized)]       // the Wi-Fi key does not open USB
    [InlineData("wifi", null, HttpStatusCode.Forbidden)]
    [InlineData("wifi", "wrong", HttpStatusCode.Forbidden)]
    [InlineData("wifi", UsbKey, HttpStatusCode.Forbidden)]          // nor the USB key Wi-Fi
    [InlineData("wifi", WebhookKey, HttpStatusCode.Forbidden)]      // the webhook key opens only the webhook paths
    public async Task AWrongKeyIsRefused(string via, string? key, HttpStatusCode expected)
    {
        Assert.Equal(expected, await Post(via, key));
        using var res = await OpenStream(via, key);
        Assert.Equal(expected, res.StatusCode);
        Assert.Equal(0, _pb.Writes);
    }

    // ---- an open stream whose key stops being valid ----

    /// <summary>Reads the stream until the agent ends it; fails if it is still open after a few seconds.</summary>
    private static async Task<string> UntilEnded(HttpResponseMessage res)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        await using var body = await res.Content.ReadAsStreamAsync(timeout.Token);
        var text = new StringBuilder();
        var buf = new byte[4096];
        try
        {
            int n;
            while ((n = await body.ReadAsync(buf, timeout.Token)) > 0) text.Append(Encoding.UTF8.GetString(buf, 0, n));
        }
        catch (OperationCanceledException) { Assert.Fail("the stream stayed open"); }
        return text.ToString();
    }

    /// <summary>The stream is open (the agent has started its loop and its key check) before the test changes anything.</summary>
    private static async Task Opened(HttpResponseMessage res)
    {
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        await Task.Delay(100);
    }

    [Fact]
    public async Task AWifiStreamEndsWhenANewWifiKeyIsMade()
    {
        using var res = await OpenStream("wifi", WifiKey);
        await Opened(res);
        WriteConfig(wifiOn: true, wifiKey: "a-new-key-0123456789abcdef0123456789abcd", clipboard: true);
        var text = await UntilEnded(res);
        Assert.StartsWith(BridgeApi.PingFrame, text);
    }

    [Fact]
    public async Task AWifiStreamEndsWhenWifiIsTurnedOff()
    {
        using var res = await OpenStream("wifi", WifiKey);
        await Opened(res);
        WriteConfig(wifiOn: false, wifiKey: WifiKey, clipboard: true);
        await UntilEnded(res);
    }

    [Fact]
    public async Task AUsbStreamEndsWhenThePhonesKeyIsForgotten()
    {
        using var res = await OpenStream("usb", UsbKey);
        await Opened(res);
        _usb!.Forget("serial1");
        await UntilEnded(res);
    }

    [Fact]
    public async Task AStreamWithAValidKeyStaysOpenAndHearsTheMac()
    {
        using var res = await OpenStream("wifi", WifiKey);
        await Opened(res);
        await Task.Delay(1200);                 // a few passes of the key check
        _stream.Publish("clipboard", "copied on the Mac", null);
        await Task.Delay(300);
        WriteConfig(wifiOn: true, wifiKey: WifiKey, clipboard: false); // the switch ends it, after the copy went out
        var text = await UntilEnded(res);
        Assert.Contains("id: 1\nevent: clipboard\ndata: {\"seq\":1,\"text\":\"copied on the Mac\"}\n\n", text);
    }

    // ---- files to the phone: the stream follows either switch, and each event type its own ----

    [Fact]
    public async Task AFilesOnlyStreamHearsFilesAndNoClipboard()
    {
        WriteConfig(wifiOn: true, wifiKey: WifiKey, clipboard: false, filesToPhone: true);
        using var res = await OpenStream("wifi", WifiKey);
        await Opened(res);
        _stream.Publish("clipboard", "a copy that must not reach a files-only phone", null);
        _stream.Publish("file", "{\"id\":\"0123456789abcdef0123456789abcdef\",\"name\":\"a.jpg\",\"size\":3,\"mime\":\"image/jpeg\"}", null);
        await Task.Delay(1500); // the stream wakes at once, and looks at the switches every pass
        WriteConfig(wifiOn: true, wifiKey: WifiKey, clipboard: false, filesToPhone: false); // both off: the stream ends
        var text = await UntilEnded(res);
        Assert.Contains("event: file\n", text);
        Assert.DoesNotContain("event: clipboard", text);
        Assert.DoesNotContain("must not reach", text);
    }

    [Fact]
    public async Task AClipboardOnlyStreamHearsNoFiles()
    {
        using var res = await OpenStream("wifi", WifiKey);
        await Opened(res);
        _stream.Publish("file", "{\"id\":\"0123456789abcdef0123456789abcdef\",\"name\":\"a.jpg\",\"size\":3,\"mime\":\"image/jpeg\"}", null);
        _stream.Publish("clipboard", "copied", null);
        await Task.Delay(1500);
        WriteConfig(wifiOn: true, wifiKey: WifiKey, clipboard: false);
        var text = await UntilEnded(res);
        Assert.Contains("event: clipboard\n", text);
        Assert.DoesNotContain("event: file", text);
    }

    [Fact]
    public async Task AStreamWithNeitherSwitchOnIsRefused()
    {
        WriteConfig(wifiOn: true, wifiKey: WifiKey, clipboard: false, filesToPhone: false);
        using var res = await OpenStream("wifi", WifiKey);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    private async Task<HttpResponseMessage> Get(string via, string path, string? key, string? range = null)
    {
        var m = Message(HttpMethod.Get, Url(via, path), key);
        if (range is not null) m.Headers.TryAddWithoutValidation("Range", range);
        return await _http!.SendAsync(m);
    }

    [Theory]
    [InlineData("usb")]
    [InlineData("wifi")]
    public async Task ThePhoneListsFetchesAndConfirmsAFile(string via)
    {
        WriteConfig(wifiOn: true, wifiKey: WifiKey, clipboard: false, filesToPhone: true);
        var key = via == "usb" ? UsbKey : WifiKey;
        var item = (await _outbox.AddAsync("a.txt", null, "text/plain", new MemoryStream("0123456789"u8.ToArray()), default)).Item!;
        using (var list = await Get(via, "/api/bridge/files/out", key))
        {
            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
            var json = await list.Content.ReadAsStringAsync();
            Assert.Contains(item.Id, json);
            Assert.Contains("\"name\":\"a.txt\"", json);
        }
        using (var whole = await Get(via, "/api/bridge/files/out/" + item.Id, key))
        {
            Assert.Equal(HttpStatusCode.OK, whole.StatusCode);
            Assert.Equal("text/plain", whole.Content.Headers.ContentType?.MediaType);
            Assert.Equal("0123456789", await whole.Content.ReadAsStringAsync());
        }
        using (var part = await Get(via, "/api/bridge/files/out/" + item.Id, key, "bytes=3-"))
        {
            Assert.Equal(HttpStatusCode.PartialContent, part.StatusCode);
            Assert.Equal("3456789", await part.Content.ReadAsStringAsync());
        }
        using (var done = await _http!.SendAsync(Message(HttpMethod.Post, Url(via, "/api/bridge/files/out/" + item.Id + "/done"), key)))
            Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        using (var gone = await Get(via, "/api/bridge/files/out/" + item.Id, key))
            Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        using (var twice = await _http!.SendAsync(Message(HttpMethod.Post, Url(via, "/api/bridge/files/out/" + item.Id + "/done"), key)))
            Assert.Equal(HttpStatusCode.NotFound, twice.StatusCode);
    }

    [Fact]
    public async Task TheOutboxEndpointsRefuseWhileTheSwitchIsOffAndWithoutTheKeyAndLocally()
    {
        var item = (await _outbox.AddAsync("a.txt", null, null, new MemoryStream("x"u8.ToArray()), default)).Item!;
        var path = "/api/bridge/files/out/" + item.Id;
        // the switch is off (the default here)
        using (var off = await Get("wifi", path, WifiKey)) Assert.Equal(HttpStatusCode.Forbidden, off.StatusCode);
        WriteConfig(wifiOn: true, wifiKey: WifiKey, clipboard: false, filesToPhone: true);
        // no key, a wrong key, and the local port
        using (var none = await Get("wifi", path, null)) Assert.Equal(HttpStatusCode.Forbidden, none.StatusCode);
        using (var wrong = await Get("usb", path, "wrong")) Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        using (var local = await Get("local", path, WifiKey)) Assert.Equal(HttpStatusCode.Forbidden, local.StatusCode);
        using (var localList = await Get("local", "/api/bridge/files/out", WifiKey)) Assert.Equal(HttpStatusCode.Forbidden, localList.StatusCode);
        // an id that is not 32 hex characters never reaches the handler
        using (var bad = await Get("wifi", "/api/bridge/files/out/..%2fx", WifiKey)) Assert.NotEqual(HttpStatusCode.OK, bad.StatusCode);
        using (var upper = await Get("wifi", "/api/bridge/files/out/" + item.Id.ToUpperInvariant(), WifiKey)) Assert.NotEqual(HttpStatusCode.OK, upper.StatusCode);
        Assert.Equal(OutboxState.Waiting, Assert.Single(_outbox.List()).State); // nothing of this opened the file
    }

    // ---- notifications: phone -> Mac on the phone's ports only, the Settings test button on the local port only ----

    private const string NotificationPath = "/api/bridge/notification";
    private const string TestPath = "/api/bridge/notification/test";

    private async Task<HttpStatusCode> PostNotification(string via, string? key, string path = NotificationPath, string title = "Anna")
    {
        using var m = Message(HttpMethod.Post, Url(via, path), key);
        m.Content = new StringContent("{\"key\":\"k-" + title + "\",\"app\":\"Messages\",\"package\":\"com.example.sms\",\"title\":\"" + title
            + "\",\"text\":\"hello there\",\"category\":\"message\",\"time\":1}", Encoding.UTF8, "application/json");
        using var res = await _http!.SendAsync(m);
        return res.StatusCode;
    }

    private async Task<int> ShownCount()
    {
        await _notifications!.DrainAsync();
        lock (_shown) return _shown.Count;
    }

    [Theory]
    [InlineData("usb", UsbKey)]
    [InlineData("wifi", WifiKey)]
    public async Task ANotificationFromThePhoneWithItsKeyIsShown(string via, string key)
    {
        Assert.Equal(HttpStatusCode.OK, await PostNotification(via, key));
        Assert.Equal(1, await ShownCount());
        lock (_shown) Assert.Equal("Anna", _shown[0].Title);
    }

    [Theory]
    [InlineData("usb", null, HttpStatusCode.Unauthorized)]
    [InlineData("usb", "wrong", HttpStatusCode.Unauthorized)]
    [InlineData("usb", WifiKey, HttpStatusCode.Unauthorized)]
    [InlineData("wifi", null, HttpStatusCode.Forbidden)]
    [InlineData("wifi", "wrong", HttpStatusCode.Forbidden)]
    [InlineData("wifi", UsbKey, HttpStatusCode.Forbidden)]
    [InlineData("wifi", WebhookKey, HttpStatusCode.Forbidden)]
    public async Task ANotificationWithAWrongKeyIsRefusedBeforeAnythingIsRead(string via, string? key, HttpStatusCode expected)
    {
        Assert.Equal(expected, await PostNotification(via, key));
        Assert.Equal(0, await ShownCount());
    }

    [Theory]
    [InlineData(NotificationPath)]
    [InlineData(NotificationPath + "/")]
    [InlineData("/API/BRIDGE/NOTIFICATION")]
    public async Task TheLocalPortRefusesPhoneNotificationsEvenWithAPhonesKey(string path)
    {
        foreach (var key in new[] { null, UsbKey, WifiKey })
            Assert.Equal(HttpStatusCode.Forbidden, await PostNotification("local", key, path));
        Assert.Equal(0, await ShownCount());
    }

    [Fact]
    public async Task ANotificationWhileTheSwitchIsOffIs403OnBothPhonePorts()
    {
        WriteConfig(wifiOn: true, wifiKey: WifiKey, clipboard: true, notifications: false);
        Assert.Equal(HttpStatusCode.Forbidden, await PostNotification("usb", UsbKey));
        Assert.Equal(HttpStatusCode.Forbidden, await PostNotification("wifi", WifiKey));
        Assert.Equal(0, await ShownCount());
    }

    [Fact]
    public async Task ATooLargeNotificationIs413AndOneThatIsNoJsonIs400()
    {
        using var big = Message(HttpMethod.Post, Url("wifi", NotificationPath), WifiKey);
        big.Content = new StringContent("{\"key\":\"k\",\"title\":\"" + new string('a', BridgeApi.MaxNotificationBytes) + "\"}", Encoding.UTF8, "application/json");
        using var tooLarge = await _http!.SendAsync(big);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLarge.StatusCode);
        using var bad = Message(HttpMethod.Post, Url("usb", NotificationPath), UsbKey);
        bad.Content = new StringContent("not json", Encoding.UTF8, "application/json");
        using var invalid = await _http.SendAsync(bad);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(0, await ShownCount());
    }

    [Fact]
    public async Task TheTestButtonWorksOnTheLocalPortOnly()
    {
        // the Settings page's own request: no key, same origin
        using var local = Message(HttpMethod.Post, Url("local", TestPath), null);
        local.Headers.Add("Origin", $"http://127.0.0.1:{_local}");
        using var res = await _http!.SendAsync(local);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(1, await ShownCount());
        lock (_shown) Assert.Equal("PhoneBox test", _shown[0].Title);

        // with a phone's key it is still not served on a phone port: the USB tunnel and the Wi-Fi allow-list do not list it
        Assert.Equal(HttpStatusCode.Forbidden, await PostNotification("usb", UsbKey, TestPath));
        Assert.Equal(HttpStatusCode.Forbidden, await PostNotification("wifi", WifiKey, TestPath));
        Assert.Equal(HttpStatusCode.Unauthorized, await PostNotification("usb", null, TestPath));
        Assert.Equal(HttpStatusCode.Forbidden, await PostNotification("wifi", null, TestPath));
        Assert.Equal(1, await ShownCount());
    }

    [Fact]
    public async Task TheTestButtonIsOffWhileTheSwitchIsOffAndOnlyTheExactPathIsExempt()
    {
        Assert.False(Lan.AllowedOverLan(TestPath));
        Assert.False(UsbLink.Allowed("POST", TestPath));
        Assert.True(Lan.AllowedOverLan(NotificationPath));
        WriteConfig(wifiOn: true, wifiKey: WifiKey, clipboard: true, notifications: false);
        using var off = Message(HttpMethod.Post, Url("local", TestPath), null);
        using var res = await _http!.SendAsync(off);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        // neighbours of the one exempt path stay refused on the local port
        foreach (var path in new[] { TestPath + "/", TestPath + "/x", NotificationPath + "/test2", "/api/bridge/notifications/test", "/api/bridge/probe" })
        {
            using var m = Message(HttpMethod.Post, Url("local", path), null);
            using var r = await _http.SendAsync(m);
            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        }
        Assert.Equal(0, await ShownCount());
    }
}
