using System;
using System.Collections.Generic;
using System.Linq;
using Google.Protobuf.Reflection;
using ExcelDb.Protocol;

namespace ExcelDb.Schema
{
    public enum RefKind
    {
        /// <summary>Unconstrained: may target any ASSET table.</summary>
        Any = 0,
        /// <summary>Fixed target table.</summary>
        Fixed = 1,
        /// <summary>Target table must implement the named group.</summary>
        Group = 2,
    }

    public readonly struct RefConstraint
    {
        public readonly RefKind Kind;
        public readonly string Target;

        public RefConstraint(RefKind kind, string target)
        {
            Kind = kind;
            Target = target;
        }

        public static readonly RefConstraint Any = new RefConstraint(RefKind.Any, string.Empty);
    }

    public sealed class TableSchema
    {
        public TableId Id { get; }
        public string Name { get; }
        public TableKind Kind { get; }
        public MessageDescriptor Descriptor { get; }
        public IReadOnlyList<FieldDescriptor> KeyFields { get; }
        public IReadOnlyList<string> Implements { get; }

        internal TableSchema(
            TableId id, string name, TableKind kind, MessageDescriptor descriptor,
            IReadOnlyList<FieldDescriptor> keyFields, IReadOnlyList<string> implements)
        {
            Id = id;
            Name = name;
            Kind = kind;
            Descriptor = descriptor;
            KeyFields = keyFields;
            Implements = implements;
        }
    }

    /// <summary>
    /// Immutable view over all table schemas, built once from generated file descriptors.
    /// </summary>
    public sealed class SchemaRegistry
    {
        readonly Dictionary<int, TableSchema> _byNumber = new Dictionary<int, TableSchema>();
        readonly Dictionary<string, TableSchema> _byName = new Dictionary<string, TableSchema>(StringComparer.Ordinal);
        readonly Dictionary<Type, TableSchema> _byClrType = new Dictionary<Type, TableSchema>();
        readonly Dictionary<string, HashSet<int>> _groups = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        // Constraints are declared on fields, which may live on embedded (non-table) messages
        // as well - so they are registered globally per field descriptor.
        readonly Dictionary<FieldDescriptor, RefConstraint> _refConstraints = new Dictionary<FieldDescriptor, RefConstraint>();

        public IReadOnlyCollection<TableSchema> Tables => _byNumber.Values;

        public static SchemaRegistry FromFiles(params FileDescriptor[] files)
        {
            var registry = new SchemaRegistry();
            foreach (var file in files)
            foreach (var message in file.MessageTypes)
            {
                registry.TryRegister(message);
                registry.CollectConstraints(message);
            }
            registry.ValidateConstraints();
            return registry;
        }

        void CollectConstraints(MessageDescriptor message)
        {
            foreach (var field in message.Fields.InDeclarationOrder())
            {
                var refTable = field.GetOptions()?.GetExtension(OptionsExtensions.RefTable) ?? string.Empty;
                var refGroup = field.GetOptions()?.GetExtension(OptionsExtensions.RefGroup) ?? string.Empty;
                if (refTable.Length == 0 && refGroup.Length == 0)
                    continue;
                if (!MessageOps.IsRowRefField(field))
                    throw new SchemaException($"'{message.Name}.{field.Name}' declares a ref constraint but is not a RowRef field.");
                if (refTable.Length > 0 && refGroup.Length > 0)
                    throw new SchemaException($"'{message.Name}.{field.Name}' declares both ref_table and ref_group.");
                _refConstraints[field] = refTable.Length > 0
                    ? new RefConstraint(RefKind.Fixed, refTable)
                    : new RefConstraint(RefKind.Group, refGroup);
            }
            foreach (var nested in message.NestedTypes)
                CollectConstraints(nested);
        }

        void TryRegister(MessageDescriptor message)
        {
            var options = message.GetOptions();
            var table = options?.GetExtension(OptionsExtensions.Table);
            if (table == null || table.Kind == TableKind.Unspecified)
                return;

            if (table.Id <= 0)
                throw new SchemaException($"Table '{message.Name}' must declare a positive stable id.");
            if (_byNumber.ContainsKey(table.Id))
                throw new SchemaException($"Table id {table.Id} is used by both '{_byNumber[table.Id].Name}' and '{message.Name}'.");
            if (_byName.ContainsKey(message.Name))
                throw new SchemaException($"Table name '{message.Name}' is declared twice.");

            var keyFields = message.Fields.InDeclarationOrder()
                .Select(f => (Field: f, Order: f.GetOptions()?.GetExtension(OptionsExtensions.Key) ?? 0))
                .Where(p => p.Order > 0)
                .OrderBy(p => p.Order)
                .Select(p => p.Field)
                .ToArray();

            if (table.Kind == TableKind.Asset && keyFields.Length == 0)
                throw new SchemaException($"ASSET table '{message.Name}' must declare at least one key field.");

            var schema = new TableSchema(
                new TableId(table.Id), message.Name, table.Kind, message,
                keyFields, table.Implements.ToArray());

            _byNumber.Add(table.Id, schema);
            _byName.Add(message.Name, schema);
            _byClrType.Add(message.ClrType, schema);
            foreach (var group in table.Implements)
            {
                if (!_groups.TryGetValue(group, out var members))
                    _groups.Add(group, members = new HashSet<int>());
                members.Add(table.Id);
            }
        }

        void ValidateConstraints()
        {
            foreach (var pair in _refConstraints)
            {
                var c = pair.Value;
                var where = $"{pair.Key.ContainingType.Name}.{pair.Key.Name}";
                if (c.Kind == RefKind.Fixed && !_byName.ContainsKey(c.Target))
                    throw new SchemaException($"'{where}' references unknown table '{c.Target}'.");
                if (c.Kind == RefKind.Group && !_groups.ContainsKey(c.Target))
                    throw new SchemaException($"'{where}' references group '{c.Target}' which no table implements.");
            }
        }

        public RefConstraint ConstraintOf(FieldDescriptor field) =>
            _refConstraints.TryGetValue(field, out var c) ? c : RefConstraint.Any;

        public TableSchema Get(TableId id) =>
            _byNumber.TryGetValue(id.Number, out var s)
                ? s
                : throw new SchemaException($"Unknown table number {id.Number}.");

        public bool TryGet(TableId id, out TableSchema schema) => _byNumber.TryGetValue(id.Number, out schema!);

        public TableSchema Get(string name) =>
            _byName.TryGetValue(name, out var s)
                ? s
                : throw new SchemaException($"Unknown table '{name}'.");

        public TableSchema Get<T>() => Get(typeof(T));

        public TableSchema Get(Type clrType) =>
            _byClrType.TryGetValue(clrType, out var s)
                ? s
                : throw new SchemaException($"Type '{clrType.Name}' is not a registered table.");

        public bool GroupContains(string group, TableId table) =>
            _groups.TryGetValue(group, out var members) && members.Contains(table.Number);
    }
}
