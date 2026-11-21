namespace PoPunkouterSoftware.Shared;

/// <summary>
/// <c>GET /api/diag</c>: the anonymous, Production-safe source of the <c>/diag</c> page (System status), in the
/// shape every Po app's AGENTS.md describes. Built only from health-check statuses, latencies and fixed text:
/// never a check's data dictionary (exception messages, counts, timestamps), never the masked config block,
/// never a URI, path, connection string or identifier.
/// </summary>
/// <param name="Status"><c>Healthy</c>, <c>Degraded</c> or <c>Unhealthy</c>: the worst of the checks.</param>
/// <param name="Environment">The ASP.NET Core environment name.</param>
/// <param name="Version">The build's YYMMDD stamp (assembly metadata <c>BuildVersion</c>).</param>
/// <param name="CheckedAt">When the checks ran (UTC).</param>
/// <param name="CheckedMs">How long all checks took together.</param>
/// <param name="Checks">One row per dependency, grouped <c>api</c>, <c>data</c>, <c>service</c>.</param>
public sealed record DiagReportDto(
    string Status,
    string Environment,
    string Version,
    DateTimeOffset CheckedAt,
    double CheckedMs,
    DiagCheckDto[] Checks);

/// <summary>One dependency on the <c>/diag</c> page.</summary>
/// <param name="Name">Stable machine name, e.g. <c>TableStorage</c>.</param>
/// <param name="Group"><c>api</c>, <c>data</c> or <c>service</c>: the section it is listed under.</param>
/// <param name="Label">What the page shows, e.g. "Table Storage".</param>
/// <param name="Status"><c>Healthy</c>, <c>Degraded</c>, <c>Unhealthy</c>, or <c>Off</c> (switched off on purpose: neutral, not counted in the overall status).</param>
/// <param name="Purpose">One line: what the app uses it for.</param>
/// <param name="Detail">The result in plain words, e.g. "Working — the app read from it just now."</param>
/// <param name="LatencyMs">How long the check took; 0 when nothing was probed.</param>
/// <param name="Problem">What is wrong, in plain words; null when Healthy.</param>
public sealed record DiagCheckDto(
    string Name,
    string Group,
    string Label,
    string Status,
    string Purpose,
    string Detail,
    double LatencyMs,
    string? Problem);
