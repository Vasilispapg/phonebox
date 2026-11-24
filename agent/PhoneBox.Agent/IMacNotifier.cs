namespace PhoneBox.Agent;

/// <summary>A notifier that has something to prepare (build, ask for permission), so the first real notification already looks right.</summary>
internal interface IWarmable { void Warm(); }

/// <summary>Shows a notification on this computer.</summary>
internal interface IMacNotifier
{
    /// <summary>True when the system accepted it. <paramref name="iconPng"/> is the phone app's icon, for a notifier that can show one; a later post with the same <paramref name="replaceId"/> replaces the earlier one (a call whose caller was named after it first rang). Never throws.</summary>
    Task<bool> PostAsync(string title, string subtitle, string body, byte[]? iconPng, string? replaceId, CancellationToken ct);
}
