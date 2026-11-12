namespace PoPunkouterSoftware.Client;

/// <summary>
/// Represents an Azure resource that is safe to remove (orphaned, unused, or superceded).
/// Used only by the client-side Dashboard UI for display purposes; never serialized over the wire.
/// </summary>
public record SafeToRemoveItem
{
    public string Name { get; init; } = "";
    public string? ResourceGroup { get; init; }
    public string Type { get; init; } = "";
    public string Source { get; init; } = "";
    public string Reason { get; init; } = "";
    public string Confidence { get; init; } = "";
    public string? EstimatedMonthlyCost { get; init; }
    public string? Command { get; init; }
}
