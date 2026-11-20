using System.Text.RegularExpressions;

namespace PoPunkouterSoftware.Infrastructure;

/// <summary>
/// Cross-cutting helper for rendering secret-bearing configuration values in
/// diagnostics output without disclosing them.
/// </summary>
public static partial class SecretMasking
{
    public static string MaskValue(string? v) =>
        string.IsNullOrWhiteSpace(v) ? "(not set)" :
        v.Length <= 8 ? "****" :
        v[..4] + new string('*', Math.Min(v.Length - 8, 20)) + v[^4..];

    /// <summary>
    /// Elides the local part of an email address, keeping the domain.
    /// </summary>
    /// <remarks>
    /// <para><c>GET /api/signins</c> is anonymous for the same reason <c>/api/diag/report</c>
    /// is — the page that reads it is WASM with no credential — and it returns the addresses
    /// of real people, including people who are not the owner. An unmasked roster would
    /// publish third-party PII to any visitor of a site that has no login at all.</para>
    /// <para>The domain survives on purpose: "someone at a company tried PoTraffic" and
    /// "another throwaway gmail" are the two readings the page exists to support, and neither
    /// survives masking the whole address. The local part is what identifies the individual,
    /// so that is what goes.</para>
    /// <para>The LAST character of the local part survives alongside the first two, and that
    /// is not decoration. Keeping only a prefix made punkouter26@gmail.com and
    /// punkouter27@gmail.com render as the same string — two different people, same masked
    /// address, same display name, two rows on the roster that nothing on screen could tell
    /// apart. A mask that collapses distinct identities is worse than no mask, because the
    /// page then quietly asserts something false. The remainder is still elided, so what is
    /// left is not an address anyone can write to.</para>
    /// <para>The star run is capped so a long local part cannot be counted back to its
    /// original length, and so the roster column does not stretch.</para>
    /// </remarks>
    public static string MaskEmail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "(unknown)";

        var trimmed = value.Trim();
        // LastIndexOf: the local part of an address may legally contain '@' when quoted, and
        // the domain never does, so the last one is the real separator.
        var at = trimmed.LastIndexOf('@');
        return at <= 0
            ? MaskLocalPart(trimmed)
            : MaskLocalPart(trimmed[..at]) + trimmed[at..];
    }

    private static string MaskLocalPart(string local) => local.Length switch
    {
        // Too short to reveal anything from without revealing most of it.
        <= 3 => new string('*', 3),
        // One char each end: enough to separate two neighbouring addresses, not enough to read.
        <= 5 => local[..1] + new string('*', local.Length - 2) + local[^1..],
        _ => local[..2] + new string('*', Math.Min(local.Length - 3, 8)) + local[^1..],
    };

    /// <summary>
    /// Replaces the subscription GUID inside every ARM resource id with a fixed placeholder,
    /// leaving the rest of the id (resource group, provider, type, name) intact.
    /// </summary>
    /// <remarks>
    /// <para><c>GET /api/diag/report</c> is anonymous — it has to be, because the Advanced
    /// diagnostics panel on /azure is a WASM client with no credential — and it returns the
    /// whole inventory: every resource id, and with it the subscription id repeated a few
    /// hundred times. docs/architecture.md records that exact payload leaking once already, from
    /// <c>wwwroot/data/azure-full-report.json</c> under a served directory. Moving it behind
    /// a route did not change who can read it.</para>
    /// <para>The subscription id is the one field in that payload that is a credential-shaped
    /// identifier rather than a description of the estate: it is what an attacker needs to
    /// address the subscription in an ARM call or a support-channel pretext. Resource names
    /// and groups are already public — they are in the hostnames the portfolio links to.
    /// So this masks the id and keeps the rest, which is the difference between a report the
    /// UI can render and one it cannot.</para>
    /// <para>Applied to the serialized JSON rather than the object graph on purpose: the id
    /// appears inside free-text strings across a dozen nested record types, and a per-record
    /// <c>with</c>-expression rewrite would silently miss whichever one is added next.</para>
    /// </remarks>
    public static string MaskSubscriptionIds(string json) =>
        string.IsNullOrEmpty(json) ? json : SubscriptionIdPattern().Replace(json, "/subscriptions/****");

    // Case-insensitive: ARM ids are returned with inconsistent casing on the segment names.
    [GeneratedRegex(
        @"/subscriptions/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}",
        RegexOptions.IgnoreCase)]
    private static partial Regex SubscriptionIdPattern();
}
