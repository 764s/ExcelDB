namespace ExcelDb.Core.Identity;

/// <summary>A session-local, generation-checked runtime handle.</summary>
public readonly record struct AssetKey(int TableId, int Slot, uint Generation)
{
    public bool IsValid => TableId > 0 && Slot >= 0 && Generation != 0;
}
