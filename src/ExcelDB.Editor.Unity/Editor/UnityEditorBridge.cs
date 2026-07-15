#if UNITY_EDITOR || EXCELDB_DOTNET_VALIDATE
using System;
using System.Collections.Generic;

namespace ExcelDb.Editor.Unity
{

public readonly struct UnityEditorReportLine
{
    public UnityEditorReportLine(string severity, string code, string location, string message)
    {
        Severity = severity;
        Code = code;
        Location = location;
        Message = message;
    }

    public string Severity { get; }
    public string Code { get; }
    public string Location { get; }
    public string Message { get; }
}

public readonly struct UnityEditorBrowserRow
{
    public UnityEditorBrowserRow(
        string guid,
        string workbook,
        string table,
        string key,
        string displayName,
        string badges)
    {
        Guid = guid;
        Workbook = workbook;
        Table = table;
        Key = key;
        DisplayName = displayName;
        Badges = badges;
    }

    public string Guid { get; }
    public string Workbook { get; }
    public string Table { get; }
    public string Key { get; }
    public string DisplayName { get; }
    public string Badges { get; }
}

public readonly struct UnityEditorBrowserCounts
{
    public UnityEditorBrowserCounts(int dirty, int conflicted, int error)
    {
        Dirty = dirty;
        Conflicted = conflicted;
        Error = error;
    }

    public int Dirty { get; }
    public int Conflicted { get; }
    public int Error { get; }
}

public readonly struct UnityEditorPlanView
{
    public UnityEditorPlanView(
        string operation,
        string planHash,
        IReadOnlyList<string> sources,
        IReadOnlyList<string> mutations,
        IReadOnlyList<UnityEditorReportLine> diagnostics,
        IReadOnlyList<string> risks,
        bool canApply)
    {
        Operation = operation;
        PlanHash = planHash;
        Sources = sources;
        Mutations = mutations;
        Diagnostics = diagnostics;
        Risks = risks;
        CanApply = canApply;
    }

    public string Operation { get; }
    public string PlanHash { get; }
    public IReadOnlyList<string> Sources { get; }
    public IReadOnlyList<string> Mutations { get; }
    public IReadOnlyList<UnityEditorReportLine> Diagnostics { get; }
    public IReadOnlyList<string> Risks { get; }
    public bool CanApply { get; }
}

public readonly struct UnityEditorReportView
{
    public UnityEditorReportView(
        string operation,
        bool ok,
        string summary,
        string target,
        IReadOnlyList<string> artifacts,
        IReadOnlyList<UnityEditorReportLine> diagnostics)
    {
        Operation = operation;
        Ok = ok;
        Summary = summary;
        Target = target;
        Artifacts = artifacts;
        Diagnostics = diagnostics;
    }

    public string Operation { get; }
    public bool Ok { get; }
    public string Summary { get; }
    public string Target { get; }
    public IReadOnlyList<string> Artifacts { get; }
    public IReadOnlyList<UnityEditorReportLine> Diagnostics { get; }
}

public readonly struct UnityEditorConflictView
{
    public UnityEditorConflictView(
        string id,
        string assetGuid,
        string propertyPath,
        string baseValue,
        string mineValue,
        string theirValue)
    {
        Id = id;
        AssetGuid = assetGuid;
        PropertyPath = propertyPath;
        BaseValue = baseValue;
        MineValue = mineValue;
        TheirValue = theirValue;
    }

    public string Id { get; }
    public string AssetGuid { get; }
    public string PropertyPath { get; }
    public string BaseValue { get; }
    public string MineValue { get; }
    public string TheirValue { get; }
}

public readonly struct UnityEditorPendingChangeView
{
    public UnityEditorPendingChangeView(string guid, string path, string kind, IReadOnlyList<string> fields)
    {
        Guid = guid;
        Path = path;
        Kind = kind;
        Fields = fields;
    }

    public string Guid { get; }
    public string Path { get; }
    public string Kind { get; }
    public IReadOnlyList<string> Fields { get; }
}

public readonly struct UnityEditorWorkbookSummary
{
    public UnityEditorWorkbookSummary(
        bool isWorkbook,
        bool isMounted,
        int tableCount,
        int rowCount,
        string expectedSchemaHash,
        string actualSchemaHash)
    {
        IsWorkbook = isWorkbook;
        IsMounted = isMounted;
        TableCount = tableCount;
        RowCount = rowCount;
        ExpectedSchemaHash = expectedSchemaHash;
        ActualSchemaHash = actualSchemaHash;
    }

    public bool IsWorkbook { get; }
    public bool IsMounted { get; }
    public int TableCount { get; }
    public int RowCount { get; }
    public string ExpectedSchemaHash { get; }
    public string ActualSchemaHash { get; }
}

public readonly struct UnityEditorFreshnessView
{
    public UnityEditorFreshnessView(bool isFresh, string reason)
    {
        IsFresh = isFresh;
        Reason = reason;
    }

    public bool IsFresh { get; }
    public string Reason { get; }
}

public readonly struct UnityEditorPlayView
{
    public UnityEditorPlayView(
        bool isOpen,
        string source,
        string target,
        bool hotReload,
        bool canSwitch,
        string disabledReason)
    {
        IsOpen = isOpen;
        Source = source;
        Target = target;
        HotReload = hotReload;
        CanSwitch = canSwitch;
        DisabledReason = disabledReason;
    }

    public bool IsOpen { get; }
    public string Source { get; }
    public string Target { get; }
    public bool HotReload { get; }
    public bool CanSwitch { get; }
    public string DisabledReason { get; }
}

public readonly struct UnityEditorSettingsMetadata
{
    public UnityEditorSettingsMetadata(string schemaHash, string toolVersion)
    {
        SchemaHash = schemaHash;
        ToolVersion = toolVersion;
    }

    public string SchemaHash { get; }
    public string ToolVersion { get; }
}

/// <summary>The four Project v2 artifact locations shown by Unity settings.</summary>
public readonly struct UnityEditorProjectSettings
{
    public UnityEditorProjectSettings(
        string schemaDirectory,
        string excelDirectory,
        string generatedCSharpDirectory,
        string generatedBytesDirectory)
    {
        SchemaDirectory = schemaDirectory;
        ExcelDirectory = excelDirectory;
        GeneratedCSharpDirectory = generatedCSharpDirectory;
        GeneratedBytesDirectory = generatedBytesDirectory;
    }

    public string SchemaDirectory { get; }
    public string ExcelDirectory { get; }
    public string GeneratedCSharpDirectory { get; }
    public string GeneratedBytesDirectory { get; }
}

/// <summary>
/// Composition seam implemented by the trusted Unity host. Every method projects an existing
/// ExcelDB operation; this package contains no editor-only schema, data, or persistence semantics.
/// </summary>
public interface IExcelDbUnityEditorBridge
{
    bool HasProject { get; }
    IReadOnlyList<UnityEditorBrowserRow> Search(string filter, string displayName);
    UnityEditorBrowserCounts BrowserCounts { get; }
    IReadOnlyList<string> MountedWorkbooks { get; }
    IReadOnlyList<UnityEditorReportView> RecentReports { get; }
    IReadOnlyList<UnityEditorConflictView> Conflicts { get; }
    IReadOnlyList<UnityEditorPendingChangeView> PendingChanges { get; }
    UnityEditorProjectSettings ReadProjectSettings();
    UnityEditorSettingsMetadata ReadProjectSettingsMetadata();
    void WriteProjectSettings(UnityEditorProjectSettings settings);
    void InitializeProject();
    void Refresh();
    void SaveAll();
    void SaveAsset(string guid);
    void RevertAsset(string guid);
    IReadOnlyList<string> InspectAsset(string guid);
    void OpenInExcel(string guid);
    void Create(string table);
    void Delete(string guid);
    void Rename(string guid, string newName);
    void Move(string guid, string newPath);
    void Copy(string guid, string newPath);
    void MountWorkbook();
    void UnmountWorkbook(string workbook, bool persist);
    void OpenWorkbook(string workbook);
    IReadOnlyList<UnityEditorBrowserRow> SearchRowReferences(string filter, string refTable, string refGroup);
    void AssignRowReference(string ownerGuid, string propertyPath, string targetGuid);
    UnityEditorWorkbookSummary InspectWorkbook(string workbook);
    UnityEditorPlanView BuildPlan(string operation);
    void ApplyDisplayedPlan(string planHash);
    void ConvertClient();
    bool GuardDirty(string trigger);
    UnityEditorFreshnessView GetClientFreshness();
    void ExportReportJson(int reportIndex, string path);
    void LocateDiagnostic(int reportIndex, int diagnosticIndex);
    void ResolveConflict(string conflictId, bool reloadFromExcel);
    void SavePending(string guid);
    void RevertPending(string guid);
    bool PrepareNextPlaySource();
    void ClearPlaySourceRequest();
    UnityEditorPlayView GetPlayState();
    void SwitchPlaySource();
    void SetHotReload(bool enabled);
}

public static class ExcelDbUnityEditorBridge
{
    public static IExcelDbUnityEditorBridge? Current { get; set; }
}
}
#endif
