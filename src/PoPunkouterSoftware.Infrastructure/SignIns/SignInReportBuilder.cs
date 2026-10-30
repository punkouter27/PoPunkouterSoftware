using PoPunkouterSoftware.Shared;

namespace PoPunkouterSoftware.Infrastructure;

/// <summary>
/// Turns raw <see cref="SignInRecord"/> rows into the <see cref="SignInReport"/> the
/// <c>/users</c> page renders. Pure — no Azure client, no clock, no configuration — so the
/// grouping, masking and trend rules are reachable from a unit test, which is the same
/// split <c>DashboardInsightsBuilder</c> and <c>DashboardDerivations</c> already use.
/// </summary>
public static class SignInReportBuilder
{
    /// <summary>Apps drawn as their own line on the trend chart. The rest fold into "Other".</summary>
    private const int MaxTrendSeries = 6;

    /// <summary>Rows in the raw feed. The workbook's own cap.</summary>
    private const int MaxRecentEvents = 100;

    private const string UnknownApp = "(unknown app)";

    private const string OtherSeries = "Other";

    /// <summary>
    /// The degraded shape: a report that says why it is empty. Every caller-visible failure
    /// — nothing configured, no credential, no role, query timed out — lands here rather
    /// than throwing, because the page must be able to explain itself.
    /// </summary>
    public static SignInReport Unavailable(DateTimeOffset now, int windowDays, string reason) => new()
    {
        GeneratedAt = now.UtcDateTime,
        WindowDays = windowDays,
        Available = false,
        Unavailable = reason,
    };

    public static SignInReport Build(
        IReadOnlyList<SignInRecord> records, DateTimeOffset now, int windowDays, bool truncated)
    {
        var clean = records
            .Select(Normalize)
            .Where(r => r.Timestamp != default)
            .ToList();

        var people = BuildPeople(clean);

        return new SignInReport
        {
            GeneratedAt = now.UtcDateTime,
            WindowDays = windowDays,
            Available = true,
            Truncated = truncated,
            TotalPeople = people.Count,
            TotalSignIns = clean.Count,
            TotalApps = clean.Select(r => r.App).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            People = people,
            Apps = BuildReach(clean),
            Trend = BuildTrend(clean, now, windowDays),
            Recent = BuildRecent(clean),
        };
    }

    /// <summary>
    /// Joins each app's reach to its own resource group's 30-day spend, from the last STORED
    /// scan — no Azure call. The app name in a sign-in record is the app's friendly name, so it
    /// is matched to a scanned service the same way the portfolio matches catalog entries, and
    /// the service's resource group is the cost key.
    ///
    /// <para>A resource group absent from the cost series spent nothing (Cost Management omits
    /// zero rows), so it reads as $0 rather than unknown. An app that matches no scanned service
    /// stays null — that genuinely is unknown.</para>
    /// </summary>
    public static SignInReport AttachCosts(SignInReport signIns, AzureReport? inventory)
    {
        var groups = inventory?.Cost?.ResourceGroups;
        var services = inventory?.WebServices?.Services;
        if (!signIns.Available || groups is not { Count: > 0 } || services is not { Count: > 0 })
            return signIns;

        var costByGroup = groups
            .GroupBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Total), StringComparer.OrdinalIgnoreCase);

        return signIns with
        {
            Apps = signIns.Apps.Select(a =>
            {
                var key = NameMatching.Normalize(a.App);
                var service = services.FirstOrDefault(s =>
                    NameMatching.Normalize(s.FriendlyName) == key
                    || NameMatching.Normalize(s.Name) == key
                    || NameMatching.Normalize(s.Name) == "app" + key);
                if (service is null || string.IsNullOrWhiteSpace(service.ResourceGroup))
                    return a;

                var cost = Math.Round(costByGroup.GetValueOrDefault(service.ResourceGroup), 2);
                return a with
                {
                    Cost30Days = cost,
                    CostPerPerson = a.People > 0 ? Math.Round(cost / a.People, 2) : null,
                };
            }).ToList(),
        };
    }

    /// <summary>
    /// Blank fields are real: an identity provider that supplies no display name sends an
    /// empty string, and a row with no app name would otherwise group under the empty key.
    /// </summary>
    private static SignInRecord Normalize(SignInRecord r) => r with
    {
        App = Blank(r.App) ? UnknownApp : r.App.Trim(),
        UserId = r.UserId?.Trim() ?? "",
        Email = r.Email?.Trim() ?? "",
        DisplayName = r.DisplayName?.Trim() ?? "",
        Provider = r.Provider?.Trim() ?? "",
    };

    private static bool Blank(string? s) => string.IsNullOrWhiteSpace(s);

    /// <summary>
    /// Email first, object id only as a fallback. The workbook groups on <c>UserId</c> and
    /// that is wrong here: the same person gets a different object id per app registration,
    /// so one real address renders as two people with half the sign-ins each. The address is
    /// the identity a reader of this page actually means by "who".
    /// </summary>
    private static string IdentityKey(SignInRecord r) =>
        !Blank(r.Email) ? r.Email.ToLowerInvariant()
        : !Blank(r.UserId) ? "id:" + r.UserId.ToLowerInvariant()
        : "name:" + r.DisplayName.ToLowerInvariant();

    private static List<SignInPerson> BuildPeople(List<SignInRecord> records) =>
        records
            .GroupBy(IdentityKey, StringComparer.Ordinal)
            .Select(g => new SignInPerson
            {
                DisplayName = BestDisplayName(g),
                MaskedEmail = SecretMasking.MaskEmail(
                    g.Select(r => r.Email).FirstOrDefault(e => !Blank(e))),
                Apps = g.Select(r => r.App)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(a => a, StringComparer.OrdinalIgnoreCase)
                        .ToList(),
                Providers = g.Select(r => r.Provider)
                             .Where(p => !Blank(p))
                             .Distinct(StringComparer.OrdinalIgnoreCase)
                             .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                             .ToList(),
                SignIns = g.Count(),
                FirstSeen = g.Min(r => r.Timestamp),
                LastSeen = g.Max(r => r.Timestamp),
            })
            .OrderByDescending(p => p.LastSeen)
            .ThenBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Prefers a name that is not just the address repeated — providers vary on this within
    /// one set of rows, so the best of them wins rather than the first. When no real name was
    /// ever supplied the address is used, and MASKED: leaving it bare here would publish in
    /// the name column exactly what the email column takes care to elide.
    /// </summary>
    private static string BestDisplayName(IEnumerable<SignInRecord> group)
    {
        var names = group.Select(r => r.DisplayName).Where(n => !Blank(n)).ToList();

        var real = names.Find(n => !n.Contains('@'));
        if (real is not null)
            return real;

        if (names.Count > 0)
            return SecretMasking.MaskEmail(names[0]);

        var email = group.Select(r => r.Email).FirstOrDefault(e => !Blank(e));
        return email is not null ? SecretMasking.MaskEmail(email) : "(unnamed)";
    }

    private static List<SignInAppReach> BuildReach(List<SignInRecord> records) =>
        records
            .GroupBy(r => r.App, StringComparer.OrdinalIgnoreCase)
            .Select(g => new SignInAppReach
            {
                App = g.Key,
                People = g.Select(IdentityKey).Distinct(StringComparer.Ordinal).Count(),
                SignIns = g.Count(),
                LastSeen = g.Max(r => r.Timestamp),
            })
            .OrderByDescending(a => a.People)
            .ThenByDescending(a => a.SignIns)
            .ThenBy(a => a.App, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// A dense day grid shared by every series. Sparse points would let a chart join Monday
    /// to Friday as one straight segment and draw three quiet days as a trend; the zeroes are
    /// the signal. The busiest apps get their own line and the tail is summed into "Other", so
    /// a fleet of eleven apps does not produce eleven indistinguishable lines.
    /// </summary>
    private static List<SignInTrendSeries> BuildTrend(
        List<SignInRecord> records, DateTimeOffset now, int windowDays)
    {
        if (records.Count == 0)
            return new List<SignInTrendSeries>();

        var lastDay = now.UtcDateTime.Date;
        var days = Enumerable.Range(0, windowDays)
            .Select(i => lastDay.AddDays(i - (windowDays - 1)))
            .ToList();

        var named = records
            .GroupBy(r => r.App, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Key)
            .Take(MaxTrendSeries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var buckets = records
            .GroupBy(r => (Series: named.Contains(r.App) ? r.App : OtherSeries, Day: r.Timestamp.Date))
            .ToDictionary(g => g.Key, g => g.Count());

        var seriesNames = buckets.Keys
            .Select(k => k.Series)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s == OtherSeries)                    // the catch-all sorts last
            .ThenBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return seriesNames
            .Select(name => new SignInTrendSeries
            {
                App = name,
                Points = days.Select(d => new SignInTrendPoint
                {
                    Day = d,
                    SignIns = buckets.TryGetValue((name, d), out var n) ? n : 0,
                }).ToList(),
            })
            .ToList();
    }

    private static List<SignInEvent> BuildRecent(List<SignInRecord> records) =>
        records
            .OrderByDescending(r => r.Timestamp)
            .Take(MaxRecentEvents)
            .Select(r => new SignInEvent
            {
                Timestamp = r.Timestamp,
                App = r.App,
                DisplayName = Blank(r.DisplayName) || r.DisplayName.Contains('@')
                    ? SecretMasking.MaskEmail(Blank(r.DisplayName) ? r.Email : r.DisplayName)
                    : r.DisplayName,
                MaskedEmail = SecretMasking.MaskEmail(r.Email),
                Provider = r.Provider,
            })
            .ToList();
}
