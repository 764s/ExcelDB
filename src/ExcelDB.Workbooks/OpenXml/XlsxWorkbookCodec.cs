using System.Collections.Immutable;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using ExcelDb.Core.Diagnostics;
using ExcelDb.Core.IO;
using ExcelDb.Core.Identity;
using ExcelDb.Schema.Descriptors;
using ExcelDb.Workbooks.Importing;
using ExcelDb.Workbooks.Model;
using ExcelDb.Workbooks.References;

namespace ExcelDb.Workbooks.OpenXml;

public sealed record CellPatch(string SheetName, int Row, int Column, WorkbookCell? Cell);

public sealed record WorkbookProjectionRepairPlan(
    ContentFingerprint SourceFingerprint,
    WorkbookDefinition ProjectedWorkbook,
    ImmutableArray<string> Repairs);

public sealed record WorkbookInspectionResult(
    WorkbookDefinition Workbook,
    ImmutableArray<Diagnostic> Diagnostics,
    WorkbookProjectionRepairPlan? RepairPlan)
{
    public bool HasDrift => RepairPlan is not null;
}

public sealed record WorkbookProjectionRepairResult(
    bool Applied,
    byte[] Bytes,
    ImmutableArray<Diagnostic> Diagnostics);

/// <summary>
/// Minimal v1 SpreadsheetML codec. It deliberately owns only the ExcelDB cells it writes; patching
/// copies every other package part unchanged and rewrites only worksheets that contain a patch.
/// </summary>
public static class XlsxWorkbookCodec
{
    private static readonly XNamespace Spreadsheet =
        "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace OfficeRelationships =
        "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PackageRelationships =
        "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace ContentTypes =
        "http://schemas.openxmlformats.org/package/2006/content-types";
    private static readonly XNamespace XmlNamespace = XNamespace.Xml;
    private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(false);

    public static byte[] Write(WorkbookDefinition workbook)
    {
        ArgumentNullException.ThrowIfNull(workbook);

        var sheets = new List<SheetPart>();
        foreach (var table in workbook.Tables.OrderBy(static table => table.TableId))
        {
            sheets.Add(new SheetPart(
                table.SheetName,
                false,
                BuildTableWorksheet(table, workbook.Tables)));
        }
        foreach (var childTable in workbook.EffectiveChildTables
                     .OrderBy(static table => table.OwnerTableId)
                     .ThenBy(static table => string.Join('.', table.OwnerFieldIdPath), StringComparer.Ordinal))
        {
            sheets.Add(new SheetPart(
                childTable.SheetName,
                false,
                BuildChildTableWorksheet(childTable)));
        }

        sheets.Add(new SheetPart(
            WorkbookProtocol.MetadataSheetName,
            true,
            BuildMetadataWorksheet(workbook)));
        sheets.Add(new SheetPart(
            WorkbookProtocol.KeySheetName,
            true,
            BuildKeyWorksheet(workbook)));

        var entries = new List<PackageEntry>
        {
            new("[Content_Types].xml", Serialize(BuildContentTypes(sheets.Count))),
            new("_rels/.rels", Serialize(BuildRootRelationships())),
            new("xl/workbook.xml", Serialize(BuildWorkbook(sheets))),
            new("xl/_rels/workbook.xml.rels", Serialize(BuildWorkbookRelationships(sheets.Count))),
            new("xl/styles.xml", Serialize(BuildStyles())),
        };

        for (var index = 0; index < sheets.Count; index++)
            entries.Add(new PackageEntry($"xl/worksheets/sheet{index + 1}.xml", Serialize(sheets[index].Document)));

        return WriteEntries(entries);
    }

    /// <summary>
    /// Projects an updated ExcelDB workbook model into an existing XLSX package. Package parts and
    /// worksheets not owned by ExcelDB are retained byte-for-byte. Existing managed worksheets keep
    /// their relationship id and part name, including when a table is renamed.
    /// </summary>
    /// <param name="packageBytes">The source XLSX package.</param>
    /// <param name="projectedWorkbook">The complete desired ExcelDB workbook model.</param>
    /// <param name="purgeUnownedCells">
    /// When false, a newly managed coordinate that already contains an unowned cell blocks the
    /// projection. When true, those colliding cells may be removed or replaced.
    /// </param>
    public static byte[] Project(
        byte[] packageBytes,
        WorkbookDefinition projectedWorkbook,
        bool purgeUnownedCells = false)
    {
        ArgumentNullException.ThrowIfNull(packageBytes);
        ArgumentNullException.ThrowIfNull(projectedWorkbook);

        var sourceWorkbook = Read(packageBytes);
        var package = OpenPackage(packageBytes);
        var workbookDocument = LoadRequiredXml(package, "xl/workbook.xml");
        var relationshipDocument = LoadRequiredXml(package, "xl/_rels/workbook.xml.rels");
        var contentTypesDocument = LoadRequiredXml(package, "[Content_Types].xml");
        var sheetContainer = workbookDocument.Root?.Element(Spreadsheet + "sheets")
            ?? throw new InvalidDataException("Workbook XML has no sheets element.");
        var relationshipContainer = relationshipDocument.Root
            ?? throw new InvalidDataException("Workbook relationship XML has no root element.");
        var contentTypesContainer = contentTypesDocument.Root
            ?? throw new InvalidDataException("Content type XML has no root element.");

        ValidateProjectedSheetNames(sourceWorkbook, projectedWorkbook, package);

        var replacements = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var removedParts = new HashSet<string>(StringComparer.Ordinal);
        var addedEntries = new List<PackageEntry>();
        var sourceTables = sourceWorkbook.Tables.ToDictionary(static table => table.TableId);
        var projectedTables = projectedWorkbook.Tables.ToDictionary(static table => table.TableId);
        var sourceSheetElements = sourceWorkbook.Tables.ToDictionary(
            static table => table.TableId,
            table => FindSheetElement(sheetContainer, table.SheetName));

        foreach (var sourceTable in sourceWorkbook.Tables)
        {
            if (projectedTables.ContainsKey(sourceTable.TableId))
                continue;

            var sheetElement = sourceSheetElements[sourceTable.TableId];
            var relationshipId = RequiredRelationshipId(sheetElement, sourceTable.SheetName);
            var partName = package.Sheets[sourceTable.SheetName];
            sheetElement.Remove();
            FindRelationship(relationshipContainer, relationshipId)?.Remove();
            removedParts.Add(partName);
            RemoveWorksheetContentType(contentTypesContainer, partName);
        }

        foreach (var projectedTable in projectedWorkbook.Tables.OrderBy(static table => table.TableId))
        {
            if (sourceTables.TryGetValue(projectedTable.TableId, out var sourceTable))
            {
                var sheetElement = sourceSheetElements[sourceTable.TableId];
                sheetElement.SetAttributeValue("name", projectedTable.SheetName);
                var partName = package.Sheets[sourceTable.SheetName];
                var document = LoadXml(package.EntryBytes[partName]);
                ProjectTableWorksheet(
                    document,
                    sourceTable,
                    projectedTable,
                    projectedWorkbook.Tables,
                    purgeUnownedCells);
                replacements[partName] = Serialize(document);
                continue;
            }

            var partNameForNewSheet = AllocateWorksheetPart(package.EntryBytes.Keys, addedEntries, removedParts);
            var relationshipId = AllocateRelationshipId(relationshipContainer);
            var sheetId = AllocateSheetId(sheetContainer);
            sheetContainer.Add(new XElement(
                Spreadsheet + "sheet",
                new XAttribute("name", projectedTable.SheetName),
                new XAttribute("sheetId", sheetId),
                new XAttribute(OfficeRelationships + "id", relationshipId)));
            relationshipContainer.Add(new XElement(
                PackageRelationships + "Relationship",
                new XAttribute("Id", relationshipId),
                new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet"),
                new XAttribute("Target", partNameForNewSheet[3..])));
            AddWorksheetContentType(contentTypesContainer, partNameForNewSheet);
            addedEntries.Add(new PackageEntry(
                partNameForNewSheet,
                Serialize(BuildTableWorksheet(projectedTable, projectedWorkbook.Tables))));
        }

        var sourceChildren = sourceWorkbook.EffectiveChildTables.ToDictionary(
            static table => ChildOwnerKey(table.OwnerTableId, table.OwnerFieldIdPath),
            StringComparer.Ordinal);
        var projectedChildren = projectedWorkbook.EffectiveChildTables.ToDictionary(
            static table => ChildOwnerKey(table.OwnerTableId, table.OwnerFieldIdPath),
            StringComparer.Ordinal);
        foreach (var pair in sourceChildren)
        {
            if (projectedChildren.ContainsKey(pair.Key))
                continue;
            var sourceChild = pair.Value;
            var sheetElement = FindSheetElement(sheetContainer, sourceChild.SheetName);
            var relationshipId = RequiredRelationshipId(sheetElement, sourceChild.SheetName);
            var partName = package.Sheets[sourceChild.SheetName];
            sheetElement.Remove();
            FindRelationship(relationshipContainer, relationshipId)?.Remove();
            removedParts.Add(partName);
            RemoveWorksheetContentType(contentTypesContainer, partName);
        }
        foreach (var pair in projectedChildren.OrderBy(static item => item.Key, StringComparer.Ordinal))
        {
            var projectedChild = pair.Value;
            if (sourceChildren.TryGetValue(pair.Key, out var sourceChild))
            {
                var sheetElement = FindSheetElement(sheetContainer, sourceChild.SheetName);
                sheetElement.SetAttributeValue("name", projectedChild.SheetName);
                var partName = package.Sheets[sourceChild.SheetName];
                var document = LoadXml(package.EntryBytes[partName]);
                ProjectChildTableWorksheet(document, sourceChild, projectedChild, purgeUnownedCells);
                replacements[partName] = Serialize(document);
                continue;
            }
            var newPart = AllocateWorksheetPart(package.EntryBytes.Keys, addedEntries, removedParts);
            var relationshipId = AllocateRelationshipId(relationshipContainer);
            var sheetId = AllocateSheetId(sheetContainer);
            sheetContainer.Add(new XElement(
                Spreadsheet + "sheet",
                new XAttribute("name", projectedChild.SheetName),
                new XAttribute("sheetId", sheetId),
                new XAttribute(OfficeRelationships + "id", relationshipId)));
            relationshipContainer.Add(new XElement(
                PackageRelationships + "Relationship",
                new XAttribute("Id", relationshipId),
                new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet"),
                new XAttribute("Target", newPart[3..])));
            AddWorksheetContentType(contentTypesContainer, newPart);
            addedEntries.Add(new PackageEntry(newPart, Serialize(BuildChildTableWorksheet(projectedChild))));
        }

        ProjectProtocolSheet(
            package,
            sheetContainer,
            WorkbookProtocol.MetadataSheetName,
            BuildMetadataWorksheet(projectedWorkbook),
            replacements);
        ProjectProtocolSheet(
            package,
            sheetContainer,
            WorkbookProtocol.KeySheetName,
            BuildKeyWorksheet(projectedWorkbook),
            replacements);

        replacements["xl/workbook.xml"] = Serialize(workbookDocument);
        replacements["xl/_rels/workbook.xml.rels"] = Serialize(relationshipDocument);
        replacements["[Content_Types].xml"] = Serialize(contentTypesDocument);

        var outputEntries = package.Entries
            .Where(entry => !removedParts.Contains(entry.Name))
            .Select(entry => replacements.TryGetValue(entry.Name, out var replacement)
                ? entry with { Bytes = replacement }
                : entry)
            .Concat(addedEntries)
            .ToArray();
        return WriteEntries(outputEntries);
    }

    /// <summary>Alias for <see cref="Project(byte[], WorkbookDefinition, bool)"/>.</summary>
    public static byte[] Rewrite(
        byte[] packageBytes,
        WorkbookDefinition projectedWorkbook,
        bool purgeUnownedCells = false) =>
        Project(packageBytes, projectedWorkbook, purgeUnownedCells);

    public static WorkbookDefinition Read(byte[] packageBytes)
    {
        ArgumentNullException.ThrowIfNull(packageBytes);
        var package = OpenPackage(packageBytes);
        if (!package.Sheets.TryGetValue(WorkbookProtocol.MetadataSheetName, out var metadataPart))
            throw new InvalidDataException($"Missing hidden worksheet '{WorkbookProtocol.MetadataSheetName}'.");

        var metadata = ReadGrid(package, metadataPart);
        var version = ReadWorkbookMetadata(metadata, "format");
        if (!ushort.TryParse(version, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedVersion)
            || parsedVersion != WorkbookProtocol.FormatVersion)
        {
            throw new InvalidDataException($"Unsupported ExcelDB workbook format '{version}'.");
        }

        var workbookGuidText = ReadWorkbookMetadata(metadata, "workbook_guid");
        if (!Guid.TryParseExact(workbookGuidText, "N", out var workbookGuid) || workbookGuid == Guid.Empty)
            throw new InvalidDataException("Workbook metadata contains an invalid workbook guid.");

        var schemaHashText = ReadWorkbookMetadata(metadata, "schema_hash");
        if (!ulong.TryParse(
                schemaHashText,
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture,
                out var schemaHash))
        {
            throw new InvalidDataException("Workbook metadata contains an invalid schema hash.");
        }

        var savedUtcText = ReadWorkbookMetadata(metadata, "saved_utc");
        if (!DateTimeOffset.TryParseExact(
                savedUtcText,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var savedUtc))
        {
            throw new InvalidDataException("Workbook metadata contains an invalid save time.");
        }

        var fieldPaths = ReadFieldPaths(metadata);
        var migrationMarkers = ReadMigrationMarkers(metadata);
        var keyMap = ReadKeyMap(package);
        var tables = new List<WorkbookTable>();
        foreach (var tableMetadata in ReadTableMetadata(metadata).OrderBy(static table => table.TableId))
        {
            if (!package.Sheets.TryGetValue(tableMetadata.SheetName, out var tablePart))
                throw new InvalidDataException($"Missing managed worksheet '{tableMetadata.SheetName}'.");

            tables.Add(ReadTable(package, tablePart, tableMetadata, fieldPaths, keyMap));
        }
        var childTables = new List<WorkbookChildTable>();
        foreach (var childMetadata in ReadChildTableMetadata(metadata)
                     .OrderBy(static table => table.OwnerTableId)
                     .ThenBy(static table => string.Join('.', table.OwnerFieldIdPath), StringComparer.Ordinal))
        {
            if (!package.Sheets.TryGetValue(childMetadata.SheetName, out var childPart))
                throw new InvalidDataException($"Missing managed child worksheet '{childMetadata.SheetName}'.");
            childTables.Add(ReadChildTable(package, childPart, childMetadata, ReadChildFieldPaths(metadata)));
        }

        return new WorkbookDefinition(
            workbookGuid,
            schemaHash,
            savedUtc,
            tables.ToImmutableArray(),
            migrationMarkers,
            childTables.ToImmutableArray());
    }

    /// <summary>
    /// Descriptor-aware read used by mount/check/generate.  Unlike <see cref="Read(byte[])"/>, it
    /// treats metadata and the key sheet as rebuildable projections, derives live bindings from the
    /// descriptor plus actual sheet headers, and returns an explicit repair plan without mutating
    /// the package.
    /// </summary>
    public static WorkbookInspectionResult Inspect(
        byte[] packageBytes,
        CanonicalSchemaDescriptor schema)
    {
        ArgumentNullException.ThrowIfNull(packageBytes);
        ArgumentNullException.ThrowIfNull(schema);
        var package = OpenPackage(packageBytes);
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        var repairs = ImmutableArray.CreateBuilder<string>();
        var fingerprint = ContentFingerprint.FromBytes(packageBytes);
        WorksheetGrid? metadata = null;
        IReadOnlyList<TableMetadata> metadataTables = [];
        IReadOnlyList<ChildTableMetadata> metadataChildTables = [];
        IReadOnlyDictionary<(int TableId, int Column), string> metadataFieldPaths =
            new Dictionary<(int TableId, int Column), string>();
        IReadOnlyDictionary<(string OwnerKey, int Column), string> metadataChildFieldPaths =
            new Dictionary<(string OwnerKey, int Column), string>();
        var workbookGuid = Guid.NewGuid();
        var savedUtc = DateTimeOffset.UnixEpoch;
        ImmutableArray<string> migrationMarkers = [];

        if (package.Sheets.TryGetValue(WorkbookProtocol.MetadataSheetName, out var metadataPart))
        {
            metadata = ReadGrid(package, metadataPart);
            try
            {
                var guidText = ReadWorkbookMetadata(metadata, "workbook_guid");
                if (!Guid.TryParseExact(guidText, "N", out workbookGuid) || workbookGuid == Guid.Empty)
                    throw new InvalidDataException("Workbook metadata contains an invalid workbook guid.");
                var savedText = ReadWorkbookMetadata(metadata, "saved_utc");
                if (!DateTimeOffset.TryParseExact(
                        savedText,
                        "O",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind,
                        out savedUtc))
                {
                    throw new InvalidDataException("Workbook metadata contains an invalid save time.");
                }

                metadataTables = ReadTableMetadata(metadata);
                metadataFieldPaths = ReadFieldPaths(metadata);
                metadataChildTables = ReadChildTableMetadata(metadata);
                metadataChildFieldPaths = ReadChildFieldPaths(metadata);
                migrationMarkers = ReadMigrationMarkers(metadata);
            }
            catch (InvalidDataException exception)
            {
                diagnostics.Add(new Diagnostic(
                    "EXWB1201",
                    DiagnosticSeverity.Error,
                    WorkbookProtocol.MetadataSheetName,
                    $"Workbook metadata is invalid and requires explicit repair: {exception.Message}"));
                repairs.Add("metadata.invalid");
                metadataTables = [];
                metadataFieldPaths = new Dictionary<(int TableId, int Column), string>();
                metadataChildTables = [];
                metadataChildFieldPaths = new Dictionary<(string OwnerKey, int Column), string>();
            }
        }
        else
        {
            diagnostics.Add(new Diagnostic(
                "EXWB1200",
                DiagnosticSeverity.Error,
                WorkbookProtocol.MetadataSheetName,
                "Workbook metadata is missing; actual schema-owned sheets were inspected and a repair plan was prepared."));
            repairs.Add("metadata.missing");
        }

        var keyMap = ReadKeyMap(package);
        var tables = new List<WorkbookTable>();
        var consumedSheets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tableSchema in schema.Tables
                     .Where(static table => table.Kind == CanonicalTableKind.Asset)
                     .OrderBy(static table => table.Id))
        {
            var registered = metadataTables.FirstOrDefault(table => table.TableId == tableSchema.Id);
            var sheetName = ResolvePhysicalSheet(package, tableSchema, registered);
            if (sheetName is null)
            {
                diagnostics.Add(new Diagnostic(
                    "EXWB1204",
                    DiagnosticSeverity.Error,
                    tableSchema.SheetName,
                    $"Managed worksheet for table {tableSchema.Id}/{tableSchema.FullName} is missing."));
                repairs.Add($"table.{tableSchema.Id}.missing");
                tables.Add(WorkbookLayout.CreateTable(tableSchema));
                continue;
            }

            consumedSheets.Add(sheetName);
            var dataStartRow = registered is null
                ? WorkbookProtocol.DefaultDataStartRow
                : registered.DataStartRow;
            var fieldPaths = InferFieldPaths(
                package,
                sheetName,
                tableSchema,
                metadataFieldPaths);
            tables.Add(ReadTable(
                package,
                package.Sheets[sheetName],
                new TableMetadata(tableSchema.Id, tableSchema.Name, sheetName, dataStartRow),
                fieldPaths,
                keyMap));
        }

        var retiredById = schema.RetiredTables.ToDictionary(static table => table.Id);
        foreach (var registered in metadataTables.OrderBy(static table => table.TableId))
        {
            if (!retiredById.TryGetValue(registered.TableId, out var retired)
                || consumedSheets.Contains(registered.SheetName)
                || !package.Sheets.ContainsKey(registered.SheetName))
            {
                continue;
            }

            consumedSheets.Add(registered.SheetName);
            tables.Add(ReadTable(
                package,
                package.Sheets[registered.SheetName],
                registered,
                metadataFieldPaths,
                keyMap) with
            {
                ProtoName = retired.Name,
                IsRetiredPreserved = true,
            });
            diagnostics.Add(new Diagnostic(
                "table.retired-present",
                DiagnosticSeverity.Info,
                registered.SheetName,
                $"Historical retired table {registered.TableId}/{retired.FullName} is preserved outside the live import domain."));
        }

        foreach (var retired in schema.RetiredTables.OrderBy(static table => table.Id))
        {
            if (tables.Any(table => table.TableId == retired.Id))
                continue;
            var sheetName = package.Sheets.Keys.FirstOrDefault(name =>
                !consumedSheets.Contains(name)
                && (string.Equals(name, retired.Name, StringComparison.Ordinal)
                    || string.Equals(name, retired.FullName, StringComparison.Ordinal)));
            if (sheetName is null)
                continue;
            consumedSheets.Add(sheetName);
            tables.Add(ReadTable(
                package,
                package.Sheets[sheetName],
                new TableMetadata(
                    retired.Id,
                    retired.Name,
                    sheetName,
                    WorkbookProtocol.DefaultDataStartRow),
                new Dictionary<(int TableId, int Column), string>(),
                keyMap) with { IsRetiredPreserved = true });
            diagnostics.Add(new Diagnostic(
                "table.retired-present",
                DiagnosticSeverity.Info,
                sheetName,
                $"Historical retired table {retired.Id}/{retired.FullName} is preserved outside the live import domain."));
        }

        var childTables = new List<WorkbookChildTable>();
        foreach (var layout in WorkbookLayout.CreateChildTables(schema))
        {
            var ownerKey = ChildOwnerKey(layout.OwnerTableId, layout.OwnerFieldIdPath);
            var registered = metadataChildTables.FirstOrDefault(candidate =>
                candidate.OwnerTableId == layout.OwnerTableId
                && candidate.OwnerFieldIdPath.SequenceEqual(layout.OwnerFieldIdPath));
            var sheetName = new[] { registered?.SheetName, layout.SheetName }
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .FirstOrDefault(value => package.Sheets.ContainsKey(value!));
            if (sheetName is null)
            {
                diagnostics.Add(new Diagnostic(
                    "EXWB1205",
                    DiagnosticSeverity.Error,
                    layout.SheetName,
                    $"Managed child worksheet for {ownerKey} is missing."));
                repairs.Add($"child.{ownerKey}.missing");
                childTables.Add(layout);
                continue;
            }
            consumedSheets.Add(sheetName);
            var effectiveMetadata = registered ?? new ChildTableMetadata(
                layout.OwnerTableId,
                layout.OwnerFieldIdPath,
                layout.Kind,
                sheetName,
                layout.DataStartRow);
            var inferred = InferChildFieldPaths(
                package,
                sheetName,
                layout,
                metadataChildFieldPaths);
            childTables.Add(ReadChildTable(package, package.Sheets[sheetName], effectiveMetadata, inferred));
        }

        var workbook = new WorkbookDefinition(
            workbookGuid,
            schema.SchemaHash,
            savedUtc,
            tables.OrderBy(static table => table.TableId).ToImmutableArray(),
            migrationMarkers,
            childTables
                .OrderBy(static table => table.OwnerTableId)
                .ThenBy(static table => string.Join('.', table.OwnerFieldIdPath), StringComparer.Ordinal)
                .ToImmutableArray());
        var import = WorkbookImporter.Import("<inspection>", packageBytes, workbook, schema);
        workbook = WorkbookIdentityProjection.BindCanonicalKeys(workbook, import, schema);

        var desiredMetadata = BuildMetadataWorksheet(workbook);
        if (!ProtocolProjectionMatches(package, WorkbookProtocol.MetadataSheetName, desiredMetadata))
        {
            repairs.Add("metadata.stale");
            diagnostics.Add(new Diagnostic(
                "EXWB1202",
                DiagnosticSeverity.Error,
                WorkbookProtocol.MetadataSheetName,
                "Workbook metadata does not match the descriptor and actual managed layout; repair remains explicit and read-only."));
        }

        var desiredKeys = BuildKeyWorksheet(workbook);
        if (!ProtocolProjectionMatches(package, WorkbookProtocol.KeySheetName, desiredKeys))
        {
            repairs.Add("keys.stale");
            diagnostics.Add(new Diagnostic(
                "EXWB1203",
                DiagnosticSeverity.Warning,
                WorkbookProtocol.KeySheetName,
                "The reference-key projection is missing or stale and can be rebuilt from schema-owned key cells."));
        }

        foreach (var table in workbook.Tables.Where(static table => !table.IsRetiredPreserved))
        {
            if (!GeneratedDataValidationsMatch(package, table, workbook.Tables))
                repairs.Add($"table.{table.TableId}.data-validations");
        }

        var repairItems = repairs.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray();
        var plan = repairItems.IsEmpty
            ? null
            : new WorkbookProjectionRepairPlan(fingerprint, workbook, repairItems);
        return new WorkbookInspectionResult(workbook, diagnostics.Distinct().ToImmutableArray(), plan);
    }

    public static WorkbookProjectionRepairResult ApplyProjectionRepair(
        byte[] packageBytes,
        WorkbookProjectionRepairPlan plan)
    {
        ArgumentNullException.ThrowIfNull(packageBytes);
        ArgumentNullException.ThrowIfNull(plan);
        if (ContentFingerprint.FromBytes(packageBytes) != plan.SourceFingerprint)
        {
            return new WorkbookProjectionRepairResult(
                false,
                packageBytes,
                [new Diagnostic(
                    "EXWB1206",
                    DiagnosticSeverity.Blocker,
                    WorkbookProtocol.MetadataSheetName,
                    "Workbook projection repair plan is stale; no bytes were changed.")]);
        }

        try
        {
            var repaired = RepairProjectionPackage(packageBytes, plan.ProjectedWorkbook);
            _ = Read(repaired);
            return new WorkbookProjectionRepairResult(
                !packageBytes.AsSpan().SequenceEqual(repaired),
                repaired,
                [new Diagnostic(
                    "EXWB1207",
                    DiagnosticSeverity.Info,
                    WorkbookProtocol.MetadataSheetName,
                    $"Applied explicit projection repair: {string.Join(", ", plan.Repairs)}.")]);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or ArgumentException)
        {
            return new WorkbookProjectionRepairResult(
                false,
                packageBytes,
                [new Diagnostic(
                    "EXWB1208",
                    DiagnosticSeverity.Blocker,
                    WorkbookProtocol.MetadataSheetName,
                    $"Projection repair verification failed; original bytes were retained: {exception.Message}")]);
        }
    }

    public static byte[] PatchCells(byte[] packageBytes, IEnumerable<CellPatch> patches)
    {
        ArgumentNullException.ThrowIfNull(packageBytes);
        ArgumentNullException.ThrowIfNull(patches);
        var patchArray = patches.ToArray();
        var duplicate = patchArray
            .GroupBy(static patch => (patch.SheetName, patch.Row, patch.Column))
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException(
                $"Cell {duplicate.Key.SheetName}!{ColumnName(duplicate.Key.Column)}{duplicate.Key.Row} is patched more than once.",
                nameof(patches));
        }

        if (patchArray.Length == 0)
            return packageBytes.ToArray();

        var package = OpenPackage(packageBytes);
        var replacements = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var sheetPatches in patchArray.GroupBy(static patch => patch.SheetName, StringComparer.Ordinal))
        {
            if (!package.Sheets.TryGetValue(sheetPatches.Key, out var partName))
                throw new InvalidDataException($"Worksheet '{sheetPatches.Key}' does not exist.");

            var document = LoadXml(package.EntryBytes[partName]);
            PatchWorksheet(document, sheetPatches);
            replacements.Add(partName, Serialize(document));
        }

        var outputEntries = package.Entries
            .Select(entry => replacements.TryGetValue(entry.Name, out var replacement)
                ? entry with { Bytes = replacement }
                : entry)
            .ToArray();
        return WriteEntries(outputEntries);
    }

    public static WorkbookCell? ReadCell(byte[] packageBytes, string sheetName, int row, int column)
    {
        ArgumentNullException.ThrowIfNull(packageBytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(sheetName);
        if (row <= 0)
            throw new ArgumentOutOfRangeException(nameof(row));
        if (column <= 0)
            throw new ArgumentOutOfRangeException(nameof(column));

        var package = OpenPackage(packageBytes);
        if (!package.Sheets.TryGetValue(sheetName, out var partName))
            throw new InvalidDataException($"Worksheet '{sheetName}' does not exist.");
        return ReadGrid(package, partName).Get(row, column);
    }

    public static bool IsSheetHidden(byte[] packageBytes, string sheetName)
    {
        ArgumentNullException.ThrowIfNull(packageBytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(sheetName);
        var package = OpenPackage(packageBytes);
        if (!package.SheetStates.TryGetValue(sheetName, out var state))
            throw new InvalidDataException($"Worksheet '{sheetName}' does not exist.");
        return string.Equals(state, "hidden", StringComparison.Ordinal)
            || string.Equals(state, "veryHidden", StringComparison.Ordinal);
    }

    public static ContentFingerprint Fingerprint(byte[] packageBytes) =>
        ContentFingerprint.FromBytes(packageBytes);

    private static XDocument BuildTableWorksheet(
        WorkbookTable table,
        IEnumerable<WorkbookTable>? referenceTables = null)
    {
        var rows = new List<IReadOnlyList<WorkbookCell?>>
        {
            table.Columns.Select(static column => (WorkbookCell?)new WorkbookCell(column.DisplayName))
                .Append(new WorkbookCell(WorkbookProtocol.GuidColumnName))
                .Append(new WorkbookCell(WorkbookProtocol.RevisionColumnName))
                .ToArray(),
            table.Columns.Select(static column => (WorkbookCell?)new WorkbookCell(column.PropertyPath))
                .Append(new WorkbookCell(WorkbookProtocol.GuidColumnName))
                .Append(new WorkbookCell(WorkbookProtocol.RevisionColumnName))
                .ToArray(),
            table.Columns.Select(static column => (WorkbookCell?)new WorkbookCell(column.TypeName))
                .Append(new WorkbookCell("row_guid"))
                .Append(new WorkbookCell("uint32"))
                .ToArray(),
        };

        while (rows.Count + 1 < table.DataStartRow)
            rows.Add([]);

        foreach (var row in table.Rows)
        {
            var targetRow = row.SourceRowNumber ?? (rows.Count + 1);
            if (targetRow < table.DataStartRow || targetRow < rows.Count + 1)
                throw new InvalidDataException($"Rows in worksheet '{table.SheetName}' are not in source order.");
            while (rows.Count + 1 < targetRow)
                rows.Add([]);

            var cells = new List<WorkbookCell?>(table.Columns.Length + 2);
            foreach (var column in table.Columns)
                cells.Add(row.Cells.GetValueOrDefault(column.PropertyPath));
            cells.Add(row.RowGuid is { } rowGuid
                ? new WorkbookCell(rowGuid.ToString())
                : row.RawRowGuid is null ? null : new WorkbookCell(row.RawRowGuid));
            cells.Add(row.RawRevision is null
                ? new WorkbookCell(row.Revision.ToString(CultureInfo.InvariantCulture))
                : new WorkbookCell(row.RawRevision));
            rows.Add(cells);
        }

        var document = BuildWorksheet(rows, table.Columns.Length + 1, table.Columns.Length + 2);
        AddReferenceDataValidations(document, table, referenceTables ?? [table]);
        return document;
    }

    private static XDocument BuildChildTableWorksheet(WorkbookChildTable table)
    {
        var systemNames = table.Kind == CanonicalChildTableKind.RepeatedMessage
            ? new[] { WorkbookProtocol.ParentGuidColumnName, WorkbookProtocol.OrdinalColumnName }
            : new[] { WorkbookProtocol.ParentGuidColumnName, WorkbookProtocol.MapKeyColumnName };
        var systemTypes = table.Kind == CanonicalChildTableKind.RepeatedMessage
            ? new[] { "row_guid", "int32" }
            : new[] { "row_guid", "string" };
        var rows = new List<IReadOnlyList<WorkbookCell?>>
        {
            systemNames.Select(static name => (WorkbookCell?)new WorkbookCell(name))
                .Concat(table.Columns.Select(static column => (WorkbookCell?)new WorkbookCell(column.DisplayName)))
                .ToArray(),
            systemNames.Select(static name => (WorkbookCell?)new WorkbookCell(name))
                .Concat(table.Columns.Select(static column => (WorkbookCell?)new WorkbookCell(column.PropertyPath)))
                .ToArray(),
            systemTypes.Select(static name => (WorkbookCell?)new WorkbookCell(name))
                .Concat(table.Columns.Select(static column => (WorkbookCell?)new WorkbookCell(column.TypeName)))
                .ToArray(),
        };
        while (rows.Count + 1 < table.DataStartRow)
            rows.Add([]);
        foreach (var row in table.Rows)
        {
            var targetRow = row.SourceRowNumber ?? (rows.Count + 1);
            if (targetRow < table.DataStartRow || targetRow < rows.Count + 1)
                throw new InvalidDataException($"Rows in child worksheet '{table.SheetName}' are not in source order.");
            while (rows.Count + 1 < targetRow)
                rows.Add([]);
            var cells = new List<WorkbookCell?>(ChildSystemColumnCount(table.Kind) + table.Columns.Length)
            {
                row.ParentRowGuid is { } parent
                    ? new WorkbookCell(parent.ToString())
                    : row.RawParentRowGuid is null ? null : new WorkbookCell(row.RawParentRowGuid),
                table.Kind == CanonicalChildTableKind.RepeatedMessage
                    ? row.Ordinal is { } ordinal ? new WorkbookCell(ordinal.ToString(CultureInfo.InvariantCulture)) : null
                    : string.IsNullOrEmpty(row.MapKey) ? null : new WorkbookCell(row.MapKey),
            };
            foreach (var column in table.Columns)
                cells.Add(row.Cells.GetValueOrDefault(column.PropertyPath));
            rows.Add(cells);
        }
        return BuildWorksheet(rows);
    }

    private static XDocument BuildMetadataWorksheet(WorkbookDefinition workbook)
    {
        var rows = new List<IReadOnlyList<WorkbookCell?>>
        {
            TextRow("kind", "id", "name", "sheet", "data_row", "column", "span", "value"),
            MetadataRow("workbook", "workbook_guid", value: workbook.WorkbookGuid.ToString("N")),
            MetadataRow("workbook", "schema_hash", value: workbook.SchemaHash.ToString("x16", CultureInfo.InvariantCulture)),
            MetadataRow("workbook", "format", value: WorkbookProtocol.FormatVersion.ToString(CultureInfo.InvariantCulture)),
            MetadataRow("workbook", "saved_utc", value: workbook.SavedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)),
        };

        foreach (var marker in workbook.EffectiveMigrationMarkers.Order(StringComparer.Ordinal))
        {
            ParseMigrationMarker(marker, out var migrationId, out var migrationVersion);
            rows.Add(MetadataRow(
                "migration",
                migrationId,
                migrationVersion.ToString(CultureInfo.InvariantCulture),
                value: "applied"));
        }

        foreach (var table in workbook.Tables.OrderBy(static table => table.TableId))
        {
            rows.Add(MetadataRow(
                "table",
                table.TableId.ToString(CultureInfo.InvariantCulture),
                table.ProtoName,
                table.SheetName,
                table.DataStartRow.ToString(CultureInfo.InvariantCulture)));

            for (var columnIndex = 0; columnIndex < table.Columns.Length; columnIndex++)
            {
                var column = table.Columns[columnIndex];
                rows.Add(MetadataRow(
                    "field",
                    $"{table.TableId.ToString(CultureInfo.InvariantCulture)}:{column.FieldPath}",
                    column.PropertyPath,
                    table.SheetName,
                    column: (columnIndex + 1).ToString(CultureInfo.InvariantCulture),
                    span: "1",
                    value: column.TypeName));
            }
        }

        foreach (var childTable in workbook.EffectiveChildTables
                     .OrderBy(static table => table.OwnerTableId)
                     .ThenBy(static table => string.Join('.', table.OwnerFieldIdPath), StringComparer.Ordinal))
        {
            var ownerKey = ChildOwnerKey(childTable.OwnerTableId, childTable.OwnerFieldIdPath);
            rows.Add(MetadataRow(
                "child",
                ownerKey,
                childTable.Kind.ToString(),
                childTable.SheetName,
                childTable.DataStartRow.ToString(CultureInfo.InvariantCulture)));
            var systemColumns = ChildSystemColumnCount(childTable.Kind);
            for (var index = 0; index < childTable.Columns.Length; index++)
            {
                var column = childTable.Columns[index];
                rows.Add(MetadataRow(
                    "child_field",
                    $"{ownerKey}:{column.FieldPath}",
                    column.PropertyPath,
                    childTable.SheetName,
                    column: (systemColumns + index + 1).ToString(CultureInfo.InvariantCulture),
                    span: "1",
                    value: column.TypeName));
            }
        }

        return BuildWorksheet(rows);
    }

    private static XDocument BuildKeyWorksheet(WorkbookDefinition workbook)
    {
        var tables = workbook.Tables
            .Where(static table => !table.IsRetiredPreserved)
            .OrderBy(static table => table.TableId)
            .ToArray();
        var header = new List<WorkbookCell?>(5 + tables.Length)
        {
            new("table_id"),
            new("row_guid"),
            new("key"),
            new("source_row"),
            new("reference_token"),
        };
        header.AddRange(tables.Select(table => (WorkbookCell?)new WorkbookCell(
            $"{table.TableId.ToString(CultureInfo.InvariantCulture)}:{table.ProtoName}")));
        var rows = new List<List<WorkbookCell?>> { header };
        foreach (var table in tables)
        {
            for (var index = 0; index < table.Rows.Length; index++)
            {
                var row = table.Rows[index];
                rows.Add(TextRow(
                        table.TableId.ToString(CultureInfo.InvariantCulture),
                        row.RowGuid?.ToString() ?? row.RawRowGuid,
                        row.Key,
                        (row.SourceRowNumber ?? (table.DataStartRow + index)).ToString(CultureInfo.InvariantCulture),
                        ToReferenceToken(table.TableId, row.Key))
                    .ToList());
            }
        }

        for (var tableIndex = 0; tableIndex < tables.Length; tableIndex++)
        {
            var table = tables[tableIndex];
            var projectionColumn = 5 + tableIndex;
            for (var rowIndex = 0; rowIndex < table.Rows.Length; rowIndex++)
            {
                while (rows.Count <= rowIndex + 1)
                    rows.Add([]);
                while (rows[rowIndex + 1].Count <= projectionColumn)
                    rows[rowIndex + 1].Add(null);
                rows[rowIndex + 1][projectionColumn] = ToTextCell(ToReferenceToken(table.TableId, table.Rows[rowIndex].Key));
            }
        }

        return BuildWorksheet(rows);
    }

    private static string? ResolvePhysicalSheet(
        Package package,
        CanonicalTableDescriptor table,
        TableMetadata? registered)
    {
        var candidates = new[]
        {
            registered?.SheetName,
            table.SheetName,
            table.Name,
            table.FullName,
        };
        return candidates
            .Where(static candidate => !string.IsNullOrWhiteSpace(candidate))
            .Distinct(StringComparer.Ordinal)
            .FirstOrDefault(candidate => package.Sheets.ContainsKey(candidate!));
    }

    private static IReadOnlyDictionary<(int TableId, int Column), string> InferFieldPaths(
        Package package,
        string sheetName,
        CanonicalTableDescriptor table,
        IReadOnlyDictionary<(int TableId, int Column), string> metadataFieldPaths)
    {
        var result = new Dictionary<(int TableId, int Column), string>();
        var grid = ReadGrid(package, package.Sheets[sheetName]);
        var guidColumn = FindHeaderColumn(grid, 2, WorkbookProtocol.GuidColumnName);
        var projected = WorkbookLayout.CreateTable(table).Columns;
        for (var column = 1; column < guidColumn; column++)
        {
            if (metadataFieldPaths.TryGetValue((table.Id, column), out var registeredPath))
            {
                result[(table.Id, column)] = registeredPath;
                continue;
            }

            var propertyPath = grid.Text(2, column) ?? string.Empty;
            var match = projected.FirstOrDefault(candidate =>
                string.Equals(candidate.PropertyPath, propertyPath, StringComparison.Ordinal)
                || candidate.EffectiveAliases.Any(alias =>
                    string.Equals(alias, propertyPath, StringComparison.Ordinal)));
            result[(table.Id, column)] = match?.FieldPath
                ?? column.ToString(CultureInfo.InvariantCulture);
        }

        return result;
    }

    private static bool ProtocolProjectionMatches(
        Package package,
        string sheetName,
        XDocument desired)
    {
        if (!package.Sheets.TryGetValue(sheetName, out var partName))
            return false;
        var actualCells = ReadWorksheetCells(LoadXml(package.EntryBytes[partName]));
        var desiredCells = ReadWorksheetCells(desired);
        return actualCells.Count == desiredCells.Count
            && actualCells.All(pair => desiredCells.TryGetValue(pair.Key, out var value) && value == pair.Value);
    }

    private static bool GeneratedDataValidationsMatch(
        Package package,
        WorkbookTable table,
        IEnumerable<WorkbookTable> allTables)
    {
        if (!package.Sheets.TryGetValue(table.SheetName, out var partName))
            return false;
        var actual = LoadXml(package.EntryBytes[partName]);
        var desired = BuildTableWorksheet(table, allTables);
        return GeneratedValidationSignatures(actual).SequenceEqual(
            GeneratedValidationSignatures(desired),
            StringComparer.Ordinal);
    }

    private static IEnumerable<string> GeneratedValidationSignatures(XDocument document) =>
        document.Root?
            .Element(Spreadsheet + "dataValidations")?
            .Elements(Spreadsheet + "dataValidation")
            .Where(static item => string.Equals(
                item.Attribute("errorTitle")?.Value,
                "ExcelDB RowRef",
                StringComparison.Ordinal))
            .Select(item => string.Join(
                "|",
                item.Attribute("type")?.Value,
                item.Attribute("allowBlank")?.Value,
                item.Attribute("sqref")?.Value,
                item.Element(Spreadsheet + "formula1")?.Value))
            .Order(StringComparer.Ordinal)
        ?? Enumerable.Empty<string>();

    private static byte[] RepairProjectionPackage(
        byte[] packageBytes,
        WorkbookDefinition workbook)
    {
        var package = OpenPackage(packageBytes);
        var workbookDocument = LoadRequiredXml(package, "xl/workbook.xml");
        var relationshipDocument = LoadRequiredXml(package, "xl/_rels/workbook.xml.rels");
        var contentTypesDocument = LoadRequiredXml(package, "[Content_Types].xml");
        var sheetContainer = workbookDocument.Root?.Element(Spreadsheet + "sheets")
            ?? throw new InvalidDataException("Workbook XML has no sheets element.");
        var relationshipContainer = relationshipDocument.Root
            ?? throw new InvalidDataException("Workbook relationship XML has no root element.");
        var contentTypesContainer = contentTypesDocument.Root
            ?? throw new InvalidDataException("Content type XML has no root element.");
        var replacements = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var addedEntries = new List<PackageEntry>();

        UpsertProtocolProjection(
            package,
            sheetContainer,
            relationshipContainer,
            contentTypesContainer,
            WorkbookProtocol.MetadataSheetName,
            BuildMetadataWorksheet(workbook),
            replacements,
            addedEntries);
        UpsertProtocolProjection(
            package,
            sheetContainer,
            relationshipContainer,
            contentTypesContainer,
            WorkbookProtocol.KeySheetName,
            BuildKeyWorksheet(workbook),
            replacements,
            addedEntries);

        foreach (var table in workbook.Tables.Where(static table => !table.IsRetiredPreserved))
        {
            if (!package.Sheets.TryGetValue(table.SheetName, out var partName))
                continue;
            if (GeneratedDataValidationsMatch(package, table, workbook.Tables))
                continue;
            var document = LoadXml(package.EntryBytes[partName]);
            ProjectGeneratedDataValidations(document, BuildTableWorksheet(table, workbook.Tables));
            replacements[partName] = Serialize(document);
        }

        replacements["xl/workbook.xml"] = Serialize(workbookDocument);
        replacements["xl/_rels/workbook.xml.rels"] = Serialize(relationshipDocument);
        replacements["[Content_Types].xml"] = Serialize(contentTypesDocument);
        return WriteEntries(package.Entries
            .Select(entry => replacements.TryGetValue(entry.Name, out var replacement)
                ? entry with { Bytes = replacement }
                : entry)
            .Concat(addedEntries));
    }

    private static void UpsertProtocolProjection(
        Package package,
        XElement sheetContainer,
        XElement relationshipContainer,
        XElement contentTypesContainer,
        string sheetName,
        XDocument desired,
        IDictionary<string, byte[]> replacements,
        ICollection<PackageEntry> addedEntries)
    {
        if (package.Sheets.TryGetValue(sheetName, out var partName))
        {
            var sheet = FindSheetElement(sheetContainer, sheetName);
            sheet.SetAttributeValue("state", "hidden");
            var document = LoadXml(package.EntryBytes[partName]);
            ProjectEntireWorksheet(document, desired);
            replacements[partName] = Serialize(document);
            return;
        }

        var added = addedEntries as IEnumerable<PackageEntry> ?? addedEntries.ToArray();
        var newPartName = AllocateWorksheetPart(package.EntryBytes.Keys, added, new HashSet<string>());
        var relationshipId = AllocateRelationshipId(relationshipContainer);
        var sheetId = AllocateSheetId(sheetContainer);
        sheetContainer.Add(new XElement(
            Spreadsheet + "sheet",
            new XAttribute("name", sheetName),
            new XAttribute("sheetId", sheetId),
            new XAttribute("state", "hidden"),
            new XAttribute(OfficeRelationships + "id", relationshipId)));
        relationshipContainer.Add(new XElement(
            PackageRelationships + "Relationship",
            new XAttribute("Id", relationshipId),
            new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet"),
            new XAttribute("Target", newPartName[3..])));
        AddWorksheetContentType(contentTypesContainer, newPartName);
        addedEntries.Add(new PackageEntry(newPartName, Serialize(desired)));
    }

    private static WorkbookCell? ToTextCell(string? value) =>
        value is null ? null : new WorkbookCell(value);

    private static void AddReferenceDataValidations(
        XDocument document,
        WorkbookTable table,
        IEnumerable<WorkbookTable> referenceTables)
    {
        var orderedReferenceTables = referenceTables
            .Where(static candidate => !candidate.IsRetiredPreserved)
            .OrderBy(static candidate => candidate.TableId)
            .ToArray();
        var referenceColumns = table.Columns
            .Select((column, index) => (column, Number: index + 1))
            .Where(static item => string.Equals(item.column.TypeName, "exceldb.RowRef", StringComparison.Ordinal))
            .ToArray();
        if (referenceColumns.Length == 0)
            return;

        var validations = new XElement(
            Spreadsheet + "dataValidations",
            new XAttribute("count", referenceColumns.Length));
        foreach (var item in referenceColumns)
        {
            var columnName = ColumnName(item.Number);
            var referenceProjectionColumn = ResolveReferenceProjectionColumn(
                item.column.ReferenceTable,
                orderedReferenceTables);
            var keyColumnName = ColumnName(referenceProjectionColumn ?? 5);
            validations.Add(new XElement(
                Spreadsheet + "dataValidation",
                new XAttribute("type", "list"),
                new XAttribute("allowBlank", 1),
                new XAttribute("showErrorMessage", 1),
                new XAttribute("errorTitle", "ExcelDB RowRef"),
                new XAttribute("error", "Select a current ExcelDB asset key token."),
                new XAttribute("sqref", $"{columnName}{table.DataStartRow.ToString(CultureInfo.InvariantCulture)}:{columnName}1048576"),
                new XElement(
                    Spreadsheet + "formula1",
                    $"INDIRECT(\"'{WorkbookProtocol.KeySheetName}'!${keyColumnName}$2:${keyColumnName}$1048576\")")));
        }

        var sheetData = document.Root?.Element(Spreadsheet + "sheetData")
            ?? throw new InvalidDataException("Worksheet XML has no sheetData element.");
        sheetData.AddAfterSelf(validations);
    }

    private static int? ResolveReferenceProjectionColumn(
        string? referenceTable,
        IReadOnlyList<WorkbookTable> tables)
    {
        if (string.IsNullOrWhiteSpace(referenceTable))
            return null;
        for (var index = 0; index < tables.Count; index++)
        {
            var table = tables[index];
            if (string.Equals(referenceTable, table.ProtoName, StringComparison.Ordinal)
                || referenceTable.EndsWith('.' + table.ProtoName, StringComparison.Ordinal))
            {
                // The first five columns are the diagnostic projection; per-table lists begin at F.
                return 6 + index;
            }
        }

        return null;
    }

    private static string? ToReferenceToken(int tableId, string? canonicalKey)
    {
        if (!CanonicalKeyCodec.TryParse(canonicalKey, out var components)
            || components.Any(static component => component.Length == 0))
        {
            return null;
        }

        return RowReferenceToken.Format(tableId, components);
    }

    private static IReadOnlyList<WorkbookCell?> TextRow(params string?[] values) =>
        values.Select(static value => value is null ? null : (WorkbookCell?)new WorkbookCell(value)).ToArray();

    private static IReadOnlyList<WorkbookCell?> MetadataRow(
        string kind,
        string id,
        string? name = null,
        string? sheet = null,
        string? dataRow = null,
        string? column = null,
        string? span = null,
        string? value = null) =>
        TextRow(kind, id, name, sheet, dataRow, column, span, value);

    private static XDocument BuildWorksheet(
        IReadOnlyList<IReadOnlyList<WorkbookCell?>> rows,
        params int[] hiddenColumns)
    {
        var worksheet = new XElement(Spreadsheet + "worksheet");
        if (hiddenColumns.Length != 0)
        {
            worksheet.Add(new XElement(
                Spreadsheet + "cols",
                hiddenColumns.OrderBy(static column => column).Select(column =>
                    new XElement(
                        Spreadsheet + "col",
                        new XAttribute("min", column),
                        new XAttribute("max", column),
                        new XAttribute("hidden", 1),
                        new XAttribute("width", 0),
                        new XAttribute("customWidth", 1)))));
        }

        var sheetData = new XElement(Spreadsheet + "sheetData");
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var row = new XElement(Spreadsheet + "row", new XAttribute("r", rowIndex + 1));
            for (var columnIndex = 0; columnIndex < rows[rowIndex].Count; columnIndex++)
            {
                var cell = rows[rowIndex][columnIndex];
                if (cell is not null && !cell.IsBlank)
                    row.Add(CreateCell(rowIndex + 1, columnIndex + 1, cell));
            }

            sheetData.Add(row);
        }

        worksheet.Add(sheetData);
        return new XDocument(new XDeclaration("1.0", "utf-8", "yes"), worksheet);
    }

    private static XElement CreateCell(int row, int column, WorkbookCell cell)
    {
        var element = new XElement(
            Spreadsheet + "c",
            new XAttribute("r", $"{ColumnName(column)}{row.ToString(CultureInfo.InvariantCulture)}"));
        SetCellValue(element, cell);
        return element;
    }

    private static void SetCellValue(XElement element, WorkbookCell cell)
    {
        element.Elements().Remove();
        element.Attribute("t")?.Remove();
        if (cell.Formula is not null)
        {
            element.Add(new XElement(Spreadsheet + "f", cell.Formula));
            if (cell.Text is not null)
            {
                element.SetAttributeValue("t", "str");
                element.Add(new XElement(Spreadsheet + "v", cell.Text));
            }

            return;
        }

        if (cell.Text is null)
            return;

        element.SetAttributeValue("t", "inlineStr");
        element.Add(new XElement(
            Spreadsheet + "is",
            new XElement(
                Spreadsheet + "t",
                new XAttribute(XmlNamespace + "space", "preserve"),
                cell.Text)));
    }

    private static XDocument BuildWorkbook(IReadOnlyList<SheetPart> sheets)
    {
        var sheetElements = sheets.Select((sheet, index) =>
        {
            var element = new XElement(
                Spreadsheet + "sheet",
                new XAttribute("name", sheet.Name),
                new XAttribute("sheetId", index + 1),
                new XAttribute(OfficeRelationships + "id", $"rId{index + 1}"));
            if (sheet.Hidden)
                element.Add(new XAttribute("state", "hidden"));
            return element;
        });
        return new XDocument(
            new XDeclaration("1.0", "utf-8", "yes"),
            new XElement(
                Spreadsheet + "workbook",
                new XAttribute(XNamespace.Xmlns + "r", OfficeRelationships),
                new XElement(Spreadsheet + "sheets", sheetElements)));
    }

    private static XDocument BuildWorkbookRelationships(int sheetCount)
    {
        var relationships = new XElement(PackageRelationships + "Relationships");
        for (var index = 0; index < sheetCount; index++)
        {
            relationships.Add(new XElement(
                PackageRelationships + "Relationship",
                new XAttribute("Id", $"rId{index + 1}"),
                new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet"),
                new XAttribute("Target", $"worksheets/sheet{index + 1}.xml")));
        }

        relationships.Add(new XElement(
            PackageRelationships + "Relationship",
            new XAttribute("Id", $"rId{sheetCount + 1}"),
            new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles"),
            new XAttribute("Target", "styles.xml")));
        return new XDocument(new XDeclaration("1.0", "utf-8", "yes"), relationships);
    }

    private static XDocument BuildRootRelationships() => new(
        new XDeclaration("1.0", "utf-8", "yes"),
        new XElement(
            PackageRelationships + "Relationships",
            new XElement(
                PackageRelationships + "Relationship",
                new XAttribute("Id", "rId1"),
                new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument"),
                new XAttribute("Target", "xl/workbook.xml"))));

    private static XDocument BuildContentTypes(int sheetCount)
    {
        var types = new XElement(
            ContentTypes + "Types",
            new XElement(
                ContentTypes + "Default",
                new XAttribute("Extension", "rels"),
                new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
            new XElement(
                ContentTypes + "Default",
                new XAttribute("Extension", "xml"),
                new XAttribute("ContentType", "application/xml")),
            new XElement(
                ContentTypes + "Override",
                new XAttribute("PartName", "/xl/workbook.xml"),
                new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml")),
            new XElement(
                ContentTypes + "Override",
                new XAttribute("PartName", "/xl/styles.xml"),
                new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml")));
        for (var index = 0; index < sheetCount; index++)
        {
            types.Add(new XElement(
                ContentTypes + "Override",
                new XAttribute("PartName", $"/xl/worksheets/sheet{index + 1}.xml"),
                new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml")));
        }

        return new XDocument(new XDeclaration("1.0", "utf-8", "yes"), types);
    }

    private static XDocument BuildStyles() => new(
        new XDeclaration("1.0", "utf-8", "yes"),
        new XElement(
            Spreadsheet + "styleSheet",
            new XElement(Spreadsheet + "fonts", new XAttribute("count", 1), new XElement(Spreadsheet + "font")),
            new XElement(
                Spreadsheet + "fills",
                new XAttribute("count", 1),
                new XElement(Spreadsheet + "fill", new XElement(Spreadsheet + "patternFill", new XAttribute("patternType", "none")))),
            new XElement(
                Spreadsheet + "borders",
                new XAttribute("count", 1),
                new XElement(Spreadsheet + "border")),
            new XElement(
                Spreadsheet + "cellStyleXfs",
                new XAttribute("count", 1),
                new XElement(Spreadsheet + "xf", new XAttribute("numFmtId", 0), new XAttribute("fontId", 0), new XAttribute("fillId", 0), new XAttribute("borderId", 0))),
            new XElement(
                Spreadsheet + "cellXfs",
                new XAttribute("count", 1),
                new XElement(Spreadsheet + "xf", new XAttribute("numFmtId", 0), new XAttribute("fontId", 0), new XAttribute("fillId", 0), new XAttribute("borderId", 0), new XAttribute("xfId", 0)))));

    private static void ValidateProjectedSheetNames(
        WorkbookDefinition sourceWorkbook,
        WorkbookDefinition projectedWorkbook,
        Package package)
    {
        var projectedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in projectedWorkbook.Tables)
        {
            if (string.IsNullOrWhiteSpace(table.SheetName))
                throw new InvalidDataException($"Table {table.TableId} has no worksheet name.");
            if (string.Equals(table.SheetName, WorkbookProtocol.MetadataSheetName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(table.SheetName, WorkbookProtocol.KeySheetName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Worksheet name '{table.SheetName}' is reserved by ExcelDB.");
            }

            if (!projectedNames.Add(table.SheetName))
                throw new InvalidDataException($"Duplicate projected worksheet name '{table.SheetName}'.");
        }
        foreach (var child in projectedWorkbook.EffectiveChildTables)
        {
            if (string.IsNullOrWhiteSpace(child.SheetName))
                throw new InvalidDataException($"Child table {ChildOwnerKey(child.OwnerTableId, child.OwnerFieldIdPath)} has no worksheet name.");
            if (string.Equals(child.SheetName, WorkbookProtocol.MetadataSheetName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(child.SheetName, WorkbookProtocol.KeySheetName, StringComparison.OrdinalIgnoreCase)
                || !projectedNames.Add(child.SheetName))
            {
                throw new InvalidDataException($"Duplicate or reserved projected worksheet name '{child.SheetName}'.");
            }
        }

        var managedSourceNames = sourceWorkbook.Tables
            .Select(static table => table.SheetName)
            .Concat(sourceWorkbook.EffectiveChildTables.Select(static table => table.SheetName))
            .Append(WorkbookProtocol.MetadataSheetName)
            .Append(WorkbookProtocol.KeySheetName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var freeNames = package.Sheets.Keys
            .Where(name => !managedSourceNames.Contains(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var collision = projectedNames.FirstOrDefault(freeNames.Contains);
        if (collision is not null)
        {
            throw new InvalidDataException(
                $"Projected worksheet '{collision}' conflicts with an unowned worksheet of the same name.");
        }

        var duplicateId = projectedWorkbook.Tables
            .GroupBy(static table => table.TableId)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicateId is not null)
            throw new InvalidDataException($"Duplicate projected table id '{duplicateId.Key}'.");
        var duplicateChild = projectedWorkbook.EffectiveChildTables
            .GroupBy(static child => ChildOwnerKey(child.OwnerTableId, child.OwnerFieldIdPath), StringComparer.Ordinal)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicateChild is not null)
            throw new InvalidDataException($"Duplicate projected child table '{duplicateChild.Key}'.");
    }

    private static void ProjectTableWorksheet(
        XDocument document,
        WorkbookTable sourceTable,
        WorkbookTable projectedTable,
        IEnumerable<WorkbookTable> projectedTables,
        bool purgeUnownedCells)
    {
        var desiredWorksheet = BuildTableWorksheet(projectedTable, projectedTables);
        var desired = ReadWorksheetCells(desiredWorksheet);
        var existing = ReadWorksheetCoordinates(document);
        var patches = new Dictionary<(int Row, int Column), WorkbookCell?>();

        foreach (var coordinate in existing)
        {
            var wasOwned = IsManagedCoordinate(sourceTable, coordinate.Row, coordinate.Column);
            var becomesOwned = IsManagedCoordinate(projectedTable, coordinate.Row, coordinate.Column);
            if (!wasOwned && becomesOwned && !purgeUnownedCells)
            {
                throw new InvalidDataException(
                    $"Worksheet '{projectedTable.SheetName}' cannot manage "
                    + $"{ColumnName(coordinate.Column)}{coordinate.Row} because the source cell is not owned by ExcelDB. "
                    + "Use explicit purge mode to remove or replace the colliding cell.");
            }

            if (wasOwned || becomesOwned)
                patches[coordinate] = desired.GetValueOrDefault(coordinate);
        }

        foreach (var desiredCell in desired)
            patches[desiredCell.Key] = desiredCell.Value;

        PatchWorksheet(
            document,
            patches.Select(static patch => new CellPatch(
                string.Empty,
                patch.Key.Row,
                patch.Key.Column,
                patch.Value)));
        UpdateManagedHiddenColumns(document, sourceTable.Columns.Length, projectedTable.Columns.Length);
        ProjectGeneratedDataValidations(document, desiredWorksheet);
    }

    private static void ProjectChildTableWorksheet(
        XDocument document,
        WorkbookChildTable sourceTable,
        WorkbookChildTable projectedTable,
        bool purgeUnownedCells)
    {
        var desired = ReadWorksheetCells(BuildChildTableWorksheet(projectedTable));
        var existing = ReadWorksheetCoordinates(document);
        var patches = new Dictionary<(int Row, int Column), WorkbookCell?>();
        var sourceWidth = ChildSystemColumnCount(sourceTable.Kind) + sourceTable.Columns.Length;
        var projectedWidth = ChildSystemColumnCount(projectedTable.Kind) + projectedTable.Columns.Length;
        foreach (var coordinate in existing)
        {
            var wasOwned = coordinate.Column <= sourceWidth
                           && (coordinate.Row <= WorkbookProtocol.HeaderRows || coordinate.Row >= sourceTable.DataStartRow);
            var becomesOwned = coordinate.Column <= projectedWidth
                               && (coordinate.Row <= WorkbookProtocol.HeaderRows || coordinate.Row >= projectedTable.DataStartRow);
            if (!wasOwned && becomesOwned && !purgeUnownedCells)
            {
                throw new InvalidDataException(
                    $"Child worksheet '{projectedTable.SheetName}' cannot manage "
                    + $"{ColumnName(coordinate.Column)}{coordinate.Row} because the source cell is not owned by ExcelDB. "
                    + "Use explicit purge mode to remove or replace the colliding cell.");
            }
            if (wasOwned || becomesOwned)
                patches[coordinate] = desired.GetValueOrDefault(coordinate);
        }
        foreach (var desiredCell in desired)
            patches[desiredCell.Key] = desiredCell.Value;
        PatchWorksheet(document, patches.Select(static patch => new CellPatch(
            string.Empty,
            patch.Key.Row,
            patch.Key.Column,
            patch.Value)));
    }

    private static void ProjectGeneratedDataValidations(XDocument document, XDocument desiredDocument)
    {
        var worksheet = document.Root ?? throw new InvalidDataException("Worksheet XML has no root element.");
        var existing = worksheet.Element(Spreadsheet + "dataValidations");
        if (existing is not null)
        {
            foreach (var item in existing.Elements(Spreadsheet + "dataValidation")
                         .Where(static item => string.Equals(
                             item.Attribute("errorTitle")?.Value,
                             "ExcelDB RowRef",
                             StringComparison.Ordinal))
                         .ToArray())
            {
                item.Remove();
            }
        }

        var desired = desiredDocument.Root?
            .Element(Spreadsheet + "dataValidations")?
            .Elements(Spreadsheet + "dataValidation")
            .Select(static item => new XElement(item))
            .ToArray() ?? [];
        if (existing is null && desired.Length != 0)
        {
            existing = new XElement(Spreadsheet + "dataValidations");
            var sheetData = worksheet.Element(Spreadsheet + "sheetData")
                ?? throw new InvalidDataException("Worksheet XML has no sheetData element.");
            sheetData.AddAfterSelf(existing);
        }

        if (existing is null)
            return;
        existing.Add(desired);
        var count = existing.Elements(Spreadsheet + "dataValidation").Count();
        if (count == 0)
            existing.Remove();
        else
            existing.SetAttributeValue("count", count);
    }

    private static bool IsManagedCoordinate(WorkbookTable table, int row, int column) =>
        column <= table.Columns.Length + 2
        && (row <= WorkbookProtocol.HeaderRows || row >= table.DataStartRow);

    private static void ProjectProtocolSheet(
        Package package,
        XElement sheetContainer,
        string sheetName,
        XDocument desiredDocument,
        IDictionary<string, byte[]> replacements)
    {
        if (!package.Sheets.TryGetValue(sheetName, out var partName))
            throw new InvalidDataException($"Missing protocol worksheet '{sheetName}'.");

        var sheetElement = FindSheetElement(sheetContainer, sheetName);
        sheetElement.SetAttributeValue("state", "hidden");
        var document = LoadXml(package.EntryBytes[partName]);
        ProjectEntireWorksheet(document, desiredDocument);
        replacements[partName] = Serialize(document);
    }

    private static void ProjectEntireWorksheet(XDocument document, XDocument desiredDocument)
    {
        var desired = ReadWorksheetCells(desiredDocument);
        var patches = ReadWorksheetCoordinates(document)
            .ToDictionary(
                static coordinate => coordinate,
                coordinate => desired.GetValueOrDefault(coordinate));
        foreach (var desiredCell in desired)
            patches[desiredCell.Key] = desiredCell.Value;
        PatchWorksheet(
            document,
            patches.Select(static patch => new CellPatch(
                string.Empty,
                patch.Key.Row,
                patch.Key.Column,
                patch.Value)));
    }

    private static Dictionary<(int Row, int Column), WorkbookCell> ReadWorksheetCells(XDocument document)
    {
        var cells = new Dictionary<(int Row, int Column), WorkbookCell>();
        foreach (var element in document.Descendants(Spreadsheet + "c"))
        {
            var reference = RequiredAttribute(element, "r");
            if (!TryParseCellReference(reference, out var row, out var column))
                throw new InvalidDataException($"Invalid worksheet cell reference '{reference}'.");
            var cell = ReadCellElement(element, ImmutableArray<string>.Empty);
            if (cell is not null)
                cells.Add((row, column), cell);
        }

        return cells;
    }

    private static HashSet<(int Row, int Column)> ReadWorksheetCoordinates(XDocument document)
    {
        var cells = new HashSet<(int Row, int Column)>();
        foreach (var element in document.Descendants(Spreadsheet + "c"))
        {
            var reference = RequiredAttribute(element, "r");
            if (!TryParseCellReference(reference, out var row, out var column))
                throw new InvalidDataException($"Invalid worksheet cell reference '{reference}'.");
            if (!cells.Add((row, column)))
                throw new InvalidDataException($"Duplicate worksheet cell reference '{reference}'.");
        }

        return cells;
    }

    private static void UpdateManagedHiddenColumns(
        XDocument document,
        int sourceColumnCount,
        int projectedColumnCount)
    {
        var worksheet = document.Root ?? throw new InvalidDataException("Worksheet XML has no root element.");
        var columns = worksheet.Element(Spreadsheet + "cols");
        if (columns is not null)
        {
            foreach (var column in columns.Elements(Spreadsheet + "col").ToArray())
            {
                if (IsGeneratedHiddenColumn(column, sourceColumnCount + 1)
                    || IsGeneratedHiddenColumn(column, sourceColumnCount + 2))
                {
                    column.Remove();
                }
            }
        }

        if (columns is null)
        {
            columns = new XElement(Spreadsheet + "cols");
            var sheetData = worksheet.Element(Spreadsheet + "sheetData")
                ?? throw new InvalidDataException("Worksheet XML has no sheetData element.");
            sheetData.AddBeforeSelf(columns);
        }

        foreach (var columnNumber in new[] { projectedColumnCount + 1, projectedColumnCount + 2 })
        {
            columns.Add(new XElement(
                Spreadsheet + "col",
                new XAttribute("min", columnNumber),
                new XAttribute("max", columnNumber),
                new XAttribute("hidden", 1),
                new XAttribute("width", 0),
                new XAttribute("customWidth", 1)));
        }
    }

    private static bool IsGeneratedHiddenColumn(XElement element, int column) =>
        element.Attributes().Count() == 5
        && element.Attribute("min")?.Value == column.ToString(CultureInfo.InvariantCulture)
        && element.Attribute("max")?.Value == column.ToString(CultureInfo.InvariantCulture)
        && element.Attribute("hidden")?.Value == "1"
        && element.Attribute("width")?.Value == "0"
        && element.Attribute("customWidth")?.Value == "1";

    private static XDocument LoadRequiredXml(Package package, string partName) =>
        package.EntryBytes.TryGetValue(partName, out var bytes)
            ? LoadXml(bytes)
            : throw new InvalidDataException($"The package is missing '{partName}'.");

    private static XElement FindSheetElement(XElement sheetContainer, string sheetName) =>
        sheetContainer.Elements(Spreadsheet + "sheet")
            .FirstOrDefault(element => string.Equals(element.Attribute("name")?.Value, sheetName, StringComparison.Ordinal))
        ?? throw new InvalidDataException($"Worksheet '{sheetName}' is not registered in workbook XML.");

    private static string RequiredRelationshipId(XElement sheetElement, string sheetName) =>
        sheetElement.Attribute(OfficeRelationships + "id")?.Value
        ?? throw new InvalidDataException($"Worksheet '{sheetName}' has no relationship id.");

    private static XElement? FindRelationship(XElement relationshipContainer, string relationshipId) =>
        relationshipContainer.Elements(PackageRelationships + "Relationship")
            .FirstOrDefault(element => string.Equals(element.Attribute("Id")?.Value, relationshipId, StringComparison.Ordinal));

    private static string AllocateWorksheetPart(
        IEnumerable<string> existingPartNames,
        IEnumerable<PackageEntry> addedEntries,
        IReadOnlySet<string> removedParts)
    {
        var names = existingPartNames
            .Concat(addedEntries.Select(static entry => entry.Name))
            .Concat(removedParts)
            .ToHashSet(StringComparer.Ordinal);
        for (var index = 1; ; index++)
        {
            var candidate = $"xl/worksheets/sheet{index.ToString(CultureInfo.InvariantCulture)}.xml";
            if (!names.Contains(candidate))
                return candidate;
        }
    }

    private static string AllocateRelationshipId(XElement relationshipContainer)
    {
        var ids = relationshipContainer.Elements(PackageRelationships + "Relationship")
            .Select(static element => element.Attribute("Id")?.Value)
            .Where(static id => id is not null)
            .ToHashSet(StringComparer.Ordinal);
        for (var index = 1; ; index++)
        {
            var candidate = $"rId{index.ToString(CultureInfo.InvariantCulture)}";
            if (!ids.Contains(candidate))
                return candidate;
        }
    }

    private static int AllocateSheetId(XElement sheetContainer)
    {
        var ids = sheetContainer.Elements(Spreadsheet + "sheet")
            .Select(static element => ParsePositiveInt(element.Attribute("sheetId")?.Value))
            .Where(static id => id != int.MaxValue)
            .ToHashSet();
        for (var index = 1; ; index++)
        {
            if (!ids.Contains(index))
                return index;
        }
    }

    private static void AddWorksheetContentType(XElement contentTypesContainer, string partName)
    {
        var registeredName = "/" + partName;
        if (contentTypesContainer.Elements(ContentTypes + "Override")
            .Any(element => string.Equals(element.Attribute("PartName")?.Value, registeredName, StringComparison.Ordinal)))
        {
            return;
        }

        contentTypesContainer.Add(new XElement(
            ContentTypes + "Override",
            new XAttribute("PartName", registeredName),
            new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml")));
    }

    private static void RemoveWorksheetContentType(XElement contentTypesContainer, string partName)
    {
        var registeredName = "/" + partName;
        foreach (var element in contentTypesContainer.Elements(ContentTypes + "Override")
            .Where(element => string.Equals(element.Attribute("PartName")?.Value, registeredName, StringComparison.Ordinal))
            .ToArray())
        {
            element.Remove();
        }
    }

    private static Package OpenPackage(byte[] packageBytes)
    {
        var entries = ReadEntries(packageBytes);
        var entryBytes = entries.ToDictionary(static entry => entry.Name, static entry => entry.Bytes, StringComparer.Ordinal);
        if (!entryBytes.TryGetValue("xl/workbook.xml", out var workbookBytes)
            || !entryBytes.TryGetValue("xl/_rels/workbook.xml.rels", out var relationshipBytes))
        {
            throw new InvalidDataException("The package is missing workbook relationships.");
        }

        var relationships = LoadXml(relationshipBytes)
            .Root?
            .Elements(PackageRelationships + "Relationship")
            .ToDictionary(
                element => RequiredAttribute(element, "Id"),
                element => ResolveWorkbookTarget(RequiredAttribute(element, "Target")),
                StringComparer.Ordinal)
            ?? throw new InvalidDataException("The workbook relationship part is empty.");

        var workbook = LoadXml(workbookBytes);
        var sheets = new Dictionary<string, string>(StringComparer.Ordinal);
        var states = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var sheet in workbook.Descendants(Spreadsheet + "sheet"))
        {
            var name = RequiredAttribute(sheet, "name");
            var relationshipId = sheet.Attribute(OfficeRelationships + "id")?.Value
                ?? throw new InvalidDataException($"Worksheet '{name}' has no relationship id.");
            if (!relationships.TryGetValue(relationshipId, out var target) || !entryBytes.ContainsKey(target))
                throw new InvalidDataException($"Worksheet '{name}' points to missing part '{relationshipId}'.");
            if (!sheets.TryAdd(name, target))
                throw new InvalidDataException($"Duplicate worksheet name '{name}'.");
            states.Add(name, sheet.Attribute("state")?.Value);
        }

        var sharedStrings = entryBytes.TryGetValue("xl/sharedStrings.xml", out var sharedStringBytes)
            ? LoadXml(sharedStringBytes)
                .Descendants(Spreadsheet + "si")
                .Select(static item => string.Concat(item.Descendants(Spreadsheet + "t").Select(static text => text.Value)))
                .ToImmutableArray()
            : ImmutableArray<string>.Empty;
        return new Package(entries, entryBytes, sheets, states, sharedStrings);
    }

    private static WorksheetGrid ReadGrid(Package package, string partName)
    {
        var document = LoadXml(package.EntryBytes[partName]);
        var cells = new Dictionary<(int Row, int Column), WorkbookCell>();
        foreach (var cellElement in document.Descendants(Spreadsheet + "c"))
        {
            var reference = cellElement.Attribute("r")?.Value;
            if (reference is null || !TryParseCellReference(reference, out var row, out var column))
                continue;
            var cell = ReadCellElement(cellElement, package.SharedStrings);
            if (cell is not null)
                cells[(row, column)] = cell;
        }

        return new WorksheetGrid(cells);
    }

    private static WorkbookCell? ReadCellElement(XElement cell, ImmutableArray<string> sharedStrings)
    {
        var formula = cell.Element(Spreadsheet + "f")?.Value;
        var type = cell.Attribute("t")?.Value;
        string? text;
        if (string.Equals(type, "inlineStr", StringComparison.Ordinal))
        {
            text = string.Concat(cell.Descendants(Spreadsheet + "t").Select(static item => item.Value));
        }
        else if (string.Equals(type, "s", StringComparison.Ordinal))
        {
            var indexText = cell.Element(Spreadsheet + "v")?.Value;
            if (!int.TryParse(indexText, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                || index < 0
                || index >= sharedStrings.Length)
            {
                throw new InvalidDataException($"Invalid shared string index '{indexText}'.");
            }

            text = sharedStrings[index];
        }
        else
        {
            text = cell.Element(Spreadsheet + "v")?.Value;
        }

        return formula is null && text is null ? null : new WorkbookCell(text, formula);
    }

    private static string ReadWorkbookMetadata(WorksheetGrid grid, string id)
    {
        for (var row = 2; row <= grid.MaxRow; row++)
        {
            if (string.Equals(grid.Text(row, 1), "workbook", StringComparison.Ordinal)
                && string.Equals(grid.Text(row, 2), id, StringComparison.Ordinal))
            {
                return grid.Text(row, 8)
                    ?? throw new InvalidDataException($"Workbook metadata '{id}' has no value.");
            }
        }

        throw new InvalidDataException($"Workbook metadata '{id}' is missing.");
    }

    private static IReadOnlyList<TableMetadata> ReadTableMetadata(WorksheetGrid grid)
    {
        var tables = new List<TableMetadata>();
        for (var row = 2; row <= grid.MaxRow; row++)
        {
            if (!string.Equals(grid.Text(row, 1), "table", StringComparison.Ordinal))
                continue;
            if (!int.TryParse(grid.Text(row, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var tableId)
                || tableId <= 0)
            {
                throw new InvalidDataException($"Invalid table id at {WorkbookProtocol.MetadataSheetName}!A{row}.");
            }

            var name = grid.Text(row, 3)
                ?? throw new InvalidDataException($"Table {tableId} has no proto name.");
            var sheet = grid.Text(row, 4)
                ?? throw new InvalidDataException($"Table {tableId} has no worksheet name.");
            if (!int.TryParse(grid.Text(row, 5), NumberStyles.None, CultureInfo.InvariantCulture, out var dataRow)
                || dataRow <= WorkbookProtocol.HeaderRows)
            {
                throw new InvalidDataException($"Table {tableId} has an invalid data start row.");
            }

            tables.Add(new TableMetadata(tableId, name, sheet, dataRow));
        }

        return tables;
    }

    private static IReadOnlyList<ChildTableMetadata> ReadChildTableMetadata(WorksheetGrid grid)
    {
        var tables = new List<ChildTableMetadata>();
        for (var row = 2; row <= grid.MaxRow; row++)
        {
            if (!string.Equals(grid.Text(row, 1), "child", StringComparison.Ordinal))
                continue;
            if (!TryParseChildOwnerKey(grid.Text(row, 2), out var ownerTableId, out var ownerPath))
                throw new InvalidDataException($"Invalid child owner at {WorkbookProtocol.MetadataSheetName}!A{row}.");
            if (!Enum.TryParse<CanonicalChildTableKind>(grid.Text(row, 3), false, out var kind))
                throw new InvalidDataException($"Invalid child kind at {WorkbookProtocol.MetadataSheetName}!C{row}.");
            var sheet = grid.Text(row, 4)
                ?? throw new InvalidDataException($"Child table {ownerTableId}:{string.Join('.', ownerPath)} has no worksheet name.");
            if (!int.TryParse(grid.Text(row, 5), NumberStyles.None, CultureInfo.InvariantCulture, out var dataRow)
                || dataRow <= WorkbookProtocol.HeaderRows)
            {
                throw new InvalidDataException($"Child table '{sheet}' has an invalid data start row.");
            }
            tables.Add(new ChildTableMetadata(ownerTableId, ownerPath, kind, sheet, dataRow));
        }
        return tables;
    }

    private static ImmutableArray<string> ReadMigrationMarkers(WorksheetGrid grid)
    {
        var markers = ImmutableArray.CreateBuilder<string>();
        var unique = new HashSet<string>(StringComparer.Ordinal);
        for (var row = 2; row <= grid.MaxRow; row++)
        {
            if (!string.Equals(grid.Text(row, 1), "migration", StringComparison.Ordinal))
                continue;
            var id = grid.Text(row, 2);
            var versionText = grid.Text(row, 3);
            var state = grid.Text(row, 8);
            if (string.IsNullOrWhiteSpace(id)
                || id.Contains('@', StringComparison.Ordinal)
                || !int.TryParse(versionText, NumberStyles.None, CultureInfo.InvariantCulture, out var version)
                || version <= 0
                || !string.Equals(state, "applied", StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Invalid migration marker at {WorkbookProtocol.MetadataSheetName}!A{row}.");
            }
            var marker = $"{id}@{version.ToString(CultureInfo.InvariantCulture)}";
            if (!unique.Add(marker))
                throw new InvalidDataException($"Duplicate migration marker '{marker}'.");
            markers.Add(marker);
        }
        return markers.Order(StringComparer.Ordinal).ToImmutableArray();
    }

    private static void ParseMigrationMarker(string marker, out string id, out int version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(marker);
        var separator = marker.LastIndexOf('@');
        if (separator <= 0
            || marker.IndexOf('@') != separator
            || !int.TryParse(marker.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out version)
            || version <= 0)
        {
            throw new InvalidDataException($"Invalid migration marker '{marker}'. Expected '<id>@<positive-version>'.");
        }
        id = marker[..separator];
    }

    private static IReadOnlyDictionary<(int TableId, int Column), string> ReadFieldPaths(WorksheetGrid grid)
    {
        var paths = new Dictionary<(int TableId, int Column), string>();
        for (var row = 2; row <= grid.MaxRow; row++)
        {
            if (!string.Equals(grid.Text(row, 1), "field", StringComparison.Ordinal))
                continue;
            var id = grid.Text(row, 2);
            var separator = id?.IndexOf(':') ?? -1;
            if (separator <= 0
                || !int.TryParse(id.AsSpan(0, separator), NumberStyles.None, CultureInfo.InvariantCulture, out var tableId)
                || !int.TryParse(grid.Text(row, 6), NumberStyles.None, CultureInfo.InvariantCulture, out var column)
                || column <= 0)
            {
                throw new InvalidDataException($"Invalid field metadata at row {row}.");
            }

            if (!paths.TryAdd((tableId, column), id![(separator + 1)..]))
                throw new InvalidDataException($"Duplicate field metadata for table {tableId}, column {column}.");
        }

        return paths;
    }

    private static IReadOnlyDictionary<(string OwnerKey, int Column), string> ReadChildFieldPaths(WorksheetGrid grid)
    {
        var paths = new Dictionary<(string OwnerKey, int Column), string>();
        for (var row = 2; row <= grid.MaxRow; row++)
        {
            if (!string.Equals(grid.Text(row, 1), "child_field", StringComparison.Ordinal))
                continue;
            var id = grid.Text(row, 2) ?? string.Empty;
            var first = id.IndexOf(':');
            var second = first < 0 ? -1 : id.IndexOf(':', first + 1);
            if (first <= 0 || second <= first + 1
                || !TryParseChildOwnerKey(id[..second], out _, out _)
                || !int.TryParse(grid.Text(row, 6), NumberStyles.None, CultureInfo.InvariantCulture, out var column)
                || column <= 0)
            {
                throw new InvalidDataException($"Invalid child field metadata at row {row}.");
            }
            var ownerKey = id[..second];
            if (!paths.TryAdd((ownerKey, column), id[(second + 1)..]))
                throw new InvalidDataException($"Duplicate child field metadata for {ownerKey}, column {column}.");
        }
        return paths;
    }

    private static string ChildOwnerKey(int tableId, ImmutableArray<int> path) =>
        $"{tableId.ToString(CultureInfo.InvariantCulture)}:{string.Join('.', path)}";

    private static bool TryParseChildOwnerKey(
        string? value,
        out int tableId,
        out ImmutableArray<int> path)
    {
        tableId = 0;
        path = [];
        var separator = value?.IndexOf(':') ?? -1;
        if (separator <= 0
            || !int.TryParse(value.AsSpan(0, separator), NumberStyles.None, CultureInfo.InvariantCulture, out tableId)
            || tableId <= 0)
            return false;
        var builder = ImmutableArray.CreateBuilder<int>();
        foreach (var segment in value![(separator + 1)..].Split('.'))
        {
            if (!int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var fieldId)
                || fieldId <= 0)
                return false;
            builder.Add(fieldId);
        }
        path = builder.ToImmutable();
        return !path.IsEmpty;
    }

    private static KeyMap ReadKeyMap(Package package)
    {
        var identities = new Dictionary<(int TableId, RowGuid RowGuid), string?>();
        var locations = new Dictionary<(int TableId, int RowNumber), string?>();
        if (!package.Sheets.TryGetValue(WorkbookProtocol.KeySheetName, out var keyPart))
            return new KeyMap(identities, locations);
        var grid = ReadGrid(package, keyPart);
        for (var row = 2; row <= grid.MaxRow; row++)
        {
            if (!int.TryParse(grid.Text(row, 1), NumberStyles.None, CultureInfo.InvariantCulture, out var tableId))
                continue;
            var key = grid.Text(row, 3);
            if (RowGuid.TryParse(grid.Text(row, 2), out var rowGuid))
                identities.TryAdd((tableId, rowGuid), key);
            if (int.TryParse(grid.Text(row, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var sourceRow))
                locations.TryAdd((tableId, sourceRow), key);
        }

        return new KeyMap(identities, locations);
    }

    private static WorkbookTable ReadTable(
        Package package,
        string partName,
        TableMetadata metadata,
        IReadOnlyDictionary<(int TableId, int Column), string> fieldPaths,
        KeyMap keyMap)
    {
        var grid = ReadGrid(package, partName);
        var guidColumn = FindHeaderColumn(grid, 2, WorkbookProtocol.GuidColumnName);
        var revisionColumn = FindHeaderColumn(grid, 2, WorkbookProtocol.RevisionColumnName);
        if (guidColumn <= 1 || revisionColumn != guidColumn + 1 || revisionColumn != grid.MaxColumnAtRow(2))
        {
            throw new InvalidDataException(
                $"Worksheet '{metadata.SheetName}' must end with adjacent {WorkbookProtocol.GuidColumnName}/{WorkbookProtocol.RevisionColumnName} columns.");
        }

        var columns = new List<WorkbookColumn>();
        for (var column = 1; column < guidColumn; column++)
        {
            var displayName = grid.Text(1, column) ?? string.Empty;
            var propertyPath = grid.Text(2, column)
                ?? throw new InvalidDataException($"Worksheet '{metadata.SheetName}' column {column} has no property path.");
            var typeName = grid.Text(3, column) ?? "string";
            var fieldPath = fieldPaths.GetValueOrDefault((metadata.TableId, column))
                ?? column.ToString(CultureInfo.InvariantCulture);
            columns.Add(new WorkbookColumn(displayName, propertyPath, typeName, fieldPath));
        }

        var rows = new List<WorkbookRow>();
        for (var rowNumber = metadata.DataStartRow; rowNumber <= grid.MaxRow; rowNumber++)
        {
            if (!grid.HasAny(rowNumber, 1, revisionColumn))
                continue;
            var values = ImmutableDictionary.CreateBuilder<string, WorkbookCell>(StringComparer.Ordinal);
            for (var column = 1; column < guidColumn; column++)
            {
                var value = grid.Get(rowNumber, column);
                if (value is not null && !value.IsBlank)
                    values[columns[column - 1].PropertyPath] = value;
            }

            var rawGuid = grid.CellText(rowNumber, guidColumn);
            RowGuid? rowGuid = RowGuid.TryParse(rawGuid, out var parsedGuid) ? parsedGuid : null;
            var rawRevision = grid.CellText(rowNumber, revisionColumn);
            var revision = uint.TryParse(rawRevision, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedRevision)
                ? parsedRevision
                : 0;
            var key = keyMap.ByLocation.GetValueOrDefault((metadata.TableId, rowNumber));
            if (key is null && rowGuid is { } validGuid)
                key = keyMap.ByIdentity.GetValueOrDefault((metadata.TableId, validGuid));
            rows.Add(new WorkbookRow(
                rowGuid,
                revision,
                values.ToImmutable(),
                key,
                rawGuid,
                rawRevision,
                rowNumber,
                KeyIsProjection: key is not null));
        }

        return new WorkbookTable(
            metadata.TableId,
            metadata.ProtoName,
            metadata.SheetName,
            metadata.DataStartRow,
            columns.ToImmutableArray(),
            rows.ToImmutableArray());
    }

    private static WorkbookChildTable ReadChildTable(
        Package package,
        string partName,
        ChildTableMetadata metadata,
        IReadOnlyDictionary<(string OwnerKey, int Column), string> fieldPaths)
    {
        var grid = ReadGrid(package, partName);
        var systemColumns = ChildSystemColumnCount(metadata.Kind);
        if (FindHeaderColumn(grid, 2, WorkbookProtocol.ParentGuidColumnName) != 1)
            throw new InvalidDataException($"Child worksheet '{metadata.SheetName}' must start with {WorkbookProtocol.ParentGuidColumnName}.");
        var identityName = metadata.Kind == CanonicalChildTableKind.RepeatedMessage
            ? WorkbookProtocol.OrdinalColumnName
            : WorkbookProtocol.MapKeyColumnName;
        if (FindHeaderColumn(grid, 2, identityName) != 2)
            throw new InvalidDataException($"Child worksheet '{metadata.SheetName}' must use '{identityName}' in column 2.");

        var ownerKey = ChildOwnerKey(metadata.OwnerTableId, metadata.OwnerFieldIdPath);
        var columns = new List<WorkbookColumn>();
        for (var column = systemColumns + 1; column <= grid.MaxColumnAtRow(2); column++)
        {
            var displayName = grid.Text(1, column) ?? string.Empty;
            var propertyPath = grid.Text(2, column)
                ?? throw new InvalidDataException($"Child worksheet '{metadata.SheetName}' column {column} has no property path.");
            var typeName = grid.Text(3, column) ?? "string";
            var fieldPath = fieldPaths.GetValueOrDefault((ownerKey, column))
                ?? throw new InvalidDataException($"Child worksheet '{metadata.SheetName}' column {column} has no numeric field path.");
            columns.Add(new WorkbookColumn(displayName, propertyPath, typeName, fieldPath));
        }

        var rows = new List<WorkbookChildRow>();
        var unique = new HashSet<string>(StringComparer.Ordinal);
        for (var rowNumber = metadata.DataStartRow; rowNumber <= grid.MaxRow; rowNumber++)
        {
            if (!grid.HasAny(rowNumber, 1, grid.MaxColumnAtRow(2)))
                continue;
            var rawParent = grid.CellText(rowNumber, 1);
            RowGuid? parent = RowGuid.TryParse(rawParent, out var parsed) ? parsed : null;
            int? ordinal = null;
            string? mapKey = null;
            if (metadata.Kind == CanonicalChildTableKind.RepeatedMessage)
            {
                if (int.TryParse(grid.CellText(rowNumber, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var parsedOrdinal)
                    && parsedOrdinal > 0)
                    ordinal = parsedOrdinal;
            }
            else
            {
                mapKey = grid.CellText(rowNumber, 2);
            }
            if (parent is { } validParent)
            {
                var childIdentity = metadata.Kind == CanonicalChildTableKind.RepeatedMessage
                    ? ordinal?.ToString(CultureInfo.InvariantCulture) ?? string.Empty
                    : mapKey ?? string.Empty;
                if (childIdentity.Length == 0 || !unique.Add($"{validParent}:{childIdentity}"))
                    throw new InvalidDataException($"Child worksheet '{metadata.SheetName}' has an invalid or duplicate child identity at row {rowNumber}.");
            }
            var values = ImmutableDictionary.CreateBuilder<string, WorkbookCell>(StringComparer.Ordinal);
            for (var index = 0; index < columns.Count; index++)
            {
                var value = grid.Get(rowNumber, systemColumns + index + 1);
                if (value is not null && !value.IsBlank)
                    values[columns[index].PropertyPath] = value;
            }
            rows.Add(new WorkbookChildRow(
                parent,
                ordinal,
                mapKey,
                values.ToImmutable(),
                rawParent,
                rowNumber));
        }
        return new WorkbookChildTable(
            metadata.OwnerTableId,
            metadata.OwnerFieldIdPath,
            metadata.Kind,
            metadata.SheetName,
            metadata.DataStartRow,
            columns.ToImmutableArray(),
            rows.ToImmutableArray());
    }

    private static IReadOnlyDictionary<(string OwnerKey, int Column), string> InferChildFieldPaths(
        Package package,
        string sheetName,
        WorkbookChildTable layout,
        IReadOnlyDictionary<(string OwnerKey, int Column), string> metadataPaths)
    {
        var result = new Dictionary<(string OwnerKey, int Column), string>();
        var ownerKey = ChildOwnerKey(layout.OwnerTableId, layout.OwnerFieldIdPath);
        var grid = ReadGrid(package, package.Sheets[sheetName]);
        var systemColumns = ChildSystemColumnCount(layout.Kind);
        for (var column = systemColumns + 1; column <= grid.MaxColumnAtRow(2); column++)
        {
            if (metadataPaths.TryGetValue((ownerKey, column), out var registered))
            {
                result[(ownerKey, column)] = registered;
                continue;
            }
            var property = grid.Text(2, column) ?? string.Empty;
            var match = layout.Columns.FirstOrDefault(candidate =>
                string.Equals(candidate.PropertyPath, property, StringComparison.Ordinal)
                || candidate.EffectiveAliases.Contains(property, StringComparer.Ordinal));
            if (match is not null)
                result[(ownerKey, column)] = match.FieldPath;
        }
        return result;
    }

    private static int ChildSystemColumnCount(CanonicalChildTableKind kind) => kind switch
    {
        CanonicalChildTableKind.RepeatedMessage => 2,
        CanonicalChildTableKind.MessageMap => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static int FindHeaderColumn(WorksheetGrid grid, int row, string value)
    {
        for (var column = 1; column <= grid.MaxColumnAtRow(row); column++)
        {
            if (string.Equals(grid.Text(row, column), value, StringComparison.Ordinal))
                return column;
        }

        return -1;
    }

    private static void PatchWorksheet(XDocument document, IEnumerable<CellPatch> patches)
    {
        var worksheet = document.Root ?? throw new InvalidDataException("Worksheet XML has no root element.");
        var sheetData = worksheet.Element(Spreadsheet + "sheetData")
            ?? throw new InvalidDataException("Worksheet XML has no sheetData element.");
        foreach (var patch in patches.OrderBy(static patch => patch.Row).ThenBy(static patch => patch.Column))
        {
            var row = sheetData
                .Elements(Spreadsheet + "row")
                .FirstOrDefault(element => ParsePositiveInt(element.Attribute("r")?.Value) == patch.Row);
            if (row is null)
            {
                if (patch.Cell is null || patch.Cell.IsBlank)
                    continue;
                row = new XElement(Spreadsheet + "row", new XAttribute("r", patch.Row));
                var followingRow = sheetData
                    .Elements(Spreadsheet + "row")
                    .FirstOrDefault(element => ParsePositiveInt(element.Attribute("r")?.Value) > patch.Row);
                if (followingRow is null)
                    sheetData.Add(row);
                else
                    followingRow.AddBeforeSelf(row);
            }

            var cell = row.Elements(Spreadsheet + "c").FirstOrDefault(element =>
            {
                var reference = element.Attribute("r")?.Value;
                return reference is not null
                    && TryParseCellReference(reference, out _, out var column)
                    && column == patch.Column;
            });
            if (patch.Cell is null || patch.Cell.IsBlank)
            {
                cell?.Remove();
                continue;
            }

            if (cell is null)
            {
                cell = new XElement(
                    Spreadsheet + "c",
                    new XAttribute("r", $"{ColumnName(patch.Column)}{patch.Row.ToString(CultureInfo.InvariantCulture)}"));
                var followingCell = row.Elements(Spreadsheet + "c").FirstOrDefault(element =>
                {
                    var reference = element.Attribute("r")?.Value;
                    return reference is not null
                        && TryParseCellReference(reference, out _, out var column)
                        && column > patch.Column;
                });
                if (followingCell is null)
                    row.Add(cell);
                else
                    followingCell.AddBeforeSelf(cell);
            }

            SetCellValue(cell, patch.Cell);
        }
    }

    private static IReadOnlyList<PackageEntry> ReadEntries(byte[] packageBytes)
    {
        using var stream = new MemoryStream(packageBytes, writable: false);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        var entries = new List<PackageEntry>(archive.Entries.Count);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            if (!names.Add(entry.FullName))
                throw new InvalidDataException($"Duplicate package part '{entry.FullName}'.");
            using var entryStream = entry.Open();
            using var buffer = new MemoryStream();
            entryStream.CopyTo(buffer);
            entries.Add(new PackageEntry(entry.FullName, buffer.ToArray()));
        }

        return entries;
    }

    private static byte[] WriteEntries(IEnumerable<PackageEntry> entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var item in entries)
            {
                var entry = archive.CreateEntry(item.Name, CompressionLevel.Optimal);
                entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var entryStream = entry.Open();
                entryStream.Write(item.Bytes);
            }
        }

        return stream.ToArray();
    }

    private static XDocument LoadXml(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
        });
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
    }

    private static byte[] Serialize(XDocument document)
    {
        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, new XmlWriterSettings
        {
            Encoding = Utf8WithoutBom,
            Indent = false,
            OmitXmlDeclaration = false,
            CloseOutput = false,
        }))
        {
            document.Save(writer);
        }

        return stream.ToArray();
    }

    private static string RequiredAttribute(XElement element, string name) =>
        element.Attribute(name)?.Value
        ?? throw new InvalidDataException($"Element '{element.Name}' has no '{name}' attribute.");

    private static string ResolveWorkbookTarget(string target)
    {
        if (target.StartsWith("/", StringComparison.Ordinal))
            return target.TrimStart('/');
        var resolved = new Uri(new Uri("https://exceldb.invalid/xl/workbook.xml"), target);
        return resolved.AbsolutePath.TrimStart('/');
    }

    private static int ParsePositiveInt(string? text) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : int.MaxValue;

    private static bool TryParseCellReference(string reference, out int row, out int column)
    {
        row = 0;
        column = 0;
        var index = 0;
        while (index < reference.Length && char.IsAsciiLetter(reference[index]))
        {
            var letter = char.ToUpperInvariant(reference[index]);
            column = checked((column * 26) + (letter - 'A' + 1));
            index++;
        }

        return index > 0
            && index < reference.Length
            && int.TryParse(reference.AsSpan(index), NumberStyles.None, CultureInfo.InvariantCulture, out row)
            && row > 0
            && column > 0;
    }

    public static string ColumnName(int column)
    {
        if (column <= 0)
            throw new ArgumentOutOfRangeException(nameof(column));
        Span<char> buffer = stackalloc char[8];
        var index = buffer.Length;
        while (column > 0)
        {
            column--;
            buffer[--index] = (char)('A' + (column % 26));
            column /= 26;
        }

        return new string(buffer[index..]);
    }

    private sealed record SheetPart(string Name, bool Hidden, XDocument Document);

    private sealed record PackageEntry(string Name, byte[] Bytes);

    private sealed record Package(
        IReadOnlyList<PackageEntry> Entries,
        IReadOnlyDictionary<string, byte[]> EntryBytes,
        IReadOnlyDictionary<string, string> Sheets,
        IReadOnlyDictionary<string, string?> SheetStates,
        ImmutableArray<string> SharedStrings);

    private sealed record TableMetadata(int TableId, string ProtoName, string SheetName, int DataStartRow);

    private sealed record ChildTableMetadata(
        int OwnerTableId,
        ImmutableArray<int> OwnerFieldIdPath,
        CanonicalChildTableKind Kind,
        string SheetName,
        int DataStartRow);

    private sealed record KeyMap(
        IReadOnlyDictionary<(int TableId, RowGuid RowGuid), string?> ByIdentity,
        IReadOnlyDictionary<(int TableId, int RowNumber), string?> ByLocation);

    private sealed class WorksheetGrid(IReadOnlyDictionary<(int Row, int Column), WorkbookCell> cells)
    {
        public int MaxRow { get; } = cells.Count == 0 ? 0 : cells.Keys.Max(static key => key.Row);

        public WorkbookCell? Get(int row, int column) => cells.GetValueOrDefault((row, column));

        public string? Text(int row, int column) => Get(row, column)?.Text;

        public string? CellText(int row, int column)
        {
            var cell = Get(row, column);
            return cell?.Formula is null ? cell?.Text : $"={cell.Formula}";
        }

        public int MaxColumnAtRow(int row) => cells.Keys
            .Where(key => key.Row == row)
            .Select(static key => key.Column)
            .DefaultIfEmpty(0)
            .Max();

        public bool HasAny(int row, int firstColumn, int lastColumn) =>
            cells.Keys.Any(key => key.Row == row && key.Column >= firstColumn && key.Column <= lastColumn);
    }
}
