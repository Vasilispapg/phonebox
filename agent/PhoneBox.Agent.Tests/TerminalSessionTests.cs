using System.Diagnostics;
using System.Threading.Channels;
using PhoneBox.Agent;
using Xunit;

public sealed class TerminalSessionTests
{
    private static TerminalSession NewSession(FakePty pty, int bufferBytes = 262144) => new(pty, () => 0, bufferBytes);

    private static async Task<ulong> WaitForCursor(TerminalSession session, ulong target)
    {
        ulong cursor = 0;
        while (cursor < target) cursor = (await session.ReadAsync(cursor, 5000, CancellationToken.None)).Cursor;
        return cursor;
    }

    [Fact]
    public async Task ReadReturnsBytesAfterCursorAndWaitsForMore()
    {
        var pty = new FakePty();
        using var session = NewSession(pty);
        pty.Push(1, 2, 3);
        var first = await session.ReadAsync(0, 5000, CancellationToken.None);
        Assert.Equal([1, 2, 3], first.Data);
        Assert.Equal(3UL, first.Cursor);
        Assert.False(first.Reset);

        var waiting = session.ReadAsync(first.Cursor, 5000, CancellationToken.None);
        Assert.False(waiting.IsCompleted);
        pty.Push(4, 5);
        var second = await waiting;
        Assert.Equal([4, 5], second.Data);
        Assert.Equal(5UL, second.Cursor);

        var idle = await session.ReadAsync(second.Cursor, 50, CancellationToken.None);
        Assert.Empty(idle.Data);
        Assert.Equal(5UL, idle.Cursor);
        Assert.False(idle.Exited);
    }

    [Fact]
    public async Task ReadWithOldCursorReportsResetAndStartsAtOldestKeptByte()
    {
        var pty = new FakePty();
        using var session = NewSession(pty);
        var all = new byte[300 * 1024];
        for (var i = 0; i < all.Length; i++) all[i] = (byte)(i % 251);
        pty.Push(all);
        await WaitForCursor(session, (ulong)all.Length);

        var chunk = await session.ReadAsync(0, 5000, CancellationToken.None);
        Assert.True(chunk.Reset);
        Assert.Equal(262144, chunk.Data.Length);
        Assert.Equal((ulong)all.Length, chunk.Cursor);
        Assert.Equal(all[^262144..], chunk.Data);

        var current = await session.ReadAsync(chunk.Cursor - 10, 5000, CancellationToken.None);
        Assert.False(current.Reset);
        Assert.Equal(all[^10..], current.Data);
    }

    [Fact]
    public async Task BinaryAndInvalidUtf8SurviveAndSplitSequenceIsContiguous()
    {
        var pty = new FakePty();
        using var session = NewSession(pty);
        pty.Push(0x1b, 0x5b);
        var first = await session.ReadAsync(0, 5000, CancellationToken.None);
        pty.Push(0x31, 0x6d, 0xff, 0xfe);
        var second = await session.ReadAsync(first.Cursor, 5000, CancellationToken.None);
        Assert.Equal([0x1b, 0x5b, 0x31, 0x6d, 0xff, 0xfe], first.Data.Concat(second.Data).ToArray());
    }

    [Fact]
    public void InputIsAppliedOnceInOrder()
    {
        var pty = new FakePty();
        using var session = NewSession(pty);
        var first = session.ApplyInput(1, [0x6c, 0x73]);
        Assert.Equal(InputOutcome.Applied, first.Outcome);
        Assert.Equal(2UL, first.Expected);

        Assert.Equal(InputOutcome.Duplicate, session.ApplyInput(1, [0x6c, 0x73]).Outcome);
        var gap = session.ApplyInput(3, [0x78]);
        Assert.Equal(InputOutcome.OutOfOrder, gap.Outcome);
        Assert.Equal(2UL, gap.Expected);

        Assert.Single(pty.Writes);
        Assert.Equal([0x6c, 0x73], pty.Writes[0]);
        Assert.Equal(InputOutcome.Applied, session.ApplyInput(2, [0x0d]).Outcome);
        Assert.Equal(2, pty.Writes.Count);
    }

    [Fact]
    public void ResizeIsClamped()
    {
        var pty = new FakePty();
        using var session = NewSession(pty);
        session.Resize(0, 100000);
        session.Resize(100000, 0);
        session.Resize(80, 24);
        Assert.Equal([(10, 200), (500, 2), (80, 24)], pty.Resizes);
    }

    [Fact]
    public async Task ExitIsReportedAndReadsDoNotHang()
    {
        var pty = new FakePty();
        using var session = NewSession(pty);
        pty.Push(7);
        var first = await session.ReadAsync(0, 5000, CancellationToken.None);
        Assert.False(first.Exited);
        Assert.False(session.Exited);

        pty.End();
        var watch = Stopwatch.StartNew();
        var last = await session.ReadAsync(first.Cursor, 10000, CancellationToken.None);
        Assert.True(last.Exited);
        Assert.True(session.Exited);
        Assert.Empty(last.Data);

        var again = await session.ReadAsync(last.Cursor, 10000, CancellationToken.None);
        Assert.True(again.Exited);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void CloseKillsAndIsIdempotent()
    {
        var pty = new FakePty();
        var session = NewSession(pty);
        session.Close();
        session.Close();
        session.Dispose();
        Assert.Equal(1, pty.Kills);
        Assert.True(pty.Disposed);
        Assert.True(session.Exited);
    }

    [Fact]
    public void TouchMarksOverlayActivity()
    {
        long now = 1000;
        using var session = new TerminalSession(new FakePty(), () => now);
        Assert.Equal(1000, session.LastOverlayMs);
        now = 5000;
        session.Touch();
        Assert.Equal(5000, session.LastOverlayMs);
    }

    [Fact]
    public async Task ApplyInputBlockedInWriteDoesNotBlockReadsOrOutput()
    {
        var pty = new FakePty();
        using var session = NewSession(pty);
        pty.BlockWrites();
        var apply = Task.Run(() => session.ApplyInput(1, [0x41]));
        Assert.True(await pty.WriteEntered.WaitAsync(TimeSpan.FromSeconds(5)));

        pty.Push(9, 8, 7);
        var read = session.ReadAsync(0, 5000, CancellationToken.None);
        Assert.Same(read, await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(1))));
        Assert.Equal([9, 8, 7], (await read).Data);
        Assert.False(apply.IsCompleted);

        pty.ReleaseWrites();
        Assert.Equal(InputOutcome.Applied, (await apply).Outcome);
    }

    [Fact]
    public async Task CloseWhileWriteIsBlockedCompletes()
    {
        var pty = new FakePty();
        var session = NewSession(pty);
        pty.BlockWrites();
        var apply = Task.Run(() => session.ApplyInput(1, [0x41]));
        Assert.True(await pty.WriteEntered.WaitAsync(TimeSpan.FromSeconds(5)));

        var close = Task.Run(session.Close);
        Assert.Same(close, await Task.WhenAny(close, Task.Delay(TimeSpan.FromSeconds(5))));
        await close;
        var result = await apply; // finishes without throwing
        Assert.Equal(InputOutcome.Applied, result.Outcome);
        Assert.True(pty.Disposed);
        Assert.Equal(0, pty.CallsAfterDispose);
        Assert.Equal(1, pty.Kills);
    }

    [Fact]
    public void ApplyInputAfterCloseReturnsClosedAndNeverWrites()
    {
        var pty = new FakePty();
        var session = NewSession(pty);
        session.Close();
        var result = session.ApplyInput(1, [0x41]);
        Assert.Equal(InputOutcome.Closed, result.Outcome);
        Assert.Empty(pty.Writes);
        Assert.Equal(0, pty.CallsAfterDispose);
    }

    // ---------------------------------------------------------------- a write stuck on a full pty (the shell is not reading)

    private static async Task<T> Prompt<T>(Task<T> work)
    {
        Assert.Same(work, await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(2))));
        return await work;
    }

    [Fact]
    public async Task InputWhileAWriteIsStuckIsBusyAtOnceAndLandsOnceAfter()
    {
        var pty = new FakePty();
        using var session = NewSession(pty);
        pty.BlockWrites();
        var stuck = Task.Run(() => session.ApplyInput(1, [0x41]));
        Assert.True(await pty.WriteEntered.WaitAsync(TimeSpan.FromSeconds(5)));

        // the phone's resend of the same batch, and a later batch: neither parks a thread behind the stuck write
        Assert.Equal(InputOutcome.Busy, (await Prompt(Task.Run(() => session.ApplyInput(1, [0x41])))).Outcome);
        Assert.Equal(InputOutcome.Busy, (await Prompt(Task.Run(() => session.ApplyInput(2, [0x42])))).Outcome);
        Assert.False(stuck.IsCompleted);

        pty.ReleaseWrites();
        Assert.Equal(InputOutcome.Applied, (await stuck).Outcome);
        Assert.Equal(InputOutcome.Duplicate, session.ApplyInput(1, [0x41]).Outcome); // the resend now counts as landed
        Assert.Equal(InputOutcome.Applied, session.ApplyInput(2, [0x42]).Outcome);
        Assert.Equal([[0x41], [0x42]], pty.Writes);
    }

    [Fact]
    public async Task ResizeWhileAWriteIsStuckIsSkippedPromptly()
    {
        var pty = new FakePty();
        using var session = NewSession(pty);
        pty.BlockWrites();
        var stuck = Task.Run(() => session.ApplyInput(1, [0x41]));
        Assert.True(await pty.WriteEntered.WaitAsync(TimeSpan.FromSeconds(5)));

        await Prompt(Task.Run(() => { session.Resize(100, 30); return 0; }));
        await Prompt(Task.Run(() => { session.Redraw(100, 30); return 0; }));
        Assert.Empty(pty.Resizes); // skipped: the next size change sends the size again

        pty.ReleaseWrites();
        await stuck;
        session.Resize(100, 30);
        Assert.Equal([(100, 30)], pty.Resizes);
    }

    [Fact]
    public void RedrawChangesTheSizeTwiceSoFullScreenProgramsAreSignalled()
    {
        var pty = new FakePty();
        using var session = NewSession(pty);
        session.Redraw(80, 24); // the same size as before: still a real change, and back
        session.Redraw(100, 2);
        session.Redraw(0, 100000);
        Assert.Equal([(80, 23), (80, 24), (100, 3), (100, 2), (10, 199), (10, 200)], pty.Resizes);
    }

    [Fact]
    public async Task TheShellIsReapedWhenItsOutputEnds()
    {
        var pty = new FakePty();
        using var session = NewSession(pty);
        Assert.Equal(0, pty.ExitChecks);
        pty.End();
        await Ended(session);
        Assert.True(session.Exited);
        Assert.Equal(1, pty.ExitChecks); // asking a real pty whether the shell exited is what collects it (no zombie)
    }

    private static async Task Ended(TerminalSession session, double seconds = 5) =>
        Assert.Same(session.Reading, await Task.WhenAny(session.Reading, Task.Delay(TimeSpan.FromSeconds(seconds))));

    [Fact]
    public async Task AShellNotCollectableYetIsAskedAgainUntilItIs()
    {
        // the pty reports the end a moment before the shell is a zombie: one ask would leave it unreaped
        var pty = new FakePty { RunningChecks = 4 };
        using var session = NewSession(pty);
        pty.End();
        await Ended(session);
        Assert.Equal(5, pty.ExitChecks); // asked until the answer was yes, then never again
        await Task.Delay(300);
        Assert.Equal(5, pty.ExitChecks);
    }

    [Fact]
    public async Task ReadersHearTheEndBeforeTheShellIsCollected()
    {
        var pty = new FakePty();
        using var session = NewSession(pty);
        pty.HoldExit();
        var waiting = session.ReadAsync(0, 10_000, CancellationToken.None);
        pty.End();
        Assert.Same(waiting, await Task.WhenAny(waiting, Task.Delay(TimeSpan.FromSeconds(2))));
        Assert.True((await waiting).Exited);
        Assert.True(session.Exited);
        Assert.True(SpinWait.SpinUntil(() => pty.ExitChecks >= 2, 5000), $"asks: {pty.ExitChecks}"); // still asking meanwhile
        Assert.False(session.Reading.IsCompleted);
        pty.ReleaseExit();
        await Ended(session);
    }

    [Fact]
    public async Task CloseStopsTheAsking()
    {
        var pty = new FakePty();
        var session = NewSession(pty);
        pty.HoldExit();
        pty.End();
        Assert.True(SpinWait.SpinUntil(() => pty.ExitChecks >= 2, 5000), $"asks: {pty.ExitChecks}");
        session.Close(); // kills and reaps the shell itself
        await Ended(session, 1);
        Assert.Equal(TaskStatus.RanToCompletion, session.Reading.Status); // nothing escaped
        var asks = pty.ExitChecks;
        await Task.Delay(300);
        Assert.Equal(asks, pty.ExitChecks);
        Assert.Equal(1, pty.Kills);
    }

    [Fact]
    public async Task AShellThatIsNeverCollectableIsAskedForABoundedTime()
    {
        var pty = new FakePty();
        using var session = new TerminalSession(pty, () => 0, reapFor: TimeSpan.FromMilliseconds(300));
        pty.HoldExit(); // e.g. a pty that reports the end while its child lives on
        pty.End();
        await Ended(session, 3); // gives up by itself
        var asks = pty.ExitChecks;
        Assert.InRange(asks, 2, 20); // about every 50 ms for 300 ms
        await Task.Delay(300);
        Assert.Equal(asks, pty.ExitChecks);
        var last = await session.ReadAsync(0, 1000, CancellationToken.None); // still answers
        Assert.True(last.Exited);
        session.Close();
        Assert.Equal(1, pty.Kills);
        Assert.True(pty.Disposed);
    }

    [Fact]
    public void ResizeAfterCloseIsIgnored()
    {
        var pty = new FakePty();
        var session = NewSession(pty);
        session.Close();
        session.Resize(80, 24);
        Assert.Empty(pty.Resizes);
        Assert.Equal(0, pty.CallsAfterDispose);
    }
}
