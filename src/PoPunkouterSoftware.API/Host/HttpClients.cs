using PoPunkouterSoftware.Infrastructure;

namespace PoPunkouterSoftware.API;

/// <summary>
/// Every named <see cref="HttpClient"/> the app uses, in one place, because the interesting
/// decision about each one is the same question: does it get a resilience handler?
///
/// <para>Only <c>github</c> and <c>azure-arm</c> do — dependencies we want to succeed. The rest
/// (<c>health</c>, <c>azure-probe</c>, <c>public-api</c>, the AI client and ntfy) deliberately
/// have none: they report real reachability, or degrade, and a retry would hide the outage
/// they exist to see.</para>
///
/// <para>Handler chains are pooled and shared, so nothing may reassign
/// <c>DefaultRequestHeaders</c> on an instance from <c>CreateClient(name)</c> — a per-call
/// mutation leaks that header, credentials included, to every other consumer. Per-call values
/// go on the <see cref="HttpRequestMessage"/>.</para>
/// </summary>
internal static class HttpClients
{
    internal static IServiceCollection AddAppHttpClients(this IServiceCollection services, IConfiguration config)
    {
        services.AddHttpClient("health")
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromSeconds(5),
            });

        services.AddHttpClient("azure-probe")
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                ConnectTimeout = TimeSpan.FromSeconds(15),
            })
            .ConfigureHttpClient(c =>
            {
                c.Timeout = TimeSpan.FromSeconds(15);
                c.DefaultRequestHeaders.UserAgent.ParseAdd("PoPunkouterSoftware-Audit/3.0");
            });

        // GitHub: transient-failure-prone and wanted, so retry + circuit breaker + timeout.
        // The PAT is bound ONCE here, on the client's default headers.
        var ghPat = config["GitHub:PersonalAccessToken"];
        services.AddHttpClient("github")
            .ConfigureHttpClient(c =>
            {
                c.DefaultRequestHeaders.UserAgent.ParseAdd("PoPunkouterSoftware/1.0");
                c.Timeout = TimeSpan.FromSeconds(10);
                if (!string.IsNullOrWhiteSpace(ghPat))
                    c.DefaultRequestHeaders.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ghPat);
            })
            .AddStandardResilienceHandler();

        // nuget.org, OSV.dev, Azure Retail Prices, RDAP and the catalog content checks. Separate
        // from "github" so the PAT never leaves for a third party; no resilience because the
        // content check must report what the page did, and every other caller degrades to
        // "no data" on a failure.
        services.AddHttpClient(AzureReportService.PublicApiClientName)
            .ConfigureHttpClient(c =>
            {
                c.DefaultRequestHeaders.UserAgent.ParseAdd("PoPunkouterSoftware/1.0");
                c.Timeout = TimeSpan.FromSeconds(30);
            });

        // Raw https://management.azure.com REST calls. The ArmClient SDK retries on its own; these
        // used the unnamed default client with no pipeline, so a transient 500/429 left a gap in
        // the report. Callers keep their own tighter CancelAfter budgets.
        services.AddHttpClient("azure-arm")
            .ConfigureHttpClient(c => c.Timeout = Timeout.InfiniteTimeSpan)
            .AddStandardResilienceHandler(o =>
            {
                o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(30);
                o.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(90);
                o.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(60);
            });

        // Azure AI Foundry chat completions. AiTriageService builds the absolute URL from
        // StatusNarrator:Endpoint, so no BaseAddress. No resilience: an AI outage degrades to the
        // rule-based narrative rather than retry-storming the deployment. The timeout is generous
        // because the paragraph is written on the background scan thread.
        services.AddHttpClient(AiTriageService.HttpClientName)
            .ConfigureHttpClient(c =>
            {
                c.Timeout = TimeSpan.FromSeconds(45);
                c.DefaultRequestHeaders.UserAgent.ParseAdd("PoPunkouterSoftware/1.0");
            });

        // ntfy push alerts: best-effort, and a retry storm against a notification service during
        // an outage is the one thing worse than a missed notification.
        services.AddHttpClient(AlertNotifier.HttpClientName)
            .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(10));

        return services;
    }
}
