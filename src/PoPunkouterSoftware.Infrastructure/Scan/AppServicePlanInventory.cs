using PoPunkouterSoftware.Shared;

namespace PoPunkouterSoftware.Infrastructure;

public static class AppServicePlanInventory
{
    public static List<AppServicePlanInventoryEntry> BuildPoSharedPlanInventory(
        IEnumerable<ResourceDetail> resources,
        IEnumerable<WebService> webServices)
    {
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(webServices);

        var appCounts = webServices
            .Where(service => !string.IsNullOrWhiteSpace(service.AppServicePlan))
            .GroupBy(service => service.AppServicePlan!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        return resources
            .Where(resource =>
                string.Equals(resource.ResourceGroup, "PoShared", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(resource.Type, "Microsoft.Web/serverFarms", StringComparison.OrdinalIgnoreCase))
            .Select(resource => new AppServicePlanInventoryEntry
            {
                Name = resource.Name,
                ResourceGroup = resource.ResourceGroup,
                Location = resource.Location,
                Sku = resource.Sku,
                Type = resource.Type,
                AppCount = appCounts.TryGetValue(resource.Name, out var count) ? count : 0,
            })
            .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
