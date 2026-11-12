namespace PoPunkouterSoftware.Client;

/// <summary>
/// One ranked row of the Azure dashboard's priority queue: a single thing worth acting on,
/// with the evidence for why. Built by AzureDashboard.DerivedViews from the raw report and
/// rendered by the AzurePriorityQueue section component; never serialized over the wire.
/// </summary>
public sealed record PriorityQueueItem(
    string Actionability,
    string Item,
    string Source,
    int ImpactScore,
    string Confidence,
    string Reason,
    string Owner,
    string Environment,
    string? Command
);
