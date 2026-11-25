using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>The key file, the Finder Quick Action and the outbox exist only while "Send files to the phone" is on.</summary>
public sealed class TransferSwitchWatcherTests : IDisposable
{
    private sealed class FakeQuickAction : IQuickActionInstaller
    {
        public int Installs, Removes;
        public bool FailInstall;
        public void Install() { Installs++; if (FailInstall) throw new IOException("cannot write the workflow"); }
        public void Remove() => Removes++;
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), "pb-watch-" + Guid.NewGuid());
    private readonly ConfigStore _store;
    private readonly LocalTransferKey _key;
    private readonly Outbox _outbox;
    private readonly FakeQuickAction _qa = new();
    private readonly TransferSwitchWatcher _watcher;
    private int _stamp;

    public TransferSwitchWatcherTests()
    {
        Directory.CreateDirectory(_root);
        _store = new ConfigStore(Path.Combine(_root, "config.json"));
        _key = new LocalTransferKey(Path.Combine(_root, "transfer.json"), 8765);
        _outbox = new Outbox(Path.Combine(_root, "outbox"), TimeProvider.System);
        _watcher = new TransferSwitchWatcher(_store, _outbox, _key, _qa, TimeSpan.FromSeconds(5), NullLogger<TransferSwitchWatcher>.Instance);
    }

    private void SetSwitch(bool on)
    {
        var path = Path.Combine(_root, "config.json");
        File.WriteAllText(path, "{\"bridge\":{\"filesToPhone\":" + (on ? "true" : "false") + "}}");
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(++_stamp));
    }

    [Fact]
    public void TurningOnWritesTheKeyAndInstallsOnce()
    {
        SetSwitch(true);
        _watcher.Tick();
        _watcher.Tick();
        Assert.True(File.Exists(_key.Path));
        Assert.Equal(1, _qa.Installs);
        Assert.Equal(0, _qa.Removes);
    }

    [Fact]
    public async Task TurningOffRemovesTheKeyTheQuickActionAndTheOutbox()
    {
        SetSwitch(true);
        _watcher.Tick();
        await _outbox.AddAsync("a.txt", null, null, new MemoryStream("x"u8.ToArray()), default);
        SetSwitch(false);
        _watcher.Tick();
        _watcher.Tick();
        Assert.False(File.Exists(_key.Path));
        Assert.Equal(1, _qa.Removes);
        Assert.Empty(_outbox.List());
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(_root, "outbox")));
    }

    [Fact]
    public void StartingWithTheSwitchOffClearsWhatAnEarlierRunLeft()
    {
        File.WriteAllText(_key.Path, "{\"port\":8765,\"key\":\"left-over-key-left-over-key-1234\"}");
        SetSwitch(false);
        _watcher.Tick();
        Assert.False(File.Exists(_key.Path));
        Assert.Equal(1, _qa.Removes);
    }

    [Fact]
    public void AFailingInstallDoesNotStopTheWatcherAndIsTriedAgain()
    {
        SetSwitch(true);
        _qa.FailInstall = true;
        _watcher.Tick(); // throws inside, is swallowed and logged
        _qa.FailInstall = false;
        _watcher.Tick();
        Assert.Equal(2, _qa.Installs);
        _watcher.Tick();
        Assert.Equal(2, _qa.Installs); // done: not again
    }

    [Fact]
    public async Task TheSweepRunsWhileOn()
    {
        SetSwitch(true);
        var clock = new Clock();
        var outbox = new Outbox(Path.Combine(_root, "o2"), clock);
        var watcher = new TransferSwitchWatcher(_store, outbox, _key, _qa, TimeSpan.FromSeconds(5), NullLogger<TransferSwitchWatcher>.Instance);
        await outbox.AddAsync("a", null, null, new MemoryStream("x"u8.ToArray()), default);
        clock.Now += TimeSpan.FromHours(2);
        watcher.Tick();
        Assert.Equal(OutboxState.Expired, Assert.Single(outbox.List()).State);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }
}
