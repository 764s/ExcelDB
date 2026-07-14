using System.Collections.Immutable;
using ExcelDb.Core.Identity;
using ExcelDb.Runtime.References;

namespace ExcelDb.Runtime;

public abstract class RuntimeTableBinding
{
    protected RuntimeTableBinding(int tableId, Type runtimeType)
    {
        if (tableId <= 0)
            throw new ArgumentOutOfRangeException(nameof(tableId));

        TableId = tableId;
        RuntimeType = runtimeType ?? throw new ArgumentNullException(nameof(runtimeType));
    }

    public int TableId { get; }

    public Type RuntimeType { get; }

    public abstract object Create();

    public abstract void Apply(object instance, RuntimeAssetRecord record);

    /// <summary>
    /// Provider-aware apply seam. Existing generated bindings remain valid; bindings containing
    /// external references override this method and retain only pre-bound handles on the instance.
    /// </summary>
    public virtual void Apply(
        object instance,
        RuntimeAssetRecord record,
        RuntimeReferenceServices referenceServices) => Apply(instance, record);

    public abstract void ResetToDefaults(object instance);

    public abstract object CaptureState(object instance);

    /// <summary>
    /// True when rollback state can be allocated once at session initialization and overwritten
    /// in place before each steady-state patch.
    /// </summary>
    public virtual bool SupportsReusableRollbackState => false;

    public virtual object CreateReusableRollbackState(object instance) =>
        throw new NotSupportedException("This binding does not provide reusable rollback state.");

    public virtual void CaptureReusableRollbackState(object instance, object state) =>
        throw new NotSupportedException("This binding does not provide reusable rollback state.");

    public abstract void RestoreState(object instance, object state);

    public virtual bool CanPatch(RuntimeAssetRecord previous, RuntimeAssetRecord candidate) => true;
}

/// <summary>A deterministic delegate binding intended to be emitted by M1 code generation.</summary>
public sealed class RuntimeTableBinding<T> : RuntimeTableBinding
    where T : class
{
    private readonly Func<T> _factory;
    private readonly Action<T, RuntimeAssetRecord> _apply;
    private readonly Action<T, RuntimeAssetRecord, RuntimeReferenceServices>? _applyWithReferences;
    private readonly Action<T> _reset;
    private readonly Func<T, object>? _capture;
    private readonly Func<T, object>? _createReusableRollbackState;
    private readonly Action<T, object>? _captureReusableRollbackState;
    private readonly Action<T, object> _restore;
    private readonly Func<RuntimeAssetRecord, RuntimeAssetRecord, bool>? _canPatch;

    public RuntimeTableBinding(
        int tableId,
        Func<T> factory,
        Action<T, RuntimeAssetRecord> apply,
        Action<T> reset,
        Func<T, object> captureState,
        Action<T, object> restoreState,
        Func<RuntimeAssetRecord, RuntimeAssetRecord, bool>? canPatch = null)
        : base(tableId, typeof(T))
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        _reset = reset ?? throw new ArgumentNullException(nameof(reset));
        _capture = captureState ?? throw new ArgumentNullException(nameof(captureState));
        _restore = restoreState ?? throw new ArgumentNullException(nameof(restoreState));
        _canPatch = canPatch;
    }

    public RuntimeTableBinding(
        int tableId,
        Func<T> factory,
        Action<T, RuntimeAssetRecord> apply,
        Action<T> reset,
        Func<T, object> createReusableRollbackState,
        Action<T, object> captureReusableRollbackState,
        Action<T, object> restoreState,
        Func<RuntimeAssetRecord, RuntimeAssetRecord, bool>? canPatch = null)
        : base(tableId, typeof(T))
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        _reset = reset ?? throw new ArgumentNullException(nameof(reset));
        _createReusableRollbackState = createReusableRollbackState
            ?? throw new ArgumentNullException(nameof(createReusableRollbackState));
        _captureReusableRollbackState = captureReusableRollbackState
            ?? throw new ArgumentNullException(nameof(captureReusableRollbackState));
        _restore = restoreState ?? throw new ArgumentNullException(nameof(restoreState));
        _canPatch = canPatch;
    }

    public RuntimeTableBinding(
        int tableId,
        Func<T> factory,
        Action<T, RuntimeAssetRecord, RuntimeReferenceServices> apply,
        Action<T> reset,
        Func<T, object> captureState,
        Action<T, object> restoreState,
        Func<RuntimeAssetRecord, RuntimeAssetRecord, bool>? canPatch = null)
        : base(tableId, typeof(T))
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _apply = static (_, _) => { };
        _applyWithReferences = apply ?? throw new ArgumentNullException(nameof(apply));
        _reset = reset ?? throw new ArgumentNullException(nameof(reset));
        _capture = captureState ?? throw new ArgumentNullException(nameof(captureState));
        _restore = restoreState ?? throw new ArgumentNullException(nameof(restoreState));
        _canPatch = canPatch;
    }

    public RuntimeTableBinding(
        int tableId,
        Func<T> factory,
        Action<T, RuntimeAssetRecord, RuntimeReferenceServices> apply,
        Action<T> reset,
        Func<T, object> createReusableRollbackState,
        Action<T, object> captureReusableRollbackState,
        Action<T, object> restoreState,
        Func<RuntimeAssetRecord, RuntimeAssetRecord, bool>? canPatch = null)
        : base(tableId, typeof(T))
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _apply = static (_, _) => { };
        _applyWithReferences = apply ?? throw new ArgumentNullException(nameof(apply));
        _reset = reset ?? throw new ArgumentNullException(nameof(reset));
        _createReusableRollbackState = createReusableRollbackState
            ?? throw new ArgumentNullException(nameof(createReusableRollbackState));
        _captureReusableRollbackState = captureReusableRollbackState
            ?? throw new ArgumentNullException(nameof(captureReusableRollbackState));
        _restore = restoreState ?? throw new ArgumentNullException(nameof(restoreState));
        _canPatch = canPatch;
    }

    public override object Create() =>
        _factory() ?? throw new InvalidOperationException($"Factory for table {TableId} returned null.");

    public override void Apply(object instance, RuntimeAssetRecord record) => _apply(RequireType(instance), record);

    public override void Apply(
        object instance,
        RuntimeAssetRecord record,
        RuntimeReferenceServices referenceServices)
    {
        RuntimeCompatibility.NotNull(referenceServices, nameof(referenceServices));
        var typed = RequireType(instance);
        if (_applyWithReferences is null)
            _apply(typed, record);
        else
            _applyWithReferences(typed, record, referenceServices);
    }

    public override void ResetToDefaults(object instance) => _reset(RequireType(instance));

    public override object CaptureState(object instance)
    {
        var typed = RequireType(instance);
        if (_capture is not null)
            return _capture(typed);

        var state = _createReusableRollbackState!(typed);
        _captureReusableRollbackState!(typed, state);
        return state;
    }

    public override bool SupportsReusableRollbackState => _captureReusableRollbackState is not null;

    public override object CreateReusableRollbackState(object instance) =>
        _createReusableRollbackState!(RequireType(instance));

    public override void CaptureReusableRollbackState(object instance, object state) =>
        _captureReusableRollbackState!(RequireType(instance), state);

    public override void RestoreState(object instance, object state) => _restore(RequireType(instance), state);

    public override bool CanPatch(RuntimeAssetRecord previous, RuntimeAssetRecord candidate) =>
        _canPatch?.Invoke(previous, candidate) ?? true;

    private static T RequireType(object instance) => instance as T
        ?? throw new ArgumentException($"Expected an instance of {typeof(T).FullName}.", nameof(instance));
}

public abstract class RuntimeSchemaRegistry
{
    private readonly ImmutableArray<RuntimeTableBinding> _bindings;

    protected RuntimeSchemaRegistry(
        ulong expectedSchemaHash,
        ExportTargetId expectedExportTarget,
        IEnumerable<RuntimeTableBinding> bindings)
    {
        if (expectedSchemaHash == 0)
            throw new ArgumentOutOfRangeException(nameof(expectedSchemaHash));
        if (expectedExportTarget.IsEmpty)
            throw new ArgumentException("Expected export target must not be empty.", nameof(expectedExportTarget));
        RuntimeCompatibility.NotNull(bindings, nameof(bindings));

        _bindings = bindings.ToImmutableArray();
        if (_bindings.Any(static binding => binding is null))
            throw new ArgumentException("Registry bindings must not contain null.", nameof(bindings));
        if (_bindings.Select(static binding => binding.TableId).Distinct().Count() != _bindings.Length)
            throw new ArgumentException("Registry table ids must be unique.", nameof(bindings));
        if (_bindings.Select(static binding => binding.RuntimeType).Distinct().Count() != _bindings.Length)
            throw new ArgumentException("Registry runtime types must be unique.", nameof(bindings));

        ExpectedSchemaHash = expectedSchemaHash;
        ExpectedExportTarget = expectedExportTarget;
    }

    public ulong ExpectedSchemaHash { get; }

    public ExportTargetId ExpectedExportTarget { get; }

    public IReadOnlyList<RuntimeTableBinding> Bindings => _bindings;
}
