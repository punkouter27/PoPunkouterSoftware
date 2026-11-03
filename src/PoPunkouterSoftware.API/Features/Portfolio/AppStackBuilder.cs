using PoPunkouterSoftware.Shared;

namespace PoPunkouterSoftware.API;

/// <summary>
/// One app's row in the home page's stack matrix: the curated <c>stack</c> block from
/// <c>apps.json</c>, with the two facts the Azure scan knows better filled in from it.
/// Hosting always comes from the scan when the app was found, because the catalog can only
/// repeat what someone typed and a plan move would leave it lying. Values are short labels;
/// the longer wording goes into <see cref="AppStack.Details"/> for the cell's hint. Data falls back to
/// "Azure Storage" when the app's own resource group holds a storage account — that the account
/// exists is visible to ARM, but whether the app uses tables or blobs is not.
/// </summary>
internal static class AppStackBuilder
{
    internal static AppStack Build(
        AppStack? catalog, IReadOnlyList<string>? technologies, WebService? service, IReadOnlySet<string> storageGroups)
    {
        catalog ??= new AppStack();
        var details = new Dictionary<string, string>(catalog.Details ?? [], StringComparer.OrdinalIgnoreCase);
        var hasStorage = service is not null && storageGroups.Contains(service.ResourceGroup);

        var hosting = Hosting(service);
        if (hosting is { } scanned)
            details["hosting"] = scanned.Detail;
        var data = Blank(catalog.Data);
        if (data is null && hasStorage)
        {
            data = "Azure Storage";
            details.TryAdd("data", "The Azure scan found a storage account in this app's resource group");
        }

        return catalog with
        {
            Hosting = hosting?.Short
                ?? Blank(catalog.Hosting)
                ?? Shorten(technologies?.FirstOrDefault(t => t.StartsWith("Azure ", StringComparison.OrdinalIgnoreCase))),
            Frontend = Blank(catalog.Frontend),
            Backend = Blank(catalog.Backend),
            Data = data,
            Ai = Blank(catalog.Ai),
            SignIn = Blank(catalog.SignIn),
            Phone = Blank(catalog.Phone),
            Delivery = Blank(catalog.Delivery),
            Details = details,
            AiServices = (catalog.AiServices ?? []).Where(a => !string.IsNullOrWhiteSpace(a.Name)).ToList(),
        };
    }

    /// <summary>"App Service · Linux" for the column, "Azure App Service on Linux, free tier (F1)" for the hint.</summary>
    private static (string Short, string Detail)? Hosting(WebService? service)
    {
        if (service is null) return null;
        if (service.ResourceType.EndsWith("/staticSites", StringComparison.OrdinalIgnoreCase))
            return ("Static Web Apps", "Azure Static Web Apps: static files served from Azure's edge, no server of its own");
        if (!service.ResourceType.EndsWith("/sites", StringComparison.OrdinalIgnoreCase))
            return null;

        var os = service.Kind?.Contains("linux", StringComparison.OrdinalIgnoreCase) == true ? "Linux" : "Windows";
        var tier = service.AppServicePlanSku?.ToUpperInvariant() switch
        {
            "F1" => ", free tier (F1)",
            "D1" => ", shared tier (D1)",
            null or "" => "",
            var sku => $", {sku} plan",
        };
        return ($"App Service · {os}", $"Azure App Service on {os}{tier}");
    }

    /// <summary>"Azure App Service" → "App Service": the column header already says it is hosting on Azure.</summary>
    private static string? Shorten(string? technology) =>
        technology is null ? null : technology.StartsWith("Azure ", StringComparison.OrdinalIgnoreCase) ? technology[6..] : technology;

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
