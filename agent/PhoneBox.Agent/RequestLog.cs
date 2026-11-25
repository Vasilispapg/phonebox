namespace PhoneBox.Agent;

/// <summary>
/// Typed terminal text travels in the query string (the phone's transport sends no request bodies), and so may other
/// secrets. ASP.NET Core's request lines ("Request starting … /api/terminal/in?d=…"), its HTTP logging, Kestrel's
/// bad-request details and the minimal-API binding failures print query values, so those categories are silenced for
/// every provider, whatever level the configuration or the command line asks for.
/// </summary>
internal static class RequestLog
{
    internal static readonly string[] QueryCategories =
    [
        "Microsoft.AspNetCore.Hosting.Diagnostics",
        "Microsoft.AspNetCore.HttpLogging",
        "Microsoft.AspNetCore.Server.Kestrel.BadRequests",
        "Microsoft.AspNetCore.Http.RequestDelegateFactory",
    ];

    public static void KeepQueriesOut(ILoggingBuilder logging) =>
        // after every other rule (the configuration's included, and again on a reload): the most specific category, once
        // for every provider a rule names and once for the rest, so no other rule can turn these back on
        logging.Services.PostConfigure<LoggerFilterOptions>(o =>
        {
            var providers = o.Rules.Select(r => r.ProviderName).Where(p => p is not null).Distinct().Append(null).ToList();
            foreach (var provider in providers)
                foreach (var category in QueryCategories)
                    o.Rules.Add(new LoggerFilterRule(provider, category, LogLevel.None, null));
        });
}
