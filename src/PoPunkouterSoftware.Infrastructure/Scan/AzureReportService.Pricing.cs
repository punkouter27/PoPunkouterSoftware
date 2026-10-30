using System.Text.Json;
using Microsoft.Extensions.Logging;
using PoPunkouterSoftware.Shared;

namespace PoPunkouterSoftware.Infrastructure;

/// <summary>
/// What the paid App Service tiers would cost per month in each region the estate runs in,
/// from the Azure Retail Prices API (public, keyless). Every Po app is on F1, so "should this one
/// move up?" is the cost question the dashboard could not answer; the app drawer prints these
/// beside the app's own plan.
/// </summary>
public partial class AzureReportService
{
    /// <summary>The tiers worth comparing an F1 app against: cheapest always-on, the classic step, the modern one.</summary>
    private static readonly string[] PricedSkus = ["B1", "S1", "P0v3"];

    private async Task<List<TierPrice>> GetTierPricesAsync(IEnumerable<string?> regions, CancellationToken ct)
    {
        var http = _httpClientFactory.CreateClient(PublicApiClientName);
        var skuFilter = string.Join(" or ", PricedSkus.Select(s => $"skuName eq '{s}'"));
        var prices = await BoundedParallelAsync(
            regions.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r!).Distinct(StringComparer.OrdinalIgnoreCase),
            maxConcurrency: 4,
            async region =>
            {
                try
                {
                    var filter = $"serviceName eq 'Azure App Service' and armRegionName eq '{region}' and priceType eq 'Consumption' and ({skuFilter})";
                    var json = await http.GetStringAsync(
                        $"https://prices.azure.com/api/retail/prices?$filter={Uri.EscapeDataString(filter)}", ct);
                    return ParseTierPrices(json, region);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    _logger.LogWarning(ex, "Retail prices unavailable for {Region}", region);
                    return [];
                }
            }, ct);
        return prices.SelectMany(p => p).ToList();
    }

    /// <summary>
    /// Pure: one hourly plan meter per SKU and OS, × 730 hours. A plan's product name ends in
    /// "- Linux" for Linux and has no suffix for Windows; anything else (isolated, dedicated
    /// hosts, per-app meters) is skipped.
    /// </summary>
    public static List<TierPrice> ParseTierPrices(string json, string region)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("Items").EnumerateArray()
            .Where(i => i.GetProperty("unitOfMeasure").GetString() == "1 Hour"
                && i.GetProperty("productName").GetString() is { } p && p.Contains(" Plan", StringComparison.Ordinal))
            .Select(i => new TierPrice
            {
                Region = region,
                Linux = i.GetProperty("productName").GetString()!.EndsWith("- Linux", StringComparison.Ordinal),
                Sku = i.GetProperty("skuName").GetString() ?? "",
                MonthlyUsd = Math.Round(i.GetProperty("retailPrice").GetDouble() * 730, 2),
            })
            .Where(t => PricedSkus.Contains(t.Sku))
            .GroupBy(t => (t.Sku, t.Linux))
            .Select(g => g.First())
            .OrderBy(t => t.MonthlyUsd)
            .ToList();
    }
}
