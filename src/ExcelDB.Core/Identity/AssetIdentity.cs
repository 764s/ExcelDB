namespace ExcelDb.Core.Identity;

/// <summary>Cross-source canonical identity: stable table id plus stable workbook row guid.</summary>
public readonly record struct AssetIdentity
{
    public AssetIdentity(int tableId, RowGuid rowGuid)
    {
        if (tableId <= 0)
            throw new ArgumentOutOfRangeException(nameof(tableId), "A table id must be positive.");
        if (rowGuid.IsEmpty)
            throw new ArgumentException("A row guid must be non-zero.", nameof(rowGuid));

        TableId = tableId;
        RowGuid = rowGuid;
    }

    public int TableId { get; }

    public RowGuid RowGuid { get; }

    public override string ToString() => $"{TableId}:{RowGuid}";

    public static bool TryParse(string? value, out AssetIdentity identity)
    {
        identity = default;
        if (value is null)
            return false;

        var separator = value.IndexOf(':');
        if (separator <= 0
            || !int.TryParse(value.AsSpan(0, separator), out var tableId)
            || !RowGuid.TryParse(value[(separator + 1)..], out var rowGuid)
            || tableId <= 0)
        {
            return false;
        }

        identity = new AssetIdentity(tableId, rowGuid);
        return true;
    }
}
