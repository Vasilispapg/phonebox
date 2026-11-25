namespace PhoneBox.Agent;

/// <summary>One notification the phone's app reports. <paramref name="Category"/> is "call" (an incoming call), "missed_call", or anything else (a message or an app).</summary>
public sealed record PhoneNotification(string Key, string App, string Package, string Title, string Text, string Category, long Time);

public enum NotifyResult { Queued, Duplicate, Off }
