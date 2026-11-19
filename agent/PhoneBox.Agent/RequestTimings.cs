namespace PhoneBox.Agent;

/// <summary>
/// How long the agent takes to answer each kind of request, so "it feels slow" can be told from "the computer is busy".
/// Counts every request but keeps only the latest few durations per route, so it never grows.
/// </summary>
public sealed class RequestTimings(int keep = 200)
{
    public const long SlowMs = 300;
    private readonly object _gate = new();
    private readonly Dictionary<string, Series> _routes = [];

    private sealed class Series(int keep)
    {
        public long Count, Slow, Max;
        public readonly long[] Recent = new long[keep];
        public int Next, Filled;
    }

    public sealed record Row(string Route, long Count, long P50Ms, long P95Ms, long MaxMs, long Slow);

    public void Add(string route, long ms)
    {
        lock (_gate)
        {
            if (!_routes.TryGetValue(route, out var s)) _routes[route] = s = new Series(keep);
            s.Count++;
            if (ms >= SlowMs) s.Slow++;
            if (ms > s.Max) s.Max = ms;
            s.Recent[s.Next] = ms;
            s.Next = (s.Next + 1) % keep;
            if (s.Filled < keep) s.Filled++;
        }
    }

    public IReadOnlyList<Row> Summary()
    {
        lock (_gate)
            return _routes.Select(kv =>
            {
                var s = kv.Value;
                var sorted = s.Recent.Take(s.Filled).Order().ToArray();
                long At(double q) => sorted.Length == 0 ? 0 : sorted[(int)Math.Min(sorted.Length - 1, Math.Floor(q * (sorted.Length - 1) + 0.5))];
                // the median of an even count is the mean of the middle two
                var p50 = sorted.Length % 2 == 0 && sorted.Length > 0 ? (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2 : At(0.5);
                return new Row(kv.Key, s.Count, p50, At(0.95), s.Max, s.Slow);
            }).OrderByDescending(r => r.Count).ToList();
    }

    /// <summary>"POST /api/media/playpause", with an id at the end folded away ("/api/approvals/abc" → "/api/approvals").</summary>
    public static string Route(string method, string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var keep = parts.Length >= 2 && parts[1] is "media" or "volume" or "state" ? Math.Min(parts.Length, 3) : Math.Min(parts.Length, 2);
        return method + " /" + string.Join('/', parts.Take(keep));
    }
}
