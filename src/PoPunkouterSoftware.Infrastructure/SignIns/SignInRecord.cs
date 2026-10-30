namespace PoPunkouterSoftware.Infrastructure;

/// <summary>
/// One row of the Application Insights <c>traces</c> query, before any grouping or masking.
/// Its own file rather than a nested type because it crosses a component boundary:
/// <see cref="SignInQueryService"/> produces it and <see cref="SignInReportBuilder"/> — a
/// pure function with no Azure dependency, so it can be tested without one — consumes it.
/// </summary>
public sealed record SignInRecord(
    DateTime Timestamp,
    string App,
    string UserId,
    string Email,
    string DisplayName,
    string Provider);
