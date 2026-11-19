using PhoneBox.Agent;
using Xunit;

public sealed class RequestTimingsTests
{
    [Fact]
    public void SummaryGivesCountMedianSlowestAndTheSlowShare()
    {
        var t = new RequestTimings();
        foreach (var ms in new[] { 10, 20, 30, 40, 50, 60, 70, 80, 90, 1000 }) t.Add("POST /api/volume", ms);
        t.Add("GET /api/state", 5);

        var rows = t.Summary();
        var volume = rows.Single(r => r.Route == "POST /api/volume");
        Assert.Equal(10, volume.Count);
        Assert.Equal(55, volume.P50Ms);
        Assert.Equal(1000, volume.MaxMs);
        Assert.Equal(1, volume.Slow); // over 300 ms
        Assert.Equal(1, rows.Single(r => r.Route == "GET /api/state").Count);
    }

    [Fact]
    public void OnlyTheLatestRequestsOfARouteAreKept()
    {
        var t = new RequestTimings(keep: 5);
        for (var i = 1; i <= 20; i++) t.Add("POST /api/volume", i * 100);
        var row = t.Summary().Single();
        Assert.Equal(20, row.Count); // every request is counted
        Assert.Equal(2000, row.MaxMs);
        Assert.Equal(1800, row.P50Ms); // the median of the last five: 1600..2000
    }

    [Theory]
    [InlineData("POST", "/api/media/playpause", "POST /api/media/playpause")]
    [InlineData("GET", "/api/state", "GET /api/state")]
    [InlineData("POST", "/api/approvals/abc123", "POST /api/approvals")]
    [InlineData("GET", "/api/widget/page1/w3", "GET /api/widget")]
    public void RoutesAreGroupedWithoutIds(string method, string path, string expected) =>
        Assert.Equal(expected, RequestTimings.Route(method, path));
}
