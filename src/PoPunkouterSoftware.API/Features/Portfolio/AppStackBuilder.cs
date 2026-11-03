using PoPunkouterSoftware.Shared;

namespace PoPunkouterSoftware.API;

/// <summary>
/// One app's row in the home page's stack matrix: the curated <c>stack</c> block from
/// <c>apps.json</c>, trimmed. Values are short labels; the longer wording is in
/// <see cref="AppStack.Details"/> for the cell's hint. Data falls back to "Azure Storage"
/// when the scan finds a storage account in the app's own resource group — that the account
/// exists is visible to ARM, but whether the app uses tables or blobs is not. (A Hosting
/// column, read from the scan, went on 2026-11-03: owner's call.)
/// </summary>
internal static class AppStackBuilder
{
    internal static AppStack Build(AppStack? catalog, WebService? service, IReadOnlySet<string> storageGroups)
    {
        catalog ??= new AppStack();
        var details = new Dictionary<string, string>(catalog.Details ?? [], StringComparer.OrdinalIgnoreCase);
        var hasStorage = service is not null && storageGroups.Contains(service.ResourceGroup);

        var data = Blank(catalog.Data);
        if (data is null && hasStorage)
        {
            data = "Azure Storage";
            details.TryAdd("data", "The Azure scan found a storage account in this app's resource group");
        }

        // No sign-in listed means no login — web app or game — so say "None", and drop a hint
        // left behind from when it had one, which would contradict the cell.
        var signIn = Blank(catalog.SignIn);
        if (signIn is null)
        {
            signIn = "None";
            details.Remove("signIn");
        }

        return catalog with
        {
            Frontend = Blank(catalog.Frontend),
            Backend = Blank(catalog.Backend),
            Extras = Blank(catalog.Extras),
            Realtime = Blank(catalog.Realtime),
            Data = data,
            Ai = Blank(catalog.Ai),
            Physics = Blank(catalog.Physics),
            Training = Blank(catalog.Training),
            TrainingPath = Blank(catalog.TrainingPath)?.Trim('/'),
            // "None" in words, not a dash (owner's call, 2026-11-03 — some apps will drop
            // login; the games have none). Sign-in is a column in both tables.
            SignIn = signIn,
            Delivery = Blank(catalog.Delivery),
            Details = details,
            AiServices = (catalog.AiServices ?? []).Where(a => !string.IsNullOrWhiteSpace(a.Name)).ToList(),
        };
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
