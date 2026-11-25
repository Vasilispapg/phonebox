using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>
/// The terminal's routes: a shell only with the master switch and the widget's own control on, for a widget on the asking
/// phone's enabled page; output as a binary frame with the cursor inside; numbered input applied once; nothing typed logged.
/// </summary>
public sealed class TerminalServiceTests : IDisposable
{
    private const string Widget = "t/term";
    private const string Phone = "phone";
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"phonebox-terminal-svc-{Guid.NewGuid():N}.json");
    private readonly List<FakePty> _ptys = [];
    private readonly List<(int Cols, int Rows)> _started = [];
    private long _now = 1_000;
    private int _writes;
    private ConfigStore? _store;
    private TerminalSessions? _sessions;

    public void Dispose()
    {
        _sessions?.CloseAll();
        try { File.Delete(_path); File.Delete(_path + ".bak"); } catch { }
    }

    private void WriteConfig(bool enabled = true, bool control = true, bool stackCard = true)
    {
        File.WriteAllText(_path, $$"""
            { "pages": [ { "id": "t", "title": "T", "enabled": true, "widgets": [
                { "id": "term", "type": "terminal", "x": 0, "y": 0, "w": 6, "h": 4, "options": { "control": {{(control ? "true" : "false")}} } },
                { "id": "term2", "type": "terminal", "x": 6, "y": 0, "w": 6, "h": 4, "options": { "control": true } },
                { "id": "term3", "type": "terminal", "x": 0, "y": 4, "w": 6, "h": 4, "options": { "control": true } },
                { "id": "stk", "type": "stack", "x": 6, "y": 4, "w": 6, "h": 4, "options": { "widgets": [
                    { "id": "card", "type": "terminal", "enabled": {{(stackCard ? "true" : "false")}}, "options": { "control": true } } ] } } ] },
                { "id": "off", "title": "Off", "enabled": false, "widgets": [
                    { "id": "term", "type": "terminal", "x": 0, "y": 0, "w": 6, "h": 4, "options": { "control": true } } ] },
                { "id": "other", "title": "Other", "enabled": true, "widgets": [] } ],
              "phones": [ { "id": "phone-other", "name": "Other", "pages": [ "other" ] } ],
              "terminal": { "enabled": {{(enabled ? "true" : "false")}} } }
            """);
        File.SetLastWriteTimeUtc(_path, DateTime.UtcNow.AddSeconds(++_writes));
        _store ??= new ConfigStore(_path);
    }

    private TerminalService Make(bool enabled = true, bool control = true, bool supported = true, PtyFactory? factory = null, bool stackCard = true,
        ILogger<TerminalSessions>? log = null)
    {
        WriteConfig(enabled, control, stackCard);
        factory ??= (cols, rows) =>
        {
            var p = new FakePty();
            lock (_ptys) { _ptys.Add(p); _started.Add((cols, rows)); }
            return p;
        };
        _sessions = new TerminalSessions(_store!, factory, () => _now, log ?? NullLogger<TerminalSessions>.Instance);
        return new TerminalService(_store!, _sessions) { Supported = supported };
    }

    private static JsonElement Body(object body) => JsonSerializer.SerializeToElement(body);
    private static string? Error(object body) => Body(body).TryGetProperty("error", out var e) ? e.GetString() : null;

    private static (byte Flags, ulong Cursor, byte[] Data) Decode(byte[] frame)
    {
        Assert.True(frame.Length >= 9);
        return (frame[0], BinaryPrimitives.ReadUInt64BigEndian(frame.AsSpan(1, 8)), frame[9..]);
    }

    private void OpenOk(TerminalService t, string device = Phone, string widget = Widget)
    {
        var (status, body) = t.Open(device, widget, 80, 24);
        Assert.True(status == 200, JsonSerializer.Serialize(body));
    }

    // ---------------------------------------------------------------- the two switches and the widget

    [Fact]
    public void MasterOffRefusesOpen()
    {
        var t = Make(enabled: false);
        var (status, body) = t.Open(Phone, Widget, 80, 24);
        Assert.Equal(403, status);
        Assert.Contains("Settings › General › Terminal", Error(body)); // where the switch really is
        Assert.Empty(_ptys);
    }

    [Fact]
    public void WidgetControlOffRefusesOpen()
    {
        var t = Make(control: false);
        Assert.Equal(403, t.Open(Phone, Widget, 80, 24).Status);
        Assert.Empty(_ptys);
        Assert.Equal(200, t.Open(Phone, "t/term2", 80, 24).Status); // another widget's control is its own
    }

    [Fact]
    public async Task UnknownWidgetIs404()
    {
        var t = Make();
        foreach (var widget in new[] { "t/nowhere", "", null, "x/term", "term" })
        {
            Assert.Equal(404, t.Open(Phone, widget, 80, 24).Status);
            Assert.Equal(404, t.State(Phone, widget).Status);
            Assert.Equal(404, t.In(Phone, widget, 1, "x").Status);
            Assert.Equal(404, t.Size(Phone, widget, 80, 24).Status);
            Assert.Equal(404, (await t.Out(Phone, widget, 0, 0, CancellationToken.None)).Status);
        }
        Assert.Empty(_ptys);
    }

    [Fact]
    public void NotMacOsIs501AndStartsNothing()
    {
        var t = Make(supported: false);
        var (status, body) = t.State(Phone, Widget);
        Assert.Equal(200, status);
        Assert.False(Body(body).GetProperty("supported").GetBoolean());
        Assert.Equal(501, t.Open(Phone, Widget, 80, 24).Status);
        Assert.Empty(_ptys);
    }

    [Fact]
    public void ThirdLiveSessionIs429()
    {
        var t = Make();
        OpenOk(t);
        OpenOk(t, widget: "t/term2");
        var (status, body) = t.Open(Phone, "t/term3", 80, 24);
        Assert.Equal(429, status);
        Assert.False(string.IsNullOrWhiteSpace(Error(body)));
        Assert.Equal(2, _ptys.Count);
    }

    [Theory]
    [InlineData("master")]
    [InlineData("control")]
    public async Task SettingsToggleKillsSessionWithinOneSweep(string which)
    {
        var t = Make();
        OpenOk(t);
        if (which == "master") WriteConfig(enabled: false); else WriteConfig(control: false);
        _sessions!.Sweep();
        Assert.Equal(1, _ptys[0].Kills);
        Assert.Null(_sessions.Get(Phone, Widget));
        Assert.Equal(403, (await t.Out(Phone, Widget, 0, 0, CancellationToken.None)).Status);
        Assert.Equal(403, t.In(Phone, Widget, 1, "ls\r").Status);
        Assert.Empty(_ptys[0].Writes);
        var state = Body(t.State(Phone, Widget).Body);
        Assert.False(state.GetProperty("open").GetBoolean());
    }

    [Fact]
    public void ASwitchTurnedOffRefusesInputBeforeTheSweep()
    {
        var t = Make();
        OpenOk(t);
        WriteConfig(enabled: false); // no sweep yet: the session is still there
        Assert.NotNull(_sessions!.Get(Phone, Widget));
        Assert.Equal(403, t.In(Phone, Widget, 1, "ls\r").Status);
        Assert.Equal(403, t.Size(Phone, Widget, 100, 30).Status);
        Assert.Empty(_ptys[0].Writes);
        Assert.Empty(_ptys[0].Resizes);
    }

    [Fact]
    public void ATerminalCardInASmartStackIsGatedLikeAnyWidget()
    {
        var t = Make();
        Assert.Equal(200, t.Open(Phone, "t/stk:card", 80, 24).Status);
        Assert.Equal(404, t.Open(Phone, "t/stk", 80, 24).Status); // the stack itself is not a terminal
        Assert.Equal(404, t.Open(Phone, "t/stk:nowhere", 80, 24).Status);
        Assert.Single(_ptys);

        var off = Make(stackCard: false);
        Assert.Equal(404, off.Open(Phone, "t/stk:card", 80, 24).Status);
        Assert.Equal(404, off.State(Phone, "t/stk:card").Status);
        Assert.Single(_ptys);

        var masterOff = Make(enabled: false);
        Assert.Equal(403, masterOff.Open(Phone, "t/stk:card", 80, 24).Status);
        Assert.Single(_ptys);
    }

    [Fact]
    public void ATerminalOnADisabledPageIs404()
    {
        var t = Make();
        Assert.Equal(404, t.Open(Phone, "off/term", 80, 24).Status);
        Assert.Equal(404, t.State(Phone, "off/term").Status);
        Assert.Empty(_ptys);
    }

    [Fact]
    public void ATerminalOnAPageThisPhoneDoesNotShowIs404()
    {
        var t = Make();
        Assert.Equal(404, t.Open("phone-other", Widget, 80, 24).Status); // this phone shows only the "other" page
        Assert.Equal(404, t.State("phone-other", Widget).Status);
        Assert.Empty(_ptys);
        Assert.Equal(200, t.Open(Phone, Widget, 80, 24).Status); // a phone without its own page list sees every page
        Assert.Single(_ptys);
        Assert.Equal(404, t.In("phone-other", Widget, 1, "x").Status);
        Assert.Empty(_ptys[0].Writes);
    }

    // ---------------------------------------------------------------- open never waits for a shell being killed

    [Fact]
    public void EvictingAQuietSessionDoesNotHoldUpTheOpen()
    {
        var stubs = new List<StubPty>();
        var t = Make(factory: (_, _) => { var p = new StubPty { KillDelayMs = 2000 }; lock (stubs) stubs.Add(p); return p; });
        OpenOk(t);
        OpenOk(t, widget: "t/term2");
        _now += TerminalSessions.DetachedAfterMs + 1_000;
        _sessions!.Get(Phone, "t/term2")!.Touch(); // only the first has gone quiet
        var sw = Stopwatch.StartNew();
        var (status, _) = t.Open(Phone, "t/term3", 80, 24);
        Assert.Equal(200, status);
        Assert.True(sw.ElapsedMilliseconds < 1000, $"open took {sw.ElapsedMilliseconds} ms");
        Assert.Null(_sessions.Get(Phone, Widget)); // the victim is unreachable at once
        Assert.True(SpinWait.SpinUntil(() => stubs[0].Kills == 1, 10_000));
        Assert.Equal(0, stubs[1].Kills);
        Assert.Equal(0, stubs[2].Kills);
    }

    [Fact]
    public void ReopeningOverAnEndedShellDoesNotHoldUpTheOpen()
    {
        var stubs = new List<StubPty>();
        var t = Make(factory: (_, _) => { var p = new StubPty { KillDelayMs = 2000 }; lock (stubs) stubs.Add(p); return p; });
        OpenOk(t);
        var first = _sessions!.Get(Phone, Widget)!;
        stubs[0].End();
        Assert.True(SpinWait.SpinUntil(() => first.Exited, 5_000));
        var sw = Stopwatch.StartNew();
        var (status, _) = t.Open(Phone, Widget, 80, 24);
        Assert.Equal(200, status);
        Assert.True(sw.ElapsedMilliseconds < 1000, $"open took {sw.ElapsedMilliseconds} ms");
        Assert.NotSame(first, _sessions.Get(Phone, Widget));
        Assert.True(SpinWait.SpinUntil(() => stubs[0].Kills == 1, 10_000));
        Assert.Equal(0, stubs[1].Kills);
    }

    // ---------------------------------------------------------------- state and activity

    [Fact]
    public async Task StateNeverTouchesTheSession()
    {
        var t = Make();
        OpenOk(t);
        var session = _sessions!.Get(Phone, Widget)!;
        var before = session.LastOverlayMs;
        _now += 5_000;
        var (status, body) = t.State(Phone, Widget);
        Assert.Equal(200, status);
        var state = Body(body);
        Assert.True(state.GetProperty("supported").GetBoolean());
        Assert.True(state.GetProperty("enabled").GetBoolean());
        Assert.True(state.GetProperty("control").GetBoolean());
        Assert.True(state.GetProperty("open").GetBoolean());
        Assert.Equal(before, session.LastOverlayMs);

        // the overlay's own calls are activity
        await t.Out(Phone, Widget, 0, 0, CancellationToken.None);
        Assert.Equal(_now, session.LastOverlayMs);
        _now += 1_000;
        t.In(Phone, Widget, 1, "x");
        Assert.Equal(_now, session.LastOverlayMs);
        _now += 1_000;
        t.Size(Phone, Widget, 90, 30);
        Assert.Equal(_now, session.LastOverlayMs);
    }

    // ---------------------------------------------------------------- output frame

    [Fact]
    public async Task OutFrameLayoutAndFlags()
    {
        var t = Make();
        OpenOk(t);
        _ptys[0].Push(0x00, 0xFF, 0x1B);
        var first = await t.Out(Phone, Widget, 0, 2500, CancellationToken.None);
        Assert.Equal(200, first.Status);
        Assert.Null(first.Error);
        Assert.Equal(new byte[] { 0x00, 0, 0, 0, 0, 0, 0, 0, 3, 0x00, 0xFF, 0x1B }, first.Frame);

        var idle = await t.Out(Phone, Widget, 3, 0, CancellationToken.None);
        Assert.Equal(new byte[] { 0x00, 0, 0, 0, 0, 0, 0, 0, 3 }, idle.Frame);

        // a cursor the buffer does not know: reset, and everything kept from the start
        var reset = Decode((await t.Out(Phone, Widget, 99, 0, CancellationToken.None)).Frame!);
        Assert.Equal(0x02, reset.Flags);
        Assert.Equal(3UL, reset.Cursor);
        Assert.Equal(new byte[] { 0x00, 0xFF, 0x1B }, reset.Data);

        _ptys[0].End();
        var ended = Decode((await t.Out(Phone, Widget, 3, 2500, CancellationToken.None)).Frame!);
        Assert.Equal(0x01, ended.Flags);
        Assert.Equal(3UL, ended.Cursor);
        Assert.Empty(ended.Data);

        Assert.Equal(new byte[] { 0x03, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFE, 0x1B, 0x00, 0xFF },
            TerminalService.Frame(new TerminalChunk(ulong.MaxValue - 1, Reset: true, Exited: true, [0x1B, 0x00, 0xFF])));
    }

    [Fact]
    public async Task OutWithAnAbortedRequestIsQuiet()
    {
        var t = Make();
        OpenOk(t);
        using var cts = new CancellationTokenSource(50);
        var (status, frame, error) = await t.Out(Phone, Widget, 0, 2500, cts.Token);
        Assert.Equal(499, status);
        Assert.Null(frame);
        Assert.Null(error);
    }

    [Fact]
    public async Task OutWaitIsClampedToTwoAndAHalfSeconds()
    {
        var t = Make();
        OpenOk(t);
        var sw = Stopwatch.StartNew();
        var r = await t.Out(Phone, Widget, 0, 600_000, CancellationToken.None);
        Assert.Equal(200, r.Status);
        Assert.InRange(sw.ElapsedMilliseconds, 2000, 6000);

        sw.Restart();
        await t.Out(Phone, Widget, 0, -5, CancellationToken.None);
        Assert.True(sw.ElapsedMilliseconds < 1000);
    }

    [Fact]
    public async Task OutWithoutASessionIsClosed()
    {
        var t = Make();
        var r = await t.Out(Phone, Widget, 0, 0, CancellationToken.None);
        Assert.Equal(410, r.Status);
        Assert.Null(r.Frame);
        Assert.Equal("closed", Error(r.Error!));
    }

    // ---------------------------------------------------------------- input

    [Fact]
    public void InputOver4KbIs413AndNothingWritten()
    {
        var t = Make();
        OpenOk(t);
        Assert.Equal(413, t.In(Phone, Widget, 1, new string('a', 4097)).Status);
        Assert.Equal(413, t.In(Phone, Widget, 1, string.Concat(Enumerable.Repeat("é", 2049))).Status); // 4098 bytes, 2049 characters
        Assert.Empty(_ptys[0].Writes);

        var (status, body) = t.In(Phone, Widget, 1, string.Concat(Enumerable.Repeat("é", 2048))); // exactly 4096 bytes
        Assert.Equal(200, status);
        Assert.True(Body(body).GetProperty("applied").GetBoolean());
        Assert.Equal(4096, Assert.Single(_ptys[0].Writes).Length);
    }

    [Fact]
    public void GapSeqIs409()
    {
        var t = Make();
        OpenOk(t);
        var (status, body) = t.In(Phone, Widget, 2, "x");
        Assert.Equal(409, status);
        var json = Body(body);
        Assert.Equal("out-of-order", json.GetProperty("error").GetString());
        Assert.Equal(1UL, json.GetProperty("expected").GetUInt64());
        Assert.Empty(_ptys[0].Writes);

        Assert.Equal(400, t.In(Phone, Widget, 0, "x").Status);
        Assert.Equal(400, t.In(Phone, Widget, 1, null).Status);
        Assert.Empty(_ptys[0].Writes);
    }

    [Fact]
    public void InputWithoutASessionIsClosed()
    {
        var t = Make();
        var (status, body) = t.In(Phone, Widget, 1, "x");
        Assert.Equal(410, status);
        Assert.Equal("closed", Error(body));
    }

    [Fact]
    public void InputToAClosedSessionIs410()
    {
        var t = Make();
        OpenOk(t);
        _sessions!.Get(Phone, Widget)!.Close(); // still listed until the sweep, but its pty is gone
        var (status, body) = t.In(Phone, Widget, 1, "x");
        Assert.Equal(410, status);
        Assert.Equal("closed", Error(body));
        Assert.Equal(410, t.Size(Phone, Widget, 100, 30).Status);
    }

    [Fact]
    public void AnExceptionFromThePtyIsAClosedSessionAndLeaksNothing()
    {
        var t = Make(factory: (_, _) => new StubPty { ThrowOnWrite = true, ThrowOnResize = true });
        OpenOk(t);
        var (status, body) = t.In(Phone, Widget, 1, "x");
        Assert.Equal(410, status);
        Assert.Equal("closed", Error(body));
        Assert.DoesNotContain(StubPty.Secret, JsonSerializer.Serialize(body));
        var size = t.Size(Phone, Widget, 100, 30);
        Assert.Equal(410, size.Status);
        Assert.DoesNotContain(StubPty.Secret, JsonSerializer.Serialize(size.Body));
    }

    [Fact]
    public void SessionsOfTwoPhonesAreSeparate()
    {
        var t = Make();
        OpenOk(t, device: "phone-a");
        Assert.Equal(410, t.In("phone-b", Widget, 1, "b").Status); // phone-b has no shell of its own yet
        OpenOk(t, device: "phone-b");
        Assert.Equal(200, t.In("phone-a", Widget, 1, "a").Status);
        Assert.Equal(200, t.In("phone-b", Widget, 1, "b").Status);
        Assert.Equal(2, _ptys.Count);
        Assert.Equal("a", Encoding.UTF8.GetString(Assert.Single(_ptys[0].Writes)));
        Assert.Equal("b", Encoding.UTF8.GetString(Assert.Single(_ptys[1].Writes)));
    }

    // ---------------------------------------------------------------- a write stuck on a full pty (the shell is not reading)

    private static async Task<T> Prompt<T>(Task<T> work)
    {
        Assert.Same(work, await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(2))));
        return await work;
    }

    [Fact]
    public async Task WhileAWriteIsStuckInputIs503BusySizeAndReattachAnswerAndNothingIsKeptAlive()
    {
        var t = Make();
        OpenOk(t);
        var session = _sessions!.Get(Phone, Widget)!;
        var pty = _ptys[0];
        pty.BlockWrites();
        var stuck = Task.Run(() => t.In(Phone, Widget, 1, "x"));
        Assert.True(await pty.WriteEntered.WaitAsync(TimeSpan.FromSeconds(5)));

        _now += 5_000;
        var before = session.LastOverlayMs;
        var (status, body) = await Prompt(Task.Run(() => t.In(Phone, Widget, 1, "x"))); // the phone's resend
        Assert.Equal(503, status);
        Assert.Equal("busy", Error(body));
        Assert.Equal(before, session.LastOverlayMs); // a busy answer is not activity: the idle timer still runs

        Assert.Equal(200, (await Prompt(Task.Run(() => t.Size(Phone, Widget, 100, 30)))).Status);
        var reopen = Body((await Prompt(Task.Run(() => t.Open(Phone, Widget, 80, 24)))).Body);
        Assert.True(reopen.GetProperty("attached").GetBoolean());
        Assert.Equal(1UL, reopen.GetProperty("nextSeq").GetUInt64()); // still in flight: the phone resends it

        pty.ReleaseWrites();
        Assert.Equal(200, (await stuck).Status);
        var (again, againBody) = t.In(Phone, Widget, 1, "x"); // the resend after the release: already in, counted as landed
        Assert.Equal(200, again);
        Assert.False(Body(againBody).GetProperty("applied").GetBoolean());
        Assert.Equal("x", Encoding.UTF8.GetString(Assert.Single(pty.Writes)));
    }

    [Fact]
    public async Task BusyIs503OverHttp()
    {
        var t = Make();
        await using var host = await Host.Start(t);
        await host.Post($"/api/terminal/open?widget={Widget}");
        _ptys[0].BlockWrites();
        var stuck = host.Post($"/api/terminal/in?widget={Widget}&seq=1&d=x");
        Assert.True(await _ptys[0].WriteEntered.WaitAsync(TimeSpan.FromSeconds(5)));
        var busy = await host.Post($"/api/terminal/in?widget={Widget}&seq=1&d=x");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, busy.StatusCode);
        Assert.Equal("busy", JsonDocument.Parse(await busy.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetString());
        _ptys[0].ReleaseWrites();
        Assert.Equal(HttpStatusCode.OK, (await stuck).StatusCode);
    }

    // ---------------------------------------------------------------- the phone's id

    [Theory]
    [InlineData("abc123XYZ", "abc123XYZ")]
    [InlineData("0123456789abcdef0123456789abcdef", "0123456789abcdef0123456789abcdef")] // 32: the longest kept
    [InlineData("0123456789abcdef0123456789abcdef01234567", "")] // 40
    [InlineData("phone with spaces", "")]
    [InlineData("phone\nINFO: a forged log line", "")]
    [InlineData("phone-a", "")]
    [InlineData("téléphone", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void ADeviceIdIsKeptOnlyWhenItIsShortLettersAndDigits(string? id, string expected) =>
        Assert.Equal(expected, PhoneRegistry.CleanId(id));

    [Theory]
    [InlineData("phone with spaces")]
    [InlineData("0123456789abcdef0123456789abcdef01234567")]
    public async Task AnOddDeviceIdIsNeitherTheSessionKeyNorLogged(string id)
    {
        var capture = new CaptureProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(capture).SetMinimumLevel(LogLevel.Trace));
        var t = Make(log: factory.CreateLogger<TerminalSessions>());
        await using var host = await Host.Start(t);
        Assert.Equal(HttpStatusCode.OK, (await host.Post($"/api/terminal/open?widget={Widget}", id)).StatusCode);
        Assert.Null(_sessions!.Get(id, Widget));
        Assert.NotNull(_sessions.Get("", Widget)); // the same as a phone that sends no id
        Assert.Equal(HttpStatusCode.OK, (await host.Post($"/api/terminal/close?widget={Widget}", id)).StatusCode);
        Assert.True(SpinWait.SpinUntil(() => _ptys[0].Kills == 1, 10_000));
        Assert.True(SpinWait.SpinUntil(() => capture.Entries.Count(e => e.Contains("terminal session")) >= 2, 10_000));
        var log = string.Join("\n", capture.Entries);
        Assert.Contains("terminal session opened", log); // the capture works
        Assert.DoesNotContain(id, log);
    }

    // ---------------------------------------------------------------- size and close

    [Fact]
    public void OpenWithoutASizeUsesEightyByTwentyFour()
    {
        var t = Make();
        Assert.Equal(200, t.Open(Phone, Widget, null, null).Status);
        Assert.Equal((80, 24), Assert.Single(_started));
    }

    [Fact]
    public void CloseAnswersWithoutWaitingForTheShell()
    {
        var slow = new StubPty { KillDelayMs = 2000 };
        var t = Make(factory: (_, _) => slow);
        OpenOk(t);
        var sw = Stopwatch.StartNew();
        var (status, _) = t.Close(Phone, Widget);
        Assert.Equal(200, status);
        Assert.True(sw.ElapsedMilliseconds < 1000, $"close took {sw.ElapsedMilliseconds} ms");
        Assert.Null(_sessions!.Get(Phone, Widget));
        Assert.True(SpinWait.SpinUntil(() => slow.Kills == 1, 10_000));
        Assert.Equal(200, t.Close(Phone, Widget).Status); // nothing left to close is not an error
    }

    // ---------------------------------------------------------------- over HTTP (the routes themselves)

    [Fact]
    public async Task AwkwardTextRoundTrips()
    {
        var t = Make();
        await using var host = await Host.Start(t);
        Assert.Equal(HttpStatusCode.OK, (await host.Post($"/api/terminal/open?widget={Widget}&cols=80&rows=24")).StatusCode);
        string[] texts = ["%", "+", "&", "#", "é😀", "\r", "\n", "100% + a&b=c #1 é😀\r\n", "?x=1;y", "\u001b[A"];
        for (var i = 0; i < texts.Length; i++)
        {
            var res = await host.Post($"/api/terminal/in?widget={Uri.EscapeDataString(Widget)}&seq={i + 1}&d={Uri.EscapeDataString(texts[i])}");
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        }
        Assert.Equal(texts.Length, _ptys[0].Writes.Count);
        for (var i = 0; i < texts.Length; i++) Assert.Equal(Encoding.UTF8.GetBytes(texts[i]), _ptys[0].Writes[i]);
    }

    [Fact]
    public async Task ResentSeqIsAppliedOnce()
    {
        var t = Make();
        await using var host = await Host.Start(t);
        await host.Post($"/api/terminal/open?widget={Widget}");
        var first = await host.Post($"/api/terminal/in?widget={Widget}&seq=1&d=ls%0D");
        var again = await host.Post($"/api/terminal/in?widget={Widget}&seq=1&d=ls%0D");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.True(JsonDocument.Parse(await first.Content.ReadAsStringAsync()).RootElement.GetProperty("applied").GetBoolean());
        Assert.False(JsonDocument.Parse(await again.Content.ReadAsStringAsync()).RootElement.GetProperty("applied").GetBoolean());
        Assert.Single(_ptys[0].Writes);

        var gap = await host.Post($"/api/terminal/in?widget={Widget}&seq=5&d=x");
        Assert.Equal(HttpStatusCode.Conflict, gap.StatusCode);
        var gapBody = JsonDocument.Parse(await gap.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("out-of-order", gapBody.GetProperty("error").GetString());
        Assert.Equal(2, gapBody.GetProperty("expected").GetInt32());

        Assert.Equal(HttpStatusCode.BadRequest, (await host.Post($"/api/terminal/in?widget={Widget}&d=x")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Post($"/api/terminal/in?widget={Widget}&seq=abc&d=x")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Post($"/api/terminal/in?widget={Widget}&seq=0&d=x")).StatusCode);
        Assert.Single(_ptys[0].Writes);
    }

    // the phone continues a reattached shell's numbering from nextSeq, and resolves a batch whose answer it never saw
    [Fact]
    public void OpenSaysWhetherItReattachedTheNextInputNumberAndWhichShell()
    {
        var t = Make();
        static (bool Attached, ulong Next, string Shell) Info((int Status, object Body) r)
        {
            Assert.Equal(200, r.Status);
            var b = Body(r.Body);
            Assert.True(b.GetProperty("ok").GetBoolean());
            var shell = b.GetProperty("shell").GetString()!;
            Assert.Matches("^[0-9a-f]{16}$", shell);
            return (b.GetProperty("attached").GetBoolean(), b.GetProperty("nextSeq").GetUInt64(), shell);
        }
        var first = Info(t.Open(Phone, Widget, 80, 24));
        Assert.Equal((false, 1UL), (first.Attached, first.Next));
        Assert.Equal(200, t.In(Phone, Widget, 1, "ls\r").Status);
        Assert.Equal(200, t.In(Phone, Widget, 2, "pwd\r").Status);
        Assert.Equal((true, 3UL, first.Shell), Info(t.Open(Phone, Widget, 80, 24)));
        Assert.Single(_ptys);

        _ptys[0].End(); // the shell exits: the next open starts a fresh one, with an identity of its own
        Assert.True(SpinWait.SpinUntil(() => _sessions!.Get(Phone, Widget)!.Exited, 10_000));
        var fresh = Info(t.Open(Phone, Widget, 80, 24));
        Assert.Equal((false, 1UL), (fresh.Attached, fresh.Next));
        Assert.NotEqual(first.Shell, fresh.Shell);
        Assert.Equal(2, _ptys.Count);
    }

    // input numbered for one shell never reaches another (one that replaced it after an end, a restart or a reap)
    [Fact]
    public async Task InputForAnotherShellIsRefusedUntyped()
    {
        var t = Make();
        await using var host = await Host.Start(t);
        var open = await host.Post($"/api/terminal/open?widget={Widget}");
        var shell = JsonDocument.Parse(await open.Content.ReadAsStringAsync()).RootElement.GetProperty("shell").GetString();
        var other = await host.Post($"/api/terminal/in?widget={Widget}&seq=1&s=0123456789abcdef&d=ls%0D");
        Assert.Equal(HttpStatusCode.Conflict, other.StatusCode);
        Assert.Equal("other-session", JsonDocument.Parse(await other.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetString());
        Assert.Empty(_ptys[0].Writes);

        Assert.Equal(HttpStatusCode.OK, (await host.Post($"/api/terminal/in?widget={Widget}&seq=1&s={shell}&d=ls%0D")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.Post($"/api/terminal/in?widget={Widget}&seq=2&d=pwd%0D")).StatusCode); // no s: not checked
        Assert.Equal(["ls\r", "pwd\r"], _ptys[0].Writes.Select(Encoding.UTF8.GetString));
    }

    [Fact]
    public async Task SizeClampedAtRoute()
    {
        var t = Make();
        await using var host = await Host.Start(t);
        Assert.Equal(HttpStatusCode.OK, (await host.Post($"/api/terminal/open?widget={Widget}&cols=abc&rows=")).StatusCode);
        Assert.Equal((80, 24), Assert.Single(_started)); // garbage on open is the default size
        Assert.Equal(HttpStatusCode.OK, (await host.Post($"/api/terminal/size?widget={Widget}&cols=0&rows=100000")).StatusCode);
        Assert.Equal((10, 200), _ptys[0].Resizes[^1]);
        var count = _ptys[0].Resizes.Count;
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Post($"/api/terminal/size?widget={Widget}&cols=wide&rows=30")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Post($"/api/terminal/size?widget={Widget}&rows=30")).StatusCode);
        Assert.Equal(count, _ptys[0].Resizes.Count);
    }

    [Fact]
    public async Task OutRouteAnswersTheFrameAsOctetStream()
    {
        var t = Make();
        await using var host = await Host.Start(t);
        await host.Post($"/api/terminal/open?widget={Widget}");
        _ptys[0].Push(0x1B, 0x00, 0xFF);
        var res = await host.Http.GetAsync($"/api/terminal/out?widget={Widget}&since=0&wait=2500");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("application/octet-stream", res.Content.Headers.ContentType?.MediaType);
        Assert.Equal(new byte[] { 0x00, 0, 0, 0, 0, 0, 0, 0, 3, 0x1B, 0x00, 0xFF }, await res.Content.ReadAsByteArrayAsync());

        var state = await host.Http.GetAsync($"/api/terminal/state?widget={Widget}");
        Assert.Equal("application/json", state.Content.Headers.ContentType?.MediaType);
        var missing = await host.Http.GetAsync("/api/terminal/out?widget=t/nowhere&since=0");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("application/json", missing.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task RequestUrlsOfTerminalAreNotLogged()
    {
        var secret = "pb-typed-" + Guid.NewGuid().ToString("N");
        // the worst case: someone turned every log level up to Trace on the command line, for every provider
        string[] verbose = ["--Logging:LogLevel:Default=Trace", "--Logging:LogLevel:Microsoft=Trace", "--Logging:Capture:LogLevel:Default=Trace",
            "--Logging:Capture:LogLevel:Microsoft.AspNetCore=Trace"];

        async Task<CaptureProvider> Run(bool safe)
        {
            var capture = new CaptureProvider();
            var t = Make();
            await using (var host = await Host.Start(t, verbose, capture, safe))
            {
                await host.Post($"/api/terminal/open?widget={Widget}&x={secret}");
                await host.Post($"/api/terminal/in?widget={Widget}&seq=1&d={secret}");
                await host.Post($"/api/terminal/in?widget={Widget}&seq=one&d={secret}"); // an unreadable number as well
                await host.Post($"/api/terminal/size?widget={Widget}&cols={secret}&rows={secret}");
                await host.Http.GetAsync($"/api/terminal/out?widget={Widget}&since={secret}&wait=0");
                await host.Http.GetAsync($"/api/terminal/state?widget={Widget}&d={secret}");
                await host.Post($"/api/terminal/close?widget={Widget}&d={secret}");
            }
            _sessions?.CloseAll();
            return capture;
        }

        // the test can see a leak: without the filter ASP.NET Core prints the whole URL
        var unsafeRun = await Run(safe: false);
        Assert.Contains(unsafeRun.Entries, e => e.Contains(secret));

        var safeRun = await Run(safe: true);
        Assert.Contains(safeRun.Entries, e => e.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)); // Trace really is on
        Assert.DoesNotContain(safeRun.Entries, e => e.Contains(secret));
    }

    // ---------------------------------------------------------------- Wi-Fi

    [Theory]
    [InlineData("/api/terminal/state")]
    [InlineData("/api/terminal/open")]
    [InlineData("/api/terminal/out")]
    [InlineData("/api/terminal/in")]
    [InlineData("/api/terminal/size")]
    [InlineData("/api/terminal/close")]
    public void TerminalIsAPhoneKeyPathOverLanAndNotAWebhook(string path)
    {
        var lan = new LanConfig { Enabled = true, Port = 8766, Token = "phone-tok", WebhookToken = "hook-tok" };
        Assert.True(Lan.AllowedOverLan(new PathString(path)));
        Assert.False(Lan.IsWebhook(new PathString(path)));
        Assert.Equal(Lan.LanAccess.Phone, Lan.AuthorizeRequest(lan, "GET", path, "", "Bearer phone-tok", null));
        Assert.Equal(Lan.LanAccess.Denied, Lan.AuthorizeRequest(lan, "GET", path, "", "Bearer hook-tok", null));
        Assert.Equal(Lan.LanAccess.Denied, Lan.AuthorizeRequest(lan, "POST", path, "", null, "hook-tok"));
        Assert.Equal(Lan.LanAccess.Denied, Lan.AuthorizeRequest(lan, "GET", path, "https://evil.example", "Bearer phone-tok", null));
    }

    [Fact]
    public void TheIdleClampIsNotWrittenToTheConfig()
    {
        var json = JsonSerializer.Serialize(new AgentConfig(), ConfigStore.WriteOpts);
        Assert.DoesNotContain("effectiveIdle", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(120, new TerminalConfig { IdleMinutes = 5000 }.EffectiveIdleMinutes());
        Assert.Equal(0, new TerminalConfig { IdleMinutes = -3 }.EffectiveIdleMinutes());
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>The terminal routes on a loopback Kestrel, the way Program.cs maps them.</summary>
    private sealed class Host : IAsyncDisposable
    {
        public required WebApplication App { get; init; }
        public required HttpClient Http { get; init; }

        public static async Task<Host> Start(TerminalService service, string[]? args = null, ILoggerProvider? capture = null, bool safeLogging = true)
        {
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = args ?? [] });
            builder.Logging.ClearProviders();
            if (capture is not null) builder.Logging.AddProvider(capture);
            if (safeLogging) RequestLog.KeepQueriesOut(builder.Logging);
            builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
            builder.Services.AddSingleton(service);
            var app = builder.Build();
            TerminalRoutes.Map(app);
            await app.StartAsync();
            var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            http.DefaultRequestHeaders.Add("X-PhoneBox-Device", Phone);
            return new Host { App = app, Http = http };
        }

        public Task<HttpResponseMessage> Post(string url) => Http.PostAsync(url, null);

        /// <summary>A POST that names another phone (X-PhoneBox-Device sent as it is).</summary>
        public Task<HttpResponseMessage> Post(string url, string device)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.TryAddWithoutValidation("X-PhoneBox-Device", device);
            return Http.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            Http.Dispose();
            await App.StopAsync();
            await App.DisposeAsync();
        }
    }
}

/// <summary>The service with a real shell (macOS): zsh without the developer's settings, see <see cref="PhoneBox.Agent.Tests.RealShellEnvironment"/>.</summary>
[Collection(PhoneBox.Agent.Tests.RealShellCollection.Name)]
public sealed class TerminalServiceRealShellTests : IDisposable
{
    private const string Widget = "t/term", Phone = "phone";
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"phonebox-terminal-real-{Guid.NewGuid():N}.json");

    public void Dispose() { try { File.Delete(_path); File.Delete(_path + ".bak"); } catch { } }

    [Fact]
    public async Task RealShellThroughTheService()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var wait = PhoneBox.Agent.Tests.PtyMacTests.Wait;
        File.WriteAllText(_path, """
            { "pages": [ { "id": "t", "title": "T", "enabled": true, "widgets": [
                { "id": "term", "type": "terminal", "x": 0, "y": 0, "w": 6, "h": 4, "options": { "control": true } } ] } ],
              "terminal": { "enabled": true } }
            """);
        IPtyProcess? real = null;
        var store = new ConfigStore(_path);
        var sessions = new TerminalSessions(store, (c, r) => real = Pty.Start(c, r), () => 0, NullLogger<TerminalSessions>.Instance);
        var t = new TerminalService(store, sessions) { Supported = true };
        try
        {
            Assert.Equal(200, t.Open(Phone, Widget, 80, 24).Status);
            Assert.Equal(200, t.In(Phone, Widget, 1, "echo __T__$((6*7))\r").Status);
            var seen = new List<byte>();
            ulong cursor = 0;
            var deadline = DateTime.UtcNow + wait;
            while (!Encoding.UTF8.GetString(seen.ToArray()).Contains("__T__42") && DateTime.UtcNow < deadline)
            {
                var (status, frame, _) = await t.Out(Phone, Widget, cursor, 2500, CancellationToken.None);
                Assert.Equal(200, status);
                Assert.Equal(0, frame![0] & 0x01);
                seen.AddRange(frame[9..]);
                cursor = BinaryPrimitives.ReadUInt64BigEndian(frame.AsSpan(1, 8));
            }
            Assert.Contains("__T__42", Encoding.UTF8.GetString(seen.ToArray()));
            Assert.Equal(200, t.Close(Phone, Widget).Status);
            Assert.True(PhoneBox.Agent.Tests.PtyMacTests.Until(() => real!.HasExited, wait), "the shell outlived close");
        }
        finally
        {
            sessions.CloseAll();
            if (real is not null) Assert.True(PhoneBox.Agent.Tests.PtyMacTests.Until(() => real.HasExited, wait));
        }
    }
}
