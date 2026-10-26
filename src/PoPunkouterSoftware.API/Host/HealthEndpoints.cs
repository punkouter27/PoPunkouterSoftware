using Azure;
using Azure.Core;
using Azure.Data.Tables;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using PoPunkouterSoftware.Infrastructure;

namespace PoPunkouterSoftware.API;

internal static class HealthEndpoints
{
    internal static WebApplication MapHealthEndpoints(this WebApplication app)
    {
        // ─── Health — probes all external connections ──────────────────────────
        // NET_RULES §3: "/health .net health check that shows all connection status".
        //
        // Every check here authenticates the way the app does and performs the smallest
        // real operation it depends on. An anonymous HTTP GET at the service URL is NOT a
        // health check: it proves DNS and TLS and nothing else. That is what these used to
        // do, and it is why /health reported Key Vault "healthy (httpStatus 404)" and Table
        // Storage "degraded (httpStatus 400)" — two meaningless verdicts — through a week
        // in which the app could not read a single Azure resource.
        //
        // Retries are still deliberately absent: these must report real reachability, not
        // retry through the outage they exist to detect.
        //
        // No check here returns Unhealthy, and that is a decision rather than an oversight.
        // MapHealthChecks answers Unhealthy with 503, and this app has no hard dependency:
        // Table Storage falls back to the local report file, a missing vault means secrets
        // simply were not bound, and an absent report degrades to "no report". Every one of
        // those is a designed path that still serves both pages. A dependency fault shows up
        // as `"status": "degraded"` plus the check's own verdict, which is the honest signal;
        // 503 would claim the instance is dead when it is serving fine.
        app.MapHealthChecks("/health", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
        {
            ResponseWriter = HealthResponseWriter.WriteAsync,
        })
        .WithName("GetHealth")
        .WithTags("Health");

        // Lightweight platform probe — does not call external dependencies.
        app.MapGet("/healthz", () => Results.Ok(new
        {
            status = "ok",
            timestamp = DateTime.UtcNow,
        }))
        .WithName("GetLiveness")
        .WithTags("Health");

        return app;
    }
}

/// <summary>
/// Reads secret metadata from the shared Key Vault with the app's own credential. Listing
/// one page of secret properties is the least-privilege operation that proves the grant the
/// app actually depends on (secrets get/list) — it never touches a secret VALUE.
/// </summary>
public sealed class KeyVaultHealthCheck : IHealthCheck
{
    private readonly TokenCredential _credential;
    private readonly IConfiguration _config;

    public KeyVaultHealthCheck(TokenCredential credential, IConfiguration config)
    {
        _credential = credential;
        _config = config;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        // Mirrors Program.cs's resolution exactly, including its blank check — the probe must
        // report on the vault the app actually bound, not a different one. A blank value is
        // "no vault" (Program.cs skips binding it), which is healthy-not-configured rather
        // than a failure: the hermetic Testing config blanks these keys on purpose.
        var uri = _config["KeyVault:Uri"] ?? _config["AzureKeyVaultUri"] ?? "https://kv-poshared2.vault.azure.net/";
        if (string.IsNullOrWhiteSpace(uri) || !Uri.TryCreate(uri, UriKind.Absolute, out var vaultUri))
        {
            return HealthCheckResult.Healthy("not-configured", new Dictionary<string, object>
            {
                ["note"] = "no Key Vault URI configured",
            });
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(8));

            var client = new SecretClient(vaultUri, _credential);
            var secrets = 0;
            await foreach (var page in client.GetPropertiesOfSecretsAsync(cts.Token).AsPages(pageSizeHint: 1))
            {
                secrets = page.Values.Count;
                break; // one page is proof of access; the app loads the rest at startup
            }

            return HealthCheckResult.Healthy("readable", new Dictionary<string, object>
            {
                ["secretsVisible"] = secrets,
            });
        }
        catch (RequestFailedException ex) when (ex.Status is 401 or 403)
        {
            // Degraded, not Unhealthy: the vault is up and the app still serves every page.
            // Unhealthy answers /health with 503, and "one dependency is misconfigured" is
            // not the same claim as "this instance is dead" — see the note on the writer.
            return Degraded("forbidden", ex.Status, "the app's identity cannot list secrets in this vault");
        }
        catch (Exception ex) when (ex is Azure.Identity.CredentialUnavailableException or Azure.Identity.AuthenticationFailedException)
        {
            // No usable credential — a managed identity that has not been bound, or a
            // developer machine with no `az login`. A local ergonomics problem must not
            // report the deployment as dead.
            return Degraded("no-credential", null, ex.Message);
        }
        catch (Exception ex)
        {
            return Degraded("unreachable", null, ex.Message);
        }
    }

    private static HealthCheckResult Degraded(string description, int? status, string note)
    {
        var data = new Dictionary<string, object> { ["note"] = note };
        if (status is not null)
            data["httpStatus"] = status.Value;
        return HealthCheckResult.Degraded(description, data: data);
    }
}

/// <summary>
/// Queries one row from the app's own table with the app's own credential. Reuses
/// <see cref="TableClientProvider"/> so the probe resolves the client through the exact
/// code path the stores use — a probe that builds its own client can pass while the app's
/// client fails.
/// </summary>
public sealed class TableStorageHealthCheck : IHealthCheck
{
    private readonly IConfiguration _config;
    private readonly ILogger<TableStorageHealthCheck> _logger;

    public TableStorageHealthCheck(IConfiguration config, ILogger<TableStorageHealthCheck> logger)
    {
        _config = config;
        _logger = logger;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var connStr = _config["AzureTableStorage:ConnectionString"];
        var endpoint = _config["AzureTableStorage:Endpoint"];
        if (string.IsNullOrWhiteSpace(connStr) && string.IsNullOrWhiteSpace(endpoint))
        {
            return HealthCheckResult.Healthy("not-configured", new Dictionary<string, object>
            {
                ["note"] = "no connection string or endpoint configured",
            });
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(8));

            var provider = new TableClientProvider(_config, _logger, "health probe");
            var client = await provider.GetAsync(cts.Token);
            if (client is null)
            {
                // TableClientProvider returns null for both "not configured" and "cannot
                // reach it", and so does the app — in either case it serves from the local
                // report file. Reporting the same verdict it acts on keeps the two aligned.
                return HealthCheckResult.Degraded("unavailable", data: new Dictionary<string, object>
                {
                    ["note"] = "table client could not be created — serving from the file cache",
                });
            }

            var rows = 0;
            await foreach (var page in client.QueryAsync<TableEntity>(maxPerPage: 1, cancellationToken: cts.Token).AsPages())
            {
                rows = page.Values.Count;
                break; // one page is proof of a working data-plane read
            }

            return HealthCheckResult.Healthy("readable", new Dictionary<string, object>
            {
                ["rowsVisible"] = rows,
            });
        }
        catch (RequestFailedException ex) when (ex.Status is 401 or 403)
        {
            // Control-plane Reader does not grant table data access — this is the check that
            // tells those two apart. See infra/main.bicep's storage data-plane assignments.
            return HealthCheckResult.Degraded("forbidden", data: new Dictionary<string, object>
            {
                ["httpStatus"] = ex.Status,
                ["note"] = "the app's identity lacks Storage Table Data Contributor",
            });
        }
        catch (Exception ex) when (ex is Azure.Identity.CredentialUnavailableException or Azure.Identity.AuthenticationFailedException)
        {
            return HealthCheckResult.Degraded("no-credential", data: new Dictionary<string, object>
            {
                ["note"] = ex.Message,
            });
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Degraded("unreachable", data: new Dictionary<string, object>
            {
                ["note"] = ex.Message,
            });
        }
    }
}

/// <summary>
/// The check the app was missing: does the stored inventory actually contain anything?
///
/// <para>ARM answers an unauthorized list with an EMPTY page rather than a 403, so a scan
/// running without the subscription Reader role completes successfully and stores a report
/// describing zero resources. Every downstream view — services, cost, uptime, history —
/// then renders a truthful zero, and nothing anywhere reports a fault. Production ran that
/// way while /health showed all green.</para>
///
/// <para>Reads the stored report only; adds no outbound call of its own, so an anonymous
/// caller cannot use /health to drive Azure traffic.</para>
/// </summary>
public sealed class AzureInventoryHealthCheck : IHealthCheck
{
    private readonly AzureReportStore _store;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<AzureInventoryHealthCheck> _logger;

    public AzureInventoryHealthCheck(AzureReportStore store, IWebHostEnvironment env, ILogger<AzureInventoryHealthCheck> logger)
    {
        _store = store;
        _env = env;
        _logger = logger;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _store.LoadAsync(cancellationToken);
            var report = result.IsSuccess && result.Value is not null
                ? result.Value
                : await ReportFileCache.TryLoadFromFileAsync(_env, _logger, cancellationToken);

            if (report is null)
            {
                return HealthCheckResult.Degraded("no-report", data: new Dictionary<string, object>
                {
                    ["note"] = "no inventory has been stored yet — run a refresh",
                });
            }

            var services = report.WebServices?.Total ?? 0;
            var resources = report.AllResourceSummary?.Total ?? 0;
            var data = new Dictionary<string, object>
            {
                ["services"] = services,
                ["resources"] = resources,
                ["generatedAt"] = report.GeneratedAt?.ToString("O") ?? "(unknown)",
            };

            if (services == 0 && resources == 0)
            {
                data["note"] = "the scan completed but discovered nothing — check the app "
                    + "identity's Reader role on the subscription (infra/main.bicep)";
                return HealthCheckResult.Degraded("empty-inventory", data: data);
            }

            return HealthCheckResult.Healthy("populated", data);
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Degraded("unavailable", ex);
        }
    }
}

/// <summary>
/// Reports the configured Application Insights connection string presence
/// without revealing the value. We never leak the actual string — the
/// InstrumentationKey is a stable per-resource identifier and even the
/// masked prefix reveals region + AI workspace target.
/// </summary>
public sealed class AppInsightsHealthCheck : IHealthCheck
{
    private readonly IConfiguration _config;

    public AppInsightsHealthCheck(IConfiguration config) => _config = config;

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var connStr = _config["ApplicationInsights:ConnectionString"];
        var present = !string.IsNullOrWhiteSpace(connStr);
        var data = new Dictionary<string, object>
        {
            ["configured"] = present,
            ["value"] = present ? "configured (redacted)" : "(not set)",
        };
        return Task.FromResult(present
            ? HealthCheckResult.Healthy("configured", data)
            : HealthCheckResult.Healthy("not-configured", data));
    }
}

internal static class HealthResponseWriter
{
    public static async Task WriteAsync(HttpContext context, HealthReport report)
    {
        var env = context.RequestServices.GetRequiredService<IWebHostEnvironment>();
        var config = context.RequestServices.GetRequiredService<IConfiguration>();

        context.Response.ContentType = "application/json";

        // The masked config block is a DEVELOPMENT diagnostic and is omitted in Production.
        //
        // /health is anonymous, so in Production this block published the environment name,
        // which settings are bound, and masked-but-suffixed values for the Key Vault URI and
        // the storage endpoint — the same class of disclosure the /diag page was deleted for
        // on 2026-09-04, reintroduced on the route that replaced it. Masking is not the
        // control here: the last four characters of a vault URI name the vault.
        var payload = new
        {
            status = report.Status.ToString().ToLowerInvariant(),
            application = "PoPunkouterSoftware",
            environment = env.EnvironmentName,
            timestamp = DateTime.UtcNow,
            checks = report.Entries.ToDictionary(
                kvp => kvp.Key,
                kvp => (object)new
                {
                    status = kvp.Value.Status.ToString().ToLowerInvariant(),
                    description = kvp.Value.Description,
                    data = kvp.Value.Data,
                }),
            config = env.IsProduction() ? null : BuildMaskedConfig(env, config),
        };
        await context.Response.WriteAsJsonAsync(payload);
    }

    private static Dictionary<string, string> BuildMaskedConfig(IWebHostEnvironment env, IConfiguration config) =>
        new()
        {
            ["ASPNETCORE_ENVIRONMENT"] = env.EnvironmentName,
            ["AzureKeyVaultUri"] = SecretMasking.MaskValue(
                config["KeyVault:Uri"] ?? config["AzureKeyVaultUri"]),
            ["AzureTableStorage:ConnectionString"] = SecretMasking.MaskValue(
                config["AzureTableStorage:ConnectionString"]),
            ["AzureTableStorage:Endpoint"] = SecretMasking.MaskValue(
                config["AzureTableStorage:Endpoint"]),
            // Screenshots resolve their container from this key when no connection string is
            // set. It was absent from this block, so "blob storage unavailable — screenshots
            // disabled" was undiagnosable from the outside.
            ["AzureBlobStorage:Endpoint"] = SecretMasking.MaskValue(
                config["AzureBlobStorage:Endpoint"]),
            // Never the masked prefix for App Insights: even four characters of a connection
            // string reveal the region and the target workspace.
            ["ApplicationInsights:ConnectionString"] =
                string.IsNullOrWhiteSpace(config["ApplicationInsights:ConnectionString"])
                    ? "(not set)"
                    : "configured (redacted)",
        };
}
