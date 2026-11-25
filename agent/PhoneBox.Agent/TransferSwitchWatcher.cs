namespace PhoneBox.Agent;

/// <summary>Puts the Finder Quick Action "Send to phone" in place, or takes it away.</summary>
internal interface IQuickActionInstaller
{
    void Install();
    void Remove();
}

/// <summary>Used where there is no Quick Action to manage.</summary>
internal sealed class NoQuickAction : IQuickActionInstaller
{
    public static readonly NoQuickAction Instance = new();
    public void Install() { }
    public void Remove() { }
}

/// <summary>The ways to send from the Mac (the Finder Quick Action and the droplet app) are put in place and taken away together; one that fails does not keep the other from being tried.</summary>
internal sealed class CombinedQuickAction(params IQuickActionInstaller[] parts) : IQuickActionInstaller
{
    public void Install() => Each(p => p.Install());
    public void Remove() => Each(p => p.Remove());

    private void Each(Action<IQuickActionInstaller> step)
    {
        Exception? first = null;
        foreach (var part in parts)
        {
            try { step(part); }
            catch (Exception e) { first ??= e; }
        }
        if (first is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(first).Throw();
    }
}

/// <summary>
/// Keeps everything that exists only for "Send files to the phone" in step with its switch: while on, the local key file and the
/// Quick Action are in place and old files in the outbox are swept; while off, the key, the Quick Action and the outbox are gone
/// (also what an earlier run left behind). A step that fails is logged and tried again on the next tick.
/// </summary>
internal sealed class TransferSwitchWatcher(ConfigStore cfg, Outbox outbox, LocalTransferKey key, IQuickActionInstaller quickAction,
    TimeSpan period, ILogger<TransferSwitchWatcher> log) : BackgroundService
{
    private bool? _applied; // the switch as it was last put into effect; null until the first tick

    public void Tick()
    {
        try
        {
            var on = cfg.Get().Bridge.FilesToPhone;
            if (on)
            {
                key.Ensure();
                if (_applied != true) quickAction.Install();
                outbox.Sweep();
            }
            else if (_applied != false)
            {
                key.Delete();
                quickAction.Remove();
                outbox.Clear();
            }
            _applied = on;
        }
        catch (Exception e)
        {
            log.LogWarning("Sending files to the phone: a step failed ({Error}); trying again", e.GetType().Name);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Tick(); // at start: an old key file and outbox go when the switch is off
        using var timer = new PeriodicTimer(period);
        try { while (await timer.WaitForNextTickAsync(stoppingToken)) Tick(); }
        catch (OperationCanceledException) { }
    }
}
