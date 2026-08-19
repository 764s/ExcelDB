using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace ExcelDb.Editor.Model
{
    /// <summary>
    /// Converts generator-emitted table/field metadata into the host-neutral editor model.
    /// The input contract is consumed by property name so this assembly does not depend on a generated namespace.
    /// </summary>
    public sealed class GeneratedBindingFactory
    {
        private readonly object _gate = new object();
        private readonly Dictionary<CacheKey, EditorObjectBinding> _cache =
            new Dictionary<CacheKey, EditorObjectBinding>();

        public static GeneratedBindingFactory Shared { get; } = new GeneratedBindingFactory();

        public int CachedBindingCount
        {
            get
            {
                lock (_gate)
                    return _cache.Count;
            }
        }

        public EditorObjectBinding Create(
            object generatedTableBinding,
            IEnumerable allGeneratedTableBindings)
        {
            if (generatedTableBinding == null)
                throw new ArgumentNullException(nameof(generatedTableBinding));
            if (allGeneratedTableBindings == null)
                throw new ArgumentNullException(nameof(allGeneratedTableBindings));

            var tableObjects = Enumerate(allGeneratedTableBindings).ToList();
            if (!tableObjects.Any(item => ReferenceEquals(item, generatedTableBinding)))
                tableObjects.Add(generatedTableBinding);

            var catalog = tableObjects
                .Select(TableMetadata.Read)
                .OrderBy(static item => item.TableId)
                .ThenBy(static item => item.FullName, StringComparer.Ordinal)
                .ToArray();
            ValidateCatalog(catalog);

            var target = catalog.First(item => ReferenceEquals(item.Source, generatedTableBinding));
            var key = new CacheKey(generatedTableBinding, catalog.Select(static item => item.Source).ToArray());
            lock (_gate)
            {
                EditorObjectBinding? cached;
                if (_cache.TryGetValue(key, out cached))
                    return cached;

                var binding = Build(target, catalog);
                _cache.Add(key, binding);
                return binding;
            }
        }

        public void ClearCache()
        {
            lock (_gate)
                _cache.Clear();
        }

        private static EditorObjectBinding Build(TableMetadata target, IReadOnlyList<TableMetadata> catalog)
        {
            var fields = target.Fields;
            var storage = new List<EditorPropertyBinding>();
            var projections = new List<EditorPropertyProjection>();
            var topLevelPaths = fields
                .Select(static field => FirstSegment(field.PropertyPath))
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            foreach (var topLevelPath in topLevelPaths)
            {
                var group = fields
                    .Where(field => string.Equals(
                        FirstSegment(field.PropertyPath),
                        topLevelPath,
                        StringComparison.Ordinal))
                    .ToArray();
                var root = group.FirstOrDefault(field => string.Equals(
                    field.PropertyPath,
                    topLevelPath,
                    StringComparison.Ordinal)) ?? group[0];
                var topLevelMember = FirstSegment(root.MemberPath);
                var rootType = ResolveDirectMemberType(target.ClrType, topLevelMember);
                var storageKind = InferStorageKind(rootType);
                var storageIndex = storage.Count;
                storage.Add(EditorPropertyBinding.CreateFromMemberPath(
                    root.FieldIdPath[0],
                    new[] { root.FieldIdPath[0] },
                    topLevelPath,
                    topLevelMember,
                    target.ClrType,
                    storageKind,
                    root.DisplayName,
                    tooltip: root.HeaderComment,
                    isReadOnly: string.Equals(root.PropertyPath, topLevelPath, StringComparison.Ordinal)
                        && root.KeyOrder > 0,
                    hasPresence: root.HasPresence,
                    required: root.Required,
                    keyOrder: string.Equals(root.PropertyPath, topLevelPath, StringComparison.Ordinal)
                        ? root.KeyOrder
                        : 0));

                foreach (var field in group)
                {
                    var collectionAncestor = FindCollectionAncestor(field, fields);
                    if (collectionAncestor != null
                        && string.Equals(collectionAncestor.Shape, "Map", StringComparison.Ordinal))
                        continue;

                    var memberType = ResolveGeneratedMemberType(target.ClrType, field.MemberPath);
                    var reference = CreateReferenceAdapter(field, memberType, catalog);
                    var unsupportedReason = GetUnsupportedReason(field, memberType);
                    var kind = unsupportedReason == null
                        ? InferKind(field, memberType, reference != null)
                        : EditorPropertyKind.Unsupported;
                    var metadata = new EditorPropertyBinding(
                        field.FieldId,
                        field.FieldIdPath,
                        field.PropertyPath,
                        field.MemberPath,
                        target.ClrType,
                        memberType,
                        kind,
                        static _ => throw new InvalidOperationException("Schema projection metadata has no resident getter."),
                        static (_, __) => throw new InvalidOperationException("Schema projection metadata has no resident setter."),
                        field.DisplayName,
                        tooltip: field.HeaderComment,
                        isReadOnly: field.KeyOrder > 0 || field.IsPropertyGroup || unsupportedReason != null,
                        reference: reference,
                        hasPresence: field.HasPresence,
                        required: field.Required,
                        keyOrder: field.KeyOrder,
                        unsupportedReason: unsupportedReason);
                    projections.Add(new EditorPropertyProjection(
                        metadata,
                        storageIndex,
                        RelativeMemberPath(topLevelMember, field.MemberPath),
                        isTemplate: collectionAncestor != null));
                }
            }

            if (projections.Count == 0)
                throw new InvalidOperationException(
                    "Generated table '" + target.FullName + "' has no editor-visible property bindings.");
            return EditorObjectBinding.CreateProjected(target.ClrType, storage, projections);
        }

        private static EditorReferenceAdapter? CreateReferenceAdapter(
            FieldMetadata field,
            Type memberType,
            IReadOnlyList<TableMetadata> catalog)
        {
            var hasTable = !string.IsNullOrWhiteSpace(field.ReferenceTable);
            var hasGroup = !string.IsNullOrWhiteSpace(field.ReferenceGroup);
            if (!hasTable && !hasGroup)
                return null;
            if (hasTable == hasGroup)
                throw new InvalidOperationException(
                    "Generated field '" + field.PropertyPath
                    + "' must declare exactly one ReferenceTable or ReferenceGroup.");

            EditorReferenceConstraint constraint;
            if (hasTable)
            {
                var target = ResolveTable(field.ReferenceTable!, catalog);
                constraint = EditorReferenceConstraint.ForTable(
                    target.ToReferenceTarget(),
                    allowNull: !field.Required);
            }
            else
            {
                var group = field.ReferenceGroup!;
                var targets = catalog
                    .Where(table => table.Implements.Any(item => SymbolMatches(item, group)))
                    .Select(static table => table.ToReferenceTarget())
                    .ToArray();
                if (targets.Length == 0)
                    throw new InvalidOperationException(
                        "Reference group '" + group + "' on field '" + field.PropertyPath
                        + "' has no implementing target tables.");
                constraint = EditorReferenceConstraint.ForGroup(
                    group,
                    targets,
                    allowNull: !field.Required);
            }

            var rowRef = RowRefContract.Create(memberType, field.PropertyPath);
            return new EditorReferenceAdapter(
                constraint,
                value => rowRef.Read(value, constraint),
                value => rowRef.Write(value, constraint));
        }

        private static EditorPropertyKind InferKind(
            FieldMetadata field,
            Type memberType,
            bool isReference)
        {
            if (isReference)
                return EditorPropertyKind.Reference;
            if (field.IsPropertyGroup)
                return EditorPropertyKind.Group;

            switch (field.Shape)
            {
                case "Scalar":
                case "Enum":
                    return EditorPropertyKind.Scalar;
                case "Message":
                    return EditorPropertyKind.Object;
                case "Map":
                    throw new InvalidOperationException("Map fields must be classified as Unsupported before kind inference.");
                case "OneOfVariant":
                    return IsScalarType(memberType)
                        ? EditorPropertyKind.Scalar
                        : EditorPropertyKind.Object;
                case "RepeatedScalar":
                case "RepeatedEnum":
                case "RepeatedMessage":
                    if (!typeof(IList).IsAssignableFrom(memberType))
                        throw new InvalidOperationException(
                            "Repeated generated field '" + field.PropertyPath + "' must implement IList.");
                    return EditorPropertyKind.List;
                default:
                    throw new InvalidOperationException(
                        "Unsupported GeneratedFieldShape '" + field.Shape
                        + "' on field '" + field.PropertyPath + "'.");
            }
        }

        private static EditorPropertyKind InferStorageKind(Type memberType)
        {
            if (typeof(IList).IsAssignableFrom(memberType))
                return EditorPropertyKind.List;
            return IsScalarType(memberType)
                ? EditorPropertyKind.Scalar
                : EditorPropertyKind.Object;
        }

        private static string? GetUnsupportedReason(FieldMetadata field, Type memberType)
        {
            if (string.Equals(field.Shape, "Map", StringComparison.Ordinal))
                return "Generated map editing is not supported by the host-neutral editor model.";
            var type = Nullable.GetUnderlyingType(memberType) ?? memberType;
            if (string.Equals(type.Name, "UnityResourceRef", StringComparison.Ordinal))
                return "UnityResourceRef requires a host resource picker and is not editable by this model.";
            if (string.Equals(type.Name, "LocalizedTextRef", StringComparison.Ordinal))
                return "LocalizedTextRef requires localization-aware authoring and is not editable by this model.";
            return null;
        }

        private static bool IsScalarType(Type memberType)
        {
            var type = Nullable.GetUnderlyingType(memberType) ?? memberType;
            return type.IsPrimitive
                || type.IsEnum
                || type == typeof(string)
                || type == typeof(decimal)
                || type == typeof(Guid)
                || type == typeof(DateTime)
                || type == typeof(DateTimeOffset)
                || type == typeof(TimeSpan);
        }

        private static FieldMetadata? FindCollectionAncestor(
            FieldMetadata field,
            IReadOnlyList<FieldMetadata> fields)
        {
            FieldMetadata? nearest = null;
            foreach (var candidate in fields)
            {
                if (ReferenceEquals(candidate, field) || !candidate.IsCollectionRoot)
                    continue;
                if (IsAncestor(candidate.PropertyPath, field.PropertyPath)
                    || IsAncestor(candidate.MemberPath, field.MemberPath))
                {
                    if (nearest == null || candidate.PropertyPath.Length > nearest.PropertyPath.Length)
                        nearest = candidate;
                }
            }
            return nearest;
        }

        private static bool IsAncestor(string candidate, string path)
        {
            return path.Length > candidate.Length
                && path.StartsWith(candidate, StringComparison.Ordinal)
                && path[candidate.Length] == '.';
        }

        private static TableMetadata ResolveTable(string name, IReadOnlyList<TableMetadata> catalog)
        {
            var exact = catalog.Where(item => string.Equals(item.FullName, name, StringComparison.Ordinal)).ToArray();
            if (exact.Length == 1)
                return exact[0];

            var compatible = catalog.Where(item => SymbolMatches(item.FullName, name)).ToArray();
            if (compatible.Length == 1)
                return compatible[0];
            if (compatible.Length > 1)
                throw new InvalidOperationException("Reference table '" + name + "' is ambiguous.");
            throw new InvalidOperationException("Reference table '" + name + "' was not found.");
        }

        private static bool SymbolMatches(string candidate, string expected)
        {
            if (string.Equals(candidate, expected, StringComparison.Ordinal))
                return true;
            var separator = candidate.LastIndexOf('.');
            return separator >= 0
                && string.Equals(candidate.Substring(separator + 1), expected, StringComparison.Ordinal);
        }

        private static Type ResolveDirectMemberType(Type ownerType, string memberName)
        {
            return ResolveMember(ownerType, memberName);
        }

        private static Type ResolveGeneratedMemberType(Type ownerType, string memberPath)
        {
            if (string.IsNullOrWhiteSpace(memberPath))
                throw new InvalidOperationException("Generated MemberPath is required.");

            var current = ownerType;
            foreach (var name in memberPath.Split('.'))
            {
                current = Nullable.GetUnderlyingType(current) ?? current;
                if (typeof(IList).IsAssignableFrom(current))
                    current = GetListElementType(current);
                current = ResolveMember(current, name);
            }
            return current;
        }

        private static Type ResolveMember(Type ownerType, string memberName)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public;
            var property = ownerType.GetProperty(memberName, flags);
            if (property != null && property.GetIndexParameters().Length == 0 && property.GetMethod != null)
                return property.PropertyType;
            var field = ownerType.GetField(memberName, flags);
            if (field != null)
                return field.FieldType;
            throw new InvalidOperationException(
                "Generated member '" + memberName + "' was not found on '" + ownerType.FullName + "'.");
        }

        private static Type GetListElementType(Type listType)
        {
            if (listType.IsArray)
                return listType.GetElementType()!;
            var generic = listType.GetInterfaces()
                .Concat(new[] { listType })
                .FirstOrDefault(type => type.IsGenericType
                    && type.GetGenericTypeDefinition() == typeof(IList<>));
            if (generic == null)
                throw new InvalidOperationException(
                    "Generated repeated field type '" + listType.FullName + "' has no IList<T> element type.");
            return generic.GetGenericArguments()[0];
        }

        private static string FirstSegment(string path)
        {
            var separator = path.IndexOf('.');
            return separator < 0 ? path : path.Substring(0, separator);
        }

        private static string RelativeMemberPath(string topLevelMember, string memberPath)
        {
            if (string.Equals(memberPath, topLevelMember, StringComparison.Ordinal))
                return string.Empty;
            var prefix = topLevelMember + ".";
            if (!memberPath.StartsWith(prefix, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "Generated member path '" + memberPath + "' is not rooted at '" + topLevelMember + "'.");
            return memberPath.Substring(prefix.Length);
        }

        private static IEnumerable<object> Enumerate(IEnumerable source)
        {
            foreach (var item in source)
            {
                if (item == null)
                    throw new ArgumentException("Generated table bindings cannot contain null.", nameof(source));
                yield return item;
            }
        }

        private static void ValidateCatalog(IReadOnlyList<TableMetadata> catalog)
        {
            if (catalog.Count == 0)
                throw new ArgumentException("At least one generated table binding is required.", nameof(catalog));
            if (catalog.Select(static item => item.TableId).Distinct().Count() != catalog.Count)
                throw new InvalidOperationException("Generated table ids must be unique.");
            if (catalog.Select(static item => item.FullName).Distinct(StringComparer.Ordinal).Count() != catalog.Count)
                throw new InvalidOperationException("Generated table full names must be unique.");
        }

        private sealed class RowRefContract
        {
            private readonly Type _declaredType;
            private readonly Type _rowRefType;
            private readonly PropertyInfo _table;
            private readonly PropertyInfo _rowGuid;
            private readonly ConstructorInfo _constructor;

            private RowRefContract(
                Type declaredType,
                Type rowRefType,
                PropertyInfo table,
                PropertyInfo rowGuid,
                ConstructorInfo constructor)
            {
                _declaredType = declaredType;
                _rowRefType = rowRefType;
                _table = table;
                _rowGuid = rowGuid;
                _constructor = constructor;
            }

            public static RowRefContract Create(Type memberType, string propertyPath)
            {
                var rowRefType = Nullable.GetUnderlyingType(memberType) ?? memberType;
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public;
                var table = rowRefType.GetProperty("Table", flags);
                var rowGuid = rowRefType.GetProperty("RowGuid", flags);
                var constructor = rowRefType.GetConstructor(new[] { typeof(int), typeof(Guid) });
                if (table == null || table.PropertyType != typeof(int) || table.GetMethod == null
                    || rowGuid == null || rowGuid.PropertyType != typeof(Guid) || rowGuid.GetMethod == null
                    || constructor == null)
                {
                    throw new InvalidOperationException(
                        "Reference field '" + propertyPath
                        + "' must use generated RowRef(int table, Guid rowGuid), optionally nullable.");
                }
                return new RowRefContract(memberType, rowRefType, table, rowGuid, constructor);
            }

            public EditorReferenceValue? Read(object? value, EditorReferenceConstraint constraint)
            {
                if (value == null)
                    return null;
                var tableId = (int)_table.GetValue(value, null)!;
                var rowGuid = (Guid)_rowGuid.GetValue(value, null)!;
                if (tableId == 0 && rowGuid == Guid.Empty)
                    return null;

                var target = constraint.AllowedTargetTables.FirstOrDefault(item => item.TableId == tableId);
                if (target == null)
                    throw new InvalidOperationException(
                        "Resident RowRef table id '" + tableId + "' is not allowed by this reference constraint.");
                return new EditorReferenceValue(
                    target.TableId,
                    target.FullName,
                    rowGuid.ToString("N"));
            }

            public object? Write(EditorReferenceValue? value, EditorReferenceConstraint constraint)
            {
                if (value == null)
                {
                    return Nullable.GetUnderlyingType(_declaredType) != null
                        ? null
                        : Activator.CreateInstance(_rowRefType);
                }

                var target = constraint.ResolveTarget(value);
                if (target == null)
                    throw new ArgumentException("The reference target table is not allowed.", nameof(value));
                Guid rowGuid;
                if (!Guid.TryParse(value.TargetIdentity, out rowGuid))
                    throw new ArgumentException(
                        "RowRef target identity must be a Guid.",
                        nameof(value));
                return _constructor.Invoke(new object[] { target.TableId, rowGuid });
            }
        }

        private sealed class TableMetadata
        {
            private TableMetadata(
                object source,
                int tableId,
                string fullName,
                string? displayName,
                Type clrType,
                IReadOnlyList<string> implements,
                IReadOnlyList<FieldMetadata> fields)
            {
                Source = source;
                TableId = tableId;
                FullName = fullName;
                DisplayName = displayName;
                ClrType = clrType;
                Implements = implements;
                Fields = fields;
            }

            public object Source { get; }

            public int TableId { get; }

            public string FullName { get; }

            public string? DisplayName { get; }

            public Type ClrType { get; }

            public IReadOnlyList<string> Implements { get; }

            public IReadOnlyList<FieldMetadata> Fields { get; }

            public static TableMetadata Read(object source)
            {
                var tableId = Contract.Read<int>(source, "TableId");
                if (tableId <= 0)
                    throw new InvalidOperationException("Generated TableId must be positive.");
                return new TableMetadata(
                    source,
                    tableId,
                    Contract.ReadNonBlankString(source, "FullName"),
                    Contract.ReadNullableString(source, "DisplayName"),
                    Contract.Read<Type>(source, "ClrType"),
                    Contract.ReadStrings(source, "Implements"),
                    Contract.ReadObjects(source, "Fields").Select(FieldMetadata.Read).ToArray());
            }

            public EditorReferenceTargetTable ToReferenceTarget()
            {
                return new EditorReferenceTargetTable(TableId, FullName, DisplayName, ClrType, Implements);
            }
        }

        private sealed class FieldMetadata
        {
            private FieldMetadata(
                int fieldId,
                IReadOnlyList<int> fieldIdPath,
                string propertyPath,
                string memberPath,
                string? displayName,
                string? headerComment,
                string physicalKind,
                string shape,
                bool hasPresence,
                bool required,
                int keyOrder,
                string? referenceTable,
                string? referenceGroup)
            {
                FieldId = fieldId;
                FieldIdPath = fieldIdPath;
                PropertyPath = propertyPath;
                MemberPath = memberPath;
                DisplayName = displayName;
                HeaderComment = headerComment;
                PhysicalKind = physicalKind;
                Shape = shape;
                HasPresence = hasPresence;
                Required = required;
                KeyOrder = keyOrder;
                ReferenceTable = referenceTable;
                ReferenceGroup = referenceGroup;
            }

            public int FieldId { get; }

            public IReadOnlyList<int> FieldIdPath { get; }

            public string PropertyPath { get; }

            public string MemberPath { get; }

            public string? DisplayName { get; }

            public string? HeaderComment { get; }

            public string PhysicalKind { get; }

            public string Shape { get; }

            public bool HasPresence { get; }

            public bool Required { get; }

            public int KeyOrder { get; }

            public string? ReferenceTable { get; }

            public string? ReferenceGroup { get; }

            public bool IsPropertyGroup => string.Equals(PhysicalKind, "PropertyGroup", StringComparison.Ordinal);

            public bool IsCollectionRoot => string.Equals(Shape, "RepeatedScalar", StringComparison.Ordinal)
                || string.Equals(Shape, "RepeatedEnum", StringComparison.Ordinal)
                || string.Equals(Shape, "RepeatedMessage", StringComparison.Ordinal)
                || string.Equals(Shape, "Map", StringComparison.Ordinal);

            public static FieldMetadata Read(object source)
            {
                var fieldId = Contract.Read<int>(source, "FieldId");
                var keyOrder = Contract.Read<int>(source, "KeyOrder");
                if (fieldId <= 0)
                    throw new InvalidOperationException("Generated FieldId must be positive.");
                if (keyOrder < 0)
                    throw new InvalidOperationException("Generated KeyOrder cannot be negative.");
                return new FieldMetadata(
                    fieldId,
                    Contract.ReadInts(source, "FieldIdPath"),
                    Contract.ReadNonBlankString(source, "PropertyPath"),
                    Contract.ReadNonBlankString(source, "MemberPath"),
                    Contract.ReadNullableString(source, "DisplayName"),
                    Contract.ReadNullableString(source, "HeaderComment"),
                    Contract.ReadRequiredValue(source, "PhysicalKind").ToString()!,
                    Contract.ReadRequiredValue(source, "Shape").ToString()!,
                    Contract.Read<bool>(source, "HasPresence"),
                    Contract.Read<bool>(source, "Required"),
                    keyOrder,
                    Contract.ReadNullableString(source, "ReferenceTable"),
                    Contract.ReadNullableString(source, "ReferenceGroup"));
            }
        }

        private static class Contract
        {
            public static T Read<T>(object source, string propertyName)
            {
                var value = ReadRequiredValue(source, propertyName);
                if (value is T typed)
                    return typed;
                throw new InvalidOperationException(
                    "Generated metadata property '" + propertyName + "' on '"
                    + source.GetType().FullName + "' must be a " + typeof(T).FullName + ".");
            }

            public static object ReadRequiredValue(object source, string propertyName)
            {
                var property = source.GetType().GetProperty(
                    propertyName,
                    BindingFlags.Instance | BindingFlags.Public);
                if (property == null || property.GetIndexParameters().Length != 0 || property.GetMethod == null)
                    throw new InvalidOperationException(
                        "Generated metadata type '" + source.GetType().FullName
                        + "' is missing readable property '" + propertyName + "'.");
                var value = property.GetValue(source, null);
                if (value == null)
                    throw new InvalidOperationException(
                        "Generated metadata property '" + propertyName + "' cannot be null.");
                return value;
            }

            public static string ReadNonBlankString(object source, string propertyName)
            {
                var value = Read<string>(source, propertyName);
                if (string.IsNullOrWhiteSpace(value))
                    throw new InvalidOperationException(
                        "Generated metadata property '" + propertyName + "' cannot be blank.");
                return value;
            }

            public static string? ReadNullableString(object source, string propertyName)
            {
                var property = source.GetType().GetProperty(
                    propertyName,
                    BindingFlags.Instance | BindingFlags.Public);
                if (property == null || property.GetIndexParameters().Length != 0 || property.GetMethod == null)
                    throw new InvalidOperationException(
                        "Generated metadata type '" + source.GetType().FullName
                        + "' is missing readable property '" + propertyName + "'.");
                var value = property.GetValue(source, null);
                if (value == null)
                    return null;
                var text = value as string;
                if (text == null)
                    throw new InvalidOperationException(
                        "Generated metadata property '" + propertyName + "' must be a string.");
                return string.IsNullOrWhiteSpace(text) ? null : text;
            }

            public static IReadOnlyList<int> ReadInts(object source, string propertyName)
            {
                return ReadObjects(source, propertyName).Select(item =>
                {
                    if (item is int value)
                        return value;
                    throw new InvalidOperationException(
                        "Generated metadata sequence '" + propertyName + "' must contain Int32 values.");
                }).ToArray();
            }

            public static IReadOnlyList<string> ReadStrings(object source, string propertyName)
            {
                return ReadObjects(source, propertyName).Select(item =>
                {
                    var value = item as string;
                    if (value != null)
                        return value;
                    throw new InvalidOperationException(
                        "Generated metadata sequence '" + propertyName + "' must contain strings.");
                }).ToArray();
            }

            public static IReadOnlyList<object> ReadObjects(object source, string propertyName)
            {
                var value = ReadRequiredValue(source, propertyName) as IEnumerable;
                if (value == null)
                    throw new InvalidOperationException(
                        "Generated metadata property '" + propertyName + "' must be enumerable.");
                return Enumerate(value).ToArray();
            }
        }

        private sealed class CacheKey : IEquatable<CacheKey>
        {
            private readonly object _target;
            private readonly object[] _catalog;
            private readonly int _hashCode;

            public CacheKey(object target, object[] catalog)
            {
                _target = target;
                _catalog = catalog;
                unchecked
                {
                    var hash = RuntimeHelpers.GetHashCode(target);
                    foreach (var item in catalog)
                        hash = (hash * 397) ^ RuntimeHelpers.GetHashCode(item);
                    _hashCode = hash;
                }
            }

            public bool Equals(CacheKey? other)
            {
                if (other == null || !ReferenceEquals(_target, other._target)
                    || _catalog.Length != other._catalog.Length)
                    return false;
                for (var index = 0; index < _catalog.Length; index++)
                {
                    if (!ReferenceEquals(_catalog[index], other._catalog[index]))
                        return false;
                }
                return true;
            }

            public override bool Equals(object? obj) => Equals(obj as CacheKey);

            public override int GetHashCode() => _hashCode;
        }
    }
}
