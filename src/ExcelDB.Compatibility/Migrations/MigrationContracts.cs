using ExcelDb.Core.Values;

namespace ExcelDb.Compatibility.Migrations;

public readonly record struct MigrationKey(string Id, int Version)
{
    public override string ToString() => $"{Id}@{Version}";
}

public enum MigrationDisposition : byte
{
    Converted,
    Skipped,
    Error,
}

public readonly record struct MigrationContext(
    int TableId,
    IReadOnlyList<int> SourceFieldPath,
    IReadOnlyList<int> TargetFieldPath)
{
    public int TargetTableId { get; init; } = TableId;

    public string WorkbookId { get; init; } = string.Empty;

    public string RowIdentity { get; init; } = string.Empty;
}

public readonly record struct MigrationResult(
    MigrationDisposition Disposition,
    CanonicalValue Value,
    string? Message = null)
{
    public static MigrationResult Converted(CanonicalValue value) => new(MigrationDisposition.Converted, value);

    public static MigrationResult Skipped(string? message = null) => new(MigrationDisposition.Skipped, CanonicalValue.Missing, message);

    public static MigrationResult Error(string message) => new(MigrationDisposition.Error, CanonicalValue.Missing, message);
}

public interface ICanonicalValueMigration
{
    string Id { get; }

    int Version { get; }

    MigrationResult Transform(in MigrationContext context, CanonicalValue source);
}
