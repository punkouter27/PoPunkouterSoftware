using System.Diagnostics;
using System.Net;
using Azure;
using Azure.Core;
using Azure.Monitor.Query;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using PoPunkouterSoftware.Infrastructure;
using PoPunkouterSoftware.Shared;

namespace PoPunkouterSoftware.Api;

/// <summary>
/// Live probes for every outside service the app calls that has no health check of its own, for
/// the Services/Data rows of <c>GET /api/diag</c>. Each probe makes the smallest real call the app
/// depends on (a free one: no paid AI completion, no ntfy push), in parallel, with a 5 second budget.
///
/// <para>The rows carry fixed sentences written here, never an exception message, URL or id: the
/// page is anonymous. Results are cached for a minute by <see cref="DiagSnapshotCache"/> (with the health
/// checks, one round for /health and /api/diag) so an anonymous visitor hammering /diag cannot turn this
/// server into a traffic source against GitHub, nuget.org or the AI endpoint.</para>
///
/// <para>Off in the Testing environment and wherever <c>Diag:ProbeServices</c> is false (the Production
/// boot test sets it), so no test calls the internet; the rows then say "Not probed in this environment."</para>
/// </summary>
internal sealed class ExternalServiceProbes(
    IHttpClientFactory httpFactory,
    TokenCredential credential,
    IConfiguration config,
    AiTriageService ai,
    IWebHostEnvironment env,
    ILogger<ExternalServiceProbes> logger)
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    /// <param name="OffReason">Non-null when the service is switched off on purpose on this server (an
    /// opt-in feature left unconfigured): the row then shows "Off" — neutral, not counted in the banner — and
    /// nothing is called.</param>
    private sealed record Spec(string Name, string Group, string Label, string Purpose,
        Func<CancellationToken, Task<Verdict>> Probe, Func<string?>? OffReason = null);

    /// <summary>One probe's answer. <paramref name="Healthy"/> false means Degraded.</summary>
    private sealed record Verdict(bool Healthy, string Detail);

    /// <summary>The /diag status of a service switched off on purpose (see <see cref="Spec"/>).</summary>
    internal const string OffStatus = "Off";

    private static Verdict Ok(string detail) => new(true, detail);
    private static Verdict Bad(string detail) => new(false, detail);

    /// <summary>Runs every probe once, in parallel. Uncached here: callers go through DiagSnapshotCache.</summary>
    internal async Task<IReadOnlyList<DiagCheckDto>> RunAsync(CancellationToken ct)
    {
        var probe = config.GetValue("Diag:ProbeServices", !env.IsEnvironment("Testing"));
        return await Task.WhenAll(Specs().Select(s =>
            s.OffReason?.Invoke() is { } off
                ? Task.FromResult(new DiagCheckDto(s.Name, s.Group, s.Label, OffStatus, s.Purpose, off, 0, null))
                : probe
                    ? RunOneAsync(s, ct)
                    : Task.FromResult(new DiagCheckDto(s.Name, s.Group, s.Label, nameof(HealthStatus.Healthy), s.Purpose,
                        "Not probed in this environment.", 0, null))));
    }

    private async Task<DiagCheckDto> RunOneAsync(Spec spec, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        Verdict verdict;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(Budget);
            verdict = await spec.Probe(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            verdict = Bad("Did not answer within 5 seconds.");
        }
        catch (RequestFailedException ex) when (ex.Status is 401 or 403)
        {
            verdict = Bad("Reachable, but the app's identity is not allowed to use it.");
        }
        catch (Exception ex) when (ex is Azure.Identity.CredentialUnavailableException or Azure.Identity.AuthenticationFailedException)
        {
            verdict = Bad("No Azure sign-in is available to reach it.");
        }
        catch (Exception ex) when (ex is HttpRequestException or RequestFailedException)
        {
            logger.LogInformation(ex, "Diag probe {Probe} failed", spec.Name);
            verdict = Bad("Could not be reached.");
        }

        var status = verdict.Healthy ? nameof(HealthStatus.Healthy) : nameof(HealthStatus.Degraded);
        return new DiagCheckDto(spec.Name, spec.Group, spec.Label, status, spec.Purpose, verdict.Detail,
            Math.Round(sw.Elapsed.TotalMilliseconds, 1), verdict.Healthy ? null : verdict.Detail);
    }

    private Spec[] Specs() =>
    [
        new("screenshots", "data", "Screenshot storage",
            "Blob container holding each app card's last captured screenshot (no longer updated since 2026-11-20).",
            ProbeScreenshotsAsync),
        new("AzureResourceManager", "service", "Azure Resource Manager",
            "Where every scan reads the Azure estate: sites, costs, advisor tips, storage and security settings.",
            ProbeArmAsync),
        new("ApplicationInsights", "service", "Application Insights",
            "Telemetry for this app, and the sign-in data behind the Users page.",
            ProbeAppInsightsAsync),
        new("ai", "service", "AI summaries",
            "Writes the plain-English status paragraph on the Azure page.",
            ProbeAiAsync),
        new("GitHub", "service", "GitHub",
            "Repos, latest build results and security alerts for each app.",
            ProbeGitHubAsync),
        new("NuGet", "service", "NuGet",
            "Newest package versions, to flag apps with outdated packages.",
            ct => ProbePublicAsync("https://api.nuget.org/v3/index.json", HttpMethod.Get, ct)),
        new("OSV", "service", "OSV vulnerability database",
            "Known vulnerabilities in the packages each app uses.",
            ct => ProbePublicAsync("https://api.osv.dev/v1/vulns/GHSA-vh95-rmgr-6w4m", HttpMethod.Get, ct)),
        new("AzurePrices", "service", "Azure retail prices",
            "List prices behind the cost estimates on the Azure page.",
            ct => ProbePublicAsync("https://prices.azure.com/api/retail/prices?$filter=serviceName%20eq%20%27Bandwidth%27%20and%20armRegionName%20eq%20%27eastus%27", HttpMethod.Get, ct)),
        new("RDAP", "service", "Domain registry (RDAP)",
            "Expiry dates of the custom domains the apps use.",
            ct => ProbePublicAsync("https://rdap.org/domain/example.com", HttpMethod.Head, ct)),
        new("alerts", "service", "Outage alerts (ntfy)",
            "Pushes a phone notification when a Po app goes down or comes back.",
            ProbeNtfyAsync,
            // AlertNotifier is opt-in: no topic means outage pushes are switched off, not broken.
            () => string.IsNullOrWhiteSpace(config["Alerts:NtfyTopic"])
                ? "Off — no topic is set on this server, so outages are not pushed to a phone."
                : null),
    ];

    private async Task<Verdict> ProbeScreenshotsAsync(CancellationToken ct)
    {
        var connectionString = config["AzureTableStorage:ConnectionString"];
        var endpoint = config["AzureBlobStorage:Endpoint"];
        BlobServiceClient service;
        if (!string.IsNullOrWhiteSpace(connectionString))
            service = new BlobServiceClient(connectionString);
        else if (Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
            service = new BlobServiceClient(uri, credential);
        else
            return NotSetUp("app cards show placeholders.");

        var exists = await service.GetBlobContainerClient("app-screenshots").ExistsAsync(ct);
        return exists.Value
            ? Ok("Working — the app read from it just now.")
            : Bad("Reachable, but the screenshot container is missing — app cards show placeholders.");
    }

    private async Task<Verdict> ProbeArmAsync(CancellationToken ct)
    {
        var subscriptionId = config["Azure:SubscriptionId"];
        if (string.IsNullOrWhiteSpace(subscriptionId))
            return NotSetUp("scans cannot read Azure.");

        var token = await credential.GetTokenAsync(new TokenRequestContext(["https://management.azure.com/.default"]), ct);
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"https://management.azure.com/subscriptions/{Uri.EscapeDataString(subscriptionId)}?api-version=2022-12-01");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Token);
        using var resp = await httpFactory.CreateClient("health").SendAsync(req, ct);
        return resp.StatusCode switch
        {
            HttpStatusCode.OK => Ok("Working — the app read the subscription just now."),
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound =>
                Bad("Reachable, but the app's identity cannot read the subscription — scans come back empty."),
            _ => Bad($"Answered with an error ({(int)resp.StatusCode})."),
        };
    }

    private async Task<Verdict> ProbeAppInsightsAsync(CancellationToken ct)
    {
        var resourceId = config["SignIns:ResourceId"];
        if (string.IsNullOrWhiteSpace(resourceId))
            return NotSetUp("the Users page has no data.");

        var client = new LogsQueryClient(credential);
        await client.QueryResourceAsync(new ResourceIdentifier(resourceId), "print ok = 1",
            new QueryTimeRange(TimeSpan.FromHours(1)), cancellationToken: ct);
        var exporting = !string.IsNullOrWhiteSpace(config["ApplicationInsights:ConnectionString"])
            || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("APPLICATIONINSIGHTS_CONNECTION_STRING"));
        return Ok(exporting
            ? "Working — the app ran a query just now and sends its telemetry here."
            : "Working — the app ran a query just now (this server does not send telemetry).");
    }

    private async Task<Verdict> ProbeAiAsync(CancellationToken ct)
    {
        if (!ai.IsEnabled)
            return Ok("Turned off on this server.");

        var endpoint = (config["StatusNarrator:Endpoint"] is { Length: > 0 } e ? e : AiTriageService.DefaultEndpoint).TrimEnd('/');
        var apiVersion = config["StatusNarrator:ApiVersion"] is { Length: > 0 } v ? v : "2024-10-21";
        // Listing the models is free; a chat completion is not, so this is the deepest probe we make.
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{endpoint}/openai/models?api-version={Uri.EscapeDataString(apiVersion)}");
        var key = config["StatusNarrator:ApiKey"] is { Length: > 0 } k ? k : config["AzureOpenAI:ApiKey"];
        if (!string.IsNullOrWhiteSpace(key))
        {
            req.Headers.Add("api-key", key);
        }
        else
        {
            var token = await credential.GetTokenAsync(new TokenRequestContext(["https://cognitiveservices.azure.com/.default"]), ct);
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Token);
        }

        using var resp = await httpFactory.CreateClient("health").SendAsync(req, ct);
        return resp.IsSuccessStatusCode
            ? Ok("Working — the AI service answered just now (no paid summary was written).")
            : resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? Bad("Reachable, but the app is not allowed to use it — the page falls back to a rule-based summary.")
                : Bad($"Answered with an error ({(int)resp.StatusCode}) — the page falls back to a rule-based summary.");
    }

    private async Task<Verdict> ProbeGitHubAsync(CancellationToken ct)
    {
        // /rate_limit does not count against the rate limit.
        using var resp = await httpFactory.CreateClient("github").GetAsync("https://api.github.com/rate_limit", ct);
        if (!resp.IsSuccessStatusCode)
            return Bad($"Answered with an error ({(int)resp.StatusCode}).");
        var signedIn = !string.IsNullOrWhiteSpace(config["GitHub:PersonalAccessToken"]);
        return signedIn
            ? Ok("Working — reached just now with the app's token.")
            : Bad("Reachable, but no token is set — private repos and build results are missing.");
    }

    private async Task<Verdict> ProbePublicAsync(string url, HttpMethod method, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, url);
        using var resp = await httpFactory.CreateClient(AzureReportService.PublicApiClientName)
            .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        return (int)resp.StatusCode < 500
            ? Ok("Reachable — answered just now.")
            : Bad($"Answered with an error ({(int)resp.StatusCode}).");
    }

    private async Task<Verdict> ProbeNtfyAsync(CancellationToken ct)
    {
        var server = (config["Alerts:NtfyServer"] is { Length: > 0 } s ? s : "https://ntfy.sh").TrimEnd('/');
        // The health route sends nothing to anyone; a test push would page the owner.
        using var resp = await httpFactory.CreateClient("health").GetAsync(server + "/v1/health", ct);
        return resp.IsSuccessStatusCode
            ? Ok("Working — reachable just now, and a topic is set.")
            : Bad($"Answered with an error ({(int)resp.StatusCode}).");
    }

    private Verdict NotSetUp(string consequence) =>
        new(!env.IsProduction(), $"Not set up on this server — {consequence}");
}
