using Azure.Core;
using Azure.Identity;
using Azure.Monitor.Query;
using Azure.Monitor.Query.Models;
using Azure.ResourceManager;
using Azure.ResourceManager.AppService;
using Azure.ResourceManager.AppService.Models;
using Azure.ResourceManager.Resources;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PoPunkouterSoftware.Shared;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace PoPunkouterSoftware.Infrastructure;

/// <summary>
/// Steps 6-8: SSL certificate expiry, per-service configuration drift, and storage-account exposure.
/// </summary>
public partial class AzureReportService
{
    private async Task<List<SslEntry>> CheckSslAsync(List<RawService> services, CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(8);
        var tasks = services.Select(async svc =>
        {
            await gate.WaitAsync(ct);
            try
            {
                if (svc.Url is not { } url || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    return new SslEntry { Name = svc.Name, Url = svc.Url, Error = "Non-HTTPS" };

                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                    return new SslEntry { Name = svc.Name, Url = url, Error = "Invalid URL" };

                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(TimeSpan.FromSeconds(8));
                    using var tcp = new TcpClient();
                    await tcp.ConnectAsync(uri.Host, 443, cts.Token);
                    using var ssl = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
                    await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                    {
                        TargetHost = uri.Host,
                    }, cts.Token);

                    var cert = ssl.RemoteCertificate;
                    if (cert is null)
                        return new SslEntry { Name = svc.Name, Url = url, Error = "No cert" };

                    var expiry = DateTime.Parse(cert.GetExpirationDateString());
                    var daysLeft = (int)(expiry - DateTime.UtcNow).TotalDays;
                    return new SslEntry { Name = svc.Name, Url = url, Expiry = expiry.ToString("yyyy-MM-dd"), DaysLeft = daysLeft, Subject = cert.Subject };
                }
                catch (Exception ex)
                {
                    return new SslEntry { Name = svc.Name, Url = url, Error = ex.Message };
                }
            }
            finally
            {
                gate.Release();
            }
        });

        return (await Task.WhenAll(tasks)).ToList();
    }

    private async Task<List<ConfigDriftItem>> GetConfigDriftAsync(
        List<RawService> services, ArmClient arm, CancellationToken ct)
    {
        var targets = services.Where(s => s.ResourceTypeRaw == "Microsoft.Web/sites" && s.ResourceId is not null).ToList();
        using var gate = new SemaphoreSlim(6);
        var tasks = targets.Select(async svc =>
        {
            await gate.WaitAsync(ct);
            try
            {
                // Get the site config child resource directly by resource ID (no RG traversal needed)
                var siteRes = arm.GetWebSiteResource(new ResourceIdentifier(svc.ResourceId!));
                var configRes = siteRes.GetWebSiteConfig();
                var configResp = await configRes.GetAsync(cancellationToken: ct);
                var cfg = configResp.Value.Data;

                var issues = new List<ConfigIssue>();
                if (cfg.FtpsState is not null &&
                    cfg.FtpsState != AppServiceFtpsState.Disabled &&
                    cfg.FtpsState != AppServiceFtpsState.FtpsOnly)
                    issues.Add(new ConfigIssue { Severity = "high", Issue = $"FTP enabled ({cfg.FtpsState}) — use FTPS-only or Disabled" });
                if (cfg.IsHttp20Enabled == false)
                    issues.Add(new ConfigIssue { Severity = "low", Issue = "HTTP/2 disabled" });
                if (cfg.MinTlsVersion is not null &&
                    string.Compare(cfg.MinTlsVersion.ToString(), "1.2", StringComparison.Ordinal) < 0)
                    issues.Add(new ConfigIssue { Severity = "high", Issue = $"Min TLS {cfg.MinTlsVersion} — must be ≥1.2" });
                if (cfg.IsAlwaysOn == false)
                    issues.Add(new ConfigIssue { Severity = "low", Issue = "Always-On disabled (cold starts)" });
                if (cfg.Cors?.AllowedOrigins?.Contains("*") == true)
                    issues.Add(new ConfigIssue { Severity = "medium", Issue = "CORS * — all origins allowed" });

                return new ConfigDriftItem
                {
                    Name = svc.Name,
                    FriendlyName = svc.FriendlyName,
                    ResourceGroup = svc.ResourceGroup,
                    IssueCount = issues.Count,
                    Issues = issues,
                };
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Config drift check failed for {Name}", svc.Name);
                return null;
            }
            finally
            {
                gate.Release();
            }
        });

        var results = await Task.WhenAll(tasks);
        return results.OfType<ConfigDriftItem>().OrderBy(x => x.Name).ToList();
    }

    private async Task<List<StorageItem>> GetStorageInventoryAsync(
        List<GenericResourceData> allResources, string? armToken, CancellationToken ct)
    {
        var results = new List<StorageItem>();
        if (armToken is null)
            return results;
        var storages = allResources
            .Where(r => r.ResourceType.ToString().Equals(
                "Microsoft.Storage/storageAccounts", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (storages.Count == 0)
            return results;

        var client = _httpClientFactory.CreateClient("azure-arm");

        async Task<StorageItem?> CheckOneAsync(GenericResourceData sa)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(15));

                using var req = new HttpRequestMessage(HttpMethod.Get,
                    $"https://management.azure.com{sa.Id}?api-version=2023-01-01");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", armToken!);
                using var resp = await client.SendAsync(req, cts.Token);

                bool publicBlob = false;
                bool httpsOnly = true;
                string? minTls = null;

                if (resp.IsSuccessStatusCode)
                {
                    var json = await resp.Content.ReadAsStringAsync(cts.Token);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("properties", out var p))
                    {
                        if (p.TryGetProperty("allowBlobPublicAccess", out var pub))
                            publicBlob = pub.GetBoolean();
                        if (p.TryGetProperty("supportsHttpsTrafficOnly", out var https))
                            httpsOnly = https.GetBoolean();
                        if (p.TryGetProperty("minimumTlsVersion", out var tls))
                            minTls = tls.GetString();
                    }
                }

                var issues = new List<StorageIssue>();
                if (publicBlob)
                    issues.Add(new StorageIssue { Severity = "high", Issue = "Public blob access enabled — potential data exposure" });
                if (!httpsOnly)
                    issues.Add(new StorageIssue { Severity = "high", Issue = "HTTPS-only is off — HTTP traffic allowed" });
                if (minTls is not null && string.Compare(minTls, "TLS1_2", StringComparison.Ordinal) < 0)
                    issues.Add(new StorageIssue { Severity = "medium", Issue = $"Min TLS {minTls} — upgrade to TLS 1.2" });

                return new StorageItem
                {
                    Name = sa.Name,
                    ResourceGroup = sa.Id?.ResourceGroupName,
                    Sku = sa.Sku?.Name?.ToString(),
                    PublicBlobAccess = publicBlob,
                    HttpsOnly = httpsOnly,
                    MinTls = minTls,
                    IssueCount = issues.Count,
                    Issues = issues,
                };
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Storage check failed for {Name}", sa.Name);
                return null;
            }
        }

        var items = await Task.WhenAll(storages.Select(CheckOneAsync));
        results.AddRange(items.OfType<StorageItem>());
        return results;
    }
}
