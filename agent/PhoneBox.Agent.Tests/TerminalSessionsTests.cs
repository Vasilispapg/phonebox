using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>Terminal sessions are keyed per phone and widget, capped at two, closed when idle, and gone the moment the switches go off.</summary>
public sealed class TerminalSessionsTests : IDisposable
{
    private const string Widget = "t/term";
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"phonebox-terminal-{Guid.NewGuid():N}.json");
    private readonly List<FakePty> _ptys = [];
    private long _now = 1_000;
    private int _writes;
    private ConfigStore? _store;

    public void Dispose() { try { File.Delete(_path); File.Delete(_path + ".bak"); } catch { } }

    private void WriteConfig(bool enabled = true, int? idleMinutes = null, bool control = true, bool widget = true, bool second = true, string token = "key-A")
    {
        var idle = idleMinutes is { } m ? $", \"idleMinutes\": {m}" : "";
        File.WriteAllText(_path, $$"""
            { "pages": [ { "id": "t", "title": "T", "enabled": true, "widgets": [
                {{(widget ? $$"""{ "id": "term", "type": "terminal", "x": 0, "y": 0, "w": 6, "h": 4, "options": { "control": {{(control ? "true" : "false")}} } },""" : "")}}
                {{(second ? """{ "id": "term2", "type": "terminal", "x": 6, "y": 0, "w": 6, "h": 4, "options": { "control": true } },""" : "")}}
                { "id": "term3", "type": "terminal", "x": 0, "y": 4, "w": 6, "h": 4, "options": { "control": true } } ] } ],
              "terminal": { "enabled": {{(enabled ? "true" : "false")}}{{idle}} },
              "lan": { "token": "{{token}}" } }
            """);
        // a rewrite inside the file system's timestamp step would not be noticed by ConfigStore
        File.SetLastWriteTimeUtc(_path, DateTime.UtcNow.AddSeconds(++_writes));
        _store ??= new ConfigStore(_path);
    }

    private TerminalSessions Make(Action? configure = null, PtyFactory? factory = null, ILogger<TerminalSessions>? log = null)
    {
        if (configure is null) WriteConfig(); else configure();
        return new TerminalSessions(_store!, factory ?? ((_, _) => { var p = new FakePty(); _ptys.Add(p); return p; }), () => _now, log ?? NullLogger<TerminalSessions>.Instance);
    }

    private static void KilledOnce(FakePty pty) => Assert.True(SpinWait.SpinUntil(() => pty.Kills == 1, 10_000), $"kills: {pty.Kills}");

    private static TerminalSession Opened((TerminalSession? Session, string? Error, bool Attached) r)
    {
        Assert.Null(r.Error);
        return Assert.IsType<TerminalSession>(r.Session);
    }

    [Fact]
    public void ThirdSessionIsRefusedWhenBothAreLive()
    {
        using var sessions = Make();
        Opened(sessions.Open("phone", "t/term", 80, 24));
        Opened(sessions.Open("phone", "t/term2", 80, 24));
        _now += 10_000;
        var third = sessions.Open("phone", "t/term3", 80, 24);
        Assert.Null(third.Session);
        Assert.False(string.IsNullOrWhiteSpace(third.Error));
        Assert.Equal(2, _ptys.Count);
        Assert.All(_ptys, p => Assert.Equal(0, p.Kills));
    }

    [Fact]
    public void DetachedSessionIsEvictedByNewRequest()
    {
        using var sessions = Make();
        var first = Opened(sessions.Open("phone", "t/term", 80, 24));
        _now += 5_000;
        var second = Opened(sessions.Open("phone", "t/term2", 80, 24));

        _now = first.LastOverlayMs + 59_000; // neither is detached yet
        Assert.Null(sessions.Open("phone", "t/term3", 80, 24).Session);
        Assert.All(_ptys, p => Assert.Equal(0, p.Kills));

        second.Touch(); // the second stays attached; only the first goes quiet
        _now = first.LastOverlayMs + 61_000;
        var third = Opened(sessions.Open("phone", "t/term3", 80, 24));
        KilledOnce(_ptys[0]); // an open kills what it replaces on the pool
        Assert.Equal(0, _ptys[1].Kills);
        Assert.Null(sessions.Get("phone", "t/term"));
        Assert.Same(second, sessions.Get("phone", "t/term2"));
        Assert.Same(third, sessions.Get("phone", "t/term3"));
    }

    [Fact]
    public void TheOldestDetachedSessionIsTheOneEvicted()
    {
        using var sessions = Make();
        Opened(sessions.Open("phone", "t/term", 80, 24));
        _now += 5_000;
        Opened(sessions.Open("phone", "t/term2", 80, 24));
        _now += 120_000; // both detached; the first has been quiet longest
        Opened(sessions.Open("phone", "t/term3", 80, 24));
        KilledOnce(_ptys[0]); // an open kills what it replaces on the pool
        Assert.Equal(0, _ptys[1].Kills);
    }

    [Fact]
    public void AnEndedShellIsReplacedWithoutWaitingForTheIdleTimer()
    {
        using var sessions = Make();
        var first = Opened(sessions.Open("phone", "t/term", 80, 24));
        Opened(sessions.Open("phone", "t/term2", 80, 24));
        _ptys[0].End();
        SpinWait.SpinUntil(() => first.Exited, 5000);
        Opened(sessions.Open("phone", "t/term3", 80, 24)); // frees the slot of the shell that already ended
        KilledOnce(_ptys[0]); // an open kills what it replaces on the pool
        Assert.Equal(0, _ptys[1].Kills);
    }

    [Fact]
    public void ReopeningAfterTheShellEndedStartsAFreshOne()
    {
        using var sessions = Make();
        var first = Opened(sessions.Open("phone", "t/term", 80, 24));
        _ptys[0].End();
        SpinWait.SpinUntil(() => first.Exited, 5000);
        var again = Opened(sessions.Open("phone", "t/term", 80, 24));
        Assert.NotSame(first, again);
        Assert.Equal(2, _ptys.Count);
        KilledOnce(_ptys[0]); // an open kills what it replaces on the pool
        Assert.Same(again, sessions.Get("phone", "t/term"));
    }

    [Fact]
    public void ReopenReattachesToTheSameSession()
    {
        using var sessions = Make();
        var first = Opened(sessions.Open("phone", "t/term", 80, 24));
        _now += 30_000;
        var again = Opened(sessions.Open("phone", "t/term", 100, 30));
        Assert.Same(first, again);
        Assert.Same(first, sessions.Get("phone", "t/term"));
        Assert.Single(_ptys);
        Assert.Equal(_now, first.LastOverlayMs); // reattaching is overlay activity
        // the size changes twice, so a full-screen program gets its window-size signal and redraws (also at the same size)
        Assert.Equal([(100, 29), (100, 30)], _ptys[0].Resizes);
        Opened(sessions.Open("phone", "t/term", 100, 30));
        Assert.Equal([(100, 29), (100, 30), (100, 29), (100, 30)], _ptys[0].Resizes);
    }

    [Fact]
    public async Task ReattachDoesNotWaitForAStuckWrite()
    {
        using var sessions = Make();
        var first = Opened(sessions.Open("phone", "t/term", 80, 24));
        _ptys[0].BlockWrites();
        var stuck = Task.Run(() => first.ApplyInput(1, [0x41]));
        Assert.True(await _ptys[0].WriteEntered.WaitAsync(TimeSpan.FromSeconds(5)));
        var open = Task.Run(() => sessions.Open("phone", "t/term", 90, 30));
        Assert.Same(open, await Task.WhenAny(open, Task.Delay(TimeSpan.FromSeconds(2))));
        var again = await open;
        Assert.True(again.Attached);
        Assert.Same(first, again.Session);
        Assert.Equal(1UL, first.NextInput); // the stuck batch has not landed yet
        _ptys[0].ReleaseWrites();
        Assert.Equal(InputOutcome.Applied, (await stuck).Outcome);
        Assert.Equal(2UL, first.NextInput);
    }

    [Fact]
    public void IdleSessionIsKilledAfterConfiguredMinutes()
    {
        using var sessions = Make();
        var session = Opened(sessions.Open("phone", "t/term", 80, 24));
        _now += 10 * 60_000 - 1;
        sessions.Sweep();
        Assert.Same(session, sessions.Get("phone", "t/term"));
        Assert.Equal(0, _ptys[0].Kills);

        session.Touch(); // overlay activity restarts the clock
        _now += 10 * 60_000 - 1;
        sessions.Sweep();
        Assert.Equal(0, _ptys[0].Kills);

        _now += 1;
        sessions.Sweep();
        Assert.Equal(1, _ptys[0].Kills);
        Assert.Null(sessions.Get("phone", "t/term"));
    }

    [Fact]
    public void ZeroIdleMinutesKillsThirtySecondsAfterTheOverlayStops()
    {
        using var sessions = Make(() => WriteConfig(idleMinutes: 0));
        Opened(sessions.Open("phone", "t/term", 80, 24));
        _now += 29_999;
        sessions.Sweep();
        Assert.Equal(0, _ptys[0].Kills);
        _now += 1;
        sessions.Sweep();
        Assert.Equal(1, _ptys[0].Kills);
    }

    [Fact]
    public void IdleMinutesIsClampedToTwoHours()
    {
        using var sessions = Make(() => WriteConfig(idleMinutes: 100_000));
        Opened(sessions.Open("phone", "t/term", 80, 24));
        _now += 120 * 60_000 - 1;
        sessions.Sweep();
        Assert.Equal(0, _ptys[0].Kills);
        _now += 1;
        sessions.Sweep();
        Assert.Equal(1, _ptys[0].Kills);
    }

    [Fact]
    public void MasterSwitchOffKillsWithinOneSweep()
    {
        using var sessions = Make();
        Opened(sessions.Open("phone", "t/term", 80, 24));
        Opened(sessions.Open("phone", "t/term2", 80, 24));
        WriteConfig(enabled: false);
        sessions.Sweep();
        Assert.All(_ptys, p => Assert.Equal(1, p.Kills));
        Assert.Null(sessions.Get("phone", "t/term"));
        Assert.Null(sessions.Get("phone", "t/term2"));
    }

    [Fact]
    public void OpenIsRefusedWhileTheSwitchOrTheWidgetControlIsOff()
    {
        using var sessions = Make(() => WriteConfig(enabled: false));
        Assert.Null(sessions.Open("phone", "t/term", 80, 24).Session);
        WriteConfig(control: false);
        Assert.Null(sessions.Open("phone", "t/term", 80, 24).Session);
        Assert.Null(sessions.Open("phone", "t/nowhere", 80, 24).Session);
        Assert.Empty(_ptys);
    }

    [Theory]
    [InlineData("control")]
    [InlineData("removed")]
    public void WidgetControlOffOrWidgetRemovedKillsWithinOneSweep(string change)
    {
        using var sessions = Make();
        Opened(sessions.Open("phone", "t/term", 80, 24));
        Opened(sessions.Open("phone", "t/term2", 80, 24));
        if (change == "control") WriteConfig(control: false);
        else WriteConfig(widget: false);
        sessions.Sweep();
        Assert.Equal(1, _ptys[0].Kills);
        Assert.Equal(0, _ptys[1].Kills); // the other widget is untouched
        Assert.Null(sessions.Get("phone", "t/term"));
        Assert.NotNull(sessions.Get("phone", "t/term2"));
    }

    [Fact]
    public void SessionsOfTwoPhonesAreSeparate()
    {
        using var sessions = Make();
        var a = Opened(sessions.Open("phone-a", "t/term", 80, 24));
        var b = Opened(sessions.Open("phone-b", "t/term", 80, 24));
        Assert.NotSame(a, b);
        Assert.Same(a, sessions.Get("phone-a", "t/term"));
        Assert.Same(b, sessions.Get("phone-b", "t/term"));
        Assert.Null(sessions.Get("phone-c", "t/term"));
        Assert.Equal(2, _ptys.Count);
    }

    [Fact]
    public void CloseAllKillsEveryPty()
    {
        using var sessions = Make();
        Opened(sessions.Open("phone-a", "t/term", 80, 24));
        Opened(sessions.Open("phone-b", "t/term", 80, 24));
        sessions.CloseAll();
        Assert.All(_ptys, p => Assert.Equal(1, p.Kills));
        Assert.Null(sessions.Get("phone-a", "t/term"));
        Assert.Null(sessions.Get("phone-b", "t/term"));
    }

    [Fact]
    public void AShellThatCannotStartIsAnErrorNotAnException()
    {
        WriteConfig();
        using var sessions = new TerminalSessions(_store!, (_, _) => throw new InvalidOperationException("no shell"), () => _now, NullLogger<TerminalSessions>.Instance);
        var result = sessions.Open("phone", "t/term", 80, 24);
        Assert.Null(result.Session);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        Assert.DoesNotContain("no shell", result.Error);
        Assert.Null(sessions.Get("phone", "t/term"));
    }

    // "New key" in the settings saves lan.token as empty; the save generates a new one, so Lan.Token changes in the config.

    [Fact]
    public void NewKeyEndsRunningSessionsWithinOneSweep()
    {
        using var sessions = Make();
        Opened(sessions.Open("phone", "t/term", 80, 24));
        Opened(sessions.Open("phone", "t/term2", 80, 24));
        sessions.Sweep();
        Assert.All(_ptys, p => Assert.Equal(0, p.Kills));
        WriteConfig(token: "key-B");
        sessions.Sweep();
        Assert.All(_ptys, p => Assert.Equal(1, p.Kills));
        Assert.Null(sessions.Get("phone", "t/term"));
        Assert.Null(sessions.Get("phone", "t/term2"));
        sessions.Sweep();
        Assert.All(_ptys, p => Assert.Equal(1, p.Kills));
    }

    [Fact]
    public void ClearingTheKeyEndsRunningSessions()
    {
        using var sessions = Make();
        Opened(sessions.Open("phone", "t/term", 80, 24));
        WriteConfig(token: ""); // the store generates a fresh key for an empty one: a different key
        sessions.Sweep();
        Assert.Equal(1, _ptys[0].Kills);
        Assert.Null(sessions.Get("phone", "t/term"));

        var second = Opened(sessions.Open("phone", "t/term2", 80, 24));
        _store!.Get().Lan.Token = ""; // and a key that is simply gone
        sessions.Sweep();
        Assert.Equal(1, _ptys[1].Kills);
        Assert.Null(sessions.Get("phone", "t/term2"));
        Assert.True(second.Exited);
    }

    [Fact]
    public void ASessionOpenedBeforeAnyKeyExistedSurvivesALaterKey()
    {
        using var sessions = Make();
        _store!.Get().Lan.Token = "";
        var session = Opened(sessions.Open("phone", "t/term", 80, 24));
        _store.Get().Lan.Token = "key-B";
        sessions.Sweep();
        var again = sessions.Open("phone", "t/term", 80, 24);
        Assert.True(again.Attached);
        Assert.Same(session, again.Session);
        sessions.Sweep();
        Assert.Equal(0, _ptys[0].Kills);
        Assert.Same(session, sessions.Get("phone", "t/term"));
    }

    [Fact]
    public void ASessionOpenedWithTheCurrentKeyIsNotKilled()
    {
        using var sessions = Make();
        var session = Opened(sessions.Open("phone", "t/term", 80, 24));
        for (var i = 0; i < 50; i++)
        {
            _now += 100;
            session.Touch();
            sessions.Sweep();
        }
        var again = sessions.Open("phone", "t/term", 80, 24);
        Assert.True(again.Attached);
        Assert.Same(session, again.Session);
        sessions.Sweep();
        Assert.Equal(0, _ptys[0].Kills);
        Assert.Single(_ptys);
    }

    [Fact]
    public void ReattachOverAStaleSessionStartsAFreshOneInTheBackground()
    {
        var stubs = new List<StubPty>();
        using var sessions = Make(factory: (_, _) => { var p = new StubPty { KillDelayMs = 2000 }; lock (stubs) stubs.Add(p); return p; });
        var stale = Opened(sessions.Open("phone", "t/term", 80, 24));
        WriteConfig(token: "key-B"); // the key changed; this sweep has not run yet

        var clock = Stopwatch.StartNew();
        var fresh = sessions.Open("phone", "t/term", 80, 24);
        clock.Stop();
        Assert.Null(fresh.Error);
        Assert.False(fresh.Attached);
        Assert.NotSame(stale, fresh.Session);
        Assert.True(clock.ElapsedMilliseconds < 1000, $"Open waited {clock.ElapsedMilliseconds} ms for the stale shell");
        Assert.Same(fresh.Session, sessions.Get("phone", "t/term"));
        Assert.True(SpinWait.SpinUntil(() => stubs[0].Kills == 1, 10_000));
        sessions.Sweep();
        Assert.Equal(0, stubs[1].Kills); // the fresh shell belongs to the new key
    }

    [Fact]
    public void TheKeyAndItsFingerprintNeverReachTheLog()
    {
        const string KeyA = "super-secret-pairing-key-A", KeyB = "super-secret-pairing-key-B";
        var capture = new CaptureProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(capture).SetMinimumLevel(LogLevel.Trace));
        using var sessions = Make(() => WriteConfig(token: KeyA), log: factory.CreateLogger<TerminalSessions>());
        Opened(sessions.Open("phone", "t/term", 80, 24));
        WriteConfig(token: KeyB);
        sessions.Open("phone", "t/term", 80, 24);
        sessions.Sweep();
        sessions.CloseAll();
        SpinWait.SpinUntil(() => _ptys.All(p => p.Kills > 0), 5000);

        var log = string.Join("\n", capture.Entries);
        Assert.Contains("terminal session opened", log); // the capture works
        foreach (var key in new[] { KeyA, KeyB })
        {
            var hex = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
            foreach (var secret in new[] { key, hex, hex.ToLowerInvariant(), Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(key))) })
                Assert.DoesNotContain(secret, log);
        }
    }
}
