using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace ExcelDb.Editor.Model
{
    public enum EditorPropertyKind
    {
        Scalar = 0,
        Object = 1,
        List = 2,
        Reference = 3,
        Group = 4,
        Unsupported = 5,
    }

    public sealed class EditorReferenceTargetTable
    {
        public EditorReferenceTargetTable(
            int tableId,
            string fullName,
            string? displayName = null,
            Type? clrType = null,
            IEnumerable<string>? implements = null)
        {
            if (tableId < 0)
                throw new ArgumentOutOfRangeException(nameof(tableId));
            if (string.IsNullOrWhiteSpace(fullName))
                throw new ArgumentException("A reference target table name is required.", nameof(fullName));

            TableId = tableId;
            FullName = fullName;
            DisplayName = displayName;
            ClrType = clrType ?? typeof(object);
            Implements = Array.AsReadOnly(
                (implements ?? Array.Empty<string>())
                .Where(static item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static item => item, StringComparer.Ordinal)
                .ToArray());
        }

        public int TableId { get; }

        public string FullName { get; }

        public string? DisplayName { get; }

        public Type ClrType { get; }

        public IReadOnlyList<string> Implements { get; }
    }

    public sealed class EditorReferenceConstraint
    {
        private readonly IReadOnlyList<string> _allowedTables;

        /// <summary>Compatibility constructor for an existing single-table reference.</summary>
        public EditorReferenceConstraint(string table, bool allowNull = true)
            : this(
                table,
                null,
                new[] { new EditorReferenceTargetTable(0, table) },
                allowNull)
        {
        }

        private EditorReferenceConstraint(
            string? referenceTable,
            string? referenceGroup,
            IEnumerable<EditorReferenceTargetTable> allowedTargetTables,
            bool allowNull)
        {
            var hasTable = !string.IsNullOrWhiteSpace(referenceTable);
            var hasGroup = !string.IsNullOrWhiteSpace(referenceGroup);
            if (hasTable == hasGroup)
                throw new ArgumentException("Exactly one reference table or reference group is required.");
            if (allowedTargetTables == null)
                throw new ArgumentNullException(nameof(allowedTargetTables));

            var targets = allowedTargetTables
                .OrderBy(static item => item.TableId)
                .ThenBy(static item => item.FullName, StringComparer.Ordinal)
                .ToArray();
            if (targets.Length == 0)
                throw new ArgumentException("At least one allowed target table is required.", nameof(allowedTargetTables));
            if (targets.Select(static item => item.TableId).Distinct().Count() != targets.Length)
                throw new ArgumentException("Allowed target table ids must be unique.", nameof(allowedTargetTables));
            if (targets.Select(static item => item.FullName).Distinct(StringComparer.Ordinal).Count() != targets.Length)
                throw new ArgumentException("Allowed target table names must be unique.", nameof(allowedTargetTables));

            ReferenceTable = hasTable ? referenceTable : null;
            ReferenceGroup = hasGroup ? referenceGroup : null;
            AllowedTargetTables = Array.AsReadOnly(targets);
            _allowedTables = Array.AsReadOnly(targets.Select(static item => item.FullName).ToArray());
            AllowNull = allowNull;
        }

        public string? ReferenceTable { get; }

        public string? ReferenceGroup { get; }

        public IReadOnlyList<EditorReferenceTargetTable> AllowedTargetTables { get; }

        public IReadOnlyList<string> AllowedTables => _allowedTables;

        /// <summary>Compatibility alias for ReferenceTable.</summary>
        public string? Table => ReferenceTable;

        /// <summary>Compatibility alias for ReferenceGroup.</summary>
        public string? Group => ReferenceGroup;

        public bool AllowNull { get; }

        public static EditorReferenceConstraint ForTable(
            EditorReferenceTargetTable targetTable,
            bool allowNull = true)
        {
            if (targetTable == null)
                throw new ArgumentNullException(nameof(targetTable));
            return new EditorReferenceConstraint(
                targetTable.FullName,
                null,
                new[] { targetTable },
                allowNull);
        }

        public static EditorReferenceConstraint ForGroup(
            string referenceGroup,
            IEnumerable<EditorReferenceTargetTable> allowedTargetTables,
            bool allowNull = true)
        {
            if (string.IsNullOrWhiteSpace(referenceGroup))
                throw new ArgumentException("A reference group is required.", nameof(referenceGroup));
            return new EditorReferenceConstraint(null, referenceGroup, allowedTargetTables, allowNull);
        }

        public EditorReferenceTargetTable? ResolveTarget(EditorReferenceValue value)
        {
            if (value == null)
                throw new ArgumentNullException(nameof(value));

            foreach (var target in AllowedTargetTables)
            {
                var idMatches = value.TargetTableId == 0 || target.TableId == 0
                    || value.TargetTableId == target.TableId;
                if (idMatches
                    && string.Equals(value.TargetTable, target.FullName, StringComparison.Ordinal))
                    return target;
            }
            return null;
        }

        public bool AllowsTable(int tableId, string fullName)
        {
            return ResolveTarget(new EditorReferenceValue(tableId, fullName, "constraint-probe")) != null;
        }
    }

    public sealed class EditorReferenceValue : IEquatable<EditorReferenceValue>
    {
        public EditorReferenceValue(string table, string targetIdentity, string? displayKey = null)
            : this(0, table, targetIdentity, displayKey)
        {
        }

        public EditorReferenceValue(
            int targetTableId,
            string targetTable,
            string targetIdentity,
            string? displayKey = null)
        {
            if (targetTableId < 0)
                throw new ArgumentOutOfRangeException(nameof(targetTableId));
            if (string.IsNullOrWhiteSpace(targetTable))
                throw new ArgumentException("A reference target table is required.", nameof(targetTable));
            if (string.IsNullOrWhiteSpace(targetIdentity))
                throw new ArgumentException("A target identity is required.", nameof(targetIdentity));
            TargetTableId = targetTableId;
            TargetTable = targetTable;
            TargetIdentity = targetIdentity;
            DisplayKey = displayKey;
        }

        public int TargetTableId { get; }

        public string TargetTable { get; }

        /// <summary>Compatibility alias for TargetTable.</summary>
        public string Table => TargetTable;

        public string TargetIdentity { get; }

        public string? DisplayKey { get; }

        public bool Equals(EditorReferenceValue? other)
        {
            return other != null
                && TargetTableId == other.TargetTableId
                && string.Equals(TargetTable, other.TargetTable, StringComparison.Ordinal)
                && string.Equals(TargetIdentity, other.TargetIdentity, StringComparison.Ordinal)
                && string.Equals(DisplayKey, other.DisplayKey, StringComparison.Ordinal);
        }

        public override bool Equals(object? obj) => Equals(obj as EditorReferenceValue);

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = TargetTableId;
                hash = (hash * 397) ^ StringComparer.Ordinal.GetHashCode(TargetTable);
                hash = (hash * 397) ^ StringComparer.Ordinal.GetHashCode(TargetIdentity);
                hash = (hash * 397) ^ (DisplayKey == null ? 0 : StringComparer.Ordinal.GetHashCode(DisplayKey));
                return hash;
            }
        }
    }

    public sealed class EditorReferenceAdapter
    {
        private readonly Func<object?, EditorReferenceValue?> _read;
        private readonly Func<EditorReferenceValue?, object?> _write;

        public EditorReferenceAdapter(
            EditorReferenceConstraint constraint,
            Func<object?, EditorReferenceValue?> read,
            Func<EditorReferenceValue?, object?> write)
        {
            Constraint = constraint ?? throw new ArgumentNullException(nameof(constraint));
            _read = read ?? throw new ArgumentNullException(nameof(read));
            _write = write ?? throw new ArgumentNullException(nameof(write));
        }

        public EditorReferenceConstraint Constraint { get; }

        internal EditorReferenceValue? Read(object? value) => _read(value);

        internal object? Write(EditorReferenceValue? value)
        {
            if (value == null && !Constraint.AllowNull)
                throw new ArgumentException(
                    "Reference '" + (Constraint.ReferenceTable ?? Constraint.ReferenceGroup) + "' does not allow null.",
                    nameof(value));
            if (value != null && Constraint.ResolveTarget(value) == null)
                throw new ArgumentException(
                    "Reference target table '" + value.TargetTable + "' is not allowed by '"
                    + (Constraint.ReferenceTable ?? Constraint.ReferenceGroup) + "'.",
                    nameof(value));
            return _write(value);
        }
    }

    /// <summary>
    /// Host-neutral metadata and accessors for one generated authoring property.
    /// PropertyPath and FieldIdPath are schema identities; MemberPath addresses the generated POCO.
    /// </summary>
    public sealed class EditorPropertyBinding
    {
        private readonly Func<object, object?> _getter;
        private readonly Action<object, object?> _setter;
        private readonly Func<object?, object?, bool> _equals;
        private readonly Func<object?, object?> _clone;
        private readonly EditorReferenceAdapter? _reference;

        public EditorPropertyBinding(
            int fieldId,
            IEnumerable<int> fieldIdPath,
            string propertyPath,
            string memberPath,
            Type targetType,
            Type valueType,
            EditorPropertyKind kind,
            Func<object, object?> getter,
            Action<object, object?> setter,
            string? displayName = null,
            Func<object?, object?, bool>? equals = null,
            Func<object?, object?>? clone = null,
            string? tooltip = null,
            bool isReadOnly = false,
            EditorReferenceAdapter? reference = null,
            bool hasPresence = false,
            bool required = false,
            int keyOrder = 0,
            string? unsupportedReason = null)
        {
            if (fieldId <= 0)
                throw new ArgumentOutOfRangeException(nameof(fieldId));
            if (fieldIdPath == null)
                throw new ArgumentNullException(nameof(fieldIdPath));
            if (string.IsNullOrWhiteSpace(propertyPath))
                throw new ArgumentException("A schema property path is required.", nameof(propertyPath));
            if (string.IsNullOrWhiteSpace(memberPath))
                throw new ArgumentException("A generated member path is required.", nameof(memberPath));
            if (keyOrder < 0)
                throw new ArgumentOutOfRangeException(nameof(keyOrder));
            TargetType = targetType ?? throw new ArgumentNullException(nameof(targetType));
            ValueType = valueType ?? throw new ArgumentNullException(nameof(valueType));
            _getter = getter ?? throw new ArgumentNullException(nameof(getter));
            _setter = setter ?? throw new ArgumentNullException(nameof(setter));
            _equals = equals ?? EditorValueComparer.StructuralEquals;
            _clone = clone ?? EditorValueCloner.Clone;
            _reference = reference;
            if (kind == EditorPropertyKind.Unsupported && string.IsNullOrWhiteSpace(unsupportedReason))
                throw new ArgumentException("Unsupported properties require a diagnostic reason.", nameof(unsupportedReason));
            if (kind != EditorPropertyKind.Unsupported && unsupportedReason != null)
                throw new ArgumentException("Only Unsupported properties can carry an unsupported reason.", nameof(unsupportedReason));

            var path = fieldIdPath.ToArray();
            if (path.Length == 0 || path.Any(static item => item <= 0))
                throw new ArgumentException("A field id path must contain positive ids.", nameof(fieldIdPath));

            FieldId = fieldId;
            FieldIdPath = Array.AsReadOnly(path);
            PropertyPath = propertyPath;
            MemberPath = memberPath;
            Kind = kind;
            DisplayName = displayName;
            Tooltip = tooltip;
            HasPresence = hasPresence;
            Required = required;
            KeyOrder = keyOrder;
            IsGroupContainer = kind == EditorPropertyKind.Group;
            UnsupportedReason = unsupportedReason;
            IsReadOnly = isReadOnly || keyOrder > 0 || IsGroupContainer
                || kind == EditorPropertyKind.Unsupported;
            ParentPropertyPath = ParentPath(propertyPath);
            Depth = propertyPath.Count(static character => character == '.');
            if (reference != null && kind != EditorPropertyKind.Reference)
                throw new ArgumentException("A reference adapter requires Reference property kind.", nameof(reference));
        }

        public int FieldId { get; }

        public IReadOnlyList<int> FieldIdPath { get; }

        public string PropertyPath { get; }

        public string MemberPath { get; }

        public Type TargetType { get; }

        public Type ValueType { get; }

        public EditorPropertyKind Kind { get; }

        public string? DisplayName { get; }

        public string? Tooltip { get; }

        public bool IsReadOnly { get; }

        public bool HasPresence { get; }

        public bool Required { get; }

        public int KeyOrder { get; }

        public bool IsGroupContainer { get; }

        public string? UnsupportedReason { get; }

        public string? ParentPropertyPath { get; }

        public int Depth { get; }

        public EditorReferenceConstraint? ReferenceConstraint => _reference?.Constraint;

        public static EditorPropertyBinding Create<TObject, TValue>(
            int fieldId,
            IEnumerable<int> fieldIdPath,
            string propertyPath,
            string memberPath,
            EditorPropertyKind kind,
            Func<TObject, TValue> getter,
            Action<TObject, TValue> setter,
            string? displayName = null,
            Func<TValue, TValue, bool>? equals = null,
            Func<TValue, TValue>? clone = null,
            string? tooltip = null,
            bool isReadOnly = false,
            EditorReferenceAdapter? reference = null,
            bool hasPresence = false,
            bool required = false,
            int keyOrder = 0,
            string? unsupportedReason = null)
            where TObject : class
        {
            if (getter == null)
                throw new ArgumentNullException(nameof(getter));
            if (setter == null)
                throw new ArgumentNullException(nameof(setter));

            return new EditorPropertyBinding(
                fieldId,
                fieldIdPath,
                propertyPath,
                memberPath,
                typeof(TObject),
                typeof(TValue),
                kind,
                target => getter(CastTarget<TObject>(target)),
                (target, value) => setter(CastTarget<TObject>(target), CastValue<TValue>(value)),
                displayName,
                equals == null ? null : (left, right) => equals(CastValue<TValue>(left), CastValue<TValue>(right)),
                clone == null ? null : value => clone(CastValue<TValue>(value)),
                tooltip,
                isReadOnly,
                reference,
                hasPresence,
                required,
                keyOrder,
                unsupportedReason);
        }

        /// <summary>
        /// Creates accessors from the MemberPath emitted by GeneratedFieldBinding.
        /// A generator can replace this reflection adapter with Create and static delegates without changing consumers.
        /// </summary>
        public static EditorPropertyBinding CreateFromMemberPath(
            int fieldId,
            IEnumerable<int> fieldIdPath,
            string propertyPath,
            string memberPath,
            Type targetType,
            EditorPropertyKind kind,
            string? displayName = null,
            Func<object?, object?, bool>? equals = null,
            Func<object?, object?>? clone = null,
            string? tooltip = null,
            bool isReadOnly = false,
            EditorReferenceAdapter? reference = null,
            bool hasPresence = false,
            bool required = false,
            int keyOrder = 0,
            string? unsupportedReason = null)
        {
            if (targetType == null)
                throw new ArgumentNullException(nameof(targetType));
            var accessor = MemberPathAccessor.Create(targetType, memberPath);
            return new EditorPropertyBinding(
                fieldId,
                fieldIdPath,
                propertyPath,
                memberPath,
                targetType,
                accessor.ValueType,
                kind,
                accessor.GetValue,
                accessor.SetValue,
                displayName,
                equals,
                clone,
                tooltip,
                isReadOnly,
                reference,
                hasPresence,
                required,
                keyOrder,
                unsupportedReason);
        }

        internal object? Read(object target)
        {
            EnsureTarget(target);
            return _getter(target);
        }

        internal void Write(object target, object? value)
        {
            EnsureTarget(target);
            if (IsReadOnly)
                throw new InvalidOperationException("Property '" + PropertyPath + "' is read-only.");
            EnsureValue(value);
            _setter(target, value);
        }

        internal bool ValuesEqual(object? left, object? right) => _equals(left, right);

        internal object? CloneValue(object? value) => _clone(value);

        internal object? PrepareValue(object? value)
        {
            if (IsReadOnly)
                throw new InvalidOperationException("Property '" + PropertyPath + "' is read-only.");
            EnsureValue(value);
            return CloneValue(value);
        }

        internal EditorReferenceValue? ReadReference(object? value)
        {
            if (_reference == null)
                throw new InvalidOperationException("Property '" + PropertyPath + "' has no host-neutral reference adapter.");
            return _reference.Read(value);
        }

        internal object? WriteReference(EditorReferenceValue? value)
        {
            if (_reference == null)
                throw new InvalidOperationException("Property '" + PropertyPath + "' has no host-neutral reference adapter.");
            return _reference.Write(value);
        }

        private void EnsureTarget(object target)
        {
            if (target == null)
                throw new ArgumentNullException(nameof(target));
            if (!TargetType.IsInstanceOfType(target))
                throw new ArgumentException(
                    "Target type '" + target.GetType().FullName + "' does not match '" + TargetType.FullName + "'.",
                    nameof(target));
        }

        private void EnsureValue(object? value)
        {
            if (value == null)
            {
                if (ValueType.IsValueType && Nullable.GetUnderlyingType(ValueType) == null)
                    throw new ArgumentException("Property '" + PropertyPath + "' does not accept null.", nameof(value));
                return;
            }

            var accepted = Nullable.GetUnderlyingType(ValueType) ?? ValueType;
            if (!accepted.IsInstanceOfType(value))
                throw new ArgumentException(
                    "Value type '" + value.GetType().FullName + "' does not match '" + ValueType.FullName + "'.",
                    nameof(value));
        }

        private static TObject CastTarget<TObject>(object target)
            where TObject : class
        {
            var typed = target as TObject;
            if (typed == null)
                throw new ArgumentException("Target is not a " + typeof(TObject).FullName + ".", nameof(target));
            return typed;
        }

        private static TValue CastValue<TValue>(object? value)
        {
            if (value == null)
            {
                if (typeof(TValue).IsValueType && Nullable.GetUnderlyingType(typeof(TValue)) == null)
                    throw new ArgumentException("A non-null value is required.", nameof(value));
                return default!;
            }

            if (value is TValue typed)
                return typed;
            throw new ArgumentException("Value is not a " + typeof(TValue).FullName + ".", nameof(value));
        }

        private static string? ParentPath(string propertyPath)
        {
            var separator = propertyPath.LastIndexOf('.');
            return separator < 0 ? null : propertyPath.Substring(0, separator);
        }

        private sealed class MemberPathAccessor
        {
            private readonly MemberInfo[] _members;

            private MemberPathAccessor(MemberInfo[] members, Type valueType)
            {
                _members = members;
                ValueType = valueType;
            }

            public Type ValueType { get; }

            public static MemberPathAccessor Create(Type targetType, string memberPath)
            {
                if (string.IsNullOrWhiteSpace(memberPath))
                    throw new ArgumentException("A member path is required.", nameof(memberPath));

                var names = memberPath.Split('.');
                var members = new MemberInfo[names.Length];
                var currentType = targetType;
                for (var index = 0; index < names.Length; index++)
                {
                    var member = FindMember(currentType, names[index]);
                    members[index] = member;
                    currentType = GetMemberType(member);
                }

                EnsureWritable(members[members.Length - 1], memberPath);
                return new MemberPathAccessor(members, currentType);
            }

            public object? GetValue(object target)
            {
                object? current = target;
                for (var index = 0; index < _members.Length; index++)
                {
                    if (current == null)
                        throw new InvalidOperationException("Member path crosses a null value at '" + _members[index].Name + "'.");
                    current = ReadMember(_members[index], current);
                }
                return current;
            }

            public void SetValue(object target, object? value)
            {
                object? current = target;
                for (var index = 0; index < _members.Length - 1; index++)
                {
                    if (current == null)
                        throw new InvalidOperationException("Member path crosses a null value at '" + _members[index].Name + "'.");
                    current = ReadMember(_members[index], current);
                }

                if (current == null)
                    throw new InvalidOperationException("The owner of member '" + _members[_members.Length - 1].Name + "' is null.");
                WriteMember(_members[_members.Length - 1], current, value);
            }

            private static MemberInfo FindMember(Type owner, string name)
            {
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public;
                var property = owner.GetProperty(name, flags);
                if (property != null && property.GetIndexParameters().Length == 0 && property.GetMethod != null)
                    return property;
                var field = owner.GetField(name, flags);
                if (field != null)
                    return field;
                throw new ArgumentException(
                    "Public readable member '" + name + "' was not found on '" + owner.FullName + "'.",
                    nameof(name));
            }

            private static Type GetMemberType(MemberInfo member)
            {
                var property = member as PropertyInfo;
                return property != null ? property.PropertyType : ((FieldInfo)member).FieldType;
            }

            private static object? ReadMember(MemberInfo member, object owner)
            {
                var property = member as PropertyInfo;
                return property != null ? property.GetValue(owner, null) : ((FieldInfo)member).GetValue(owner);
            }

            private static void WriteMember(MemberInfo member, object owner, object? value)
            {
                var property = member as PropertyInfo;
                if (property != null)
                    property.SetValue(owner, value, null);
                else
                    ((FieldInfo)member).SetValue(owner, value);
            }

            private static void EnsureWritable(MemberInfo member, string path)
            {
                var property = member as PropertyInfo;
                if (property != null && property.SetMethod == null)
                    throw new ArgumentException("Final member in '" + path + "' is read-only.", nameof(path));
                var field = member as FieldInfo;
                if (field != null && field.IsInitOnly)
                    throw new ArgumentException("Final member in '" + path + "' is read-only.", nameof(path));
            }
        }
    }

    /// <summary>
    /// Raised only when an apply failed and best-effort rollback also encountered setter failures.
    /// The original apply exception is preserved separately from every rollback exception.
    /// </summary>
    public sealed class EditorAtomicApplyException : InvalidOperationException
    {
        internal EditorAtomicApplyException(
            string operation,
            Exception applyException,
            IEnumerable<Exception> rollbackExceptions)
            : base(
                operation + " failed and one or more rollback setters also failed; resident state may be uncertain.",
                applyException)
        {
            ApplyException = applyException ?? throw new ArgumentNullException(nameof(applyException));
            if (rollbackExceptions == null)
                throw new ArgumentNullException(nameof(rollbackExceptions));
            RollbackExceptions = Array.AsReadOnly(rollbackExceptions.ToArray());
            if (RollbackExceptions.Count == 0)
                throw new ArgumentException("At least one rollback exception is required.", nameof(rollbackExceptions));
        }

        public Exception ApplyException { get; }

        public IReadOnlyList<Exception> RollbackExceptions { get; }
    }

    internal sealed class EditorPropertyProjection
    {
        public EditorPropertyProjection(
            EditorPropertyBinding metadata,
            int storageIndex,
            string relativeMemberPath,
            bool isTemplate)
        {
            Metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
            if (storageIndex < 0)
                throw new ArgumentOutOfRangeException(nameof(storageIndex));
            StorageIndex = storageIndex;
            RelativeMemberPath = relativeMemberPath ?? throw new ArgumentNullException(nameof(relativeMemberPath));
            IsTemplate = isTemplate;
        }

        public EditorPropertyBinding Metadata { get; }

        public int StorageIndex { get; }

        public string RelativeMemberPath { get; }

        public bool IsTemplate { get; }
    }

    /// <summary>
    /// Canonical property snapshot/patch boundary for one generated authoring POCO type.
    /// Correctness does not depend on a host object's clone implementation being deep.
    /// </summary>
    public sealed class EditorObjectBinding
    {
        private readonly Dictionary<string, EditorPropertyBinding> _byPath;
        private readonly Dictionary<string, int> _indexByPath;
        private readonly Dictionary<string, EditorPropertyBinding> _schemaByPath;
        private readonly Dictionary<int, Dictionary<string, EditorPropertyBinding>> _templates;
        private readonly EditorPropertyProjection[] _projections;
        private readonly IReadOnlyList<EditorPropertyBinding> _properties;
        private readonly IReadOnlyList<EditorPropertyBinding> _schemaProperties;
        private readonly IReadOnlyList<EditorPropertyBinding> _storageProperties;
        private readonly int[] _applyOrder;

        public EditorObjectBinding(
            Type targetType,
            IEnumerable<EditorPropertyBinding> properties)
            : this(targetType, EditorBindingLayout.ForManual(targetType, properties))
        {
        }

        private EditorObjectBinding(Type targetType, EditorBindingLayout layout)
        {
            TargetType = targetType ?? throw new ArgumentNullException(nameof(targetType));
            _storageProperties = Array.AsReadOnly(layout.StorageProperties);
            _projections = layout.Projections.Where(static item => !item.IsTemplate).ToArray();
            _properties = Array.AsReadOnly(_projections.Select(static item => item.Metadata).ToArray());
            _schemaProperties = Array.AsReadOnly(layout.Projections.Select(static item => item.Metadata).ToArray());

            _byPath = new Dictionary<string, EditorPropertyBinding>(StringComparer.Ordinal);
            _indexByPath = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var index = 0; index < _projections.Length; index++)
            {
                var property = _projections[index].Metadata;
                if (_byPath.ContainsKey(property.PropertyPath))
                    throw new ArgumentException("Duplicate addressable property path '" + property.PropertyPath + "'.");
                _byPath.Add(property.PropertyPath, property);
                _indexByPath.Add(property.PropertyPath, index);
            }

            _schemaByPath = new Dictionary<string, EditorPropertyBinding>(StringComparer.Ordinal);
            _templates = new Dictionary<int, Dictionary<string, EditorPropertyBinding>>();
            foreach (var projection in layout.Projections)
            {
                if (_schemaByPath.ContainsKey(projection.Metadata.PropertyPath))
                    throw new ArgumentException(
                        "Duplicate schema property path '" + projection.Metadata.PropertyPath + "'.");
                _schemaByPath.Add(projection.Metadata.PropertyPath, projection.Metadata);
                if (!projection.IsTemplate)
                    continue;

                Dictionary<string, EditorPropertyBinding>? byMemberPath;
                if (!_templates.TryGetValue(projection.StorageIndex, out byMemberPath))
                {
                    byMemberPath = new Dictionary<string, EditorPropertyBinding>(StringComparer.Ordinal);
                    _templates.Add(projection.StorageIndex, byMemberPath);
                }
                if (byMemberPath.ContainsKey(projection.RelativeMemberPath))
                    throw new ArgumentException(
                        "Duplicate dynamic metadata path '" + projection.RelativeMemberPath + "'.");
                byMemberPath.Add(projection.RelativeMemberPath, projection.Metadata);
            }

            _applyOrder = Enumerable.Range(0, layout.StorageProperties.Length)
                .OrderBy(index => layout.StorageProperties[index].MemberPath.Count(static character => character == '.'))
                .ThenBy(static index => index)
                .ToArray();
        }

        public Type TargetType { get; }

        /// <summary>Addressable schema properties. Dynamic collection templates are exposed by SchemaProperties.</summary>
        public IReadOnlyList<EditorPropertyBinding> Properties => _properties;

        public IReadOnlyList<EditorPropertyBinding> SchemaProperties => _schemaProperties;

        internal IReadOnlyList<EditorPropertyBinding> StorageProperties => _storageProperties;

        internal static EditorObjectBinding CreateProjected(
            Type targetType,
            IEnumerable<EditorPropertyBinding> storageProperties,
            IEnumerable<EditorPropertyProjection> projections)
        {
            return new EditorObjectBinding(
                targetType,
                EditorBindingLayout.ForProjected(targetType, storageProperties, projections));
        }

        public static EditorObjectBinding Create<TObject>(IEnumerable<EditorPropertyBinding> properties)
            where TObject : class
        {
            return new EditorObjectBinding(typeof(TObject), properties);
        }

        public bool TryGetProperty(string propertyPath, out EditorPropertyBinding? property)
        {
            if (propertyPath == null)
                throw new ArgumentNullException(nameof(propertyPath));
            return _byPath.TryGetValue(propertyPath, out property);
        }

        public EditorPropertyBinding GetProperty(string propertyPath)
        {
            EditorPropertyBinding? property;
            if (!TryGetProperty(propertyPath, out property))
                throw new KeyNotFoundException("Unknown editor property '" + propertyPath + "'.");
            return property!;
        }

        public bool TryGetSchemaProperty(string propertyPath, out EditorPropertyBinding? property)
        {
            if (propertyPath == null)
                throw new ArgumentNullException(nameof(propertyPath));
            return _schemaByPath.TryGetValue(propertyPath, out property);
        }

        public EditorPropertyBinding GetSchemaProperty(string propertyPath)
        {
            EditorPropertyBinding? property;
            if (!TryGetSchemaProperty(propertyPath, out property))
                throw new KeyNotFoundException("Unknown editor schema property '" + propertyPath + "'.");
            return property!;
        }

        internal int GetPropertyIndex(string propertyPath)
        {
            int index;
            if (!_indexByPath.TryGetValue(propertyPath, out index))
                throw new KeyNotFoundException("Unknown editor property '" + propertyPath + "'.");
            return index;
        }

        internal EditorPropertyProjection GetProjection(int propertyIndex)
        {
            if (propertyIndex < 0 || propertyIndex >= _projections.Length)
                throw new ArgumentOutOfRangeException(nameof(propertyIndex));
            return _projections[propertyIndex];
        }

        internal bool HasTemplates(int storageIndex) => _templates.ContainsKey(storageIndex);

        internal bool HasTemplateDescendant(int storageIndex, string relativeMemberPath)
        {
            Dictionary<string, EditorPropertyBinding>? templates;
            if (!_templates.TryGetValue(storageIndex, out templates))
                return false;
            return templates.Keys.Any(path => IsAncestor(relativeMemberPath, path));
        }

        internal bool TryGetTemplate(
            int storageIndex,
            string relativeMemberPath,
            out EditorPropertyBinding? property)
        {
            Dictionary<string, EditorPropertyBinding>? templates;
            if (_templates.TryGetValue(storageIndex, out templates))
                return templates.TryGetValue(relativeMemberPath, out property);
            property = null;
            return false;
        }

        internal object?[] Capture(object target)
        {
            EnsureTarget(target);
            var values = new object?[_storageProperties.Count];
            foreach (var index in _applyOrder)
            {
                var property = _storageProperties[index];
                values[index] = property.CloneValue(property.Read(target));
            }
            return values;
        }

        internal object?[] CloneSnapshot(object?[] snapshot)
        {
            EnsureSnapshot(snapshot);
            var clone = new object?[snapshot.Length];
            for (var index = 0; index < snapshot.Length; index++)
                clone[index] = _storageProperties[index].CloneValue(snapshot[index]);
            return clone;
        }

        internal void ApplySnapshot(object target, object?[] snapshot)
        {
            EnsureTarget(target);
            EnsureSnapshot(snapshot);
            var before = Capture(target);
            try
            {
                ApplySnapshotCore(target, snapshot, reverse: false);
            }
            catch (Exception applyException)
            {
                var rollbackExceptions = ApplySnapshotCoreBestEffort(target, before, reverse: true);
                if (rollbackExceptions.Count != 0)
                    throw new EditorAtomicApplyException("Editor property snapshot apply", applyException, rollbackExceptions);
                throw;
            }
        }

        internal bool TargetEqualsSnapshot(object target, object?[] snapshot)
        {
            EnsureTarget(target);
            EnsureSnapshot(snapshot);
            for (var index = 0; index < _storageProperties.Count; index++)
            {
                var property = _storageProperties[index];
                if (property.IsGroupContainer)
                    continue;
                if (!property.ValuesEqual(property.Read(target), snapshot[index]))
                    return false;
            }
            return true;
        }

        internal bool SnapshotsEqual(object?[] left, object?[] right)
        {
            EnsureSnapshot(left);
            EnsureSnapshot(right);
            for (var index = 0; index < _storageProperties.Count; index++)
            {
                if (_storageProperties[index].IsGroupContainer)
                    continue;
                if (!_storageProperties[index].ValuesEqual(left[index], right[index]))
                    return false;
            }
            return true;
        }

        private void EnsureTarget(object value)
        {
            if (value == null)
                throw new ArgumentNullException(nameof(value));
            if (!TargetType.IsInstanceOfType(value))
                throw new ArgumentException("Object is not a " + TargetType.FullName + ".", nameof(value));
        }

        private void ApplySnapshotCore(object target, object?[] snapshot, bool reverse)
        {
            var indexes = reverse ? _applyOrder.Reverse() : _applyOrder;
            foreach (var index in indexes)
            {
                var property = _storageProperties[index];
                if (!property.IsReadOnly)
                    property.Write(target, property.CloneValue(snapshot[index]));
            }
        }

        private IReadOnlyList<Exception> ApplySnapshotCoreBestEffort(
            object target,
            object?[] snapshot,
            bool reverse)
        {
            var exceptions = new List<Exception>();
            var indexes = reverse ? _applyOrder.Reverse() : _applyOrder;
            foreach (var index in indexes)
            {
                var property = _storageProperties[index];
                if (property.IsReadOnly)
                    continue;
                try
                {
                    property.Write(target, property.CloneValue(snapshot[index]));
                }
                catch (Exception exception)
                {
                    exceptions.Add(exception);
                }
            }
            return exceptions;
        }

        private void EnsureSnapshot(object?[] snapshot)
        {
            if (snapshot == null)
                throw new ArgumentNullException(nameof(snapshot));
            if (snapshot.Length != _storageProperties.Count)
                throw new ArgumentException("Snapshot property count does not match the binding.", nameof(snapshot));
        }

        private static bool IsAncestor(string candidate, string path)
        {
            return path.Length > candidate.Length
                && path.StartsWith(candidate, StringComparison.Ordinal)
                && path[candidate.Length] == '.';
        }

        private sealed class EditorBindingLayout
        {
            private EditorBindingLayout(
                EditorPropertyBinding[] storageProperties,
                EditorPropertyProjection[] projections)
            {
                StorageProperties = storageProperties;
                Projections = projections;
            }

            public EditorPropertyBinding[] StorageProperties { get; }

            public EditorPropertyProjection[] Projections { get; }

            public static EditorBindingLayout ForManual(
                Type targetType,
                IEnumerable<EditorPropertyBinding> properties)
            {
                if (targetType == null)
                    throw new ArgumentNullException(nameof(targetType));
                if (properties == null)
                    throw new ArgumentNullException(nameof(properties));
                var storage = properties.ToArray();
                ValidateStorage(targetType, storage, allowGroupOverlap: true, nameof(properties));
                var projections = storage
                    .Select((property, index) => new EditorPropertyProjection(property, index, string.Empty, false))
                    .ToArray();
                return new EditorBindingLayout(storage, projections);
            }

            public static EditorBindingLayout ForProjected(
                Type targetType,
                IEnumerable<EditorPropertyBinding> storageProperties,
                IEnumerable<EditorPropertyProjection> projections)
            {
                if (targetType == null)
                    throw new ArgumentNullException(nameof(targetType));
                if (storageProperties == null)
                    throw new ArgumentNullException(nameof(storageProperties));
                if (projections == null)
                    throw new ArgumentNullException(nameof(projections));
                var storage = storageProperties.ToArray();
                var projected = projections.ToArray();
                ValidateStorage(targetType, storage, allowGroupOverlap: false, nameof(storageProperties));
                if (projected.Length == 0)
                    throw new ArgumentException("At least one schema projection is required.", nameof(projections));
                foreach (var projection in projected)
                {
                    if (projection.Metadata.TargetType != targetType)
                        throw new ArgumentException("Every projection must bind the object target type.", nameof(projections));
                    if (projection.StorageIndex >= storage.Length)
                        throw new ArgumentException("Projection storage index is outside the storage binding set.", nameof(projections));
                }
                return new EditorBindingLayout(storage, projected);
            }

            private static void ValidateStorage(
                Type targetType,
                EditorPropertyBinding[] properties,
                bool allowGroupOverlap,
                string parameterName)
            {
                if (properties.Length == 0)
                    throw new ArgumentException("At least one storage property binding is required.", parameterName);
                if (properties.Any(item => item.TargetType != targetType))
                    throw new ArgumentException("Every storage property must bind the object target type.", parameterName);
                for (var left = 0; left < properties.Length; left++)
                {
                    for (var right = left + 1; right < properties.Length; right++)
                    {
                        var leftIsAncestor = IsAncestor(properties[left].PropertyPath, properties[right].PropertyPath)
                            || IsAncestor(properties[left].MemberPath, properties[right].MemberPath);
                        var rightIsAncestor = IsAncestor(properties[right].PropertyPath, properties[left].PropertyPath)
                            || IsAncestor(properties[right].MemberPath, properties[left].MemberPath);
                        var allowedGroupContainer = allowGroupOverlap
                            && ((leftIsAncestor && properties[left].IsGroupContainer)
                                || (rightIsAncestor && properties[right].IsGroupContainer));
                        if ((leftIsAncestor || rightIsAncestor) && !allowedGroupContainer)
                        {
                            throw new ArgumentException(
                                "Overlapping editor bindings '" + properties[left].PropertyPath + "' and '"
                                + properties[right].PropertyPath + "' would patch the same object graph twice.",
                                parameterName);
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Default deep snapshotter for generated POCO values. Bindings for opaque application types can supply
    /// an explicit clone delegate instead.
    /// </summary>
    public static class EditorValueCloner
    {
        private static readonly MethodInfo MemberwiseCloneMethod = typeof(object).GetMethod(
            "MemberwiseClone",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("System.Object.MemberwiseClone was not found.");

        public static object? Clone(object? value)
        {
            if (value == null)
                return null;
            return Clone(value, new Dictionary<object, object>(ObjectReferenceComparer.Instance));
        }

        private static object Clone(object value, IDictionary<object, object> visited)
        {
            var type = value.GetType();
            if (IsAtomicImmutable(type))
                return value;

            object? existing;
            if (visited.TryGetValue(value, out existing))
                return existing;

            var array = value as Array;
            if (array != null)
            {
                if (array.Rank != 1)
                    throw new InvalidOperationException("Only one-dimensional arrays are supported by the default editor snapshotter.");
                var clone = Array.CreateInstance(type.GetElementType()!, array.Length);
                visited.Add(value, clone);
                for (var index = 0; index < array.Length; index++)
                    clone.SetValue(array.GetValue(index) == null ? null : Clone(array.GetValue(index)!, visited), index);
                return clone;
            }

            var dictionary = value as IDictionary;
            if (dictionary != null)
            {
                var clone = CreateInstance(type) as IDictionary;
                if (clone == null)
                    throw OpaqueType(type);
                visited.Add(value, clone);
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (entry.Key == null)
                        throw new InvalidOperationException("Null dictionary keys are not supported by the default editor snapshotter.");
                    var key = Clone(entry.Key, visited);
                    var item = entry.Value == null ? null : Clone(entry.Value, visited);
                    clone.Add(key, item);
                }
                return clone;
            }

            var list = value as IList;
            if (list != null)
            {
                var clone = CreateInstance(type) as IList;
                if (clone == null)
                    throw OpaqueType(type);
                visited.Add(value, clone);
                foreach (var item in list)
                    clone.Add(item == null ? null : Clone(item, visited));
                return clone;
            }

            var instance = MemberwiseCloneMethod.Invoke(value, null);
            if (instance == null)
                throw OpaqueType(type);
            visited.Add(value, instance);
            foreach (var field in GetInstanceFields(type))
            {
                var item = field.GetValue(value);
                var cloned = item == null ? null : Clone(item, visited);
                if (ReferenceEquals(item, cloned) || (item != null && IsAtomicImmutable(item.GetType())))
                    continue;
                try
                {
                    field.SetValue(instance, cloned);
                }
                catch (Exception exception) when (
                    exception is FieldAccessException
                    || exception is ArgumentException
                    || exception is TargetException)
                {
                    throw new InvalidOperationException(
                        "Field '" + field.Name + "' on type '" + type.FullName
                        + "' contains mutable state but cannot be safely deep-cloned. "
                        + "Provide a clone delegate on EditorPropertyBinding.",
                        exception);
                }
            }
            return instance;
        }

        internal static bool IsAtomicImmutable(Type type)
        {
            if (type == typeof(string)
                || type == typeof(Type)
                || type == typeof(Uri)
                || type == typeof(Version)
                || typeof(Delegate).IsAssignableFrom(type))
                return true;
            if (!type.IsValueType)
                return false;
            return IsAtomicImmutableValueType(type, new HashSet<Type>());
        }

        internal static IReadOnlyList<FieldInfo> GetInstanceFields(Type type)
        {
            var fields = new List<FieldInfo>();
            for (var current = type; current != null; current = current.BaseType)
            {
                fields.AddRange(current.GetFields(
                        BindingFlags.Instance
                        | BindingFlags.Public
                        | BindingFlags.NonPublic
                        | BindingFlags.DeclaredOnly)
                    .Where(static field => !field.IsStatic));
            }
            return fields
                .OrderBy(static field => field.DeclaringType?.FullName, StringComparer.Ordinal)
                .ThenBy(static field => field.Name, StringComparer.Ordinal)
                .ToArray();
        }

        private static bool IsAtomicImmutableValueType(Type type, ISet<Type> visiting)
        {
            if (!visiting.Add(type))
                return true;
            foreach (var field in GetInstanceFields(type))
            {
                var fieldType = field.FieldType;
                if (fieldType == typeof(string)
                    || fieldType == typeof(Type)
                    || fieldType == typeof(Uri)
                    || fieldType == typeof(Version)
                    || typeof(Delegate).IsAssignableFrom(fieldType))
                    continue;
                if (!fieldType.IsValueType || !IsAtomicImmutableValueType(fieldType, visiting))
                {
                    visiting.Remove(type);
                    return false;
                }
            }
            visiting.Remove(type);
            return true;
        }

        private static object? CreateInstance(Type type)
        {
            try
            {
                return Activator.CreateInstance(type);
            }
            catch (Exception exception) when (
                exception is MissingMethodException
                || exception is MemberAccessException
                || exception is TargetInvocationException)
            {
                return null;
            }
        }

        private static InvalidOperationException OpaqueType(Type type)
        {
            return new InvalidOperationException(
                "Type '" + type.FullName + "' cannot be deep-cloned by the default editor snapshotter. "
                + "Provide a clone delegate on EditorPropertyBinding.");
        }

        private sealed class ObjectReferenceComparer : IEqualityComparer<object>
        {
            public static readonly ObjectReferenceComparer Instance = new ObjectReferenceComparer();

            public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);

            public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
        }
    }

    /// <summary>Deterministic value equality used by default for lists and nested generated POCOs.</summary>
    public static class EditorValueComparer
    {
        public static bool StructuralEquals(object? left, object? right)
        {
            return StructuralEquals(left, right, new HashSet<ReferencePair>(ReferencePairComparer.Instance));
        }

        private static bool StructuralEquals(object? left, object? right, ISet<ReferencePair> visited)
        {
            if (ReferenceEquals(left, right))
                return true;
            if (left == null || right == null || left.GetType() != right.GetType())
                return false;

            var type = left.GetType();
            if (EditorValueCloner.IsAtomicImmutable(type))
                return left.Equals(right);

            var pair = new ReferencePair(left, right);
            if (!visited.Add(pair))
                return true;

            var leftDictionary = left as IDictionary;
            var rightDictionary = right as IDictionary;
            if (leftDictionary != null && rightDictionary != null)
            {
                if (leftDictionary.Count != rightDictionary.Count)
                    return false;
                foreach (DictionaryEntry entry in leftDictionary)
                {
                    if (!rightDictionary.Contains(entry.Key)
                        || !StructuralEquals(entry.Value, rightDictionary[entry.Key], visited))
                        return false;
                }
                return true;
            }

            var leftEnumerable = left as IEnumerable;
            var rightEnumerable = right as IEnumerable;
            if (leftEnumerable != null && rightEnumerable != null)
            {
                var first = leftEnumerable.GetEnumerator();
                var second = rightEnumerable.GetEnumerator();
                try
                {
                    while (true)
                    {
                        var firstNext = first.MoveNext();
                        var secondNext = second.MoveNext();
                        if (firstNext != secondNext)
                            return false;
                        if (!firstNext)
                            return true;
                        if (!StructuralEquals(first.Current, second.Current, visited))
                            return false;
                    }
                }
                finally
                {
                    (first as IDisposable)?.Dispose();
                    (second as IDisposable)?.Dispose();
                }
            }

            foreach (var field in EditorValueCloner.GetInstanceFields(type))
            {
                if (!StructuralEquals(field.GetValue(left), field.GetValue(right), visited))
                    return false;
            }
            return true;
        }

        private sealed class ReferencePair
        {
            public ReferencePair(object left, object right)
            {
                Left = left;
                Right = right;
            }

            public object Left { get; }

            public object Right { get; }
        }

        private sealed class ReferencePairComparer : IEqualityComparer<ReferencePair>
        {
            public static readonly ReferencePairComparer Instance = new ReferencePairComparer();

            public bool Equals(ReferencePair? x, ReferencePair? y)
            {
                return ReferenceEquals(x, y)
                    || (x != null && y != null
                        && ReferenceEquals(x.Left, y.Left)
                        && ReferenceEquals(x.Right, y.Right));
            }

            public int GetHashCode(ReferencePair obj)
            {
                unchecked
                {
                    return (RuntimeHelpers.GetHashCode(obj.Left) * 397) ^ RuntimeHelpers.GetHashCode(obj.Right);
                }
            }
        }
    }
}
