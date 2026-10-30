using Azure.Monitor.OpenTelemetry.AspNetCore;
using OpenTelemetry;
using OpenTelemetry.Instrumentation.AspNetCore;
using OpenTelemetry.Instrumentation.Http;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Events;

namespace PoPunkouterSoftware.API;

/// <summary>
/// Logging and telemetry. Serilog writes Console + File; Azure Monitor via OpenTelemetry is the
/// SOLE Application Insights pipeline (the Serilog AI sink was removed to stop duplicate
/// traces). The connection string comes from Key Vault or APPLICATIONINSIGHTS_CONNECTION_STRING,
/// never from appsettings.json.
/// </summary>
internal static class Observability
{
    internal static WebApplicationBuilder AddAppLogging(this WebApplicationBuilder builder)
    {
        // CorrelationId enricher requires IHttpContextAccessor
        builder.Services.AddHttpContextAccessor();

        // writeToProviders: true keeps the OpenTelemetry ILogger provider (registered by
        // UseAzureMonitor) in the pipeline. Without it, UseSerilog replaces the logger factory
        // and application LOGS silently never reach Application Insights — only traces and
        // metrics do — leaving production logs stranded on the App Service filesystem.
        builder.Host.UseSerilog((ctx, services, cfg) =>
        {
            try
            {
                cfg.ReadFrom.Configuration(ctx.Configuration)
                   .ReadFrom.Services(services);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Serilog configuration loading failed; falling back to code-based defaults only.");
            }

            cfg.MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
               .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
               .Enrich.FromLogContext()
               .Enrich.WithEnvironmentName()
               .Enrich.WithCorrelationId()
               .Enrich.WithProperty("Application", "PoPunkouterSoftware")
               .WriteTo.Console(outputTemplate:
                   "[{Timestamp:HH:mm:ss} {Level:u3}] {CorrelationId} {Message:lj}{NewLine}{Exception}")
               .WriteTo.File("logs/app-.log",
                   rollingInterval: RollingInterval.Day,
                   retainedFileCountLimit: 14,
                   outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {CorrelationId} {UserId} {Environment} {Message:lj}{NewLine}{Exception}");
        }, writeToProviders: true);

        return builder;
    }

    /// <summary>
    /// Metrics and spans for the app's own meter, exported to Azure Monitor only when a
    /// connection string is present; without one they are recorded in-process and harmless.
    /// </summary>
    internal static WebApplicationBuilder AddAppTelemetry(this WebApplicationBuilder builder, string? aiConnectionString)
    {
        // cloud_RoleName comes from the resource's service.name. Without it traces surface as
        // "unknown_service:dotnet". Resolved by reflection on the entry assembly — in the API
        // only, never in the WASM client.
        var serviceName = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name ?? "PoPunkouterSoftware";
        var otel = builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(serviceName))
            .WithMetrics(m =>
            {
                m.AddMeter(Infrastructure.Telemetry.MeterName);

                // Cost cap: the ASP.NET hosting / HTTP client meters were ~80% of one 3-day bill
                // and mostly duplicate the traces. OTel has no "remove meter"; a Drop view is how
                // an instrument is suppressed. Re-enable by config without a redeploy.
                if (!builder.Configuration.GetValue("ApplicationInsights:EnableAspNetCoreMeters", false))
                {
                    m.AddView(i => i.Meter.Name is "Microsoft.AspNetCore.Hosting" or "System.Net.Http"
                        ? MetricStreamConfiguration.Drop
                        : null);
                }
            })
            // Without this source every outbound ARM/GitHub span recorded during a refresh is
            // orphaned instead of parented under the refresh's root span.
            .WithTracing(t => t.AddSource(Infrastructure.Telemetry.MeterName));

        if (string.IsNullOrWhiteSpace(aiConnectionString))
            return builder;

        // Fixed-rate sampling caps ingestion cost. 10% is safe because EXCEPTIONS ARE NOT LOST
        // to it: GlobalExceptionHandler logs every one through ILogger, and the logs pipeline is
        // not trace-sampled.
        var samplingRatio = builder.Configuration.GetValue<float?>("ApplicationInsights:SamplingRatio") ?? 0.10f;
        otel.UseAzureMonitor(o =>
        {
            o.ConnectionString = aiConnectionString;
            o.SamplingRatio = samplingRatio;
        });

        // Heartbeat traffic is not signal: drop /health and /healthz before it is sampled and billed.
        builder.Services.Configure<AspNetCoreTraceInstrumentationOptions>(o =>
            o.Filter = ctx =>
            {
                var path = ctx.Request.Path.Value;
                return path is null || !path.StartsWith("/health", StringComparison.OrdinalIgnoreCase);
            });

        // The "azure-probe" client tests reachability on a schedule; its spans are not an
        // application dependency worth recording every cycle.
        builder.Services.Configure<HttpClientTraceInstrumentationOptions>(o =>
            o.FilterHttpRequestMessage = req =>
                !req.Headers.UserAgent.ToString().Contains("PoPunkouterSoftware-Audit", StringComparison.OrdinalIgnoreCase));

        return builder;
    }
}
