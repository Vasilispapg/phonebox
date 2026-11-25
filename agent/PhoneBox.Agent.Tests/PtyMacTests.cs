using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using PhoneBox.Agent;
using Xunit;

namespace PhoneBox.Agent.Tests;

/// <summary>
/// The tests that start a real shell: /bin/zsh, with ZDOTDIR on a fresh empty folder, so no rc file of the developer (or of
/// a CI runner) is read and the history goes to that folder, never to ~/.zsh_history. Pty.Start takes $SHELL and copies
/// the agent's environment, so both variables are set for the whole process while this collection runs, alone.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RealShellCollection : ICollectionFixture<RealShellEnvironment>
{
    public const string Name = "Real shell";
}

public sealed class RealShellEnvironment : IDisposable
{
    private readonly string? _shell = Environment.GetEnvironmentVariable("SHELL");
    private readonly string? _zdotdir = Environment.GetEnvironmentVariable("ZDOTDIR");
    public string Folder { get; } = Directory.CreateTempSubdirectory("phonebox-zdotdir-").FullName;

    public RealShellEnvironment()
    {
        Environment.SetEnvironmentVariable("SHELL", "/bin/zsh");
        Environment.SetEnvironmentVariable("ZDOTDIR", Folder);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("SHELL", _shell);
        Environment.SetEnvironmentVariable("ZDOTDIR", _zdotdir);
        try { Directory.Delete(Folder, recursive: true); } catch (Exception) { }
    }
}

/// <summary>
/// The macOS pseudo-terminal with a real shell (see <see cref="RealShellEnvironment"/>). Markers use shell arithmetic
/// (<c>$((6*7))</c>) so the echo of the typed line never matches the output that is waited for. Every test kills what it
/// started. Time limits are generous (a shared CI runner is slow): failing one means a hang, not a slow machine.
/// </summary>
[Collection(RealShellCollection.Name)]
public class PtyMacTests(RealShellEnvironment environment)
{
    internal static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    [Fact]
    public void EchoComesBack()
    {
        if (!OperatingSystem.IsMacOS()) return;
        using var pty = Shell();
        using var output = new Output(pty);
        Type(pty, "echo hi; echo __A__$((6*7))\n");
        Assert.True(output.WaitFor("__A__42", Wait), output.Text);
        Assert.Contains("hi", output.Text);
    }

    [Fact]
    public void CdPersistsBetweenInputs()
    {
        if (!OperatingSystem.IsMacOS()) return;
        using var pty = Shell();
        using var output = new Output(pty);
        Type(pty, "cd /tmp\n");
        Type(pty, "echo __B__$(pwd)__\n");
        Assert.True(output.WaitFor("__B__/tmp__", Wait), output.Text);
    }

    [Fact]
    public void CtrlCEndsSleepAndShellSurvives()
    {
        if (!OperatingSystem.IsMacOS()) return;
        using var pty = Shell();
        using var output = new Output(pty);
        var sid = Pid(pty);
        Ready(pty, output);
        Type(pty, "sleep 4242\n");
        Assert.True(Until(() => Sleeps(sid).Count == 1), "sleep did not start");
        pty.Write([0x03]);
        Assert.True(Until(() => Sleeps(sid).Count == 0), "Ctrl-C did not end sleep");
        Type(pty, "echo __C__$((1+1))\n");
        Assert.True(output.WaitFor("__C__2", Wait), output.Text);
        Assert.False(pty.HasExited);
    }

    [Fact]
    public void ResizeIsSeenByStty()
    {
        if (!OperatingSystem.IsMacOS()) return;
        using var pty = Shell();
        using var output = new Output(pty);
        pty.Resize(100, 30);
        Type(pty, "echo __D__$(stty size)__\n");
        Assert.True(output.WaitFor("__D__30 100__", Wait), output.Text);
    }

    [Fact]
    public void KillLeavesNoChildren()
    {
        if (!OperatingSystem.IsMacOS()) return;
        using var pty = Shell();
        using var output = new Output(pty);
        var sid = Pid(pty);
        Ready(pty, output);
        // a background job, a nohup one in its own process group and a foreground one
        Type(pty, "sleep 4244 & nohup sleep 4245 >/dev/null 2>&1 & sleep 4243\n");
        Assert.True(Until(() => Sleeps(sid).Count == 3), "the sleeps did not start");
        var members = PtyProcess.SessionMembers(sid);
        Assert.Contains(sid, members);

        var watch = Stopwatch.StartNew();
        pty.Kill();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), $"Kill took {watch.Elapsed}"); // about 20 ms, at most 2.5 s by design

        Assert.Empty(PtyProcess.SessionMembers(sid));
        Assert.All(members, p => Assert.False(Alive(p, sid), $"{p} is still running"));
        Assert.True(pty.HasExited);
        pty.Dispose();
    }

    [Fact]
    public void ExitIsReported()
    {
        if (!OperatingSystem.IsMacOS()) return;
        using var pty = Shell();
        using var output = new Output(pty);
        Ready(pty, output);
        Type(pty, "exit\n");
        Assert.True(output.Ended.Wait(Wait), "ReadAsync did not return 0");
        Assert.True(Until(() => pty.HasExited), "the shell was not reaped");
    }

    [Fact]
    public async Task AShellThatExitsIsReapedWithoutAskingTheSession()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var pty = Shell();
        var pid = Pid(pty);
        using var session = new TerminalSession(pty, () => 0);
        var output = new SessionOutput(session);
        Assert.Equal(InputOutcome.Applied, session.ApplyInput(1, Encoding.UTF8.GetBytes("echo __Z__$((40+2))\n")).Outcome);
        Assert.True(await output.WaitFor("__Z__42"), output.Text);
        Assert.Equal(InputOutcome.Applied, session.ApplyInput(2, "exit\n"u8).Outcome);
        Assert.True(Until(() => session.Exited), "the session did not see the shell end");
        // nothing called HasExited or Close: the session itself collected the shell, so no zombie keeps its pid
        Assert.True(Until(() => ProcessStatus(pid).Length == 0), $"the shell is still there: {ProcessStatus(pid)}");
    }

    [Fact]
    public async Task RedrawAtTheSameSizeSignalsTheProgramInFront()
    {
        if (!OperatingSystem.IsMacOS()) return;
        using var session = new TerminalSession(Shell(), () => 0);
        var output = new SessionOutput(session);
        // a program in front that says when it gets the window-size signal (the typed line's echo shows $((...)) unexpanded)
        session.ApplyInput(1, Encoding.UTF8.GetBytes("zsh -fc 'trap \"echo __W__\\$((6*7)); exit\" WINCH; echo __S__$((1+1)); while :; do sleep 0.1; done'\n"));
        Assert.True(await output.WaitFor("__S__2"), output.Text);
        session.Redraw(80, 24); // the size the shell started with: still a change, and back
        Assert.True(await output.WaitFor("__W__42"), output.Text);
    }

    [Fact]
    public void KillAfterTheShellWasReapedSignalsNobody()
    {
        if (!OperatingSystem.IsMacOS()) return;
        using var pty = Shell();
        using var output = new Output(pty);
        var process = (PtyProcess)pty;
        Ready(pty, output);
        Type(pty, "exit\n");
        Assert.True(output.Ended.Wait(Wait), "ReadAsync did not return 0");
        Assert.True(Until(() => pty.HasExited), "the shell was not reaped");
        // the pid is free now and may become another session's id: Kill must not look for that session's members
        Assert.True(process.SessionGone);
        pty.Kill();
        Assert.Equal(0, process.SignalsSent);
    }

    [Fact]
    public void KillAfterTheShellLeftStillEndsItsJobs()
    {
        if (!OperatingSystem.IsMacOS()) return;
        using var pty = Shell();
        using var output = new Output(pty);
        var process = (PtyProcess)pty;
        var sid = Pid(pty);
        Ready(pty, output);
        Type(pty, "nohup sleep 4246 >/dev/null 2>&1 & disown; exit\n");
        Assert.True(Until(() => pty.HasExited), "the shell was not reaped");
        Assert.True(Until(() => Sleeps(sid).Count == 1), "the job did not outlive the shell");
        Assert.False(process.SessionGone);
        pty.Kill();
        Assert.True(process.SignalsSent > 0);
        Assert.Empty(PtyProcess.SessionMembers(sid));
        Assert.True(process.SessionGone);
    }

    [Fact]
    public async Task FiftyCyclesDoNotLeakDescriptors()
    {
        if (!OperatingSystem.IsMacOS()) return;
        // Only terminal descriptors count: other test classes open files and sockets in parallel.
        var before = TerminalDescriptors();
        var buffer = new byte[4096];
        for (var i = 0; i < 50; i++)
        {
            var pty = Pty.Start(80, 24); // nothing is typed, so nothing goes into the history; the first output is the shell's
            var sid = Pid(pty);
            try
            {
                using var cts = new CancellationTokenSource(Wait);
                Assert.True(await pty.ReadAsync(buffer, cts.Token) > 0);
                if (i == 0) Assert.Equal(before + 1, TerminalDescriptors()); // the master counts, so a leak would show
            }
            finally
            {
                pty.Kill();
                pty.Dispose();
            }
            Assert.Empty(PtyProcess.SessionMembers(sid));
        }
        Assert.Equal(before, TerminalDescriptors());
    }

    [Fact]
    public async Task KillUnblocksABlockedWrite()
    {
        if (!OperatingSystem.IsMacOS()) return;
        using var pty = Shell();
        using var output = new Output(pty);
        var sid = Pid(pty);
        Ready(pty, output);
        Type(pty, "sleep 100\n");
        Assert.True(Until(() => Sleeps(sid).Count == 1), "sleep did not start");

        // Whole lines: the tty keeps them for sleep, which never reads, so the input queue fills and the write waits.
        var line = Encoding.ASCII.GetBytes(new string('a', 63) + "\n");
        var big = new byte[256 * 1024];
        for (var i = 0; i < big.Length; i += line.Length) line.CopyTo(big, i);
        var write = Task.Factory.StartNew(() => pty.Write(big), TaskCreationOptions.LongRunning);
        var blocked = await Task.WhenAny(write, Task.Delay(500)) != write;
        Assert.True(blocked, "the write did not wait on the full pty");

        pty.Kill();
        var finished = await Task.WhenAny(write, Task.Delay(Wait)) == write;
        Assert.True(finished, "Kill did not unblock the write");
        // the rest of the data is dropped, and Write says so as it documents
        await Assert.ThrowsAsync<ObjectDisposedException>(() => write);
        Assert.Empty(PtyProcess.SessionMembers(sid));
        pty.Dispose();
    }

    [Fact]
    public void WriteAfterKillThrowsOrIsHarmless()
    {
        if (!OperatingSystem.IsMacOS()) return;
        using var pty = Shell();
        var sid = Pid(pty);
        pty.Kill();
        Assert.Throws<ObjectDisposedException>(() => pty.Write("echo late\n"u8));
        pty.Resize(90, 20); // harmless: nothing to size
        pty.Dispose();
        Assert.Throws<ObjectDisposedException>(() => pty.Write("echo later\n"u8));
        Assert.Empty(PtyProcess.SessionMembers(sid));
        Assert.True(pty.HasExited);
    }

    /// <summary>zsh without the developer's settings (<see cref="RealShellEnvironment"/>): its history goes to the empty folder.</summary>
    private IPtyProcess Shell()
    {
        Assert.Equal(environment.Folder, Environment.GetEnvironmentVariable("ZDOTDIR"));
        return Pty.Start(80, 24);
    }

    private static void Type(IPtyProcess pty, string text) => pty.Write(Encoding.UTF8.GetBytes(text));

    private static int Pid(IPtyProcess pty) => ((PtyProcess)pty).Pid;

    /// <summary>Waits until the shell has read its startup files and runs commands.</summary>
    private static void Ready(IPtyProcess pty, Output output)
    {
        Type(pty, "echo __R__$((40+2))\n");
        Assert.True(output.WaitFor("__R__42", Wait), output.Text);
    }

    internal static bool Until(Func<bool> condition) => Until(condition, Wait);

    internal static bool Until(Func<bool> condition, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < timeout)
        {
            if (condition()) return true;
            Thread.Sleep(20);
        }
        return condition();
    }

    /// <summary><c>ps -o stat= -p pid</c>: empty once the process is gone and reaped ("Z" for a zombie).</summary>
    private static string ProcessStatus(int pid)
    {
        using var ps = Process.Start(new ProcessStartInfo("/bin/ps", ["-o", "stat=", "-p", pid.ToString()]) { RedirectStandardOutput = true, UseShellExecute = false })!;
        var text = ps.StandardOutput.ReadToEnd().Trim();
        ps.WaitForExit();
        return text;
    }

    private static List<int> Sleeps(int sid) => PtyProcess.SessionMembers(sid).Where(p =>
    {
        try
        {
            using var process = Process.GetProcessById(p);
            return process.ProcessName == "sleep";
        }
        catch (Exception) { return false; }
    }).ToList();

    [DllImport("libc", SetLastError = true)] private static extern int kill(int pid, int sig);
    [DllImport("libc")] private static extern int getsid(int pid);
    [DllImport("libc")] private static extern int isatty(int fd);

    /// <summary>The pid still names a process of that session (a reused pid in another session does not count).</summary>
    private static bool Alive(int pid, int sid) => kill(pid, 0) == 0 && getsid(pid) == sid;

    private static int TerminalDescriptors() =>
        Directory.GetFiles("/dev/fd").Select(f => int.TryParse(Path.GetFileName(f), out var fd) ? fd : -1)
            .Count(fd => fd > 2 && isatty(fd) == 1);

    /// <summary>Reads a session's output as the overlay does (tests only).</summary>
    private sealed class SessionOutput(TerminalSession session)
    {
        private readonly StringBuilder _text = new();
        private ulong _cursor;

        public string Text => _text.ToString();

        public async Task<bool> WaitFor(string marker)
        {
            var watch = Stopwatch.StartNew();
            while (!Text.Contains(marker) && watch.Elapsed < Wait)
            {
                var chunk = await session.ReadAsync(_cursor, 2500, CancellationToken.None);
                _text.Append(Encoding.Latin1.GetString(chunk.Data));
                _cursor = chunk.Cursor;
                if (chunk.Exited && chunk.Data.Length == 0) break;
            }
            return Text.Contains(marker);
        }
    }

    /// <summary>
    /// Collects the shell's output in the background (tests only: the pty itself never decodes it). A read that fails
    /// (other than being stopped by Dispose) is not swallowed: it ends <see cref="Text"/>, which every assertion prints.
    /// </summary>
    private sealed class Output : IDisposable
    {
        private readonly StringBuilder _text = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly ManualResetEventSlim _ended = new();
        private readonly Task _pump;
        private Exception? _failure;

        public Output(IPtyProcess pty)
        {
            _pump = Task.Run(async () =>
            {
                var buffer = new byte[4096];
                try
                {
                    while (true)
                    {
                        var n = await pty.ReadAsync(buffer, _stop.Token);
                        if (n <= 0) break;
                        lock (_text) _text.Append(Encoding.Latin1.GetString(buffer, 0, n));
                    }
                    _ended.Set();
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
                catch (Exception ex) { Volatile.Write(ref _failure, ex); }
            });
        }

        public string Text
        {
            get
            {
                string text;
                lock (_text) text = _text.ToString();
                return Volatile.Read(ref _failure) is { } failure ? $"{text}\n[reading the pty failed: {failure}]" : text;
            }
        }

        public ManualResetEventSlim Ended => _ended;

        public bool WaitFor(string marker, TimeSpan timeout)
        {
            Until(() => Volatile.Read(ref _failure) is not null || Text.Contains(marker), timeout);
            return Volatile.Read(ref _failure) is null && Text.Contains(marker);
        }

        public void Dispose()
        {
            _stop.Cancel();
            try { _pump.Wait(TimeSpan.FromSeconds(2)); } catch (Exception) { }
        }
    }
}
