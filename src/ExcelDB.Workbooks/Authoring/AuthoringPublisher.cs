using System.Collections.Immutable;
using ExcelDb.Core.Diagnostics;
using ExcelDb.Core.Identity;
using ExcelDb.Workbooks.Importing;

namespace ExcelDb.Workbooks.Authoring;

public enum PublishPhase
{
    ResidentAndIndex,
    Snapshot,
    Report,
    ImportEvent,
    RuntimeNotification,
}

/// <summary>Publishes a successful import in one deterministic, non-reentrant sequence.</summary>
public sealed class AuthoringPublisher
{
    private bool _publishing;

    public ImmutableArray<ImportedRow> ResidentRows { get; private set; } = [];

    public ImmutableDictionary<AssetIdentity, ImportedRow> IdentityIndex { get; private set; } =
        ImmutableDictionary<AssetIdentity, ImportedRow>.Empty;

    public ImmutableDictionary<TableKey, ImportedRow> KeyIndex { get; private set; } =
        ImmutableDictionary<TableKey, ImportedRow>.Empty;

    public ImportSnapshot? Snapshot { get; private set; }

    public OperationReport? LastReport { get; private set; }

    public event Action<PublishPhase>? PhaseCompleted;

    public event Action<OperationReport>? ReportPublished;

    public event Action<WorkbookImportResult>? ImportPublished;

    public event Action<ImportSnapshot>? RuntimeNotification;

    public void Publish(WorkbookImportResult import, OperationReport report)
    {
        ArgumentNullException.ThrowIfNull(import);
        ArgumentNullException.ThrowIfNull(report);
        if (_publishing)
            throw new InvalidOperationException("Authoring publication is not reentrant.");
        _publishing = true;
        try
        {
            ResidentRows = import.Rows;
            IdentityIndex = import.IdentityIndex;
            KeyIndex = import.KeyIndex;
            PhaseCompleted?.Invoke(PublishPhase.ResidentAndIndex);

            Snapshot = import.Snapshot;
            PhaseCompleted?.Invoke(PublishPhase.Snapshot);

            LastReport = report;
            ReportPublished?.Invoke(report);
            PhaseCompleted?.Invoke(PublishPhase.Report);

            ImportPublished?.Invoke(import);
            PhaseCompleted?.Invoke(PublishPhase.ImportEvent);

            RuntimeNotification?.Invoke(import.Snapshot);
            PhaseCompleted?.Invoke(PublishPhase.RuntimeNotification);
        }
        finally
        {
            _publishing = false;
        }
    }
}
