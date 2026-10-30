using System.Text.Json;
using Microsoft.Extensions.Logging;
using PoPunkouterSoftware.Shared;

namespace PoPunkouterSoftware.Infrastructure;

/// <summary>
/// Registration expiry for every custom domain a scanned app answers on, over RDAP (the
/// structured, keyless successor to WHOIS; rdap.org redirects to the right registry). The SSL
/// step already watches certificates, but a lapsed domain is the worse outage: the
/// certificate cannot renew for a name nobody owns. Apps on Azure's own host names have
/// nothing to look up, so with no custom domains this step makes no calls.
/// </summary>
public partial class AzureReportService
{
    private async Task<List<DomainExpiry>> GetDomainExpiriesAsync(IEnumerable<string> hosts, CancellationToken ct)
    {
        var http = _httpClientFactory.CreateClient(PublicApiClientName);
        var expiries = await BoundedParallelAsync(
            hosts.Select(RegistrableDomain).Where(d => d is not null).Select(d => d!).Distinct(StringComparer.OrdinalIgnoreCase),
            maxConcurrency: 4,
            async domain =>
            {
                try
                {
                    using var doc = JsonDocument.Parse(await http.GetStringAsync($"https://rdap.org/domain/{domain}", ct));
                    var at = doc.RootElement.GetProperty("events").EnumerateArray()
                        .Where(e => e.GetProperty("eventAction").GetString() == "expiration")
                        .Select(e => e.GetProperty("eventDate").GetDateTime())
                        .FirstOrDefault();
                    return at == default ? null : new DomainExpiry { Domain = domain, ExpiresAt = at.ToUniversalTime() };
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    _logger.LogWarning(ex, "RDAP lookup failed for {Domain}", domain);
                    return null;
                }
            }, ct);
        return expiries.Where(e => e is not null).Select(e => e!).OrderBy(e => e.ExpiresAt).ToList();
    }

    /// <summary>
    /// Pure: "www.shop.example.com" → "example.com". The last two labels, or three under a
    /// two-letter country code with a short second level ("example.co.uk").
    /// ponytail: a heuristic, not the Public Suffix List — enough for an estate of a handful
    /// of domains; swap in the list if one ever lands under an unusual suffix.
    /// </summary>
    public static string? RegistrableDomain(string host)
    {
        var labels = host.Trim().TrimEnd('.').ToLowerInvariant().Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (labels.Length < 2)
            return null;
        var take = labels.Length >= 3 && labels[^1].Length == 2 && labels[^2].Length <= 3 ? 3 : 2;
        return string.Join('.', labels[^take..]);
    }
}
