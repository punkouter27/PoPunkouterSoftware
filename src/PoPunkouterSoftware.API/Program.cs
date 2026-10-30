using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager;
using PoPunkouterSoftware.API;
using PoPunkouterSoftware.API.Platform;
using PoPunkouterSoftware.Infrastructure;
using Radzen;
using Scalar.AspNetCore;
using Serilog;
using Serilog.Events;

// ─── Bootstrap Serilog early so startup errors are captured ──────────────────
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);
    ThreadPool.GetMinThreads(out var minWorkerThreads, out var minCompletionPortThreads);
    ThreadPool.SetMinThreads(
        Math.Max(minWorkerThreads, builder.Configuration.GetValue("ThreadPool:MinWorkerThreads", 100)),
        Math.Max(minCompletionPortThreads, builder.Configuration.GetValue("ThreadPool:MinCompletionPortThreads", 100)));

    // ─── Azure Key Vault — default to shared PoShared vault unless overridden ───
    // Skipped under the "Testing" environment so integration/E2E runs are hermetic
    // and never bind to (or leak secrets from) the real shared vault.
    var kvUriStr = builder.Configuration["KeyVault:Uri"] ?? builder.Configuration["AzureKeyVaultUri"];
    if (kvUriStr is null)
    {
        kvUriStr = "https://kv-poshared2.vault.azure.net/";
    }
    if (!builder.Environment.IsEnvironment("Testing") && !string.IsNullOrWhiteSpace(kvUriStr))
    {
        try
        {
            builder.Configuration.AddAzureKeyVault(
                new Uri(kvUriStr),
                new DefaultAzureCredential(),
                new AppKeyVaultSecretManager());
        }
        catch (Exception ex)
        {
            // Non-fatal: Key Vault unavailable without managed identity / az login
            Log.Warning("Key Vault at {Uri} could not be reached — continuing without it. Reason: {Reason}", kvUriStr, ex.Message);
        }
    }

    // ─── Logging, telemetry, error handling (Host/Observability.cs) ──────────
    var aiConnectionString = PoPlatform.ResolveAppInsightsConnectionString(builder.Configuration);
    if (string.IsNullOrWhiteSpace(aiConnectionString))
        aiConnectionString = builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
    builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
    builder.Services.AddProblemDetails();
    builder.AddAppLogging();

    // ─── .NET 10 TimeProvider abstraction — enables testable time ────────────
    builder.Services.AddSingleton(TimeProvider.System);

    builder.AddAppTelemetry(aiConnectionString);

    // ─── OpenAPI / Scalar ────────────────────────────────────────────
    builder.Services.AddOpenApi();

    builder.WebHost.UseStaticWebAssets();

    builder.Services.AddRazorComponents()
        .AddInteractiveWebAssemblyComponents();

    builder.Services.AddRadzenComponents();
    builder.Services.AddScoped<DialogService>();
    builder.Services.AddScoped<NotificationService>();

    // NOTE: No CORS. This is a single-origin Blazor Web App — the WASM client is
    // served from this same host (see MapStaticAssets + MapRazorComponents below),
    // so there is no cross-origin caller to authorize. Adding CORS here would be
    // dead configuration and needless attack surface.

    // ─── Authentication / Authorization (NET_RULES §3) ───────────────────────
    // FakeAuthHandler reads X-Fake-User / X-Fake-Roles and is registered ONLY
    // outside Production. The handler constructor itself throws on Production
    // initialization — a guardrail against environmental misconfiguration.
    //
    // That guardrail only works if the registration is actually conditional. It was
    // not: the scheme was added unconditionally AND named as the default, so
    // UseAuthentication resolved it on EVERY request and the constructor threw —
    // turning the guardrail into a site-wide 500 in Production instead of a
    // fail-closed management gate.
    //
    // In Production no scheme is registered at all, so the default-scheme lookup
    // returns null and UseAuthentication no-ops. The Management policy must not name
    // the absent scheme either: AddAuthenticationSchemes("Fake") with no handler
    // throws "No authentication handler is registered" (another 500). It denies
    // outright instead, which is the same answer a real fail-closed gate would give.
    var isProduction = builder.Environment.IsProduction();
    if (isProduction)
    {
        builder.Services.AddAuthentication();
    }
    else
    {
        builder.Services
            .AddAuthentication(FakeAuthHandler.SchemeName)
            .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, FakeAuthHandler>(FakeAuthHandler.SchemeName, _ => { });
    }
    builder.Services.AddAuthorization(options =>
    {
        options.AddPolicy("Management", p =>
        {
            if (isProduction)
                p.RequireAssertion(_ => false);
            else
                p.AddAuthenticationSchemes(FakeAuthHandler.SchemeName)
                    .RequireAuthenticatedUser()
                    .RequireRole(FakeAuthHandler.ManagementRole);
        });
    });

    // ─── Named HttpClients — which get resilience and which deliberately do not ───
    builder.Services.AddAppHttpClients(builder.Configuration);

    // ─── Azure report analysis + Blob persistence ─────────────────────────────
    // Shared credential and ArmClient registered as Singletons so every service
    // and endpoint reuses the same credential chain walk instead of re-initialising
    // DefaultAzureCredential on every scan or control action.
    builder.Services.AddSingleton<TokenCredential>(_ => new DefaultAzureCredential());
    builder.Services.AddSingleton<ArmClient>(sp =>
        new ArmClient(sp.GetRequiredService<TokenCredential>()));

    builder.Services.AddSingleton<AzureReportStore>();
    builder.Services.AddSingleton<UptimeSampleStore>();
    builder.Services.AddSingleton<AppScreenshotService>();
    builder.Services.AddTransient<AzureReportService>();
    builder.Services.AddTransient<DowntimeDiagnosisService>();
    builder.Services.AddSingleton<ServicePingerService>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<ServicePingerService>());
    builder.Services.AddSignalR();
    builder.Services.AddSingleton<RefreshSessionManager>();
    builder.Services.AddSingleton<ReportRefreshRunner>();
    builder.Services.AddSingleton<AiTriageService>();
    builder.Services.AddSingleton<AlertNotifier>();
    builder.Services.AddAppRateLimits(builder.Configuration);
    // Singleton: LogsQueryClient walks the credential chain on construction, and /users is a
    // live read on every page load. Reused instance, one chain walk for the lifetime of the app.
    builder.Services.AddSingleton<SignInQueryService>();

    // ─── Health checks (NET_RULES §3) ─────────────────────────────────────────
    // One check per external dependency, each performing the real operation the app
    // depends on with the app's own credential. AzureInventory is not a dependency probe
    // but a data-truth probe: it is the only check that can see the failure where every
    // dependency answers correctly and the inventory is empty anyway.
    builder.Services.AddHealthChecks()
        .AddCheck<KeyVaultHealthCheck>("KeyVault", tags: new[] { "external", "kv" })
        .AddCheck<TableStorageHealthCheck>("TableStorage", tags: new[] { "external", "storage" })
        .AddCheck<AzureInventoryHealthCheck>("AzureInventory", tags: new[] { "data" })
        .AddCheck<AppInsightsHealthCheck>("ApplicationInsights", tags: new[] { "external", "telemetry" });

    // ─── Response compression for dynamic JSON only ───────────────────────────
    // The report/summary/history payloads are large, highly compressible JSON that
    // Kestrel does not compress by default. .NET 10's MapStaticAssets already serves
    // fingerprinted `.br`/`.gz` blobs from disk with the right Content-Encoding; if
    // UseResponseCompression runs over those the browser hits an integrity/body mismatch
    // ("empty SHA-256 + Failed to fetch" cascade for blazor.boot.json, .wasm, .pdb).
    // Limiting compression to application/json keeps the static-asset pipeline intact
    // while still shrinking every JSON payload.
    builder.Services.AddResponseCompression(o =>
    {
        o.EnableForHttps = true;
        o.MimeTypes = new[] { "application/json" };
    });

    var app = builder.Build();

    app.UseExceptionHandler();

    // First in the pipeline so the headers land on every response — static assets, the
    // WASM payload and API JSON alike, not just the routes that reach an endpoint.
    app.UseSecurityHeaders();

    // The commit this build came from (SDK-stamped as "<version>+<sha>"), so a deploy can prove
    // which build is answering: CI checks /health/live for X-Commit equal to the pushed sha.
    var commit = PoPlatform.Version.Split('+') is [_, var sha, ..] ? sha : "unknown";
    app.Use((ctx, next) =>
    {
        ctx.Response.Headers["X-Commit"] = commit;
        return next();
    });

    // A browser that mistypes a URL, or follows a link to an app that was renamed, must get
    // the app: the shell, the header, the skip link and a way back. It used to get
    // `{"status":404,"path":"…","message":"Route not found."}` on a blank white page, because
    // a single `MapFallback` swallowed every unmatched path — which also made the
    // `<NotFound>` block in Routes.razor dead code, since a real navigation never reached the
    // router.
    //
    // A RE-EXECUTING STATUS PAGE, not a catch-all route, and the difference is the status
    // code. A catch-all route matches by definition, so the response says 200 — a soft 404
    // that tells a link checker, a monitor or a search engine the page exists. Setting the
    // status from the page's own OnInitialized does not work either: by then Blazor has begun
    // writing the response. Both versions of that mistake were tried and both shipped a 200
    // (caught by NotFoundRouteRegressionTests and by ProductionBootTests, which saw
    // /openapi/v1.json answer 200 in Production). This middleware re-executes the pipeline
    // against /not-found and then RESTORES the original status, which is the only combination
    // that gives a real 404 with a real body.
    //
    // Anything that already wrote a 4xx body — an unknown /api path, which answers JSON — has
    // started its response, so the middleware leaves it alone. Known routes are untouched.
    app.UseStatusCodePagesWithReExecute("/not-found", "?statusCode={0}");

    // HSTS in Production only: it pins the browser to HTTPS for the max-age, which is
    // exactly wrong to send from a plain-HTTP localhost run.
    if (app.Environment.IsProduction())
        app.UseHsts();

    // ─── Startup configuration health-checks (non-fatal, informational) ──────
    var startupLog = app.Services.GetRequiredService<ILogger<Program>>();
    if (string.IsNullOrWhiteSpace(builder.Configuration["AzureTableStorage:ConnectionString"]) &&
        string.IsNullOrWhiteSpace(builder.Configuration["AzureTableStorage:Endpoint"]))
        startupLog.LogWarning("AzureTableStorage is not configured — report will use local JSON fallback only.");
    if (string.IsNullOrWhiteSpace(builder.Configuration["ApplicationInsights:ConnectionString"]))
        startupLog.LogInformation("ApplicationInsights:ConnectionString is not set — metrics/logs SDK clients will be skipped. Expected in local dev.");

    app.UseSerilogRequestLogging(o =>
    {
        o.EnrichDiagnosticContext = (diag, ctx) =>
        {
            diag.Set("UserId", "anonymous");
            diag.Set("SessionId", ctx.TraceIdentifier);
            diag.Set("Environment", app.Environment.EnvironmentName);
        };
        o.GetLevel = (ctx, _, ex) =>
        {
            // Ignore expected client disconnect noise (499 / aborted requests).
            if (ctx.Response.StatusCode == 499)
                return LogEventLevel.Debug;
            if (ex is OperationCanceledException && ctx.RequestAborted.IsCancellationRequested)
                return LogEventLevel.Debug;

            // GlobalExceptionHandler already logs the Error with the full exception; a second
            // Error event here for the same request doubles log volume and muddies triage.
            // Warning keeps failed requests visible in the request log without duplication.
            if (ex is not null || ctx.Response.StatusCode >= 500)
                return LogEventLevel.Warning;
            return LogEventLevel.Information;
        };
    });

    // Convert expected client-aborted request cancellations into a 499 response
    // so they do not surface as unhandled exception telemetry.
    app.Use(async (ctx, next) =>
    {
        try
        {
            await next();
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
        {
            if (!ctx.Response.HasStarted)
                ctx.Response.StatusCode = 499;
        }
    });

    app.UseStaticFiles();
    // Run UseResponseCompression AFTER UseStaticFiles so the fingerprinted asset
    // pipeline (MapStaticAssets, .br / .gz) is not double-compressed. JSON-only policy
    // above is the second guard rail; ordering is the first.
    app.UseResponseCompression();
    app.UseAntiforgery();
    app.UseAuthentication();
    app.UseAuthorization();
    app.UseRateLimiter();

    // MapStaticAssets serves compressed + fingerprinted static web assets from the client WASM project.
    // Must be called before MapRazorComponents per framework requirement.
    app.MapStaticAssets();

    app.MapRazorComponents<PoPunkouterSoftware.API.App>()
       .AddInteractiveWebAssemblyRenderMode()
       .AddAdditionalAssemblies(typeof(PoPunkouterSoftware.Client.MainLayout).Assembly);

    // ─── OpenAPI / Scalar UI — never in Production ────────────────────
    // These were mapped unconditionally, so /openapi/v1.json and /scalar/v1 published the
    // full route table, parameter shapes and response schemas of an app with no login to
    // anonymous callers on the live site — including the management routes, which are the
    // ones worth finding. There is no consumer for them in Production: the API has exactly
    // one client and it is compiled from the same solution.
    if (!app.Environment.IsProduction())
    {
        app.MapOpenApi();
        app.MapScalarApiReference(o =>
        {
            o.Title = "PoPunkouterSoftware API";
            o.Theme = ScalarTheme.Default;
        });
    }

    // ─── Host-level plumbing (not part of any slice) ──────────────────
    app.MapGet("/favicon.ico", () => Results.Redirect("/images/favicon.ico", permanent: false))
        .ExcludeFromDescription();
    app.MapGet("/robots.txt", () => Results.Text("User-agent: *\nDisallow:\n", "text/plain"))
        .ExcludeFromDescription();
    app.MapGet("/robots{suffix}.txt", (string suffix) => Results.NoContent())
        .ExcludeFromDescription();

    // ─── Feature slices ───────────────────────────────────────────────
    app.MapConfigEndpoints();
    app.MapHealthEndpoints();
    // Uniform cross-app liveness probe (see PoPlatform). Same shape in every Po app, which
    // is what lets the portfolio dashboard poll them all and render one uptime grid.
    app.MapPoLiveness();
    app.MapDiagEndpoints();
    app.MapPortfolioEndpoints();
    app.MapSignInEndpoints();
    app.MapHub<RefreshHub>("/hubs/refresh");

    // Unmatched API route. Every /api/* path is a JSON contract — the WASM client reads
    // these bodies and UnknownApiRoute_Returns404_WithJsonStatusAndPath pins the shape — so
    // an unknown one keeps answering JSON.
    //
    // This is deliberately NOT a MapFallback any more. A fallback matches EVERY unmatched
    // path, so `/does-not-exist` in a browser got this same JSON blob on a blank white page
    // with no header, no skip link and no way back; it also meant the <NotFound> block in
    // Routes.razor could never run for a real navigation. Everything outside /api now reaches
    // UseStatusCodePagesWithReExecute above, which renders NotFoundPage.razor with the app's own
    // chrome and a real 404; this route answers JSON for anything under /api.
    app.Map("/api/{**rest}", async (HttpContext ctx) =>
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        await ctx.Response.WriteAsJsonAsync(new
        {
            status = 404,
            path = ctx.Request.Path.Value,
            message = "Route not found."
        });
    })
    .ExcludeFromDescription();

    app.Run();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[STARTUP FATAL ERROR]: {ex}");
    Log.Fatal(ex, "PoPunkouterSoftware terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}

