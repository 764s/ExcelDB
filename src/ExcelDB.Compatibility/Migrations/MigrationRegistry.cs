using System.Collections.Immutable;

namespace ExcelDb.Compatibility.Migrations;

public sealed class MigrationRegistry
{
    private readonly ImmutableDictionary<MigrationKey, ICanonicalValueMigration> _migrations;

    public MigrationRegistry(IEnumerable<ICanonicalValueMigration> migrations)
    {
        ArgumentNullException.ThrowIfNull(migrations);
        var builder = ImmutableDictionary.CreateBuilder<MigrationKey, ICanonicalValueMigration>();
        foreach (var migration in migrations)
        {
            ArgumentNullException.ThrowIfNull(migration);
            ArgumentException.ThrowIfNullOrWhiteSpace(migration.Id);
            if (migration.Version <= 0)
                throw new ArgumentOutOfRangeException(nameof(migrations), $"Migration '{migration.Id}' must have a positive version.");
            var key = new MigrationKey(migration.Id, migration.Version);
            if (!builder.TryAdd(key, migration))
                throw new ArgumentException($"Migration '{key}' is registered more than once.", nameof(migrations));
        }

        _migrations = builder.ToImmutable();
    }

    public IReadOnlyCollection<MigrationKey> Keys => _migrations.Keys
        .OrderBy(static key => key.Id, StringComparer.Ordinal)
        .ThenBy(static key => key.Version)
        .ToArray();

    public bool TryGet(MigrationKey key, out ICanonicalValueMigration? migration) =>
        _migrations.TryGetValue(key, out migration);

    public ICanonicalValueMigration GetRequired(MigrationKey key) =>
        _migrations.TryGetValue(key, out var migration)
            ? migration
            : throw new KeyNotFoundException($"Migration '{key}' is not registered.");
}
