using Azure.Core;
using Azure.Monitor.Query;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PoPunkouterSoftware.Shared;
using System.Text.RegularExpressions;

namespace PoPunkouterSoftware.Infrastructure;

/// <summary>
/// Reads the cross-estate sign-in roster out of Application Insights.
/// </summary>
/// <remarks>
/// <para>This is the one read path in the app that queries Azure live on a page load, and it
/// is a deliberate exception to the "everything is a projection of the stored report" rule in
/// CLAUDE.md. The inventory scan is a ~30-second ARM walk whose answer changes slowly, so
/// storing it and projecting it is right; a sign-in roster is a single cheap Log Analytics
/// query whose whole value is being current, and folding it into the nightly scan would mean
/// a visitor who signed in an hour ago does not appear until tomorrow.</para>
/// <para>It reads the same records as the "Po Sign-ins" Azure Monitor workbook in the
/// PoShared resource group — the <c>traces</c> table, not <c>customEvents</c>, because the Po
/// apps export through OpenTelemetry where custom events are not part of the pipeline.</para>
/// <para><see cref="Azure.Monitor.Query"/> is already referenced for the metrics step of the
/// scan, and the managed identity already holds Monitoring Reader at subscription scope from
/// the 2026-09-05 grant, which covers workspace queries. So this needs no new package and no
/// new role assignment.</para>
/// <para>Nothing here throws at the caller. A missing configuration key, an absent credential
/// (the normal state of a local run without <c>az login</c>), a missing role and a slow query
/// all produce <see cref="SignInReportBuilder.Unavailable"/>, because degradation is a
/// designed path in this app and a 500 tells the page nothing it can render.</para>
/// </remarks>
public sealed partial class SignInQueryService
{
    /// <summary>
    /// Retention on the shared Application Insights component. Asking for more silently
    /// returns fewer days, and a page promising "last 180 days" over 90 days of data is a lie
    /// the UI cannot detect on its own.
    /// </summary>
    public const int MaxWindowDays = 90;

    public const int DefaultWindowDaysFallback = 30;

    /// <summary>
    /// Row cap. Volume is a handful per week today; this stops a future spike turning one
    /// page load into a multi-megabyte response, and <see cref="SignInReport.Truncated"/>
    /// tells the page when the figures became a floor rather than a total.
    /// </summary>
    private const int MaxRows = 5000;

    private const string DefaultEventName = "UserSignedIn";

    /// <summary>
    /// The query is generous but the page is interactive, so it fails visibly rather than
    /// hanging. Roughly double the slowest observed Log Analytics response.
    /// </summary>
    private static readonly TimeSpan QueryBudget = TimeSpan.FromSeconds(20);

    private readonly TokenCredential _credential;
    private readonly IConfiguration _config;
    private readonly TimeProvider _clock;
    private readonly ILogger<SignInQueryService> _logger;

    /// <summary>
    /// Built once and reused: the client walks the credential chain on construction, and this
    /// service is a singleton precisely so a page load does not repeat that walk.
    /// </summary>
    private LogsQueryClient? _client;

    public SignInQueryService(
        TokenCredential credential, IConfiguration config, TimeProvider clock, ILogger<SignInQueryService> logger)
    {
        _credential = credential;
        _config = config;
        _clock = clock;
        _logger = logger;
    }

    public int DefaultWindowDays =>
        ClampWindow(_config.GetValue("SignIns:WindowDays", DefaultWindowDaysFallback));

    public static int ClampWindow(int days) =>
        days <= 0 ? DefaultWindowDaysFallback : Math.Min(days, MaxWindowDays);

    public async Task<SignInReport> GetAsync(int windowDays, CancellationToken ct)
    {
        windowDays = ClampWindow(windowDays);
        var now = _clock.GetUtcNow();

        var resourceId = _config["SignIns:ResourceId"];
        if (string.IsNullOrWhiteSpace(resourceId))
        {
            return SignInReportBuilder.Unavailable(now, windowDays,
                "Sign-in telemetry is not configured on this deployment (SignIns:ResourceId is unset).");
        }

        // The service owns its own deadline so every timeout degrades identically, wherever
        // it is called from. Only the CALLER cancelling is a real cancellation — a visitor who
        // navigated away wants no response at all, not an "unavailable" one.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(QueryBudget);

        try
        {
            var client = _client ??= new LogsQueryClient(_credential);

            var response = await client.QueryResourceAsync(
                new ResourceIdentifier(resourceId),
                BuildQuery(EventName),
                new QueryTimeRange(TimeSpan.FromDays(windowDays)),
                cancellationToken: budget.Token);

            var records = ReadRows(response.Value.Table);
            _logger.LogInformation(
                "Sign-in query returned {Rows} rows over {Days} days", records.Count, windowDays);

            return SignInReportBuilder.Build(records, now, windowDays, truncated: records.Count >= MaxRows);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Sign-in query exceeded its {Seconds}s budget", QueryBudget.TotalSeconds);
            return SignInReportBuilder.Unavailable(now, windowDays,
                $"Application Insights did not answer within {QueryBudget.TotalSeconds:N0} seconds.");
        }
        catch (Exception ex)
        {
            // Expected without a credential (a local run with no `az login`) and without the
            // Monitoring Reader role, both of which must read as "no data yet", not as a fault
            // in the page.
            _logger.LogWarning(ex, "Sign-in query failed against {ResourceId}", resourceId);
            return SignInReportBuilder.Unavailable(now, windowDays,
                $"Application Insights could not be queried: {ex.Message}");
        }
    }

    /// <summary>
    /// Restricted to the characters a KQL identifier comparison can safely carry. The value
    /// comes from configuration and is interpolated into a query, so an unconstrained string
    /// would be an injection point into the Log Analytics workspace.
    /// </summary>
    private string EventName
    {
        get
        {
            var configured = _config["SignIns:EventName"];
            return string.IsNullOrWhiteSpace(configured) || !SafeEventName().IsMatch(configured)
                ? DefaultEventName
                : configured;
        }
    }

    /// <summary>
    /// Projects only the six fields the page renders. Ordering and the row cap happen in the
    /// workspace so the wire payload is bounded regardless of how much history exists.
    /// </summary>
    private static string BuildQuery(string eventName) =>
        $$"""
        traces
        | where customDimensions.EventName == "{{eventName}}"
        | project timestamp,
                  App         = tostring(customDimensions.AppName),
                  UserId      = tostring(customDimensions.UserId),
                  Email       = tostring(customDimensions.Email),
                  DisplayName = tostring(customDimensions.DisplayName),
                  Provider    = tostring(customDimensions.Provider)
        | order by timestamp desc
        | take {{MaxRows}}
        """;

    /// <summary>
    /// Read by column name, never by ordinal: the projection above is the contract, and a
    /// positional read would silently shift every field if a column were ever inserted.
    /// </summary>
    private static List<SignInRecord> ReadRows(Azure.Monitor.Query.Models.LogsTable table)
    {
        var records = new List<SignInRecord>(table.Rows.Count);
        foreach (var row in table.Rows)
        {
            records.Add(new SignInRecord(
                Timestamp: row.GetDateTimeOffset("timestamp")?.UtcDateTime ?? default,
                App: row.GetString("App") ?? "",
                UserId: row.GetString("UserId") ?? "",
                Email: row.GetString("Email") ?? "",
                DisplayName: row.GetString("DisplayName") ?? "",
                Provider: row.GetString("Provider") ?? ""));
        }

        return records;
    }

    [GeneratedRegex(@"^[A-Za-z0-9_.\-]{1,64}$")]
    private static partial Regex SafeEventName();
}
