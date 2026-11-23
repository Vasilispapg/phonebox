namespace PhoneBox.Agent;

/// <summary>
/// The automatic phone to Mac clipboard needs a helper on the phone, which can read the clipboard in the background only because
/// it is started over adb (it then runs as the shell user, which Android treats as a system caller). This starts it, once for
/// each phone and again whenever it is not running: every 30 seconds a phone is asked whether the helper's named socket exists,
/// and only if not is the helper started (a second helper would exit at once anyway). The helper is a class in the phone app's
/// own APK, so nothing is pushed to the phone. Stopping is the app's job: it tells the helper when a switch goes off.
/// </summary>
internal sealed class ClipHelperLauncher(Func<IReadOnlyList<string>, CancellationToken, Task<string>> adb, Func<long> clock, ILogger log)
{
    public const string Package = "net.oxog.phonebox.deskpanel";
    public const string HelperClass = Package + ".ClipHelper";
    private const long CheckEveryMs = 30_000;

    /// <summary>Counts the helper's named socket in the phone's socket table; "; true" because grep exits 1 when there is none.</summary>
    public const string CheckCommand = "grep -c net.oxog.phonebox.cliphelper /proc/net/unix; true";

    /// <summary>
    /// Runs on the phone's shell: the class path is the app's own APK (wherever Android put it), the argument is the app's uid
    /// (only that uid may stop the helper), and the helper is detached so adb returns at once and the helper outlives it.
    /// </summary>
    public static readonly string StartCommand =
        $"CLASSPATH=$(pm path {Package} | head -1 | cut -d: -f2) setsid nohup app_process /system/bin {HelperClass} " +
        $"$(pm list packages -U {Package} | head -1 | sed 's/.*uid://') >/dev/null 2>&1 &";

    private readonly Dictionary<string, long> _checkedAt = [];

    /// <param name="wanted">Only on macOS while Settings › Phone bridge › clipboard is on.</param>
    public async Task EnsureAsync(IReadOnlyCollection<string> devices, bool wanted, CancellationToken ct)
    {
        if (!wanted) { _checkedAt.Clear(); return; } // when it is wanted again, every phone is looked at at once
        foreach (var gone in _checkedAt.Keys.Except(devices).ToList()) _checkedAt.Remove(gone); // a phone that returns is looked at at once
        foreach (var device in devices)
        {
            var now = clock();
            if (_checkedAt.TryGetValue(device, out var at) && now - at < CheckEveryMs) continue;
            _checkedAt[device] = now;
            try
            {
                var running = (await adb(["-s", device, "shell", CheckCommand], ct)).Trim() is { Length: > 0 } count && count != "0";
                if (running) continue;
                await adb(["-s", device, "shell", StartCommand], ct);
                log.LogInformation("{Device}: the automatic clipboard helper was started on the phone", device);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { log.LogWarning("{Device}: the clipboard helper could not be started: {Error}", device, ex.GetType().Name); }
        }
    }
}
