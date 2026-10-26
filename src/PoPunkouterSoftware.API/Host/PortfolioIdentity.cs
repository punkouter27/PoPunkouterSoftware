using PoPunkouterSoftware.Infrastructure;

namespace PoPunkouterSoftware.API;

/// <summary>
/// The one name-based exclusion every read contract has to apply, in one place.
///
/// <para>This lived as private state on <see cref="PortfolioEndpoints"/>, which is why
/// <c>/api/diag/summary</c> never applied it and reported "PoPunkouterSoftware is
/// unavailable" — this site, in its own attention list, while serving the page that
/// rendered the claim.</para>
///
/// <para>Dropping a catalog entry is never sufficient on its own: the portfolio and the
/// ops summary are both built from the Azure scan as well as from apps.json, and this
/// resource really does exist in the subscription, so it returns through the inventory
/// path no matter what the catalog says.</para>
///
/// <para>There used to be a second, retired-app set here, holding PoRepoLineTracker after
/// its host stopped resolving. The app was redeployed (<c>app-porepolinetracker2</c>, RG
/// <c>PoRepoLineTracker</c>, answering 200), so the set was emptied and removed rather
/// than left as a rule matching nothing. Retiring an app again means deleting its Azure
/// resource — a name-based denylist here silently hides a live app from its own dashboard,
/// which is the failure this comment exists to prevent repeating.</para>
/// </summary>
internal static class PortfolioIdentity
{
    /// <summary>
    /// This site itself, which must never appear as a card in its own portfolio nor as a
    /// finding in its own dashboard.
    /// </summary>
    /// <remarks>
    /// Entries are already in <see cref="NormalizeName"/> form (letters and digits only,
    /// lower-cased), so both the Azure resource name "app-popunkoutersoftware2" and the
    /// friendly name "PoPunkouterSoftware" collapse onto one entry here.
    /// </remarks>
    private static readonly HashSet<string> SelfIdentities =
        new(StringComparer.Ordinal) { "apppopunkoutersoftware", "popunkoutersoftware" };

    /// <summary>True when any of the candidate names is this site.</summary>
    internal static bool IsSelf(params string?[] candidateNames) =>
        candidateNames.Any(n => SelfIdentities.Contains(NormalizeName(n)));

    /// <summary>Letters and digits only, lower-cased — the stable join key between the
    /// curated catalog and scanned Azure inventory.</summary>
    internal static string NormalizeName(string? value) => NameMatching.Normalize(value);
}
