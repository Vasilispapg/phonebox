using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>
/// The automatic phone -> Mac clipboard needs a helper running on the phone, started over adb. These tests use a fake adb: they pin
/// the commands, that a helper is only started when none is running, how often the agent looks, and that a refusal never throws.
/// </summary>
public sealed class ClipHelperLauncherTests
{
    private sealed class FakeAdb
    {
        public readonly List<string[]> Calls = [];
        public readonly Dictionary<string, bool> Running = [];
        public readonly HashSet<string> Failing = [];
        public Task<string> Run(IReadOnlyList<string> args, CancellationToken ct)
        {
            Calls.Add([.. args]);
            var device = args[1];
            if (Failing.Contains(device)) throw new IOException("adb: device offline");
            var command = args[3];
            if (command == ClipHelperLauncher.CheckCommand) return Task.FromResult(Running.GetValueOrDefault(device) ? "1\n" : "0\n");
            if (command == ClipHelperLauncher.StartCommand) { Running[device] = true; return Task.FromResult(""); }
            throw new InvalidOperationException("an unexpected command: " + command);
        }
        public IEnumerable<string> Started => Calls.Where(c => c[3] == ClipHelperLauncher.StartCommand).Select(c => c[1]);
        public IEnumerable<string> Checked => Calls.Where(c => c[3] == ClipHelperLauncher.CheckCommand).Select(c => c[1]);
    }

    private sealed class CountingLogger : ILogger
    {
        public int Warnings;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings++;
        }
    }

    private long _now = 1_000_000;
    private readonly FakeAdb _adb = new();
    private readonly CountingLogger _log = new();
    private ClipHelperLauncher Launcher() => new(_adb.Run, () => _now, _log);

    [Fact]
    public async Task NothingIsAskedWhileTheFeatureIsNotWanted()
    {
        var launcher = Launcher();
        await launcher.EnsureAsync(["phone1"], wanted: false, CancellationToken.None);
        Assert.Empty(_adb.Calls);
    }

    [Fact]
    public async Task AHelperThatIsNotRunningIsStartedAfterACheck()
    {
        var launcher = Launcher();
        await launcher.EnsureAsync(["phone1"], wanted: true, CancellationToken.None);
        Assert.Equal([["-s", "phone1", "shell", ClipHelperLauncher.CheckCommand], ["-s", "phone1", "shell", ClipHelperLauncher.StartCommand]], _adb.Calls);
    }

    [Fact]
    public async Task AHelperThatIsAlreadyRunningIsLeftAlone()
    {
        _adb.Running["phone1"] = true;
        var launcher = Launcher();
        await launcher.EnsureAsync(["phone1"], wanted: true, CancellationToken.None);
        Assert.Single(_adb.Calls);
        Assert.Empty(_adb.Started);
    }

    [Fact]
    public async Task ThePhoneIsLookedAtEveryThirtySecondsNotEveryRound()
    {
        var launcher = Launcher();
        await launcher.EnsureAsync(["phone1"], true, CancellationToken.None);
        _now += 29_999;
        await launcher.EnsureAsync(["phone1"], true, CancellationToken.None);
        Assert.Single(_adb.Checked);
        _now += 1;
        await launcher.EnsureAsync(["phone1"], true, CancellationToken.None);
        Assert.Equal(2, _adb.Checked.Count());
        Assert.Single(_adb.Started);            // it was running by then
    }

    [Fact]
    public async Task AHelperTheAppStoppedIsStartedAgainWhenTheSwitchComesBackOn()
    {
        var launcher = Launcher();
        await launcher.EnsureAsync(["phone1"], true, CancellationToken.None);   // started
        _adb.Running["phone1"] = false;                                          // the phone's switch was turned off and on: the app stopped it
        _now += 30_000;
        await launcher.EnsureAsync(["phone1"], true, CancellationToken.None);
        Assert.Equal(2, _adb.Started.Count());
    }

    [Fact]
    public async Task APhoneThatWentAwayAndReturnedIsLookedAtAtOnce()
    {
        var launcher = Launcher();
        await launcher.EnsureAsync(["phone1"], true, CancellationToken.None);
        _now += 1_000;
        await launcher.EnsureAsync([], true, CancellationToken.None);            // unplugged
        _adb.Running["phone1"] = false;                                          // and it restarted meanwhile
        _now += 1_000;
        await launcher.EnsureAsync(["phone1"], true, CancellationToken.None);    // plugged in again, well inside 30 s
        Assert.Equal(2, _adb.Started.Count());
    }

    [Fact]
    public async Task TurningTheFeatureOffAndOnLooksAtOnce()
    {
        var launcher = Launcher();
        await launcher.EnsureAsync(["phone1"], true, CancellationToken.None);
        await launcher.EnsureAsync(["phone1"], false, CancellationToken.None);
        _adb.Running["phone1"] = false;
        _now += 1_000;
        await launcher.EnsureAsync(["phone1"], true, CancellationToken.None);
        Assert.Equal(2, _adb.Started.Count());
    }

    [Fact]
    public async Task AFailingAdbIsLoggedAndRetriedAfterThirtySecondsWithoutThrowing()
    {
        _adb.Failing.Add("phone1");
        var launcher = Launcher();
        await launcher.EnsureAsync(["phone1"], true, CancellationToken.None);
        Assert.Equal(1, _log.Warnings);
        _now += 10_000;
        await launcher.EnsureAsync(["phone1"], true, CancellationToken.None);
        Assert.Single(_adb.Calls);
        _adb.Failing.Clear();
        _now += 20_000;
        await launcher.EnsureAsync(["phone1"], true, CancellationToken.None);
        Assert.Single(_adb.Started);
    }

    [Fact]
    public async Task TwoPhonesAreHandledIndependently()
    {
        _adb.Running["phone2"] = true;
        var launcher = Launcher();
        await launcher.EnsureAsync(["phone1", "phone2"], true, CancellationToken.None);
        Assert.Equal(["phone1"], _adb.Started);
        Assert.Equal(["phone1", "phone2"], _adb.Checked);
    }

    [Fact]
    public async Task ACancelledRoundIsNotSwallowed()
    {
        var launcher = new ClipHelperLauncher((_, ct) => throw new OperationCanceledException(ct), () => _now, _log);
        await Assert.ThrowsAsync<OperationCanceledException>(() => launcher.EnsureAsync(["phone1"], true, CancellationToken.None));
    }

    [Fact]
    public void TheStartCommandRunsTheHelperFromTheAppsOwnApkAndDetachesIt()
    {
        var c = ClipHelperLauncher.StartCommand;
        Assert.Contains("CLASSPATH=$(pm path net.oxog.phonebox.deskpanel", c);          // the app's own APK, wherever Android put it
        Assert.Contains("setsid nohup app_process /system/bin net.oxog.phonebox.deskpanel.ClipHelper", c);
        Assert.Contains("pm list packages -U net.oxog.phonebox.deskpanel", c);          // the app's uid, so only the app can stop it
        Assert.EndsWith(">/dev/null 2>&1 &", c);                                         // adb returns at once; the helper outlives it
        Assert.DoesNotContain("/data/app/", c);
        Assert.DoesNotContain("pkill", c);                                               // a pkill pattern would kill the shell it runs in
    }

    [Fact]
    public void TheCheckLooksForTheHelpersNamedSocketAndAlwaysSucceeds()
    {
        Assert.Contains("net.oxog.phonebox.cliphelper", ClipHelperLauncher.CheckCommand);
        Assert.Contains("/proc/net/unix", ClipHelperLauncher.CheckCommand);
        Assert.EndsWith("; true", ClipHelperLauncher.CheckCommand);                      // grep exits 1 for "none", which adb would report as a failure
    }
}
