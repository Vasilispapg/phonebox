namespace PhoneBox.Agent;

/// <summary>
/// The automatic phone to Mac clipboard needs a helper on the phone, which can read the clipboard in the background only because
/// it is started over adb (it then runs as the shell user, which Android treats as a system caller). This starts it, once for
/// each phone and again whenever it is not running: every 30 seconds a phone is asked whether the helper's named socket exists,
/// and only if not is the helper started (a second helper would exit at once anyway). The helper is a class in the phone app's
/// own APK, so nothing is pushed to the phone. Stopping is the app's job: its answer to the helper's broadcasts tells the helper to leave when a switch goes off.
/// </summary>
internal sealed class ClipHelperLauncher(Func<IReadOnlyList<string>, CancellationToken, Task<string>> adb, Func<long> clock, ILogger log)
{
    public const string Package = "net.oxog.phonebox.deskpanel";
    public const string HelperClass = Package + ".ClipHelper";
    private const long CheckEveryMs = 30_000;

    /// <summary>Counts the helper's named socket in the phone's socket table; "; true" because grep exits 1 when there is none.</summary>
    public const string CheckCommand = "grep -c net.oxog.phonebox.cliphelper /proc/net/unix; true";

    /// <summary>
    /// Runs on the phone's shell: the class path is the app's own APK (wherever Android put it), and the helper is detached so adb
    /// returns at once and the helper outlives it. It hands copies to the app with a broadcast, so it needs nothing else.
    /// </summary>
    public static readonly string StartCommand =
        $"CLASSPATH=$(pm path {Package} | head -1 | cut -d: -f2) setsid nohup app_process /system/bin {HelperClass} >/dev/null 2>&1 &";

    private const long SlowCheckEveryMs = 600_000;
    private const int FailuresBeforeSlow = 3;

    private readonly Dictionary<string, long> _checkedAt = [];
    private readonly Dictionary<string, int> _failures = [];     // starts that were not followed by a running helper at the next look
    private readonly HashSet<string> _startedAtLastLook = [];

    private void Forget(string device) { _checkedAt.Remove(device); _failures.Remove(device); _startedAtLastLook.Remove(device); }

    /// <param name="wanted">Only on macOS while Settings › Phone bridge › clipboard is on, and while a phone has its bridge stream open
    /// (see <see cref="BridgeStream.Listeners"/>): the phone's own switch is what opens it, and the agent cannot see that switch.</param>
    /// <remarks>
    /// A phone reached over USB and Wi-Fi at once has two serials; the second is looked at before the first start has bound its
    /// socket, so a second helper is started. It exits at once because the name is taken, which is harmless.
    /// </remarks>
    public Task EnsureAsync(IReadOnlyCollection<string> devices, bool wanted, CancellationToken ct) => EnsureAsync(devices, () => wanted, ct);

    /// <param name="wanted">Asked again right before each start: a look at a phone can take up to adb's timeout, and the switch may
    /// go off (or the last phone close its stream) meanwhile; a helper must not be started after that.</param>
    public async Task EnsureAsync(IReadOnlyCollection<string> devices, Func<bool> wanted, CancellationToken ct)
    {
        if (!wanted()) { _checkedAt.Clear(); _failures.Clear(); _startedAtLastLook.Clear(); return; } // when it is wanted again, every phone is looked at at once
        foreach (var gone in _checkedAt.Keys.Except(devices).ToList()) Forget(gone); // a phone that returns is looked at at once
        foreach (var device in devices)
        {
            var now = clock();
            var failures = _failures.GetValueOrDefault(device);
            var every = failures >= FailuresBeforeSlow ? SlowCheckEveryMs : CheckEveryMs;
            if (_checkedAt.TryGetValue(device, out var at) && now - at < every) continue;
            _checkedAt[device] = now;
            try
            {
                var last = (await adb(["-s", device, "shell", CheckCommand], ct)).Trim().Split('\n')[^1].Trim();
                if (int.TryParse(last, out var count) && count > 0)
                {
                    _failures.Remove(device);
                    _startedAtLastLook.Remove(device);
                    continue;
                }
                // no longer wanted since this round began: nothing is started, and the next round looks at this phone at once
                if (!wanted()) { _checkedAt.Remove(device); return; }
                // started at the last look and gone again: it does not stay up (an app too old to have the class)
                if (_startedAtLastLook.Contains(device))
                {
                    _failures[device] = ++failures;
                    if (failures == FailuresBeforeSlow)
                        log.LogWarning("{Device}: the clipboard helper does not stay running on the phone; trying again only every 10 minutes", device);
                }
                await adb(["-s", device, "shell", StartCommand], ct);
                _startedAtLastLook.Add(device);
                if (failures == 0 && !_failures.ContainsKey(device)) log.LogInformation("{Device}: the automatic clipboard helper was started on the phone", device);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { log.LogWarning("{Device}: the clipboard helper could not be started: {Error}", device, ex.GetType().Name); }
        }
    }
}
