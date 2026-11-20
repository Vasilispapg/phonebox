using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PhoneBox.Agent;

/// <summary>
/// Runs the shortcuts defined in config.json. The phone only sends a shortcut id, never a command.
/// A shortcut starts a program, presses keys, types text or runs a macro; toggles remember or read their on / off state.
/// </summary>
public static class Shortcuts
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> Remembered = new();

    /// <returns>An error message, or null on success.</returns>
    public static string? Run(AgentConfig cfg, string id, ILogger log, PushStore? push = null, AudioControl? audio = null, bool hold = false)
    {
        var sc = cfg.Shortcuts.FirstOrDefault(s => s.Id == id);
        if (sc is null) return $"no such shortcut: {id}";
        // a long press runs the key's own LongPress target (one hop: that key's own long press is not followed)
        if (hold)
        {
            if (sc.LongPress is null || cfg.Shortcuts.FirstOrDefault(s => s.Id == sc.LongPress) is not { } held) return $"shortcut {id} has no long press";
            sc = held;
        }
        return RunCore(cfg, sc, log, push, audio, 0);
    }

    private static string? RunCore(AgentConfig? cfg, Shortcut sc, ILogger log, PushStore? push, AudioControl? audio, int depth)
    {
        if (!sc.Toggle) return Execute(sc, log, cfg, depth, push, audio);
        var on = State(sc, push, audio) ?? false;
        var target = on && sc.OffCmd is not null ? new Shortcut { Label = sc.Label, Cmd = sc.OffCmd, Args = sc.OffArgs, Cwd = sc.Cwd } : sc;
        var err = Execute(target, log, cfg, depth, push, audio);
        if (err is null && sc.StateKey is null && sc.Cmd != "builtin:mute") Remembered[sc.Id] = !on;
        return err;
    }

    /// <summary>On / off state of the toggle keys, for the phone.</summary>
    public static Dictionary<string, bool>? States(AgentConfig cfg, PushStore push, AudioControl audio)
    {
        Dictionary<string, bool>? map = null;
        foreach (var sc in cfg.Shortcuts.Where(s => s.Toggle || s.Cmd == "builtin:mute"))
            if (State(sc, push, audio) is { } on) (map ??= [])[sc.Id] = on;
        return map;
    }

    public static bool? State(Shortcut sc, PushStore? push, AudioControl? audio)
    {
        if (sc.Cmd == "builtin:mute" && sc.StateKey is null) return audio?.Snapshot()?.Muted;
        if (sc.StateKey is not null)
        {
            if (push?.Snapshot().TryGetValue(sc.StateKey, out var item) != true) return false;
            var d = item!.Data;
            if (d.ValueKind == System.Text.Json.JsonValueKind.Object && d.TryGetProperty("value", out var v)) d = v;
            return d.ValueKind switch
            {
                System.Text.Json.JsonValueKind.True => true,
                System.Text.Json.JsonValueKind.Number => d.GetDouble() != 0,
                System.Text.Json.JsonValueKind.String => d.GetString() is { Length: > 0 } t && t.ToLowerInvariant() is not ("0" or "false" or "off" or "no"),
                _ => false,
            };
        }
        return Remembered.TryGetValue(sc.Id, out var r) && r;
    }

    // builtin:cycle: the position (in the key's list) of the key that ran last; lost when the agent restarts, like a toggle's state
    // the list is stored with the position: after the list is edited the old position would point at another key
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (int At, string List)> CycleAt = new();
    private static string ListOf(Shortcut sc) => string.Join('\n', sc.Cycle ?? []);
    private static readonly object CycleLock = new();

    /// <summary>Where each cycle key stands, for the phone: the position of the key that ran last. Keys never tapped are left out.</summary>
    public static Dictionary<string, int>? CycleStates(AgentConfig cfg)
    {
        Dictionary<string, int>? map = null;
        foreach (var sc in cfg.Shortcuts.Where(s => s.Cmd == "builtin:cycle"))
            if (CycleAt.TryGetValue(sc.Id, out var last) && last.List == ListOf(sc) && last.At < (sc.Cycle?.Count ?? 0)) (map ??= [])[sc.Id] = last.At;
        return map;
    }

    /// <summary>A tap on a cycle key: run the next key of its list (the first one after the last), and remember which ran.</summary>
    private static string? RunCycle(AgentConfig? cfg, Shortcut sc, ILogger log, PushStore? push, AudioControl? audio, int depth)
    {
        if (depth > 3) return "keys call each other too deeply";
        var ids = sc.Cycle ?? [];
        if (cfg is null || ids.Count == 0) return "this key has no keys to cycle through";
        int next;
        lock (CycleLock) next = (CycleAt.TryGetValue(sc.Id, out var last) && last.List == ListOf(sc) ? last.At + 1 : 0) % ids.Count;
        var target = cfg.Shortcuts.FirstOrDefault(s => s.Id == ids[next]);
        if (target is null) return $"no such shortcut: {ids[next]}";
        var err = RunCore(cfg, target, log, push, audio, depth + 1);
        // only a key that ran counts: after a failure the next tap tries the same one again
        if (err is null) CycleAt[sc.Id] = (next, ListOf(sc));
        return err;
    }

    /// <returns>An error message, or null on success.</returns>
    public static string? Execute(Shortcut sc, ILogger log, AgentConfig? cfg = null, int depth = 0, PushStore? push = null, AudioControl? audio = null)
    {
        try
        {
            switch (sc.Cmd)
            {
                case "builtin:cycle":
                    return RunCycle(cfg, sc, log, push, audio, depth);
                case "builtin:keys":
                    KeySender.SendKeys(sc.Args ?? "");
                    break;
                case "builtin:text":
                    KeySender.TypeText(sc.Args ?? "");
                    break;
                case "builtin:macro":
                    if (depth > 3) throw new InvalidOperationException("macros call each other too deeply");
                    foreach (var step in sc.Steps ?? [])
                    {
                        var i = step.IndexOf(':');
                        var (kind, value) = i > 0 ? (step[..i].Trim().ToLowerInvariant(), step[(i + 1)..].Trim()) : ("keys", step.Trim());
                        switch (kind)
                        {
                            case "keys": KeySender.SendKeys(value); break;
                            case "text": KeySender.TypeText(value); break;
                            case "wait": Thread.Sleep(Math.Clamp(int.TryParse(value, out var ms) ? ms : 0, 0, 10_000)); break;
                            case "shortcut":
                                var other = cfg?.Shortcuts.FirstOrDefault(s => s.Id == value) ?? throw new InvalidOperationException($"no such shortcut: {value}");
                                if (RunCore(cfg, other, log, push, audio, depth + 1) is { } err) throw new InvalidOperationException(err);
                                break;
                            default: throw new InvalidOperationException($"unknown macro step \"{step}\" (use keys:, text:, wait: or shortcut:)");
                        }
                        Thread.Sleep(40);
                    }
                    break;
                case "builtin:wake":
                    // "packets sent", never "the machine is up"
                    if (WakeOnLan.Wake(sc.Args) is { } wakeError) throw new InvalidOperationException(wakeError);
                    break;
#if WINDOWS
                case "builtin:lock":
                    LockWorkStation();
                    break;
                case "builtin:monitor-off":
                    // HWND_BROADCAST, WM_SYSCOMMAND, SC_MONITORPOWER, 2 = off
                    PostMessage(0xFFFF, 0x0112, 0xF170, 2);
                    break;
                case "builtin:mute":
                    Keys.Tap(Keys.VK_VOLUME_MUTE);
                    break;
#else
                case "builtin:lock":
                    if (OperatingSystem.IsMacOS()) Unix("pmset", "displaysleepnow");
                    else if (Sh.Run("loginctl", "lock-session") is null) Unix("xdg-screensaver", "lock");
                    break;
                case "builtin:monitor-off":
                    if (OperatingSystem.IsMacOS()) Unix("pmset", "displaysleepnow");
                    else Unix("xset", "dpms", "force", "off");
                    break;
                case "builtin:mute":
                    // false: the output cannot be muted (macOS) or wpctl / pactl failed; a caller must not count it as done
                    if (!(audio ?? new AudioControl()).ToggleMute()) throw new InvalidOperationException("could not toggle mute on the current output");
                    break;
#endif
                default:
                    Start(sc);
                    break;
            }
            log.LogInformation("shortcut: {Label}", sc.Label);
            return null;
        }
        catch (Exception ex)
        {
            log.LogWarning("shortcut {Label} failed: {Msg}", sc.Label, ex.Message);
            return ex.Message;
        }
    }

    private static void Start(Shortcut sc)
    {
        var psi = new ProcessStartInfo(Home(sc.Cmd), Home(sc.Args ?? "")) { UseShellExecute = true };
        if (!string.IsNullOrEmpty(sc.Cwd)) psi.WorkingDirectory = Home(sc.Cwd);
#if WINDOWS
        try
        {
            Process.Start(psi);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 2 /* not found */ && TerminalStandIns(sc.Cmd)
            .Select(t => Path.Combine(Environment.SystemDirectory, t == "powershell.exe" ? @"WindowsPowerShell\v1.0\powershell.exe" : t))
            .FirstOrDefault(File.Exists) is { } alt)
        {
            StartStandIn(alt, sc);
        }
#else
        // a missing program does not throw here: .NET hands the name to xdg-open, which fails on its own
        if (TerminalStandIns(sc.Cmd, Environment.GetEnvironmentVariable("TERMINAL")) is { Length: > 0 } alts && Sh.Which(sc.Cmd) is null
            && alts.Select(Sh.Which).FirstOrDefault(p => p is not null) is { } alt)
        {
            StartStandIn(alt, sc);
            return;
        }
        Process.Start(psi);
#endif
    }

    // A stand-in is a fixed program at a full path on disk: start it directly, without the shell and without arguments.
    // (The agent has no console on Windows, so a console program started this way gets its own window.)
    private static void StartStandIn(string path, Shortcut sc)
    {
        var start = new ProcessStartInfo(path) { UseShellExecute = false };
        if (!string.IsNullOrEmpty(sc.Cwd)) start.WorkingDirectory = Home(sc.Cwd);
        Process.Start(start)?.Dispose();
    }

    /// <summary>
    /// What to start instead when a default shortcut's program is missing: Windows Terminal (wt.exe) is not part of
    /// Windows 10, x-terminal-emulator only exists on Debian-family Linux (#73), and the default Calculator, Settings and
    /// System Monitor shortcuts name GNOME's apps, which a KDE, Xfce or MATE desktop usually doesn't have (#238). Without
    /// this the missing name goes to xdg-open, which opens it as a web address. Empty for other commands.
    /// </summary>
    internal static string[] TerminalStandIns(string cmd, string? terminalEnv = null) => cmd.Trim().ToLowerInvariant() switch
    {
        "wt.exe" or "wt" => ["powershell.exe", "cmd.exe"],
        "x-terminal-emulator" =>
        [
            .. string.IsNullOrWhiteSpace(terminalEnv) ? [] : new[] { terminalEnv.Trim() },
            "kgx", "gnome-terminal", "konsole", "xfce4-terminal", "mate-terminal", "lxterminal", "tilix", "alacritty", "kitty", "foot", "wezterm", "xterm",
        ],
        "gnome-calculator" => ["kcalc", "mate-calc", "galculator", "qalculate-gtk", "xfce4-calculator"],
        "gnome-control-center" => ["systemsettings", "systemsettings5", "xfce4-settings-manager", "mate-control-center", "cinnamon-settings", "budgie-control-center", "lxqt-config"],
        "gnome-system-monitor" => ["plasma-systemmonitor", "ksysguard", "xfce4-taskmanager", "mate-system-monitor", "lxtask"],
        _ => [],
    };

    /// <summary>"~" at the start of a path means the home folder (Linux / macOS style).</summary>
    private static string Home(string s) =>
        s == "~" || s.StartsWith("~/") ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + s[1..] : s.Replace(" ~/", " " + Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "/");

#if WINDOWS
    [DllImport("user32.dll")] private static extern bool LockWorkStation();
    [DllImport("user32.dll")] private static extern bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);
#else
    private static void Unix(string tool, params string[] args)
    {
        if (Sh.Run(tool, args) is null && Sh.Which(tool) is null) throw new InvalidOperationException($"{tool} is not installed");
    }
#endif
}

#if WINDOWS
public static class Keys
{
    public const byte VK_VOLUME_MUTE = 0xAD;
    public const byte VK_MEDIA_PLAY_PAUSE = 0xB3;

    public static void Tap(byte vk)
    {
        keybd_event(vk, 0, 0, 0);
        keybd_event(vk, 0, 2 /* KEYEVENTF_KEYUP */, 0);
    }

    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, nuint extra);
}
#endif
