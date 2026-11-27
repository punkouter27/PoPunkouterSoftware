namespace PoPunkouterSoftware.Shared;

/// <summary>
/// Everything <c>/users</c> renders, in one payload — the wire form of the "Po Sign-ins"
/// Azure Monitor workbook in the PoShared resource group.
/// </summary>
/// <remarks>
/// <para>Every Po app writes the same <c>UserSignedIn</c> record into the shared
/// Application Insights component, so one query answers across the whole estate. The
/// records land in the <c>traces</c> table rather than <c>customEvents</c> because the apps
/// export through OpenTelemetry, where custom events are not part of the pipeline.</para>
/// <para><c>Available == false</c> is a designed path, not an error: no configured
/// component, no credential, no Monitoring Reader role, or a query that timed out all
/// produce a report with <see cref="Unavailable"/> set and empty lists, never a 500. The
/// page says why it has nothing rather than showing a spinner forever.</para>
/// <para>Zero sign-ins with <c>Available == true</c> is a different and equally real
/// answer — "nobody has signed in to anything in this window" — and must not be rendered
/// as a fault.</para>
/// </remarks>
public sealed record SignInReport
{
    public DateTime GeneratedAt { get; init; }

    /// <summary>Days of history the figures cover. Capped at the component's 90-day retention.</summary>
    public int WindowDays { get; init; }

    public bool Available { get; init; }

    /// <summary>Plain-English reason the figures are missing. Null whenever <see cref="Available"/>.</summary>
    public string? Unavailable { get; init; }

    /// <summary>Distinct people, not distinct sign-in identities — see <see cref="SignInPerson"/>.</summary>
    public int TotalPeople { get; init; }

    public int TotalSignIns { get; init; }

    public int TotalApps { get; init; }

    /// <summary>Newest sign-in first.</summary>
    public List<SignInPerson> People { get; init; } = new();

    /// <summary>Widest reach first.</summary>
    public List<SignInAppReach> Apps { get; init; } = new();

    /// <summary>One series per app over a dense day grid — see <see cref="SignInTrendSeries"/>.</summary>
    public List<SignInTrendSeries> Trend { get; init; } = new();

    /// <summary>Newest first, capped at 100.</summary>
    public List<SignInEvent> Recent { get; init; } = new();

    /// <summary>The query hit its row cap, so the figures are a floor rather than a total.</summary>
    public bool Truncated { get; init; }
}

/// <summary>
/// One human being across every app they have signed in to.
/// </summary>
/// <remarks>
/// Grouped by email address, deliberately NOT by the <c>UserId</c> claim the workbook
/// groups on. The same person arrives with a different object id per app registration —
/// punkouter26@gmail.com appears under two ids in the live data — and grouping on the id
/// renders one person as two half-populated rows. The address is the stable identity here.
/// </remarks>
public sealed record SignInPerson
{
    /// <summary>Never a bare address: an identity provider that supplies no name sends the
    /// email as the display name, so this is masked too when it looks like one.</summary>
    public string DisplayName { get; init; } = "";

    /// <summary>Local part elided, domain kept — see <c>SecretMasking.MaskEmail</c>.</summary>
    public string MaskedEmail { get; init; } = "";

    /// <summary>Coarse browser/OS and masked subnet for anonymous visits (e.g. "Chrome · Windows · 73.12.*.*"). Never a raw IP.</summary>
    public string ClientInfo { get; init; } = "";

    public List<string> Apps { get; init; } = new();

    public List<string> Providers { get; init; } = new();

    public int SignIns { get; init; }

    public DateTime FirstSeen { get; init; }

    public DateTime LastSeen { get; init; }
}

/// <summary>How far one app's reach extends: distinct people, total sign-ins, most recent.</summary>
public sealed record SignInAppReach
{
    public string App { get; init; } = "";

    public int People { get; init; }

    public int SignIns { get; init; }

    public DateTime LastSeen { get; init; }

    /// <summary>
    /// 30-day spend of the app's own resource group, from the last stored scan. Null when the
    /// app could not be matched to a scanned service or the scan carried no per-group costs.
    /// Shared-plan compute billed to another group is not split out.
    /// </summary>
    public double? Cost30Days { get; init; }

    /// <summary><see cref="Cost30Days"/> ÷ <see cref="People"/>.</summary>
    public double? CostPerPerson { get; init; }
}

/// <summary>One app's daily sign-in count over the window.</summary>
/// <remarks>
/// The points are a DENSE grid — every day in the window is present, zeroes included — so
/// every series shares one category axis and a quiet week reads as a flat line rather than
/// as a gap the chart silently closes up.
/// </remarks>
public sealed record SignInTrendSeries
{
    public string App { get; init; } = "";

    public List<SignInTrendPoint> Points { get; init; } = new();
}

public sealed record SignInTrendPoint
{
    /// <summary>
    /// The day itself, UTC midnight — a date, not a pre-formatted label. A chart given string
    /// categories draws a tick per category and honours neither <c>Step</c> nor
    /// <c>TickDistance</c>, so 30 day-labels (let alone 90) collapsed into an unreadable
    /// smear. Handed a date, the axis thins its own ticks and formats them itself.
    /// </summary>
    public DateTime Day { get; init; }

    public int SignIns { get; init; }
}

/// <summary>A single sign-in, for the raw reverse-chronological feed.</summary>
public sealed record SignInEvent
{
    public DateTime Timestamp { get; init; }

    public string App { get; init; } = "";

    public string DisplayName { get; init; } = "";

    public string MaskedEmail { get; init; } = "";

    /// <summary>Coarse browser/OS and masked subnet for anonymous visits. Never a raw IP.</summary>
    public string ClientInfo { get; init; } = "";

    public string Provider { get; init; } = "";
}
