using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;

namespace ExcelDb.Editor.Model
{
    public enum EditorApplyStatus
    {
        Applied = 0,
        NoChanges = 1,
        RevisionConflict = 2,
        StateConflict = 3,
    }

    public sealed class EditorApplyResult
    {
        internal EditorApplyResult(
            EditorApplyStatus status,
            IEnumerable<string>? changedTargets = null,
            IEnumerable<string>? conflictingTargets = null)
        {
            Status = status;
            ChangedTargets = ReadOnlyStrings(changedTargets);
            ConflictingTargets = ReadOnlyStrings(conflictingTargets);
        }

        public EditorApplyStatus Status { get; }

        public IReadOnlyList<string> ChangedTargets { get; }

        public IReadOnlyList<string> ConflictingTargets { get; }

        public bool Succeeded => Status == EditorApplyStatus.Applied
            || Status == EditorApplyStatus.NoChanges;

        private static IReadOnlyList<string> ReadOnlyStrings(IEnumerable<string>? values)
        {
            return Array.AsReadOnly(
                (values ?? Array.Empty<string>())
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static item => item, StringComparer.Ordinal)
                .ToArray());
        }
    }

    public sealed class EditorUpdateResult
    {
        internal EditorUpdateResult(int refreshedTargets, int prunedHistoryEntries)
        {
            RefreshedTargets = refreshedTargets;
            PrunedHistoryEntries = prunedHistoryEntries;
        }

        public int RefreshedTargets { get; }

        public int PrunedHistoryEntries { get; }

        public bool ObservedExternalRevision => RefreshedTargets != 0;
    }

    /// <summary>
    /// Host-neutral staged editor view over one or more generated authoring POCOs.
    /// Staged values are canonical property snapshots and never alias the resident values.
    /// </summary>
    public sealed class EditorSerializedObject : IEnumerable<EditorSerializedProperty>, IDisposable
    {
        private readonly EditorObjectBinding _binding;
        private readonly EditorEditTarget[] _targets;
        private readonly object?[][] _working;
        private readonly object?[][] _observed;
        private readonly object?[][] _saved;
        private readonly EditorSerializedProperty[] _properties;

        public EditorSerializedObject(
            EditorObjectBinding binding,
            IEnumerable<EditorEditTarget> targets,
            EditorUndoHistory? history = null)
        {
            _binding = binding ?? throw new ArgumentNullException(nameof(binding));
            if (targets == null)
                throw new ArgumentNullException(nameof(targets));
            _targets = targets.ToArray();
            if (_targets.Length == 0)
                throw new ArgumentException("At least one edit target is required.", nameof(targets));
            if (_targets.Any(target => !_binding.TargetType.IsInstanceOfType(target.Value)))
                throw new ArgumentException("Every edit target must match the object binding type.", nameof(targets));
            if (_targets.Select(static item => item.Identity).Distinct(StringComparer.Ordinal).Count() != _targets.Length)
                throw new ArgumentException("Edit target identities must be unique.", nameof(targets));

            History = history ?? new EditorUndoHistory();
            _working = new object?[_targets.Length][];
            _observed = new object?[_targets.Length][];
            _saved = new object?[_targets.Length][];
            for (var index = 0; index < _targets.Length; index++)
            {
                _targets[index].AttachBinding(_binding);
                _targets[index].AcceptCurrentRevision();
                _observed[index] = _binding.Capture(_targets[index].Value);
                _working[index] = _binding.CloneSnapshot(_observed[index]);
                _saved[index] = _binding.CloneSnapshot(_observed[index]);
            }

            _properties = _binding.Properties
                .Select((property, index) =>
                {
                    var projection = _binding.GetProjection(index);
                    var storage = _binding.StorageProperties[projection.StorageIndex];
                    return new EditorSerializedProperty(
                        this,
                        storage,
                        property,
                        projection.StorageIndex,
                        EditorValuePath.FromMemberPath(storage.ValueType, projection.RelativeMemberPath),
                        isSchemaNode: true,
                        metadataApplies: true);
                })
                .ToArray();
            Targets = new ReadOnlyCollection<EditorEditTarget>(_targets);
            Properties = new ReadOnlyCollection<EditorSerializedProperty>(_properties);
            RootProperties = new ReadOnlyCollection<EditorSerializedProperty>(
                _properties.Where(property =>
                {
                    var parent = property.SchemaParentPropertyPath;
                    EditorPropertyBinding? ignored;
                    return parent == null || !_binding.TryGetProperty(parent, out ignored);
                }).ToArray());
            History.Changed += OnHistoryChanged;
        }

        public IReadOnlyList<EditorEditTarget> Targets { get; }

        public IReadOnlyList<EditorSerializedProperty> Properties { get; }

        public IReadOnlyList<EditorSerializedProperty> RootProperties { get; }

        public EditorUndoHistory History { get; }

        public bool HasModifiedProperties
        {
            get
            {
                for (var index = 0; index < _targets.Length; index++)
                {
                    if (!_binding.SnapshotsEqual(_observed[index], _working[index]))
                        return true;
                }
                return false;
            }
        }

        public bool IsDirty
        {
            get
            {
                for (var index = 0; index < _targets.Length; index++)
                {
                    if (!_binding.TargetEqualsSnapshot(_targets[index].Value, _saved[index]))
                        return true;
                }
                return false;
            }
        }

        public EditorSerializedProperty FindProperty(string propertyPath)
        {
            return _properties[_binding.GetPropertyIndex(propertyPath)];
        }

        public bool TryFindProperty(string propertyPath, out EditorSerializedProperty? property)
        {
            EditorPropertyBinding? ignored;
            if (!_binding.TryGetProperty(propertyPath, out ignored))
            {
                property = null;
                return false;
            }
            property = _properties[_binding.GetPropertyIndex(propertyPath)];
            return true;
        }

        public IEnumerator<EditorSerializedProperty> GetEnumerator()
        {
            return ((IEnumerable<EditorSerializedProperty>)_properties).GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public void Dispose()
        {
            History.Changed -= OnHistoryChanged;
        }

        /// <summary>
        /// Refreshes staged values. An observed external revision establishes a new clean baseline and
        /// prunes absolute history touching that stable identity.
        /// </summary>
        public EditorUpdateResult Update()
        {
            var refreshed = 0;
            var pruned = 0;
            for (var index = 0; index < _targets.Length; index++)
            {
                var target = _targets[index];
                if (target.RefreshExpectedRevision())
                {
                    refreshed++;
                    pruned += History.Prune(target.Identity);
                    _observed[index] = _binding.Capture(target.Value);
                    _saved[index] = _binding.CloneSnapshot(_observed[index]);
                }
                else
                {
                    _observed[index] = _binding.Capture(target.Value);
                }
                _working[index] = _binding.CloneSnapshot(_observed[index]);
            }
            return new EditorUpdateResult(refreshed, pruned);
        }

        public EditorUpdateResult DiscardModifiedProperties() => Update();

        /// <summary>
        /// Moves the persisted/clean baseline to the resident values. History remains valid, so undoing
        /// after a save makes the object dirty again.
        /// </summary>
        public void MarkSaved()
        {
            for (var index = 0; index < _targets.Length; index++)
            {
                _observed[index] = _binding.Capture(_targets[index].Value);
                _working[index] = _binding.CloneSnapshot(_observed[index]);
                _saved[index] = _binding.CloneSnapshot(_observed[index]);
                _targets[index].AcceptCurrentRevision();
            }
        }

        public EditorApplyResult ApplyModifiedProperties(string label)
        {
            if (string.IsNullOrWhiteSpace(label))
                throw new ArgumentException("An undo label is required.", nameof(label));

            var revisionConflicts = _targets
                .Where(static target => !target.IsRevisionCurrent)
                .Select(static target => target.Identity)
                .ToArray();
            if (revisionConflicts.Length != 0)
                return new EditorApplyResult(EditorApplyStatus.RevisionConflict, conflictingTargets: revisionConflicts);

            var stateConflicts = new List<string>();
            for (var index = 0; index < _targets.Length; index++)
            {
                if (!_binding.TargetEqualsSnapshot(_targets[index].Value, _observed[index]))
                    stateConflicts.Add(_targets[index].Identity);
            }
            if (stateConflicts.Count != 0)
                return new EditorApplyResult(EditorApplyStatus.StateConflict, conflictingTargets: stateConflicts);

            var changes = new List<EditorSnapshotChange>();
            for (var index = 0; index < _targets.Length; index++)
            {
                var target = _targets[index];
                if (_binding.SnapshotsEqual(_observed[index], _working[index]))
                    continue;
                var before = _binding.Capture(target.Value);
                var beforeVersion = target.MutationVersion;
                changes.Add(new EditorSnapshotChange(
                    target,
                    _binding,
                    before,
                    _working[index],
                    beforeVersion,
                    checked(beforeVersion + 1)));
            }

            if (changes.Count == 0)
                return new EditorApplyResult(EditorApplyStatus.NoChanges);

            var applied = new List<EditorSnapshotChange>();
            try
            {
                foreach (var change in changes)
                {
                    _binding.ApplySnapshot(change.Target.Value, change.After);
                    change.Target.MutationVersion = change.AfterVersion;
                    applied.Add(change);
                }
                History.Record(label, changes);
            }
            catch (Exception applyException)
            {
                var rollbackExceptions = new List<Exception>();
                for (var index = applied.Count - 1; index >= 0; index--)
                {
                    var change = applied[index];
                    try
                    {
                        _binding.ApplySnapshot(change.Target.Value, change.Before);
                        change.Target.MutationVersion = change.BeforeVersion;
                    }
                    catch (Exception rollbackException)
                    {
                        rollbackExceptions.Add(rollbackException);
                    }
                }
                if (rollbackExceptions.Count != 0)
                    throw new EditorAtomicApplyException(
                        "Multi-target editor apply",
                        applyException,
                        rollbackExceptions);
                throw;
            }

            for (var index = 0; index < _targets.Length; index++)
            {
                _observed[index] = _binding.Capture(_targets[index].Value);
                _working[index] = _binding.CloneSnapshot(_observed[index]);
            }
            return new EditorApplyResult(
                EditorApplyStatus.Applied,
                changes.Select(static change => change.Target.Identity));
        }

        internal object? GetStagedValue(int storageIndex, EditorValuePath path, int targetIndex)
        {
            EnsureTargetIndex(targetIndex);
            var binding = _binding.StorageProperties[storageIndex];
            var detachedRoot = binding.CloneValue(_working[targetIndex][storageIndex]);
            object? value;
            return path.TryRead(detachedRoot, out value)
                ? value
                : EditorValuePath.CreateDefault(path.ValueType);
        }

        internal void SetStagedValue(int storageIndex, EditorValuePath path, object? value)
        {
            var replacements = new object?[_working.Length];
            for (var index = 0; index < _working.Length; index++)
                replacements[index] = PrepareReplacement(storageIndex, path, _working[index][storageIndex], value);
            for (var index = 0; index < _working.Length; index++)
                _working[index][storageIndex] = replacements[index];
        }

        internal void SetStagedValue(int storageIndex, EditorValuePath path, int targetIndex, object? value)
        {
            EnsureTargetIndex(targetIndex);
            _working[targetIndex][storageIndex] = PrepareReplacement(
                storageIndex,
                path,
                _working[targetIndex][storageIndex],
                value);
        }

        internal bool HasMixedValue(int storageIndex, EditorValuePath path)
        {
            var binding = _binding.StorageProperties[storageIndex];
            object? first;
            if (!path.TryRead(_working[0][storageIndex], out first))
                first = EditorValuePath.CreateDefault(path.ValueType);
            for (var index = 1; index < _working.Length; index++)
            {
                object? current;
                if (!path.TryRead(_working[index][storageIndex], out current))
                    current = EditorValuePath.CreateDefault(path.ValueType);
                if (path.IsRoot
                    ? !binding.ValuesEqual(first, current)
                    : !EditorValueComparer.StructuralEquals(first, current))
                    return true;
            }
            return false;
        }

        internal bool GetStagedPresence(int storageIndex, EditorValuePath path, int targetIndex)
        {
            EnsureTargetIndex(targetIndex);
            object? value;
            return path.TryRead(_working[targetIndex][storageIndex], out value) && value != null;
        }

        internal bool HasMixedPresence(int storageIndex, EditorValuePath path)
        {
            var first = GetStagedPresence(storageIndex, path, 0);
            for (var index = 1; index < _working.Length; index++)
            {
                if (GetStagedPresence(storageIndex, path, index) != first)
                    return true;
            }
            return false;
        }

        internal void SetStagedPresence(
            int storageIndex,
            EditorValuePath path,
            bool present,
            Type valueType)
        {
            var replacements = new object?[_working.Length];
            var materialized = present ? EditorValuePath.CreatePresentDefault(valueType) : null;
            for (var index = 0; index < _working.Length; index++)
            {
                var currentRoot = _working[index][storageIndex];
                object? current;
                var currentlyPresent = path.TryRead(currentRoot, out current) && current != null;
                replacements[index] = currentlyPresent == present
                    ? currentRoot
                    : PrepareReplacement(storageIndex, path, currentRoot, materialized);
            }
            for (var index = 0; index < _working.Length; index++)
                _working[index][storageIndex] = replacements[index];
        }

        internal bool HasDynamicTemplates(int storageIndex) => _binding.HasTemplates(storageIndex);

        internal bool HasDynamicMetadataBelow(int storageIndex, string relativeMemberPath)
        {
            return _binding.HasTemplateDescendant(storageIndex, relativeMemberPath);
        }

        internal bool TryGetDynamicMetadata(
            int storageIndex,
            string relativeMemberPath,
            out EditorPropertyBinding? property)
        {
            return _binding.TryGetTemplate(storageIndex, relativeMemberPath, out property);
        }

        internal IReadOnlyList<EditorSerializedProperty> GetSchemaChildren(string propertyPath)
        {
            return new ReadOnlyCollection<EditorSerializedProperty>(
                _properties
                    .Where(property => string.Equals(
                        property.SchemaParentPropertyPath,
                        propertyPath,
                        StringComparison.Ordinal))
                    .ToArray());
        }

        internal void EnsureListIndex(int storageIndex, EditorValuePath path, int index)
        {
            if (index < 0)
                throw new ArgumentOutOfRangeException(nameof(index));
            for (var targetIndex = 0; targetIndex < _working.Length; targetIndex++)
            {
                var list = RequireList(ReadOrDefault(_working[targetIndex][storageIndex], path), path.DisplayPath);
                if (index >= list.Count)
                    throw new ArgumentOutOfRangeException(nameof(index));
            }
        }

        internal int GetListSize(int storageIndex, EditorValuePath path, int targetIndex)
        {
            EnsureTargetIndex(targetIndex);
            return RequireList(ReadOrDefault(_working[targetIndex][storageIndex], path), path.DisplayPath).Count;
        }

        internal bool HasMixedListSize(int storageIndex, EditorValuePath path)
        {
            var first = GetListSize(storageIndex, path, 0);
            for (var index = 1; index < _working.Length; index++)
            {
                if (GetListSize(storageIndex, path, index) != first)
                    return true;
            }
            return false;
        }

        internal void ResizeList(int propertyIndex, EditorValuePath path, int size, Type elementType)
        {
            if (size < 0)
                throw new ArgumentOutOfRangeException(nameof(size));
            MutateLists(propertyIndex, path, (list, _) =>
            {
                while (list.Count > size)
                    list.RemoveAt(list.Count - 1);
                while (list.Count < size)
                    list.Add(EditorValuePath.CreateDefault(elementType));
            });
        }

        internal void InsertListElement(
            int propertyIndex,
            EditorValuePath path,
            int index,
            Type elementType,
            object? value)
        {
            EditorValuePath.EnsureValueType(elementType, value);
            MutateLists(propertyIndex, path, (list, _) =>
            {
                if (index < 0 || index > list.Count)
                    throw new ArgumentOutOfRangeException(nameof(index));
                list.Insert(index, EditorValueCloner.Clone(value));
            });
        }

        internal void DeleteListElement(int propertyIndex, EditorValuePath path, int index)
        {
            MutateLists(propertyIndex, path, (list, _) =>
            {
                if (index < 0 || index >= list.Count)
                    throw new ArgumentOutOfRangeException(nameof(index));
                list.RemoveAt(index);
            });
        }

        internal void MoveListElement(int propertyIndex, EditorValuePath path, int sourceIndex, int destinationIndex)
        {
            MutateLists(propertyIndex, path, (list, _) =>
            {
                if (sourceIndex < 0 || sourceIndex >= list.Count)
                    throw new ArgumentOutOfRangeException(nameof(sourceIndex));
                if (destinationIndex < 0 || destinationIndex >= list.Count)
                    throw new ArgumentOutOfRangeException(nameof(destinationIndex));
                if (sourceIndex == destinationIndex)
                    return;
                var value = list[sourceIndex];
                list.RemoveAt(sourceIndex);
                list.Insert(destinationIndex, value);
            });
        }

        private object? PrepareReplacement(
            int storageIndex,
            EditorValuePath path,
            object? currentRoot,
            object? value)
        {
            var binding = _binding.StorageProperties[storageIndex];
            if (binding.IsReadOnly || path.IsReadOnly)
                throw new InvalidOperationException("Property '" + binding.PropertyPath + path.DisplayPath + "' is read-only.");
            if (path.IsRoot)
                return binding.PrepareValue(value);

            EditorValuePath.EnsureValueType(path.ValueType, value);
            var detachedRoot = binding.CloneValue(currentRoot);
            var replacement = EditorValueCloner.Clone(value);
            return binding.CloneValue(path.Write(detachedRoot, replacement));
        }

        private void MutateLists(
            int storageIndex,
            EditorValuePath path,
            Action<IList, int> mutation)
        {
            var binding = _binding.StorageProperties[storageIndex];
            if (binding.IsReadOnly || path.IsReadOnly)
                throw new InvalidOperationException("Property '" + binding.PropertyPath + path.DisplayPath + "' is read-only.");
            var replacements = new object?[_working.Length];
            for (var index = 0; index < _working.Length; index++)
            {
                var detachedRoot = binding.CloneValue(_working[index][storageIndex]);
                object? listValue;
                if (!path.TryRead(detachedRoot, out listValue) || listValue == null)
                    listValue = EditorValuePath.CreateDefault(path.ValueType);
                var list = RequireList(listValue, path.DisplayPath);
                if (list.IsReadOnly || list.IsFixedSize)
                    throw new InvalidOperationException("List property '" + binding.PropertyPath + path.DisplayPath + "' is fixed or read-only.");
                mutation(list, index);
                var updatedRoot = path.IsRoot ? list : path.Write(detachedRoot, list);
                replacements[index] = binding.CloneValue(updatedRoot);
            }
            for (var index = 0; index < _working.Length; index++)
                _working[index][storageIndex] = replacements[index];
        }

        private static object? ReadOrDefault(object? root, EditorValuePath path)
        {
            object? value;
            if (!path.TryRead(root, out value) || value == null)
                return EditorValuePath.CreateDefault(path.ValueType);
            return value;
        }

        private static IList RequireList(object? value, string path)
        {
            var list = value as IList;
            if (list == null)
                throw new InvalidOperationException("Property '" + path + "' is not a mutable IList.");
            return list;
        }

        private void OnHistoryChanged(object? sender, EditorHistoryChangedEventArgs args)
        {
            if (args.Kind != EditorHistoryChangeKind.Recorded
                && args.Kind != EditorHistoryChangeKind.Undo
                && args.Kind != EditorHistoryChangeKind.Redo)
                return;
            var changed = new HashSet<string>(args.TargetIdentities, StringComparer.Ordinal);
            for (var index = 0; index < _targets.Length; index++)
            {
                if (changed.Contains(_targets[index].Identity))
                {
                    _observed[index] = _binding.Capture(_targets[index].Value);
                    _working[index] = _binding.CloneSnapshot(_observed[index]);
                }
            }
        }

        private void EnsureTargetIndex(int targetIndex)
        {
            if (targetIndex < 0 || targetIndex >= _targets.Length)
                throw new ArgumentOutOfRangeException(nameof(targetIndex));
        }
    }

    public sealed class EditorSerializedProperty
    {
        private readonly EditorSerializedObject _owner;
        private readonly EditorPropertyBinding _storageBinding;
        private readonly EditorPropertyBinding _metadataBinding;
        private readonly int _storageIndex;
        private readonly EditorValuePath _path;
        private readonly bool _isSchemaNode;
        private readonly bool _metadataApplies;

        internal EditorSerializedProperty(
            EditorSerializedObject owner,
            EditorPropertyBinding storageBinding,
            EditorPropertyBinding metadataBinding,
            int storageIndex,
            EditorValuePath path,
            bool isSchemaNode,
            bool metadataApplies)
        {
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
            _storageBinding = storageBinding ?? throw new ArgumentNullException(nameof(storageBinding));
            _metadataBinding = metadataBinding ?? throw new ArgumentNullException(nameof(metadataBinding));
            _storageIndex = storageIndex;
            _path = path ?? throw new ArgumentNullException(nameof(path));
            _isSchemaNode = isSchemaNode;
            _metadataApplies = metadataApplies;
        }

        public int FieldId => _metadataBinding.FieldId;

        public IReadOnlyList<int> FieldIdPath => _metadataBinding.FieldIdPath;

        public string PropertyPath => _isSchemaNode
            ? _metadataBinding.PropertyPath
            : _storageBinding.PropertyPath + _path.DisplayPath;

        public string SchemaPropertyPath => _metadataApplies
            ? _metadataBinding.PropertyPath
            : _storageBinding.PropertyPath;

        public string MemberPath => _isSchemaNode
            ? _metadataBinding.MemberPath
            : _storageBinding.MemberPath + _path.MemberPath;

        public string? SchemaParentPropertyPath => _metadataApplies
            ? _metadataBinding.ParentPropertyPath
            : null;

        public string? DisplayName => _metadataApplies
            ? (_metadataBinding.DisplayName ?? _path.DisplayName)
            : _path.DisplayName;

        public string? Tooltip => _metadataApplies ? _metadataBinding.Tooltip : null;

        public Type ValueType => _path.ValueType;

        public EditorPropertyKind Kind => _metadataApplies
            ? _metadataBinding.Kind
            : EditorValuePath.InferKind(_path.ValueType);

        public int Depth => _isSchemaNode
            ? _metadataBinding.Depth
            : _storageBinding.Depth + _path.Depth;

        public int TargetCount => _owner.Targets.Count;

        public bool IsReadOnly => _storageBinding.IsReadOnly
            || _path.IsReadOnly
            || (_metadataApplies && _metadataBinding.IsReadOnly);

        public bool HasPresence => _metadataApplies && _metadataBinding.HasPresence;

        public bool Required => _metadataApplies && _metadataBinding.Required;

        public int KeyOrder => _metadataApplies ? _metadataBinding.KeyOrder : 0;

        public bool IsGroupContainer => _metadataApplies && _metadataBinding.IsGroupContainer;

        public string? UnsupportedReason => _metadataApplies
            ? _metadataBinding.UnsupportedReason
            : null;

        public bool HasPresenceValue
        {
            get
            {
                EnsurePresence();
                return _owner.GetStagedPresence(_storageIndex, _path, 0);
            }
        }

        public bool HasMultipleDifferentPresenceValues
        {
            get
            {
                EnsurePresence();
                return _owner.HasMixedPresence(_storageIndex, _path);
            }
        }

        public bool HasMultipleDifferentValues => _owner.HasMixedValue(_storageIndex, _path);

        public bool IsArray => typeof(IList).IsAssignableFrom(ValueType);

        public bool HasMultipleDifferentArraySizes => IsArray && _owner.HasMixedListSize(_storageIndex, _path);

        public EditorReferenceConstraint? ReferenceConstraint => _metadataApplies
            ? _metadataBinding.ReferenceConstraint
            : null;

        public bool HasReferenceAdapter => ReferenceConstraint != null;

        public EditorReferenceValue? ReferenceValue
        {
            get
            {
                EnsureReference();
                return _metadataBinding.ReadReference(_owner.GetStagedValue(_storageIndex, _path, 0));
            }
            set
            {
                EnsureReference();
                EnsureWritable();
                _owner.SetStagedValue(_storageIndex, _path, _metadataBinding.WriteReference(value));
            }
        }

        public object? BoxedValue
        {
            get => GetBoxedValueAt(0);
            set
            {
                EnsureWritable();
                _owner.SetStagedValue(_storageIndex, _path, value);
            }
        }

        public int ArraySize
        {
            get
            {
                EnsureArray();
                return _owner.GetListSize(_storageIndex, _path, 0);
            }
            set
            {
                EnsureArray();
                EnsureWritable();
                _owner.ResizeList(_storageIndex, _path, value, EditorValuePath.GetListElementType(ValueType));
            }
        }

        public IReadOnlyList<EditorSerializedProperty> Children
        {
            get
            {
                if (_isSchemaNode)
                {
                    var schemaChildren = _owner.GetSchemaChildren(_metadataBinding.PropertyPath);
                    if (schemaChildren.Count != 0)
                        return schemaChildren;
                }
                if (Kind != EditorPropertyKind.Object && Kind != EditorPropertyKind.Group)
                    return Array.Empty<EditorSerializedProperty>();

                var filterByTemplates = _owner.HasDynamicTemplates(_storageIndex);
                var children = new List<EditorSerializedProperty>();
                foreach (var member in EditorValuePath.GetChildMembers(ValueType))
                {
                    var childPath = _path.Append(member);
                    EditorPropertyBinding? metadata;
                    var hasMetadata = _owner.TryGetDynamicMetadata(
                        _storageIndex,
                        childPath.TemplateMemberPath,
                        out metadata);
                    var hasDescendantMetadata = _owner.HasDynamicMetadataBelow(
                        _storageIndex,
                        childPath.TemplateMemberPath);
                    if (filterByTemplates && !hasMetadata && !hasDescendantMetadata)
                        continue;
                    children.Add(new EditorSerializedProperty(
                        _owner,
                        _storageBinding,
                        metadata ?? _storageBinding,
                        _storageIndex,
                        childPath,
                        isSchemaNode: false,
                        metadataApplies: hasMetadata));
                }
                return new ReadOnlyCollection<EditorSerializedProperty>(children);
            }
        }

        public bool HasChildren => Children.Count != 0;

        public object? GetBoxedValueAt(int targetIndex)
        {
            return _owner.GetStagedValue(_storageIndex, _path, targetIndex);
        }

        public void SetBoxedValueAt(int targetIndex, object? value)
        {
            EnsureWritable();
            _owner.SetStagedValue(_storageIndex, _path, targetIndex, value);
        }

        public EditorSerializedProperty? FindRelativeProperty(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("A relative property name is required.", nameof(name));
            return Children.FirstOrDefault(property =>
                string.Equals(property.DisplayName, name, StringComparison.Ordinal)
                || string.Equals(EditorValuePath.LastName(property.PropertyPath), name, StringComparison.Ordinal)
                || string.Equals(EditorValuePath.LastName(property.MemberPath), name, StringComparison.Ordinal));
        }

        public EditorSerializedProperty GetArrayElementAtIndex(int index)
        {
            EnsureArray();
            _owner.EnsureListIndex(_storageIndex, _path, index);
            return new EditorSerializedProperty(
                _owner,
                _storageBinding,
                _metadataBinding,
                _storageIndex,
                _path.Append(index, EditorValuePath.GetListElementType(ValueType)),
                isSchemaNode: false,
                metadataApplies: false);
        }

        public void InsertArrayElementAtIndex(int index)
        {
            EnsureArray();
            var elementType = EditorValuePath.GetListElementType(ValueType);
            InsertArrayElementAtIndex(index, EditorValuePath.CreateDefault(elementType));
        }

        public void InsertArrayElementAtIndex(int index, object? value)
        {
            EnsureArray();
            EnsureWritable();
            _owner.InsertListElement(
                _storageIndex,
                _path,
                index,
                EditorValuePath.GetListElementType(ValueType),
                value);
        }

        public void DeleteArrayElementAtIndex(int index)
        {
            EnsureArray();
            EnsureWritable();
            _owner.DeleteListElement(_storageIndex, _path, index);
        }

        public void MoveArrayElement(int sourceIndex, int destinationIndex)
        {
            EnsureArray();
            EnsureWritable();
            _owner.MoveListElement(_storageIndex, _path, sourceIndex, destinationIndex);
        }

        public void SetPresence(bool present)
        {
            EnsurePresence();
            EnsurePresenceWritable();
            _owner.SetStagedPresence(_storageIndex, _path, present, ValueType);
        }

        private void EnsureArray()
        {
            if (!IsArray)
                throw new InvalidOperationException("Property '" + PropertyPath + "' is not an IList.");
        }

        private void EnsureReference()
        {
            if (!HasReferenceAdapter)
                throw new InvalidOperationException("Property '" + PropertyPath + "' has no reference adapter.");
        }

        private void EnsurePresence()
        {
            if (!HasPresence)
                throw new InvalidOperationException("Property '" + PropertyPath + "' has no presence metadata.");
        }

        private void EnsurePresenceWritable()
        {
            if (ValueType.IsValueType && Nullable.GetUnderlyingType(ValueType) == null)
                throw new InvalidOperationException(
                    "Property '" + PropertyPath + "' cannot represent an absent value because '"
                    + ValueType.FullName + "' is not nullable.");
            if (_storageBinding.IsReadOnly || _path.IsReadOnly
                || _metadataBinding.KeyOrder > 0
                || _metadataBinding.UnsupportedReason != null
                || (_metadataBinding.IsReadOnly && !_metadataBinding.IsGroupContainer))
            {
                throw new InvalidOperationException(
                    "Presence for property '" + PropertyPath + "' is read-only."
                    + (UnsupportedReason == null ? string.Empty : " " + UnsupportedReason));
            }
        }

        private void EnsureWritable()
        {
            if (!IsReadOnly)
                return;
            var diagnostic = UnsupportedReason == null ? string.Empty : " " + UnsupportedReason;
            throw new InvalidOperationException("Property '" + PropertyPath + "' is read-only." + diagnostic);
        }
    }

    internal sealed class EditorValuePath
    {
        private readonly EditorPathSegment[] _segments;
        private readonly Type _rootType;

        private EditorValuePath(EditorPathSegment[] segments, Type rootType, Type valueType)
        {
            _segments = segments;
            _rootType = rootType;
            ValueType = valueType;
        }

        public bool IsRoot => _segments.Length == 0;

        public int Depth => _segments.Length;

        public Type ValueType { get; }

        public bool IsReadOnly => _segments.Length != 0 && _segments[_segments.Length - 1].IsReadOnly;

        public string DisplayName => _segments.Length == 0 ? string.Empty : _segments[_segments.Length - 1].DisplayName;

        public string DisplayPath => string.Concat(_segments.Select(static item => item.DisplayPath));

        public string MemberPath => string.Concat(_segments.Select(static item => item.MemberPath));

        public string TemplateMemberPath => string.Join(
            ".",
            _segments
                .Select(static item => item.TemplateMemberName)
                .Where(static item => item.Length != 0));

        public static EditorValuePath Root(Type valueType)
        {
            return new EditorValuePath(Array.Empty<EditorPathSegment>(), valueType, valueType);
        }

        public static EditorValuePath FromMemberPath(Type rootType, string relativeMemberPath)
        {
            var path = Root(rootType);
            if (string.IsNullOrWhiteSpace(relativeMemberPath))
                return path;
            foreach (var name in relativeMemberPath.Split('.'))
            {
                var ownerType = Nullable.GetUnderlyingType(path.ValueType) ?? path.ValueType;
                var member = GetChildMembers(ownerType).FirstOrDefault(
                    item => string.Equals(item.Name, name, StringComparison.Ordinal));
                if (member == null)
                    throw new InvalidOperationException(
                        "Projection member path '" + relativeMemberPath + "' cannot resolve '"
                        + name + "' on '" + ownerType.FullName + "'.");
                path = path.Append(member);
            }
            return path;
        }

        public EditorValuePath Append(int index, Type elementType)
        {
            return Append(new EditorPathSegment(index, elementType));
        }

        public EditorValuePath Append(MemberInfo member)
        {
            return Append(new EditorPathSegment(member));
        }

        public object? Read(object? root)
        {
            object? value;
            if (!TryRead(root, out value))
                throw new InvalidOperationException("Value path '" + DisplayPath + "' crosses null or a missing list element.");
            return value;
        }

        public bool TryRead(object? root, out object? value)
        {
            value = root;
            foreach (var segment in _segments)
            {
                if (!segment.TryRead(value, out value))
                    return false;
            }
            return true;
        }

        public object? Write(object? root, object? value)
        {
            if (IsRoot)
                return value;
            if (root == null)
                root = CreateRequiredInstance(_rootType);
            return Write(root, 0, value);
        }

        public static EditorPropertyKind InferKind(Type valueType)
        {
            if (typeof(IList).IsAssignableFrom(valueType))
                return EditorPropertyKind.List;
            var type = Nullable.GetUnderlyingType(valueType) ?? valueType;
            if (type.IsValueType || type == typeof(string))
                return EditorPropertyKind.Scalar;
            return EditorPropertyKind.Object;
        }

        public static Type GetListElementType(Type listType)
        {
            if (listType.IsArray)
                return listType.GetElementType()!;
            var generic = listType.GetInterfaces()
                .Append(listType)
                .FirstOrDefault(type => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IList<>));
            return generic == null ? typeof(object) : generic.GetGenericArguments()[0];
        }

        public static IReadOnlyList<MemberInfo> GetChildMembers(Type ownerType)
        {
            ownerType = Nullable.GetUnderlyingType(ownerType) ?? ownerType;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public;
            var properties = ownerType.GetProperties(flags)
                .Where(static property => property.GetMethod != null && property.GetIndexParameters().Length == 0)
                .Cast<MemberInfo>();
            var fields = ownerType.GetFields(flags)
                .Where(static field => !field.IsStatic)
                .Cast<MemberInfo>();
            return new ReadOnlyCollection<MemberInfo>(
                properties.Concat(fields).OrderBy(static member => member.Name, StringComparer.Ordinal).ToArray());
        }

        public static object? CreateDefault(Type type)
        {
            if (type == typeof(string))
                return string.Empty;
            if (type.IsArray)
                return Array.CreateInstance(type.GetElementType()!, 0);
            if (type.IsValueType)
                return Activator.CreateInstance(type);
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

        public static object CreatePresentDefault(Type type)
        {
            var concreteType = Nullable.GetUnderlyingType(type) ?? type;
            var value = CreateDefault(concreteType);
            if (value == null)
                throw new InvalidOperationException(
                    "Cannot materialize presence for type '" + type.FullName
                    + "'. A public parameterless constructor is required.");
            return value;
        }

        public static void EnsureValueType(Type type, object? value)
        {
            if (value == null)
            {
                if (type.IsValueType && Nullable.GetUnderlyingType(type) == null)
                    throw new ArgumentException("A non-null " + type.FullName + " value is required.", nameof(value));
                return;
            }
            var accepted = Nullable.GetUnderlyingType(type) ?? type;
            if (!accepted.IsInstanceOfType(value))
                throw new ArgumentException("Value is not a " + type.FullName + ".", nameof(value));
        }

        public static string LastName(string path)
        {
            var dot = path.LastIndexOf('.');
            var bracket = path.LastIndexOf('[');
            var index = Math.Max(dot, bracket);
            return index < 0 ? path : path.Substring(index + 1).TrimEnd(']');
        }

        private EditorValuePath Append(EditorPathSegment segment)
        {
            var segments = new EditorPathSegment[_segments.Length + 1];
            Array.Copy(_segments, segments, _segments.Length);
            segments[segments.Length - 1] = segment;
            return new EditorValuePath(segments, _rootType, segment.ValueType);
        }

        private object? Write(object? owner, int segmentIndex, object? value)
        {
            if (owner == null)
                throw new InvalidOperationException("Value path '" + DisplayPath + "' crosses null.");
            var segment = _segments[segmentIndex];
            if (segmentIndex == _segments.Length - 1)
                return segment.Write(owner, value);

            object? child;
            if (!segment.TryRead(owner, out child))
                throw new InvalidOperationException("Value path '" + DisplayPath + "' crosses a missing list element.");
            if (child == null)
                child = CreateRequiredInstance(segment.ValueType);
            var updated = Write(child, segmentIndex + 1, value);
            return segment.Write(owner, updated);
        }

        private static object CreateRequiredInstance(Type type)
        {
            var instance = CreateDefault(Nullable.GetUnderlyingType(type) ?? type);
            if (instance == null)
                throw new InvalidOperationException(
                    "Cannot materialize null editor path segment of type '" + type.FullName
                    + "'. A public parameterless constructor is required.");
            return instance;
        }
    }

    internal sealed class EditorPathSegment
    {
        private readonly int? _index;
        private readonly MemberInfo? _member;

        public EditorPathSegment(int index, Type valueType)
        {
            if (index < 0)
                throw new ArgumentOutOfRangeException(nameof(index));
            _index = index;
            ValueType = valueType;
            DisplayName = "Element " + index;
            DisplayPath = "[" + index + "]";
            MemberPath = "[" + index + "]";
        }

        public EditorPathSegment(MemberInfo member)
        {
            _member = member ?? throw new ArgumentNullException(nameof(member));
            var property = member as PropertyInfo;
            ValueType = property == null ? ((FieldInfo)member).FieldType : property.PropertyType;
            DisplayName = member.Name;
            DisplayPath = "." + member.Name;
            MemberPath = "." + member.Name;
            IsReadOnly = property == null ? ((FieldInfo)member).IsInitOnly : property.SetMethod == null;
        }

        public Type ValueType { get; }

        public bool IsReadOnly { get; }

        public string DisplayName { get; }

        public string DisplayPath { get; }

        public string MemberPath { get; }

        public string TemplateMemberName => _member == null ? string.Empty : _member.Name;

        public bool TryRead(object? owner, out object? value)
        {
            if (owner == null)
            {
                value = null;
                return false;
            }
            if (_index.HasValue)
            {
                var list = owner as IList;
                if (list == null || _index.Value >= list.Count)
                {
                    value = null;
                    return false;
                }
                value = list[_index.Value];
                return true;
            }

            var property = _member as PropertyInfo;
            value = property == null
                ? ((FieldInfo)_member!).GetValue(owner)
                : property.GetValue(owner, null);
            return true;
        }

        public object? Write(object owner, object? value)
        {
            if (_index.HasValue)
            {
                var list = owner as IList;
                if (list == null || _index.Value >= list.Count)
                    throw new InvalidOperationException("List element no longer exists.");
                list[_index.Value] = value;
                return owner;
            }

            if (IsReadOnly)
                throw new InvalidOperationException("Member '" + _member!.Name + "' is read-only.");
            var property = _member as PropertyInfo;
            if (property == null)
                ((FieldInfo)_member!).SetValue(owner, value);
            else
                property.SetValue(owner, value, null);
            return owner;
        }
    }
}
