namespace PhoneBox.Agent;

/// <summary>Shows a notification on this computer.</summary>
internal interface IMacNotifier
{
    /// <summary>True when the system accepted it. Never throws.</summary>
    Task<bool> PostAsync(string title, string subtitle, string body, CancellationToken ct);
}
