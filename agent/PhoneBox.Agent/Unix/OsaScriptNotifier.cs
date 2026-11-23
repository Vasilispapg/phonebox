namespace PhoneBox.Agent;

/// <summary>
/// A standard macOS notification through osascript: no extra program to install or sign. It carries Script Editor's icon
/// and has no buttons. The title, subtitle and body are phone text, so they travel as arguments after a "--" and are never
/// part of the script: without the "--", osascript reads an argument that starts with "-" as one of its own options,
/// and a text of "-e" followed by a title of <c>do shell script …</c> would run it.
/// </summary>
internal sealed class OsaScriptNotifier : IMacNotifier
{
    private const int TimeoutMs = 5000; // osascript can take seconds on a busy Mac; a stuck one is killed
    private static readonly string[] NotificationScript =
    [
        "on run argv",
        "display notification (item 1 of argv) with title (item 2 of argv) subtitle (item 3 of argv)",
        "end run",
    ];

    private readonly Func<IReadOnlyList<string>, CancellationToken, Task<bool>> _run;

    /// <param name="run">Runs osascript with these arguments (tests pass a fake); the default starts the real one.</param>
    public OsaScriptNotifier(Func<IReadOnlyList<string>, CancellationToken, Task<bool>>? run = null) =>
        _run = run ?? ((args, ct) => Task.Run(() => Sh.RunWithTimeout(TimeoutMs, "osascript", [.. args]) is not null, ct));

    public static IReadOnlyList<string> BuildArguments(string title, string subtitle, string body) =>
        ScriptArguments(NotificationScript, [body, title, subtitle]);

    /// <summary>"-e line" for each script line, then "--", then the values: after that everything is data.</summary>
    internal static IReadOnlyList<string> ScriptArguments(IEnumerable<string> script, IEnumerable<string> values)
    {
        var args = new List<string>();
        foreach (var line in script) { args.Add("-e"); args.Add(line); }
        args.Add("--");
        args.AddRange(values);
        return args;
    }

    public async Task<bool> PostAsync(string title, string subtitle, string body, CancellationToken ct)
    {
        try { return await _run(BuildArguments(title, subtitle, body), ct); }
        catch { return false; }
    }
}
