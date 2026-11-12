namespace PoPunkouterSoftware.Client;

/// <summary>
/// One row of the Azure dashboard's resource explorer. Built by
/// DashboardDerivations.BuildResourceExplorerItems from the raw report and rendered by the
/// AzureResourceExplorer section component; never serialized over the wire.
/// <c>Detail</c> is the concrete finding behind <c>Risk</c> ("HTTP/2 disabled", a downtime
/// cause and its fix), printed under the concern. It replaced the drawer's Evidence
/// disclosures, which repeated the same findings in a second list below the table.
/// </summary>
public sealed record ResourceExplorerItem(
    string Name,
    string Type,
    string ResourceGroup,
    string Location,
    string Sku,
    string Status,
    string Risk,
    int Requests7d,
    int Errors7d,
    int? ResponseTimeMs,
    string? Detail = null
);
