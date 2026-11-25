using System.Collections;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PhoneBox.Agent;

/// <summary>
/// The Terminal widget's pseudo-terminal on macOS: posix_openpt + posix_spawn(POSIX_SPAWN_SETSID), with /bin/sh as a fixed
/// one-line trampoline that takes the controlling tty and then execs the user's login shell in the same process. No fork
/// in managed code, no helper binary. Nothing typed or printed is decoded, logged or stored here.
/// </summary>
public static class Pty
{
    /// <summary>Starts the user's login shell (<c>$SHELL</c> when it is an absolute path that exists, else /bin/zsh) in a new pty.</summary>
    public static IPtyProcess Start(int cols, int rows)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("The terminal's pseudo-terminal is built for macOS only.");
        return PtyProcess.Spawn(cols, rows);
    }
}

/// <summary>
/// A login shell in its own session on a macOS pty. Thread-safe: <see cref="ReadAsync"/>, <see cref="Write"/>,
/// <see cref="Resize"/>, <see cref="Kill"/> and <see cref="Dispose"/> may be called from different threads. No call
/// blocks a thread for long: reads and writes wait in 200 ms polls and notice <see cref="Kill"/> between them.
/// </summary>
internal sealed class PtyProcess : IPtyProcess
{
    private const string Lib = "/usr/lib/libSystem.dylib";

    // The script is a constant and the paths are its positional arguments: nothing is ever spliced into it.
    // sh (unlike zsh) opens the tty without O_NOCTTY, so as session leader it takes it as the controlling terminal.
    private const string Trampoline = "exec 0<>\"$1\" 1>&0 2>&0 && exec \"$2\" -l";
    private const string ShPath = "/bin/sh";

    private const int O_NONBLOCK = 0x4, O_RDWR = 0x2, O_NOCTTY = 0x20000, O_CLOEXEC = 0x1000000;
    private const int F_GETFL = 3, F_SETFL = 4;
    private const short POSIX_SPAWN_SETSIGDEF = 0x4, POSIX_SPAWN_SETSIGMASK = 0x8, POSIX_SPAWN_SETSID = 0x400, POSIX_SPAWN_CLOEXEC_DEFAULT = 0x4000;
    private const nuint TIOCSWINSZ = 0x80087467;
    private const int SIGHUP = 1, SIGKILL = 9, SIGCONT = 19;
    private const int WNOHANG = 1;
    private const short POLLIN = 0x1, POLLOUT = 0x4, POLLERR = 0x8, POLLHUP = 0x10, POLLNVAL = 0x20;
    private const int EINTR = 4, EIO = 5, ECHILD = 10, EAGAIN = 35;
    private const int PollMs = 200;

    private static readonly TimeSpan HangupGrace = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan KillGrace = TimeSpan.FromSeconds(2);

    private readonly SafeFileHandle _master;
    private readonly object _reapLock = new();
    private readonly object _killLock = new();
    private bool _reaped;
    private bool _sessionGone; // latched: once true, nothing is signalled any more (the pid may belong to someone else)
    private bool _killDone;
    private int _killed;
    private int _signalsSent;

    private PtyProcess(SafeFileHandle master, int pid)
    {
        _master = master;
        Pid = pid;
    }

    /// <summary>The shell's pid, which is also its session id.</summary>
    internal int Pid { get; }

    /// <summary>How many signals Kill has sent (tests).</summary>
    internal int SignalsSent => Volatile.Read(ref _signalsSent);

    /// <summary>The shell is reaped and its session has no members left (tests).</summary>
    internal bool SessionGone
    {
        get { lock (_reapLock) return _sessionGone; }
    }

    internal static PtyProcess Spawn(int cols, int rows)
    {
        var master = posix_openpt(O_RDWR | O_NOCTTY | O_CLOEXEC);
        if (master < 0) throw Failure("posix_openpt");
        SafeFileHandle? handle = new((IntPtr)master, ownsHandle: true);
        var slave = -1;
        IntPtr actions = 0, attr = 0;
        bool actionsReady = false, attrReady = false;
        IntPtr[]? argv = null, envp = null;
        try
        {
            if (grantpt(master) != 0) throw Failure("grantpt");
            if (unlockpt(master) != 0) throw Failure("unlockpt");
            var name = new byte[128];
            if (ptsname_r(master, name, (nuint)name.Length) != 0) throw Failure("ptsname_r");
            var end = Array.IndexOf(name, (byte)0);
            var slavePath = Encoding.UTF8.GetString(name, 0, end < 0 ? name.Length : end);

            // Non-blocking, so a write to a full pty waits in polls that Kill can end (a blocked write() could not be).
            var flags = Fcntl(master, F_GETFL, 0);
            if (flags < 0 || Fcntl(master, F_SETFL, flags | O_NONBLOCK) < 0) throw Failure("fcntl");

            // TIOCSWINSZ on the master fails with ENOTTY until the slave has been opened once.
            slave = open(slavePath, O_RDWR | O_NOCTTY | O_CLOEXEC);
            if (slave < 0) throw Failure("open");
            if (!SetSize(master, cols, rows)) throw Failure("ioctl");

            var shell = ShellPath();
            var home = HomePath();

            Check(posix_spawn_file_actions_init(ref actions), "posix_spawn_file_actions_init");
            actionsReady = true;
            Check(posix_spawn_file_actions_addopen(ref actions, 0, slavePath, O_RDWR | O_NOCTTY, 0), "addopen");
            Check(posix_spawn_file_actions_adddup2(ref actions, 0, 1), "adddup2");
            Check(posix_spawn_file_actions_adddup2(ref actions, 0, 2), "adddup2");
            Check(posix_spawn_file_actions_addchdir_np(ref actions, home), "addchdir_np");

            Check(posix_spawnattr_init(ref attr), "posix_spawnattr_init");
            attrReady = true;
            // SETSIGDEF: .NET ignores SIGPIPE and an ignored signal survives exec. CLOEXEC_DEFAULT: no agent fd reaches the shell.
            Check(posix_spawnattr_setflags(ref attr, POSIX_SPAWN_SETSID | POSIX_SPAWN_SETSIGDEF | POSIX_SPAWN_SETSIGMASK | POSIX_SPAWN_CLOEXEC_DEFAULT), "setflags");
            var all = 0xFFFFFFFFu;
            var none = 0u;
            Check(posix_spawnattr_setsigdefault(ref attr, ref all), "setsigdefault");
            Check(posix_spawnattr_setsigmask(ref attr, ref none), "setsigmask");

            argv = Strings([ShPath, "-c", Trampoline, "phonebox-pty", slavePath, shell]);
            envp = Strings(ShellEnvironment(shell, home));
            Check(posix_spawn(out var pid, ShPath, ref actions, ref attr, argv, envp), "posix_spawn");

            var process = new PtyProcess(handle, pid);
            handle = null;
            return process;
        }
        finally
        {
            // posix_spawn returns after the child's file actions and exec, so the child already holds its own slave fd.
            if (slave >= 0) close(slave);
            if (actionsReady) posix_spawn_file_actions_destroy(ref actions);
            if (attrReady) posix_spawnattr_destroy(ref attr);
            Free(argv);
            Free(envp);
            handle?.Dispose();
        }
    }

    /// <summary>Reads output; returns 0 when the shell has ended or the pty was killed. Throws when <paramref name="ct"/> is cancelled.</summary>
    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct) =>
        buffer.IsEmpty ? ValueTask.FromResult(0) : new(Task.Run(() => Read(buffer, ct), ct));

    private int Read(Memory<byte> buffer, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _killed) != 0) return 0;
            var added = false;
            try
            {
                try { _master.DangerousAddRef(ref added); }
                catch (ObjectDisposedException) { return 0; }
                var fd = (int)_master.DangerousGetHandle();
                var poll = new PollFd { Fd = fd, Events = POLLIN };
                var ready = PollOnce(ref poll);
                if (ready < 0) throw Failure("poll");
                if (ready == 0) continue;
                if ((poll.REvents & POLLNVAL) != 0) return 0;
                var n = read(fd, ref MemoryMarshal.GetReference(buffer.Span), (nuint)buffer.Length);
                if (n > 0) return (int)n;
                if (n == 0) return 0;
                var error = Marshal.GetLastPInvokeError();
                if (error == EINTR) continue;
                if (error == EAGAIN && (poll.REvents & (POLLHUP | POLLERR)) == 0) continue;
                if (error is EAGAIN or EIO) return 0; // hung up: the shell has ended
                throw Failure("read", error);
            }
            finally
            {
                if (added) _master.DangerousRelease();
            }
        }
    }

    /// <summary>
    /// Writes all of <paramref name="data"/>. When the pty is full (the shell is not reading) it waits until there is room.
    /// Throws <see cref="ObjectDisposedException"/> after <see cref="Kill"/> or <see cref="Dispose"/>, also when Kill ends a
    /// write that was waiting (the rest is dropped), and <see cref="IOException"/> when the shell has ended by itself.
    /// </summary>
    public void Write(ReadOnlySpan<byte> data)
    {
        ThrowIfKilled();
        var added = false;
        try
        {
            _master.DangerousAddRef(ref added);
            var fd = (int)_master.DangerousGetHandle();
            while (!data.IsEmpty)
            {
                var n = write(fd, ref MemoryMarshal.GetReference(data), (nuint)data.Length);
                if (n > 0)
                {
                    data = data[(int)n..];
                    continue;
                }
                var error = n == 0 ? EAGAIN : Marshal.GetLastPInvokeError();
                if (error == EINTR) continue;
                if (error == EIO) throw new IOException("The shell has ended.");
                if (error != EAGAIN) throw Failure("write", error);
                ThrowIfKilled();
                var poll = new PollFd { Fd = fd, Events = POLLOUT };
                if (PollOnce(ref poll) < 0) throw Failure("poll");
                if ((poll.REvents & POLLNVAL) != 0) throw new ObjectDisposedException(nameof(PtyProcess));
                ThrowIfKilled();
            }
        }
        finally
        {
            if (added) _master.DangerousRelease();
        }
    }

    /// <summary>Sets the window size; the kernel sends SIGWINCH to the foreground job. Does nothing after <see cref="Kill"/>.</summary>
    public void Resize(int cols, int rows)
    {
        if (Volatile.Read(ref _killed) != 0) return;
        var added = false;
        try
        {
            try { _master.DangerousAddRef(ref added); }
            catch (ObjectDisposedException) { return; }
            if (!SetSize((int)_master.DangerousGetHandle(), cols, rows)) throw Failure("ioctl");
        }
        finally
        {
            if (added) _master.DangerousRelease();
        }
    }

    public bool HasExited
    {
        get { lock (_reapLock) return Reap(); }
    }

    /// <summary>
    /// Ends every process of the shell's session (background jobs, nohup and disowned ones included): SIGHUP + SIGCONT,
    /// up to 500 ms to go, then SIGKILL. Closes the pty and reaps the shell. Takes about 20 ms, about 520 ms when something
    /// ignores the hangup, and never more than a few seconds. Safe to call more than once and from several threads.
    /// </summary>
    public void Kill()
    {
        Volatile.Write(ref _killed, 1); // a waiting Write sees this within one poll
        lock (_killLock)
        {
            if (_killDone) return;
            try
            {
                Signal(SIGHUP);
                Signal(SIGCONT);
                _master.Dispose(); // the real close waits for an in-flight read or write, which ends within one poll

                if (!WaitUntilGone(HangupGrace))
                {
                    Signal(SIGKILL);
                    WaitUntilGone(KillGrace);
                }
            }
            finally
            {
                _killDone = true;
            }
        }
    }

    public void Dispose()
    {
        Kill();
        _master.Dispose();
    }

    /// <summary>The live processes whose session id is <paramref name="sid"/>.</summary>
    internal static List<int> SessionMembers(int sid)
    {
        var members = new List<int>();
        if (sid <= 1) return members;
        var count = proc_listallpids(null, 0);
        if (count <= 0) return members;
        var pids = new int[count + 64];
        var n = proc_listallpids(pids, pids.Length * sizeof(int));
        for (var i = 0; i < n && i < pids.Length; i++)
        {
            if (pids[i] > 0 && getsid(pids[i]) == sid) members.Add(pids[i]);
        }
        return members;
    }

    /// <summary>
    /// Signals the session's members, and the shell while it is not reaped. Under the reap lock, so HasExited cannot reap
    /// the shell (and free its pid) between the check and the signal.
    /// </summary>
    private void Signal(int sig)
    {
        lock (_reapLock)
        {
            foreach (var p in Members()) Send(p, sig);
            if (!Reap()) Send(Pid, sig); // not reaped, so the pid (a zombie at worst) is still ours
        }
    }

    private void Send(int pid, int sig)
    {
        Interlocked.Increment(ref _signalsSent);
        kill(pid, sig);
    }

    private bool WaitUntilGone(TimeSpan grace)
    {
        var deadline = DateTime.UtcNow + grace;
        while (true)
        {
            lock (_reapLock)
            {
                if (Reap() && Members().Count == 0) return true;
            }
            if (DateTime.UtcNow >= deadline) return false;
            Thread.Sleep(10);
        }
    }

    /// <summary>
    /// The members of the shell's own session. Once the shell is reaped and the session is seen empty, or the shell's pid
    /// is in use again, the session id can name someone else's session (launchd's jobs call setsid): from then on, none.
    /// Call under <see cref="_reapLock"/>.
    /// </summary>
    private List<int> Members()
    {
        if (_sessionGone) return [];
        var reaped = Reap();
        if (_sessionGone) return [];
        var members = SessionMembers(Pid);
        if (reaped && (members.Count == 0 || kill(Pid, 0) == 0))
        {
            _sessionGone = true;
            return [];
        }
        return members;
    }

    /// <summary>Collects the shell's exit status once, and notes whether its session ended with it. Call under <see cref="_reapLock"/>.</summary>
    private bool Reap()
    {
        if (_reaped) return true;
        var r = waitpid(Pid, out _, WNOHANG);
        if (r == Pid || (r < 0 && Marshal.GetLastPInvokeError() == ECHILD))
        {
            _reaped = true;
            _sessionGone = SessionMembers(Pid).Count == 0; // while the zombie was unreaped the pid could not be reused
        }
        return _reaped;
    }

    private void ThrowIfKilled()
    {
        if (Volatile.Read(ref _killed) != 0) throw new ObjectDisposedException(nameof(PtyProcess));
    }

    private static int PollOnce(ref PollFd poll)
    {
        var r = PollNative(ref poll, 1, PollMs);
        return r < 0 && Marshal.GetLastPInvokeError() is EINTR or EAGAIN ? 0 : r;
    }

    private static string ShellPath()
    {
        var shell = System.Environment.GetEnvironmentVariable("SHELL");
        return !string.IsNullOrEmpty(shell) && Path.IsPathFullyQualified(shell) && File.Exists(shell) ? shell : "/bin/zsh";
    }

    private static string HomePath()
    {
        var home = System.Environment.GetEnvironmentVariable("HOME");
        if (string.IsNullOrEmpty(home) || !Path.IsPathFullyQualified(home) || !Directory.Exists(home))
            home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        return !string.IsNullOrEmpty(home) && Directory.Exists(home) ? home : "/";
    }

    /// <summary>The agent's environment plus what a terminal needs. Nothing in it comes from a request.</summary>
    private static List<string> ShellEnvironment(string shell, string home)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (DictionaryEntry e in System.Environment.GetEnvironmentVariables())
        {
            if (e.Key is not string key || e.Value is not string value) continue;
            if (key.Length == 0 || key.Contains('=') || key.Contains('\0') || value.Contains('\0')) continue;
            env[key] = value;
        }
        env["TERM"] = "xterm-256color";
        env["COLORTERM"] = "truecolor";
        env["PHONEBOX_TERMINAL"] = "1";
        env["HOME"] = home;
        env["SHELL"] = shell;
        if (!env.TryGetValue("LANG", out var lang) || lang.Length == 0) env["LANG"] = "en_US.UTF-8";
        return env.Select(kv => kv.Key + "=" + kv.Value).ToList();
    }

    /// <summary>A NUL-terminated array of UTF-8 C strings; release with <see cref="Free"/>.</summary>
    private static IntPtr[] Strings(IReadOnlyList<string> values)
    {
        var result = new IntPtr[values.Count + 1];
        try
        {
            for (var i = 0; i < values.Count; i++) result[i] = Marshal.StringToCoTaskMemUTF8(values[i]);
        }
        catch
        {
            Free(result);
            throw;
        }
        return result;
    }

    private static void Free(IntPtr[]? strings)
    {
        if (strings is null) return;
        for (var i = 0; i < strings.Length; i++)
        {
            if (strings[i] != 0) Marshal.FreeCoTaskMem(strings[i]);
            strings[i] = 0;
        }
    }

    private static bool SetSize(int fd, int cols, int rows)
    {
        var size = new WinSize { Row = (ushort)Math.Clamp(rows, 1, ushort.MaxValue), Col = (ushort)Math.Clamp(cols, 1, ushort.MaxValue) };
        var r = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? ioctl_arm64(fd, TIOCSWINSZ, 0, 0, 0, 0, 0, 0, ref size)
            : ioctl_x64(fd, TIOCSWINSZ, ref size);
        return r == 0;
    }

    private static int Fcntl(int fd, int cmd, int arg) =>
        RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? fcntl_arm64(fd, cmd, 0, 0, 0, 0, 0, 0, arg)
            : fcntl_x64(fd, cmd, arg);

    private static void Check(int rc, string what)
    {
        if (rc != 0) throw Failure(what, rc); // posix_spawn* return the error code and leave errno alone
    }

    private static IOException Failure(string what) => Failure(what, Marshal.GetLastPInvokeError());

    private static IOException Failure(string what, int error) => new($"Terminal: {what} failed (errno {error}).");

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd
    {
        public int Fd;
        public short Events;
        public short REvents;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinSize
    {
        public ushort Row, Col, XPixel, YPixel;
    }

    [DllImport(Lib, SetLastError = true)] private static extern int posix_openpt(int flags);
    [DllImport(Lib, SetLastError = true)] private static extern int grantpt(int fd);
    [DllImport(Lib, SetLastError = true)] private static extern int unlockpt(int fd);
    [DllImport(Lib, SetLastError = true)] private static extern int ptsname_r(int fd, byte[] buffer, nuint length);
    // open is variadic only for the mode, which is read only with O_CREAT, so the two-argument form is safe.
    [DllImport(Lib, SetLastError = true)] private static extern int open(string path, int flags);
    [DllImport(Lib, SetLastError = true)] private static extern int close(int fd);
    [DllImport(Lib, SetLastError = true)] private static extern nint read(int fd, ref byte buffer, nuint count);
    [DllImport(Lib, SetLastError = true)] private static extern nint write(int fd, ref byte buffer, nuint count);
    [DllImport(Lib, SetLastError = true, EntryPoint = "poll")] private static extern int PollNative(ref PollFd fds, uint count, int timeoutMs);
    [DllImport(Lib, SetLastError = true)] private static extern int kill(int pid, int sig);
    [DllImport(Lib, SetLastError = true)] private static extern int waitpid(int pid, out int status, int options);
    [DllImport(Lib, SetLastError = true)] private static extern int getsid(int pid);
    [DllImport(Lib)] private static extern int proc_listallpids(int[]? buffer, int bufferBytes);

    // ioctl and fcntl are variadic. On Apple arm64 variadic arguments go on the stack, so a plain declaration would pass
    // the last argument in a register and the callee would read garbage: six dummy register arguments push it to the stack.
    [DllImport(Lib, SetLastError = true, EntryPoint = "ioctl")]
    private static extern int ioctl_arm64(int fd, nuint request, nint x2, nint x3, nint x4, nint x5, nint x6, nint x7, ref WinSize size);
    [DllImport(Lib, SetLastError = true, EntryPoint = "ioctl")]
    private static extern int ioctl_x64(int fd, nuint request, ref WinSize size);
    [DllImport(Lib, SetLastError = true, EntryPoint = "fcntl")]
    private static extern int fcntl_arm64(int fd, int cmd, nint x2, nint x3, nint x4, nint x5, nint x6, nint x7, nint arg);
    [DllImport(Lib, SetLastError = true, EntryPoint = "fcntl")]
    private static extern int fcntl_x64(int fd, int cmd, int arg);

    [DllImport(Lib)] private static extern int posix_spawn_file_actions_init(ref IntPtr actions);
    [DllImport(Lib)] private static extern int posix_spawn_file_actions_destroy(ref IntPtr actions);
    [DllImport(Lib)] private static extern int posix_spawn_file_actions_addopen(ref IntPtr actions, int fd, string path, int flags, ushort mode);
    [DllImport(Lib)] private static extern int posix_spawn_file_actions_adddup2(ref IntPtr actions, int fd, int newFd);
    [DllImport(Lib)] private static extern int posix_spawn_file_actions_addchdir_np(ref IntPtr actions, string path);
    [DllImport(Lib)] private static extern int posix_spawnattr_init(ref IntPtr attr);
    [DllImport(Lib)] private static extern int posix_spawnattr_destroy(ref IntPtr attr);
    [DllImport(Lib)] private static extern int posix_spawnattr_setflags(ref IntPtr attr, short flags);
    [DllImport(Lib)] private static extern int posix_spawnattr_setsigdefault(ref IntPtr attr, ref uint signals);
    [DllImport(Lib)] private static extern int posix_spawnattr_setsigmask(ref IntPtr attr, ref uint signals);
    [DllImport(Lib)] private static extern int posix_spawn(out int pid, string path, ref IntPtr actions, ref IntPtr attr, IntPtr[] argv, IntPtr[] envp);
}
