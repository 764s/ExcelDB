using System.Collections.Immutable;
using System.Text.RegularExpressions;
using ExcelDb.Protocol;
using ExcelDb.Schema.Authoring;
using ExcelDb.Schema.Compilation;
using ExcelDb.Schema.Diagnostics;
using Google.Protobuf.Reflection;
using ProtoFieldType = Google.Protobuf.Reflection.FieldDescriptorProto.Types.Type;
using ProtoLabel = Google.Protobuf.Reflection.FieldDescriptorProto.Types.Label;

namespace ExcelDb.Schema.Mutation;

public sealed record CreateTableMutationRequest
{
    public required string FileName { get; init; }

    public required string Package { get; init; }

    public required string TableName { get; init; }

    public ImmutableArray<SimpleFieldDefinition> Fields { get; init; } = [];

    public bool AutoKey { get; init; }

    public int? TableId { get; init; }

    public ExportTargetSelection? TableExportTargets { get; init; }

    public TableInitializationOptions InitializationOptions { get; init; } = TableInitializationOptions.Default;

    public ImmutableDictionary<string, TableFieldOptions> FieldOptions { get; init; } =
        ImmutableDictionary<string, TableFieldOptions>.Empty.WithComparers(StringComparer.Ordinal);

    public ImmutableDictionary<string, ExportTargetSelection> FieldExportTargets { get; init; } =
        ImmutableDictionary<string, ExportTargetSelection>.Empty.WithComparers(StringComparer.Ordinal);
}

public sealed record AddSimpleFieldMutation(
    SimpleFieldDefinition Definition,
    int? FieldNumber = null,
    ExportTargetSelection? ExportTargets = null);

public sealed record RenameFieldMutation(int FieldNumber, string NewName);

public sealed record EditTableMutationRequest
{
    public required string TableFullName { get; init; }

    public string? NewName { get; init; }

    public ImmutableArray<RenameFieldMutation> RenameFields { get; init; } = [];

    public ImmutableArray<AddSimpleFieldMutation> AddFields { get; init; } = [];

    public ImmutableArray<AddStructuredFieldMutation> AddStructuredFields { get; init; } = [];

    public ImmutableArray<int> RemoveFieldNumbers { get; init; } = [];

    public ExportTargetSelection? TableExportTargets { get; init; }

    public ImmutableDictionary<int, ExportTargetSelection> FieldExportTargets { get; init; } =
        ImmutableDictionary<int, ExportTargetSelection>.Empty;

    public TableOpts? TableOptionsReplacement { get; init; }

    public ImmutableDictionary<int, FieldOpts> FieldOptionsReplacements { get; init; } =
        ImmutableDictionary<int, FieldOpts>.Empty;
}

public sealed record RetireTableMutationRequest(string TableFullName);

public sealed record SchemaMutationResult(
    FileDescriptorSet? CandidateDescriptorSet,
    ImmutableDictionary<string, string> CandidateProtoFiles,
    ImmutableArray<SchemaDiagnostic> Diagnostics,
    int? AssignedTableId,
    ImmutableDictionary<string, int> AssignedFieldNumbers)
{
    public bool Succeeded => CandidateDescriptorSet is not null
        && !Diagnostics.Any(static diagnostic => diagnostic.IsBlocking);
}

/// <summary>
/// Pure structured proto mutation. Every operation clones its input, runs M1 lint,
/// and returns candidate text; the application layer owns review and atomic IO.
/// </summary>
public sealed partial class SchemaMutationEngine
{
    public SchemaMutationResult CreateTable(
        FileDescriptorSet current,
        CreateTableMutationRequest request,
        ITableInitializer? initializer = null,
        IExportTargetStrategy? exportTargetStrategy = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            ValidateLogicalFileName(request.FileName);
            ValidatePackage(request.Package);
            var candidate = current.Clone();
            var file = candidate.File.SingleOrDefault(file =>
                string.Equals(file.Name, request.FileName, StringComparison.Ordinal));
            if (file is null)
            {
                file = new FileDescriptorProto
                {
                    Name = request.FileName,
                    Package = request.Package,
                    Syntax = "proto3",
                };
                file.Dependency.Add("exceldb/options.proto");
                candidate.File.Add(file);
            }
            else if (!string.Equals(file.Package, request.Package, StringComparison.Ordinal))
            {
                return Failure(request.FileName, "Existing file package does not match the create request.");
            }

            if (FindMessage(candidate, Join(request.Package, request.TableName)) is not null)
                return Failure(request.TableName, "A message with this full name already exists.");

            var currentSchema = new SchemaCompiler().Compile(current).Descriptor;
            var context = new TableInitializationContext(request.TableName, request.Fields, request.AutoKey)
            {
                CurrentSchema = currentSchema,
                Options = request.InitializationOptions,
                FieldOptions = request.FieldOptions,
            };
            var activeInitializer = initializer ?? new DefaultTableInitializer();
            var draft = new TableDraft(request.TableName);
            activeInitializer.Initialize(in context, draft);
            var determinismProbe = new TableDraft(request.TableName);
            activeInitializer.Initialize(in context, determinismProbe);
            if (!draft.SemanticallyEquals(determinismProbe))
                return Failure(request.TableName, $"Initializer '{activeInitializer.Id}' produced a non-deterministic table draft for the same context.");
            if (request.TableExportTargets is not null)
                draft.ApplyExplicitTableExportTargets(request.TableExportTargets);
            foreach (var pair in request.FieldExportTargets.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
                draft.ApplyExplicitFieldExportTargets(pair.Key, pair.Value);

            var strategy = exportTargetStrategy ?? new StandardClientServerExportTargetStrategy();
            var strategyContext = new ExportTargetStrategyContext(request.TableName);
            strategy.Apply(in strategyContext, draft);
            if (draft.TableExportTargets is null || draft.Fields.Any(static field => field.ExportTargets is null))
                return Failure(request.TableName, "Initializer/strategy left an export target selection unspecified.");

            var tableId = request.TableId ?? SuggestTableId(candidate);
            if (tableId <= 0 || IsTableIdOccupied(candidate, tableId))
                return Failure(request.TableName, $"Table id {tableId} is not available.");

            var message = new DescriptorProto { Name = request.TableName };
            var tableOptions = new TableOpts
            {
                Kind = TableKind.Asset,
                Id = tableId,
                ExportTargets = ToProtocolTargets(draft.TableExportTargets),
                DisplayName = draft.Options.DisplayName ?? string.Empty,
                SheetName = draft.Options.SheetName ?? string.Empty,
            };
            tableOptions.Validators.Add(draft.Options.ValidatorIds);
            message.Options = new MessageOptions();
            message.Options.SetExtension(OptionsExtensions.Table, tableOptions);

            var assignedFields = ImmutableDictionary.CreateBuilder<string, int>(StringComparer.Ordinal);
            var nextFieldNumber = 1;
            var keyOrder = 1;
            foreach (var fieldDraft in draft.Fields)
            {
                var fieldNumber = SuggestFieldNumber(message, nextFieldNumber);
                nextFieldNumber = fieldNumber + 1;
                var field = CreateSimpleField(
                    candidate,
                    file,
                    fieldDraft.Name,
                    fieldDraft.Type,
                    fieldDraft.EnumType,
                    fieldNumber,
                    fieldDraft.IsKey ? keyOrder++ : 0,
                    fieldDraft.ExportTargets!,
                    fieldDraft.Options);
                message.Field.Add(field);
                assignedFields.Add(fieldDraft.Name, fieldNumber);
            }

            file.MessageType.Add(message);
            return Finish(candidate, tableId, assignedFields.ToImmutable());
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidOperationException
            or KeyNotFoundException)
        {
            return Failure(request.TableName, exception.Message);
        }
    }

    public SchemaMutationResult EditTable(
        FileDescriptorSet current,
        EditTableMutationRequest request,
        IExportTargetStrategy? exportTargetStrategy = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var candidate = current.Clone();
            var location = FindMessage(candidate, request.TableFullName);
            if (location is null)
                return Failure(request.TableFullName, "Table message does not exist.");
            if (location.Parent is not null && request.NewName is not null)
                return Failure(request.TableFullName, "Renaming nested table messages is not supported by the structured editor.");

            var message = location.Message;
            var tableOptions = GetTableOptions(message);
            if (tableOptions is null || tableOptions.Retired)
                return Failure(request.TableFullName, "Only a live table message can be edited.");

            if (request.TableOptionsReplacement is { } tableReplacement)
            {
                if (tableReplacement.Id != tableOptions.Id
                    || tableReplacement.Kind != tableOptions.Kind
                    || tableReplacement.Retired != tableOptions.Retired)
                {
                    throw new InvalidOperationException("A table option replacement cannot change stable id, kind or retirement state.");
                }

                message.Options!.SetExtension(OptionsExtensions.Table, tableReplacement.Clone());
                tableOptions = GetTableOptions(message)!;
            }

            foreach (var rename in request.RenameFields.OrderBy(static item => item.FieldNumber))
            {
                ValidateIdentifier(rename.NewName, nameof(rename.NewName));
                var field = message.Field.SingleOrDefault(field => field.Number == rename.FieldNumber)
                    ?? throw new KeyNotFoundException($"Field number {rename.FieldNumber} does not exist.");
                if (message.Field.Any(candidateField => candidateField.Number != rename.FieldNumber
                    && string.Equals(candidateField.Name, rename.NewName, StringComparison.Ordinal)))
                {
                    throw new InvalidOperationException($"Field name '{rename.NewName}' already exists.");
                }

                field.Name = rename.NewName;
                field.JsonName = ToJsonName(rename.NewName);
            }

            foreach (var fieldNumber in request.RemoveFieldNumbers.Distinct().Order())
            {
                var field = message.Field.SingleOrDefault(candidateField => candidateField.Number == fieldNumber)
                    ?? throw new KeyNotFoundException($"Field number {fieldNumber} does not exist.");
                message.Field.Remove(field);
                if (!message.ReservedRange.Any(range => fieldNumber >= range.Start && fieldNumber < range.End))
                    message.ReservedRange.Add(new DescriptorProto.Types.ReservedRange { Start = fieldNumber, End = fieldNumber + 1 });
                if (!message.ReservedName.Contains(field.Name, StringComparer.Ordinal))
                    message.ReservedName.Add(field.Name);
            }

            var assignedFields = ImmutableDictionary.CreateBuilder<string, int>(StringComparer.Ordinal);
            foreach (var addition in request.AddFields)
            {
                ArgumentNullException.ThrowIfNull(addition.Definition);
                var number = addition.FieldNumber ?? SuggestFieldNumber(message);
                if (!IsFieldNumberAvailable(message, number))
                    throw new InvalidOperationException($"Field number {number} is not available.");
                if (message.Field.Any(field => string.Equals(field.Name, addition.Definition.Name, StringComparison.Ordinal)))
                    throw new InvalidOperationException($"Field name '{addition.Definition.Name}' already exists.");

                var targets = addition.ExportTargets
                    ?? ExportTargetSelection.Explicit(ResolveTableTargets(tableOptions));
                var field = CreateSimpleField(
                    candidate,
                    location.File,
                    addition.Definition.Name,
                    addition.Definition.Type,
                    addition.Definition.EnumType,
                    number,
                    addition.Definition.IsKey ? NextKeyOrder(message) : 0,
                    targets,
                    TableFieldOptions.Default);
                message.Field.Add(field);
                assignedFields.Add(addition.Definition.Name, number);
            }

            foreach (var addition in request.AddStructuredFields)
            {
                var number = addition.FieldNumber ?? SuggestFieldNumber(message);
                if (!IsFieldNumberAvailable(message, number))
                    throw new InvalidOperationException($"Field number {number} is not available.");
                if (message.Field.Any(field => string.Equals(field.Name, addition.Name, StringComparison.Ordinal)))
                    throw new InvalidOperationException($"Field name '{addition.Name}' already exists.");
                var targets = addition.ExportTargets
                    ?? ExportTargetSelection.Explicit(ResolveTableTargets(tableOptions));
                message.Field.Add(CreateStructuredField(candidate, location, addition, number, targets));
                assignedFields.Add(addition.Name, number);
            }

            foreach (var pair in request.FieldOptionsReplacements.OrderBy(static pair => pair.Key))
            {
                var field = message.Field.SingleOrDefault(candidateField => candidateField.Number == pair.Key)
                    ?? throw new KeyNotFoundException($"Field number {pair.Key} does not exist.");
                field.Options ??= new FieldOptions();
                field.Options.SetExtension(OptionsExtensions.Field, pair.Value.Clone());
            }

            if (request.TableExportTargets is not null)
                tableOptions.ExportTargets = ToProtocolTargets(request.TableExportTargets);
            foreach (var pair in request.FieldExportTargets.OrderBy(static pair => pair.Key))
            {
                var field = message.Field.SingleOrDefault(candidateField => candidateField.Number == pair.Key)
                    ?? throw new KeyNotFoundException($"Field number {pair.Key} does not exist.");
                GetOrCreateFieldOptions(field).ExportTargets = ToProtocolTargets(pair.Value);
            }

            if (exportTargetStrategy is not null)
            {
                var targetDraft = new DescriptorExportTargetDraft(message, tableOptions);
                var context = new ExportTargetStrategyContext(message.Name);
                exportTargetStrategy.Apply(in context, targetDraft);
                targetDraft.Flush();
            }

            if (request.NewName is not null)
            {
                ValidateIdentifier(request.NewName, nameof(request.NewName));
                var oldFullName = location.FullName;
                var newFullName = Join(location.File.Package, request.NewName);
                if (FindMessage(candidate, newFullName) is not null)
                    throw new InvalidOperationException($"Message '{newFullName}' already exists.");
                var oldName = message.Name;
                message.Name = request.NewName;
                RewriteTableTypeReferences(candidate, oldFullName, newFullName, oldName, request.NewName);
            }

            return Finish(candidate, tableOptions.Id, assignedFields.ToImmutable());
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidOperationException
            or KeyNotFoundException)
        {
            return Failure(request.TableFullName, exception.Message);
        }
    }

    public SchemaMutationResult RetireTable(
        FileDescriptorSet current,
        RetireTableMutationRequest request)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(request);

        var candidate = current.Clone();
        var location = FindMessage(candidate, request.TableFullName);
        if (location is null)
            return Failure(request.TableFullName, "Table message does not exist.");
        var tableOptions = GetTableOptions(location.Message);
        if (tableOptions is null
            || tableOptions.Kind != TableKind.Asset
            || tableOptions.Id <= 0
            || tableOptions.Retired)
        {
            return Failure(request.TableFullName, "Only a live ASSET with a stable id can be retired.");
        }

        tableOptions.Retired = true;
        return Finish(candidate, tableOptions.Id, ImmutableDictionary<string, int>.Empty);
    }

    public int SuggestTableId(FileDescriptorSet descriptorSet)
    {
        ArgumentNullException.ThrowIfNull(descriptorSet);
        var candidate = 1;
        while (IsTableIdOccupied(descriptorSet, candidate))
            candidate++;
        return candidate;
    }

    public int SuggestFieldNumber(DescriptorProto message, int startAt = 1)
    {
        ArgumentNullException.ThrowIfNull(message);
        var candidate = Math.Max(1, startAt);
        while (!IsFieldNumberAvailable(message, candidate))
            candidate++;
        return candidate;
    }

    private static SchemaMutationResult Finish(
        FileDescriptorSet candidate,
        int? assignedTableId,
        ImmutableDictionary<string, int> assignedFieldNumbers)
    {
        foreach (var file in candidate.File)
            file.SourceCodeInfo = null;
        var compilation = new SchemaCompiler().Compile(candidate);
        if (!compilation.Succeeded)
        {
            return new SchemaMutationResult(
                null,
                ImmutableDictionary<string, string>.Empty,
                compilation.Diagnostics,
                assignedTableId,
                assignedFieldNumbers);
        }

        return new SchemaMutationResult(
            candidate,
            ProtoDescriptorEmitter.EmitProjectFiles(candidate),
            compilation.Diagnostics,
            assignedTableId,
            assignedFieldNumbers);
    }

    private static SchemaMutationResult Failure(string location, string message) => new(
        null,
        ImmutableDictionary<string, string>.Empty,
        [new SchemaDiagnostic("XDB000", SchemaDiagnosticSeverity.Blocker, location, message)],
        null,
        ImmutableDictionary<string, int>.Empty);

    private static FieldDescriptorProto CreateSimpleField(
        FileDescriptorSet set,
        FileDescriptorProto file,
        string name,
        SimpleFieldType type,
        string? enumType,
        int fieldNumber,
        int keyOrder,
        ExportTargetSelection targets,
        TableFieldOptions options)
    {
        ValidateIdentifier(name, nameof(name));
        if (fieldNumber <= 0)
            throw new ArgumentOutOfRangeException(nameof(fieldNumber));

        var field = new FieldDescriptorProto
        {
            Name = name,
            JsonName = ToJsonName(name),
            Number = fieldNumber,
            Label = ProtoLabel.Optional,
        };
        if (type == SimpleFieldType.Enum)
        {
            if (string.IsNullOrWhiteSpace(enumType))
                throw new InvalidOperationException($"Enum field '{name}' requires an enum type.");
            var resolved = ResolveEnumType(set, file, enumType)
                ?? throw new InvalidOperationException($"Enum type '{enumType}' does not exist.");
            field.Type = ProtoFieldType.Enum;
            field.TypeName = $".{resolved}";
        }
        else
        {
            field.Type = type switch
            {
                SimpleFieldType.String => ProtoFieldType.String,
                SimpleFieldType.Int32 => ProtoFieldType.Int32,
                SimpleFieldType.Int64 => ProtoFieldType.Int64,
                SimpleFieldType.UInt32 => ProtoFieldType.Uint32,
                SimpleFieldType.UInt64 => ProtoFieldType.Uint64,
                SimpleFieldType.Float => ProtoFieldType.Float,
                SimpleFieldType.Double => ProtoFieldType.Double,
                SimpleFieldType.Boolean => ProtoFieldType.Bool,
                _ => throw new ArgumentOutOfRangeException(nameof(type)),
            };
        }

        field.Options = new FieldOptions();
        var fieldOptions = new FieldOpts
        {
            Key = keyOrder,
            ExportTargets = ToProtocolTargets(targets),
            DisplayName = options.DisplayName ?? string.Empty,
            HeaderComment = options.HeaderComment ?? string.Empty,
            Required = options.Required,
            DefaultValue = options.DefaultValue ?? string.Empty,
            Regex = options.Regex ?? string.Empty,
            Unique = options.Unique,
        };
        fieldOptions.Aliases.Add(options.Aliases);
        if (options.Minimum is { } minimum)
            fieldOptions.Min = minimum;
        if (options.Maximum is { } maximum)
            fieldOptions.Max = maximum;
        field.Options.SetExtension(OptionsExtensions.Field, fieldOptions);
        return field;
    }

    private static string? ResolveEnumType(
        FileDescriptorSet set,
        FileDescriptorProto file,
        string reference)
    {
        var normalized = reference.TrimStart('.');
        var names = EnumerateEnums(set).ToArray();
        if (reference.StartsWith(".", StringComparison.Ordinal))
            return names.Contains(normalized, StringComparer.Ordinal) ? normalized : null;
        var packageName = Join(file.Package, normalized);
        if (names.Contains(packageName, StringComparer.Ordinal))
            return packageName;
        if (names.Contains(normalized, StringComparer.Ordinal))
            return normalized;
        var shortMatches = names.Where(name => string.Equals(
            name[(name.LastIndexOf('.') + 1)..], normalized, StringComparison.Ordinal)).ToArray();
        return shortMatches.Length == 1 ? shortMatches[0] : null;
    }

    private static IEnumerable<string> EnumerateEnums(FileDescriptorSet set)
    {
        foreach (var file in set.File)
        {
            foreach (var descriptor in file.EnumType)
                yield return Join(file.Package, descriptor.Name);
            foreach (var message in file.MessageType)
            {
                foreach (var value in EnumerateNestedEnums(message, Join(file.Package, message.Name)))
                    yield return value;
            }
        }
    }

    private static IEnumerable<string> EnumerateNestedEnums(DescriptorProto message, string prefix)
    {
        foreach (var descriptor in message.EnumType)
            yield return Join(prefix, descriptor.Name);
        foreach (var nested in message.NestedType)
        {
            foreach (var value in EnumerateNestedEnums(nested, Join(prefix, nested.Name)))
                yield return value;
        }
    }

    private static int NextKeyOrder(DescriptorProto message) => message.Field
        .Select(GetFieldOptions)
        .Where(static options => options is not null)
        .Select(static options => options!.Key)
        .DefaultIfEmpty()
        .Max() + 1;

    private static bool IsFieldNumberAvailable(DescriptorProto message, int number) =>
        number is > 0 and <= 536_870_911
        && number is not (>= 19_000 and <= 19_999)
        && !message.Field.Any(field => field.Number == number)
        && !message.ReservedRange.Any(range => number >= range.Start && number < range.End);

    private static bool IsTableIdOccupied(FileDescriptorSet set, int tableId) =>
        EnumerateMessages(set).Any(location => GetTableOptions(location.Message)?.Id == tableId);

    private static MessageLocation? FindMessage(FileDescriptorSet set, string reference)
    {
        var normalized = reference.TrimStart('.');
        var exact = EnumerateMessages(set)
            .Where(location => string.Equals(location.FullName, normalized, StringComparison.Ordinal))
            .ToArray();
        if (exact.Length == 1)
            return exact[0];
        if (normalized.Contains('.'))
            return null;
        var shortMatches = EnumerateMessages(set)
            .Where(location => string.Equals(location.Message.Name, normalized, StringComparison.Ordinal))
            .ToArray();
        return shortMatches.Length == 1 ? shortMatches[0] : null;
    }

    private static IEnumerable<MessageLocation> EnumerateMessages(FileDescriptorSet set)
    {
        foreach (var file in set.File)
        {
            foreach (var message in file.MessageType)
            {
                foreach (var location in EnumerateMessages(file, message, null, file.Package))
                    yield return location;
            }
        }
    }

    private static IEnumerable<MessageLocation> EnumerateMessages(
        FileDescriptorProto file,
        DescriptorProto message,
        DescriptorProto? parent,
        string prefix)
    {
        var fullName = Join(prefix, message.Name);
        yield return new MessageLocation(file, message, parent, fullName);
        foreach (var nested in message.NestedType)
        {
            foreach (var location in EnumerateMessages(file, nested, message, fullName))
                yield return location;
        }
    }

    private static void RewriteTableTypeReferences(
        FileDescriptorSet set,
        string oldFullName,
        string newFullName,
        string oldShortName,
        string newShortName)
    {
        foreach (var location in EnumerateMessages(set))
        {
            foreach (var field in location.Message.Field)
            {
                if (string.Equals(field.TypeName.TrimStart('.'), oldFullName, StringComparison.Ordinal))
                    field.TypeName = $".{newFullName}";
                var options = GetFieldOptions(field);
                if (options is null)
                    continue;
                if (string.Equals(options.RefTable.TrimStart('.'), oldFullName, StringComparison.Ordinal))
                    options.RefTable = options.RefTable.StartsWith(".", StringComparison.Ordinal) ? $".{newFullName}" : newFullName;
                else if (string.Equals(options.RefTable, oldShortName, StringComparison.Ordinal))
                    options.RefTable = newShortName;
            }
        }
    }

    private static IEnumerable<string> ResolveTableTargets(TableOpts options)
    {
        if (options.ExportTargets is not null)
            return options.ExportTargets.Ids;
#pragma warning disable CS0612
        return options.Export == ExportPolicy.EditorOnly ? [] : new[] { "client", "server" };
#pragma warning restore CS0612
    }

    private static ExportTargetSet ToProtocolTargets(ExportTargetSelection selection)
    {
        var result = new ExportTargetSet();
        result.Ids.Add(selection.Targets);
        return result;
    }

    private static TableOpts? GetTableOptions(DescriptorProto descriptor) =>
        descriptor.Options is { } options && options.HasExtension(OptionsExtensions.Table)
            ? options.GetExtension(OptionsExtensions.Table)
            : null;

    private static FieldOpts? GetFieldOptions(FieldDescriptorProto descriptor) =>
        descriptor.Options is { } options && options.HasExtension(OptionsExtensions.Field)
            ? options.GetExtension(OptionsExtensions.Field)
            : null;

    private static FieldOpts GetOrCreateFieldOptions(FieldDescriptorProto descriptor)
    {
        descriptor.Options ??= new FieldOptions();
        if (descriptor.Options.HasExtension(OptionsExtensions.Field))
            return descriptor.Options.GetExtension(OptionsExtensions.Field);
        var result = new FieldOpts();
        descriptor.Options.SetExtension(OptionsExtensions.Field, result);
        return result;
    }

    private static void ValidateLogicalFileName(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (!fileName.EndsWith(".proto", StringComparison.Ordinal)
            || fileName.StartsWith("/", StringComparison.Ordinal)
            || fileName.Contains('\\')
            || fileName.Split('/').Any(static part => part is "" or "." or ".."))
        {
            throw new ArgumentException($"Invalid logical proto file name '{fileName}'.", nameof(fileName));
        }
    }

    private static void ValidatePackage(string package)
    {
        if (string.IsNullOrEmpty(package))
            return;
        foreach (var segment in package.Split('.'))
            ValidateIdentifier(segment, nameof(package));
    }

    private static void ValidateIdentifier(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (!Regex.IsMatch(value, "^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant))
            throw new ArgumentException($"Invalid proto identifier '{value}'.", parameterName);
    }

    private static string ToJsonName(string protoName)
    {
        var result = Regex.Replace(protoName, "_([a-zA-Z])", static match => match.Groups[1].Value.ToUpperInvariant());
        return result;
    }

    private static string Join(string prefix, string name) =>
        string.IsNullOrEmpty(prefix) ? name : $"{prefix}.{name}";

    private sealed record MessageLocation(
        FileDescriptorProto File,
        DescriptorProto Message,
        DescriptorProto? Parent,
        string FullName);

    private sealed class DescriptorExportTargetDraft : IExportTargetDraft
    {
        private readonly DescriptorProto _message;
        private readonly TableOpts _options;
        private readonly IReadOnlyList<IExportTargetFieldDraft> _fieldView;
        private readonly Dictionary<string, DescriptorExportTargetFieldDraft> _fields;

        public DescriptorExportTargetDraft(DescriptorProto message, TableOpts options)
        {
            _message = message;
            _options = options;
            TableName = message.Name;
            TableExportTargets = options.ExportTargets is null
                ? null
                : ExportTargetSelection.Explicit(options.ExportTargets.Ids);
            _fields = message.Field.ToDictionary(
                static field => field.Name,
                field => new DescriptorExportTargetFieldDraft(
                    field,
                    GetFieldOptions(field)?.ExportTargets is { } targets
                        ? ExportTargetSelection.Explicit(targets.Ids)
                        : null),
                StringComparer.Ordinal);
            _fieldView = _fields.Values.Cast<IExportTargetFieldDraft>().ToArray();
        }

        public string TableName { get; }

        public ExportTargetSelection? TableExportTargets { get; private set; }

        public IReadOnlyList<IExportTargetFieldDraft> Fields => _fieldView;

        public bool TryFillTableExportTargets(IEnumerable<string> targets)
        {
            if (TableExportTargets is not null)
                return false;
            TableExportTargets = ExportTargetSelection.Explicit(targets);
            return true;
        }

        public bool TryFillFieldExportTargets(string fieldName, IEnumerable<string> targets)
        {
            var field = _fields.GetValueOrDefault(fieldName)
                ?? throw new KeyNotFoundException($"Field '{fieldName}' does not exist.");
            if (field.ExportTargets is not null)
                return false;
            field.ExportTargets = ExportTargetSelection.Explicit(targets);
            return true;
        }

        public void Flush()
        {
            if (TableExportTargets is not null)
                _options.ExportTargets = ToProtocolTargets(TableExportTargets);
            foreach (var field in _fields.Values)
            {
                if (field.ExportTargets is not null)
                    GetOrCreateFieldOptions(field.Descriptor).ExportTargets = ToProtocolTargets(field.ExportTargets);
            }
        }
    }

    private sealed class DescriptorExportTargetFieldDraft(
        FieldDescriptorProto descriptor,
        ExportTargetSelection? exportTargets) : IExportTargetFieldDraft
    {
        public FieldDescriptorProto Descriptor { get; } = descriptor;

        public string Name => Descriptor.Name;

        public ExportTargetSelection? ExportTargets { get; set; } = exportTargets;
    }
}
