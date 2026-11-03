namespace PoPunkouterSoftware.Shared;

/// <summary>
/// The major technical pieces one app is built from, in plain names a visitor can read — the
/// home page's matrix is one column per property. Hosting is read from the Azure scan; the
/// rest comes from the curated <c>stack</c> block in <c>apps.json</c>, because Azure cannot
/// see them: Key Vault, AI and App Insights all live in the shared PoShared resource group, so
/// the inventory cannot say which app uses which. Null means "none, or not listed".
///
/// <para>Every value is a SHORT label (one or two words) so a column scans at a glance;
/// the longer wording lives in <see cref="Details"/>, keyed by the camelCase property name,
/// and the matrix shows it as a hint on the cell.</para>
/// </summary>
public record AppStack
{
    /// <summary>Where it runs, e.g. "App Service · Linux". From the scan.</summary>
    public string? Hosting { get; init; }
    public string? Frontend { get; init; }
    public string? Backend { get; init; }
    /// <summary>Catalog value; falls back to "Azure Storage" when the scan finds a storage account in the app's resource group.</summary>
    public string? Data { get; init; }
    public string? Ai { get; init; }
    public string? SignIn { get; init; }
    /// <summary>How it reaches a phone: an installable PWA, a store build, or a mobile web page.</summary>
    public string? Phone { get; init; }
    /// <summary>How a change ships, e.g. "GitHub Actions".</summary>
    public string? Delivery { get; init; }
    /// <summary>The full wording behind a short label: "ai" → "Azure OpenAI: GPT-5 mini judges…".</summary>
    public Dictionary<string, string> Details { get; init; } = new();
}
