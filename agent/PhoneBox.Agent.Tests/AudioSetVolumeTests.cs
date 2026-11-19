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

    [Fact]
    public async Task TwoSetsAtOnceAreWrittenAndRememberedInOrder()
    {
        // a dragged slider sends requests that overlap: the cache must end on the volume the device really ended on,
        // not on the one whose write happened to finish last
        using var firstInside = new ManualResetEventSlim(false);
        using var releaseFirst = new ManualResetEventSlim(false);
        var gate = new object();
        int running = 0, maxRunning = 0, calls = 0;
        var audio = new AudioControl(() => new AudioSnapshot(50, false, true), set: _ =>
        {
            lock (gate) { running++; maxRunning = Math.Max(maxRunning, running); }
            try
            {
                if (Interlocked.Increment(ref calls) == 1) { firstInside.Set(); releaseFirst.Wait(TimeSpan.FromSeconds(10)); }
                return true;
            }
            finally { lock (gate) running--; }
        });
        audio.Current();

        var first = Task.Run(() => audio.SetVolume(20));
        Assert.True(firstInside.Wait(TimeSpan.FromSeconds(10)));
        var second = Task.Run(() => audio.SetVolume(80)); // arrives while the first write is still going
        await Task.Delay(200); // without the gate it would have finished by now
        releaseFirst.Set();
        await Task.WhenAll(first, second);

        Assert.Equal(1, maxRunning);
        Assert.Equal(80, audio.Snapshot()?.Volume);
    }

    [Theory]
    [InlineData(true, true, false)]   // the backend cleared mute: not muted
    [InlineData(false, true, true)]   // a volume-only write on a muted sink: still muted
    [InlineData(false, false, false)]
    public void ASetThatDoesNotClearMuteKeepsTheMuteState(bool clearsMute, bool wasMuted, bool expected) =>
        Assert.Equal(expected, AudioControl.MutedAfterSet(clearsMute, new AudioSnapshot(50, wasMuted, true)));

    [Fact]
    public void WithNoEarlierReadingASetIsNotMuted() => Assert.False(AudioControl.MutedAfterSet(false, null));

    [Theory]
    [InlineData(new[] { true, true }, true)]    // stereo, both written
    [InlineData(new[] { true, false }, false)]  // one channel written, one refused: not done
    [InlineData(new[] { false, false }, false)]
    public void StereoNeedsBothChannelsWritten(bool[] results, bool expected) =>
        Assert.Equal(expected, MacAudioProbe.AllWritten(results.Select(r => (bool?)r)));

    [Fact]
    public void AMonoOutputNeedsOnlyItsOneChannelAndNoChannelsIsFailure()
    {
        Assert.True(MacAudioProbe.AllWritten([true, null]));  // the second channel does not exist
        Assert.False(MacAudioProbe.AllWritten([null, null])); // nothing to write
    }
}
