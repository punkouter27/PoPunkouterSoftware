using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using PoPunkouterSoftware.Shared;

namespace PoPunkouterSoftware.Infrastructure;

/// <summary>
/// Locates and parses the on-disk azure-full-report.json cache — the degradation path
/// used when Table Storage is unavailable. Owned by Infrastructure so feature slices
/// (Diag, Portfolio) can share it without depending on each other.
/// </summary>
public static class ReportFileCache
{
    public const string ReportFileName = "azure-full-report.json";

    /// <summary>
    /// Where the report cache is written and read. Deliberately under the CONTENT root,
    /// never the web root.
    /// <para>This file used to live in <c>wwwroot/data</c> beside apps.json, which
    /// <c>UseStaticFiles()</c> happily served to anyone who asked for
    /// <c>/data/azure-full-report.json</c> — a full Azure inventory including the raw
    /// subscription id, every resource id, cost figures and SSL state. It is a server-side
    /// cache and has never had a browser consumer, so the fix is to move it off the served
    /// tree rather than to bolt an exclusion onto the static-file pipeline.</para>
    /// </summary>
    public static string GetCacheDir(IWebHostEnvironment env) =>
        Path.Combine(env.ContentRootPath, "App_Data");

    /// <summary>
    /// The PUBLIC catalog directory — <c>apps.json</c> only. Served as a static asset by
    /// design; it lists nothing that is not already public.
    /// </summary>
    // In the unified Blazor WASM model, wwwroot lives in the Client project.
    // env.WebRootPath is null on the server because the server has no wwwroot of its own.
    // In dev, resolve to the Client project's wwwroot; in production, UseStaticWebAssets()
    // publishes client assets under ContentRootPath/wwwroot, so WebRootPath is non-null.
    public static string GetCatalogDir(IWebHostEnvironment env) => Path.Combine(GetWebRoot(env), "data");

    /// <summary>
    /// Local screenshots directory fallback when blob storage is unavailable or offline.
    /// Under <c>App_Data</c>, not the web root: it was committed under <c>wwwroot</c>, churned
    /// the tree on every local capture and was served statically beside the endpoint.
    /// </summary>
    public static string GetScreenshotsDir(IWebHostEnvironment env) => Path.Combine(GetCacheDir(env), "screenshots");

    private static string GetWebRoot(IWebHostEnvironment env) =>
        env.WebRootPath
        ?? Path.GetFullPath(Path.Combine(env.ContentRootPath, "..", "PoPunkouterSoftware.Client", "wwwroot"));

    public static string GetReportPath(IWebHostEnvironment env) =>
        Path.Combine(GetCacheDir(env), ReportFileName);

    /// <summary>
    /// Creates the cache directory if it does not exist and returns the report path. The
    /// cache dir is no longer a checked-in folder, so the first write after a clean deploy
    /// has to make it.
    /// </summary>
    public static string EnsureReportPath(IWebHostEnvironment env)
    {
        Directory.CreateDirectory(GetCacheDir(env));
        return GetReportPath(env);
    }

    // Hoisted so System.Text.Json's converter metadata cache is reused instead of being
    // rebuilt for every request that hits the file-fallback path.
    private static readonly JsonSerializerOptions FileReadJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Parses a cached report file, treating corruption as "no report" instead of letting a
    /// <see cref="JsonException"/> bubble into a 500 — the file cache is the degradation path
    /// used exactly when Table Storage is already down.
    /// </summary>
    public static AzureReport? TryDeserializeReport(string json, ILogger logger)
    {
        try
        {
            return JsonSerializer.Deserialize<AzureReport>(json, FileReadJsonOptions);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Cached azure-full-report.json is corrupt — ignoring the file cache");
            return null;
        }
    }

    /// <summary>
    /// Reads and parses the cached report file, or null when it does not exist. The single
    /// place feature slices (Diag, Portfolio) should call for the Table-Storage-unavailable
    /// fallback, instead of each re-deriving the path and re-checking existence themselves.
    /// </summary>
    public static async Task<AzureReport?> TryLoadFromFileAsync(
        IWebHostEnvironment env, ILogger logger, CancellationToken ct = default)
    {
        var reportPath = GetReportPath(env);
        if (!File.Exists(reportPath))
            return null;

        var json = await File.ReadAllTextAsync(reportPath, ct);
        return TryDeserializeReport(json, logger);
    }
}
