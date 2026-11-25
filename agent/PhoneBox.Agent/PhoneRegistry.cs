using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace PhoneBox.Agent;

public sealed record PhoneSeen(string Id, string Model, long LastSeen, string Via, string? WebView = null);

/// <summary>
/// The phones that talk to this agent (they send X-PhoneBox-Device / X-PhoneBox-Model), so the settings can give each
/// one its own pages. Remembered while the agent runs.
/// </summary>
public static class PhoneRegistry
{
    private static readonly ConcurrentDictionary<string, PhoneSeen> Seen = new();
    private static readonly ConcurrentDictionary<string, string?> Warned = new();
    private static readonly Lock WarningGate = new();

    /// <summary>A phone id as the app makes it: 1 to 32 ASCII letters and digits. Anything else could carry any text.</summary>
    public static bool IsValidId([NotNullWhen(true)] string? id) => !string.IsNullOrEmpty(id) && id.Length <= 32 && id.All(char.IsAsciiLetterOrDigit);

    /// <summary>The phone id to key state by and to write to the log: the id itself when it is valid, else "" (no id).</summary>
    public static string CleanId(string? id) => IsValidId(id) ? id! : "";

    public static string? Touch(string? id, string? model, string via, string? webView = null)
    {
        if (!IsValidId(id)) return null;
        model = string.IsNullOrWhiteSpace(model) ? "Android phone" : new string(model.Where(c => c >= ' ' && c <= '~').Take(40).ToArray());
        var normalized = WebViewDoctor.Normalize(webView);
        Seen[id] = new PhoneSeen(id, model, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), via, normalized);
        var problem = WebViewDoctor.Diagnose(normalized, model);
        lock (WarningGate)
        {
            var prev = Warned.TryGetValue(id, out var old) ? old : null;
            Warned[id] = problem;
            return problem != prev ? problem : null;
        }
    }

    public static IReadOnlyCollection<PhoneSeen> All() => Seen.Values.ToList();
}
