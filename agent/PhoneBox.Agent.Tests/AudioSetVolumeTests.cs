using System.Diagnostics;
using Xunit;

namespace PhoneBox.Agent.Tests;

/// <summary>
/// Setting the volume from the phone while the computer is busy: osascript / wpctl can take seconds, and the slider
/// must neither wait for a pointless read first nor snap back to the old volume until the next reading.
/// </summary>
public class AudioSetVolumeTests
{
    [Fact]
    public void ASuccessfulSetShowsTheNewVolumeAtOnce()
    {
        var audio = new AudioControl(() => new AudioSnapshot(50, false, true), set: _ => true);
        Assert.Equal(50, audio.Current()?.Volume);

        Assert.True(audio.SetVolume(80));
        var shown = audio.Snapshot()!; // what the next state sends to the phone
        Assert.Equal(80, shown.Volume);
        Assert.False(shown.Muted);
        Assert.True(shown.CanControl);
    }

    [Fact]
    public void ASlowReaderDoesNotHoldUpASetWhenAnOlderReadingExists()
    {
        using var gate = new ManualResetEventSlim(false);
        var reads = 0;
        var audio = new AudioControl(() =>
        {
            if (Interlocked.Increment(ref reads) > 1) gate.Wait(TimeSpan.FromSeconds(10)); // the busy computer
            return new AudioSnapshot(50, false, true);
        }, set: _ => true);
        audio.Current();
        Thread.Sleep(1000); // the reading is now older than the cache allows (900 ms)

        var clock = Stopwatch.StartNew();
        Assert.True(audio.SetVolume(70));
        Assert.True(clock.ElapsedMilliseconds < 1000, $"the set waited {clock.ElapsedMilliseconds} ms for a read");
        gate.Set();
    }

    [Fact]
    public void AFailedSetKeepsTheReadingAndReportsFailure()
    {
        var audio = new AudioControl(() => new AudioSnapshot(50, false, true), set: _ => false);
        audio.Current();
        Assert.False(audio.SetVolume(90));
        Assert.Equal(50, audio.Snapshot()?.Volume);
    }
}
