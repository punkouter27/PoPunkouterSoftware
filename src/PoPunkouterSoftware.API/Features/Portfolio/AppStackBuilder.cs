using PoPunkouterSoftware.Shared;

namespace PoPunkouterSoftware.API;

/// <summary>
/// One app's row in the home page's stack matrix: the curated <c>stack</c> block from
/// <c>apps.json</c>, with the two facts the Azure scan knows better filled in from it.
/// Hosting always comes from the scan when the app was found, because the catalog can only
/// repeat what someone typed and a plan move would leave it lying. Data falls back to
/// "Azure Storage" when the app's own resource group holds a storage account — that the account
/// exists is visible to ARM, but whether the app uses tables or blobs is not.
/// </summary>
internal static class AppStackBuilder
{
    internal static AppStack Build(
        AppStack? catalog, IReadOnlyList<string>? technologies, WebService? service, IReadOnlySet<string> storageGroups)
    {
        catalog ??= new AppStack();
        var hasStorage = service is not null && storageGroups.Contains(service.ResourceGroup);
        return catalog with
        {
            Hosting = Hosting(service)
                ?? Blank(catalog.Hosting)
                ?? technologies?.FirstOrDefault(t => t.StartsWith("Azure ", StringComparison.OrdinalIgnoreCase)),
            Frontend = Blank(catalog.Frontend),
            Backend = Blank(catalog.Backend),
            Data = Blank(catalog.Data) ?? (hasStorage ? "Azure Storage" : null),
            Ai = Blank(catalog.Ai),
            SignIn = Blank(catalog.SignIn),
            Phone = Blank(catalog.Phone),
            Delivery = Blank(catalog.Delivery),
        };
    }

    /// <summary>"Azure App Service · Linux, free tier" — plain words, never a SKU code alone.</summary>
    private static string? Hosting(WebService? service)
    {
        if (service is null) return null;
        if (service.ResourceType.EndsWith("/staticSites", StringComparison.OrdinalIgnoreCase))
            return "Azure Static Web Apps";
        if (!service.ResourceType.EndsWith("/sites", StringComparison.OrdinalIgnoreCase))
            return null;

        var os = service.Kind?.Contains("linux", StringComparison.OrdinalIgnoreCase) == true ? "Linux" : "Windows";
        var tier = service.AppServicePlanSku?.ToUpperInvariant() switch
        {
            "F1" => ", free tier",
            "D1" => ", shared tier",
            null or "" => "",
            var sku => $", {sku}",
        };
        return $"Azure App Service · {os}{tier}";
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
