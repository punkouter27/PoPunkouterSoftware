namespace PoPunkouterSoftware.Shared;

/// <summary>
/// Tonight's GPU queue as the /global/queue planner keeps it in this browser's localStorage
/// (<c>po.global.queue</c>). Only job ids and hours: the commands always come from the catalog.
/// </summary>
public sealed class GlobalQueuePlanDto
{
    public string Name { get; set; } = "tonight";
    public int CoolTemp { get; set; } = 70;
    public int GapMin { get; set; } = 5;
    public bool RefreshAfter { get; set; } = true;
    public List<LocalHelperQueueItem> Jobs { get; set; } = [];
}

/// <summary>
/// Brain-vault stars kept in this browser (<c>po.global.vaultGold</c>) when the local helper is
/// not connected: path → note, or null for "unstarred here although vault_gold.json stars it".
/// </summary>
public sealed class GlobalVaultGoldLocalDto
{
    public Dictionary<string, string?> Gold { get; set; } = [];
}
