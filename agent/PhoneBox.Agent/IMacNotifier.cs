namespace PhoneBox.Agent;

/// <summary>A notifier that has something to prepare (build, ask for permission), so the first real notification already looks right.</summary>
internal interface IWarmable { void Warm(); }

/// <summary>Shows a notification on this computer.</summary>
internal interface IMacNotifier
{
    /// <summary>True when the system accepted it. <paramref name="iconPng"/> is the phone app's icon, for a notifier that can show one; a later post with the same <paramref name="replaceId"/> replaces the earlier one (a call whose caller was named after it first rang). Never throws.</summary>
    Task<bool> PostAsync(string title, string subtitle, string body, byte[]? iconPng, string? replaceId, CancellationToken ct);
}

/// <summary>
/// A notifier that can make a click on the notification show a file in Finder. The path comes from the agent, never from the phone,
/// and the click only reveals the file (selects it in a Finder window): nothing is opened or run. A notifier without it (osascript)
/// still shows the notification.
/// </summary>
internal interface IRevealingNotifier
{
    Task<bool> PostAsync(string title, string subtitle, string body, byte[]? iconPng, string? replaceId, string? revealPath, CancellationToken ct);
}
