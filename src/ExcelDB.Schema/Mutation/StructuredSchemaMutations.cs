using System.Collections.Immutable;
using ExcelDb.Protocol;
using ExcelDb.Schema.Authoring;
using Google.Protobuf.Reflection;
using ProtoFieldType = Google.Protobuf.Reflection.FieldDescriptorProto.Types.Type;
using ProtoLabel = Google.Protobuf.Reflection.FieldDescriptorProto.Types.Label;

namespace ExcelDb.Schema.Mutation;

public enum StructuredFieldCardinality
{
    Singular = 0,
    Repeated = 1,
    Map = 2,
}

/// <summary>
/// A strongly typed structured-editor field addition. TypeName is a proto scalar
/// keyword or a resolvable enum/message name; no raw proto text is accepted.
/// </summary>
public sealed record AddStructuredFieldMutation(
    string Name,
    string TypeName,
    StructuredFieldCardinality Cardinality = StructuredFieldCardinality.Singular,
    string? MapKeyTypeName = null,
    string? OneOfGroup = null,
    int? FieldNumber = null,
    FieldOpts? Options = null,
    ExportTargetSelection? ExportTargets = null);

public sealed record EnumValueMutation(
    string Name,
    int Number,
    string? DisplayName = null,
    ImmutableArray<string> Aliases = default);

public sealed record CreateEnumMutationRequest
{
    public required string FileName { get; init; }

    public required string Package { get; init; }

    public required string EnumName { get; init; }

    public ImmutableArray<EnumValueMutation> Values { get; init; } = [];
}

public sealed record RenameEnumValueMutation(int Number, string NewName);

public sealed record EditEnumMutationRequest
{
    public required string EnumFullName { get; init; }

    public string? NewName { get; init; }

    public ImmutableArray<RenameEnumValueMutation> RenameValues { get; init; } = [];

    public ImmutableArray<int> RemoveValueNumbers { get; init; } = [];

    public ImmutableArray<EnumValueMutation> AddValues { get; init; } = [];
}

public sealed partial class SchemaMutationEngine
{
    public SchemaMutationResult CreateEnum(
        FileDescriptorSet current,
        CreateEnumMutationRequest request)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            ValidateLogicalFileName(request.FileName);
            ValidatePackage(request.Package);
            ValidateIdentifier(request.EnumName, nameof(request.EnumName));
            var candidate = current.Clone();
            var file = candidate.File.SingleOrDefault(item => string.Equals(item.Name, request.FileName, StringComparison.Ordinal));
            if (file is null)
            {
                file = new FileDescriptorProto { Name = request.FileName, Package = request.Package, Syntax = "proto3" };
                file.Dependency.Add("exceldb/options.proto");
                candidate.File.Add(file);
            }
            else if (!string.Equals(file.Package, request.Package, StringComparison.Ordinal))
            {
                return Failure(request.FileName, "Existing file package does not match the enum create request.");
            }

            var fullName = string.IsNullOrEmpty(request.Package) ? request.EnumName : $"{request.Package}.{request.EnumName}";
            if (FindEnum(candidate, fullName) is not null)
                return Failure(fullName, "An enum with this full name already exists.");
            var descriptor = new EnumDescriptorProto { Name = request.EnumName };
            foreach (var value in request.Values.OrderBy(static item => item.Number).ThenBy(static item => item.Name, StringComparer.Ordinal))
                descriptor.Value.Add(CreateEnumValue(value));
            file.EnumType.Add(descriptor);
            return Finish(
                candidate,
                null,
                request.Values.ToImmutableDictionary(static value => value.Name, static value => value.Number, StringComparer.Ordinal));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return Failure(request.EnumName, exception.Message);
        }
    }

    public SchemaMutationResult EditEnum(
        FileDescriptorSet current,
        EditEnumMutationRequest request)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var candidate = current.Clone();
            var location = FindEnum(candidate, request.EnumFullName)
                ?? throw new KeyNotFoundException($"Enum '{request.EnumFullName}' does not exist.");
            var descriptor = location.Descriptor;
            foreach (var rename in request.RenameValues.OrderBy(static item => item.Number))
            {
                ValidateIdentifier(rename.NewName, nameof(rename.NewName));
                var value = descriptor.Value.SingleOrDefault(item => item.Number == rename.Number)
                    ?? throw new KeyNotFoundException($"Enum value number {rename.Number} does not exist.");
                if (descriptor.Value.Any(item => item.Number != rename.Number
                    && string.Equals(item.Name, rename.NewName, StringComparison.Ordinal)))
                {
                    throw new InvalidOperationException($"Enum value name '{rename.NewName}' already exists.");
                }
                value.Name = rename.NewName;
            }

            foreach (var number in request.RemoveValueNumbers.Distinct().Order())
            {
                var value = descriptor.Value.SingleOrDefault(item => item.Number == number)
                    ?? throw new KeyNotFoundException($"Enum value number {number} does not exist.");
                descriptor.Value.Remove(value);
                if (!descriptor.ReservedRange.Any(range => number >= range.Start && number <= range.End))
                    descriptor.ReservedRange.Add(new EnumDescriptorProto.Types.EnumReservedRange { Start = number, End = number });
                if (!descriptor.ReservedName.Contains(value.Name, StringComparer.Ordinal))
                    descriptor.ReservedName.Add(value.Name);
            }

            var assigned = ImmutableDictionary.CreateBuilder<string, int>(StringComparer.Ordinal);
            foreach (var addition in request.AddValues.OrderBy(static item => item.Number).ThenBy(static item => item.Name, StringComparer.Ordinal))
            {
                if (descriptor.Value.Any(value => value.Number == addition.Number)
                    || descriptor.ReservedRange.Any(range => addition.Number >= range.Start && addition.Number <= range.End))
                {
                    throw new InvalidOperationException($"Enum value number {addition.Number} is not available.");
                }
                if (descriptor.Value.Any(value => string.Equals(value.Name, addition.Name, StringComparison.Ordinal))
                    || descriptor.ReservedName.Contains(addition.Name, StringComparer.Ordinal))
                {
                    throw new InvalidOperationException($"Enum value name '{addition.Name}' is not available.");
                }
                descriptor.Value.Add(CreateEnumValue(addition));
                assigned.Add(addition.Name, addition.Number);
            }

            if (request.NewName is { } newName)
            {
                ValidateIdentifier(newName, nameof(request.NewName));
                var newFullName = string.IsNullOrEmpty(location.Prefix) ? newName : $"{location.Prefix}.{newName}";
                if (FindEnum(candidate, newFullName) is not null)
                    throw new InvalidOperationException($"Enum '{newFullName}' already exists.");
                var oldFullName = location.FullName;
                descriptor.Name = newName;
                RewriteEnumTypeReferences(candidate, oldFullName, newFullName);
            }

            return Finish(candidate, null, assigned.ToImmutable());
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidOperationException
            or KeyNotFoundException)
        {
            return Failure(request.EnumFullName, exception.Message);
        }
    }

    private static FieldDescriptorProto CreateStructuredField(
        FileDescriptorSet set,
        MessageLocation owner,
        AddStructuredFieldMutation mutation,
        int fieldNumber,
        ExportTargetSelection targets)
    {
        ValidateIdentifier(mutation.Name, nameof(mutation.Name));
        ArgumentException.ThrowIfNullOrWhiteSpace(mutation.TypeName);
        if (mutation.Cardinality != StructuredFieldCardinality.Map && mutation.MapKeyTypeName is not null)
            throw new InvalidOperationException("map key type is only valid for a map field.");
        if (mutation.Cardinality != StructuredFieldCardinality.Singular && mutation.OneOfGroup is not null)
            throw new InvalidOperationException("A repeated/map field cannot be a oneof variant.");

        var field = new FieldDescriptorProto
        {
            Name = mutation.Name,
            JsonName = ToJsonName(mutation.Name),
            Number = fieldNumber,
            Label = mutation.Cardinality == StructuredFieldCardinality.Singular
                ? ProtoLabel.Optional
                : ProtoLabel.Repeated,
        };

        if (mutation.Cardinality == StructuredFieldCardinality.Map)
        {
            var keyType = ResolveMapKeyType(mutation.MapKeyTypeName);
            var valueType = ResolveStructuredType(set, owner.File, mutation.TypeName);
            var entryName = ToPascalName(mutation.Name) + "Entry";
            if (owner.Message.NestedType.Any(candidate => string.Equals(candidate.Name, entryName, StringComparison.Ordinal)))
                throw new InvalidOperationException($"Nested map-entry message '{entryName}' already exists.");
            var entry = new DescriptorProto
            {
                Name = entryName,
                Options = new MessageOptions { MapEntry = true },
            };
            entry.Field.Add(new FieldDescriptorProto
            {
                Name = "key",
                JsonName = "key",
                Number = 1,
                Label = ProtoLabel.Optional,
                Type = keyType,
            });
            entry.Field.Add(new FieldDescriptorProto
            {
                Name = "value",
                JsonName = "value",
                Number = 2,
                Label = ProtoLabel.Optional,
                Type = valueType.Type,
                TypeName = valueType.TypeName ?? string.Empty,
            });
            owner.Message.NestedType.Add(entry);
            field.Type = ProtoFieldType.Message;
            field.TypeName = $".{owner.FullName}.{entryName}";
        }
        else
        {
            var type = ResolveStructuredType(set, owner.File, mutation.TypeName);
            field.Type = type.Type;
            field.TypeName = type.TypeName ?? string.Empty;
        }

        if (mutation.OneOfGroup is { } oneOfGroup)
        {
            ValidateIdentifier(oneOfGroup, nameof(mutation.OneOfGroup));
            var index = owner.Message.OneofDecl
                .Select((declaration, index) => (declaration, index))
                .Where(pair => string.Equals(pair.declaration.Name, oneOfGroup, StringComparison.Ordinal))
                .Select(static pair => (int?)pair.index)
                .SingleOrDefault();
            if (index is null)
            {
                owner.Message.OneofDecl.Add(new OneofDescriptorProto { Name = oneOfGroup });
                index = owner.Message.OneofDecl.Count - 1;
            }
            field.OneofIndex = index.Value;
        }

        var options = mutation.Options?.Clone() ?? new FieldOpts();
        options.ExportTargets = ToProtocolTargets(targets);
        field.Options = new FieldOptions();
        field.Options.SetExtension(OptionsExtensions.Field, options);
        return field;
    }

    private static (ProtoFieldType Type, string? TypeName) ResolveStructuredType(
        FileDescriptorSet set,
        FileDescriptorProto file,
        string reference)
    {
        ProtoFieldType? scalar = reference switch
        {
            "double" => ProtoFieldType.Double,
            "float" => ProtoFieldType.Float,
            "int64" => ProtoFieldType.Int64,
            "uint64" => ProtoFieldType.Uint64,
            "int32" => ProtoFieldType.Int32,
            "fixed64" => ProtoFieldType.Fixed64,
            "fixed32" => ProtoFieldType.Fixed32,
            "bool" => ProtoFieldType.Bool,
            "string" => ProtoFieldType.String,
            "bytes" => ProtoFieldType.Bytes,
            "uint32" => ProtoFieldType.Uint32,
            "sfixed32" => ProtoFieldType.Sfixed32,
            "sfixed64" => ProtoFieldType.Sfixed64,
            "sint32" => ProtoFieldType.Sint32,
            "sint64" => ProtoFieldType.Sint64,
            _ => null,
        };
        if (scalar is { } scalarType)
            return (scalarType, null);

        var enumName = ResolveEnumType(set, file, reference);
        if (enumName is not null)
            return (ProtoFieldType.Enum, $".{enumName}");
        var message = FindMessage(set, reference);
        if (message is not null)
            return (ProtoFieldType.Message, $".{message.FullName}");
        var packageReference = string.IsNullOrEmpty(file.Package) ? reference : $"{file.Package}.{reference}";
        message = FindMessage(set, packageReference);
        if (message is not null)
            return (ProtoFieldType.Message, $".{message.FullName}");
        throw new InvalidOperationException($"Field type '{reference}' does not resolve to a scalar, enum or message.");
    }

    private static ProtoFieldType ResolveMapKeyType(string? reference) => reference switch
    {
        "int32" => ProtoFieldType.Int32,
        "int64" => ProtoFieldType.Int64,
        "uint32" => ProtoFieldType.Uint32,
        "uint64" => ProtoFieldType.Uint64,
        "sint32" => ProtoFieldType.Sint32,
        "sint64" => ProtoFieldType.Sint64,
        "fixed32" => ProtoFieldType.Fixed32,
        "fixed64" => ProtoFieldType.Fixed64,
        "sfixed32" => ProtoFieldType.Sfixed32,
        "sfixed64" => ProtoFieldType.Sfixed64,
        "bool" => ProtoFieldType.Bool,
        "string" => ProtoFieldType.String,
        _ => throw new InvalidOperationException($"Unsupported proto map key type '{reference}'."),
    };

    private static string ToPascalName(string value)
    {
        var parts = value.Split('_', StringSplitOptions.RemoveEmptyEntries);
        return string.Concat(parts.Select(static part =>
            char.ToUpperInvariant(part[0]) + part[1..]));
    }

    private static EnumValueDescriptorProto CreateEnumValue(EnumValueMutation value)
    {
        ValidateIdentifier(value.Name, nameof(value.Name));
        var descriptor = new EnumValueDescriptorProto { Name = value.Name, Number = value.Number };
        if (value.DisplayName is not null || !value.Aliases.IsDefaultOrEmpty)
        {
            descriptor.Options = new EnumValueOptions();
            var options = new EnumValueOpts { DisplayName = value.DisplayName ?? string.Empty };
            options.Aliases.Add(value.Aliases.IsDefault ? [] : value.Aliases);
            descriptor.Options.SetExtension(OptionsExtensions.EnumValue, options);
        }
        return descriptor;
    }

    private static EnumLocation? FindEnum(FileDescriptorSet set, string reference)
    {
        var normalized = reference.TrimStart('.');
        var all = EnumerateEnumLocations(set).ToArray();
        var exact = all.Where(item => string.Equals(item.FullName, normalized, StringComparison.Ordinal)).ToArray();
        if (exact.Length == 1)
            return exact[0];
        if (normalized.Contains('.'))
            return null;
        var shortMatches = all.Where(item => string.Equals(item.Descriptor.Name, normalized, StringComparison.Ordinal)).ToArray();
        return shortMatches.Length == 1 ? shortMatches[0] : null;
    }

    private static IEnumerable<EnumLocation> EnumerateEnumLocations(FileDescriptorSet set)
    {
        foreach (var file in set.File)
        {
            foreach (var descriptor in file.EnumType)
                yield return new EnumLocation(file, descriptor, file.Package, Join(file.Package, descriptor.Name));
            foreach (var message in file.MessageType)
            {
                foreach (var location in EnumerateEnumLocations(file, message, Join(file.Package, message.Name)))
                    yield return location;
            }
        }
    }

    private static IEnumerable<EnumLocation> EnumerateEnumLocations(
        FileDescriptorProto file,
        DescriptorProto message,
        string prefix)
    {
        foreach (var descriptor in message.EnumType)
            yield return new EnumLocation(file, descriptor, prefix, Join(prefix, descriptor.Name));
        foreach (var nested in message.NestedType)
        {
            foreach (var location in EnumerateEnumLocations(file, nested, Join(prefix, nested.Name)))
                yield return location;
        }
    }

    private static void RewriteEnumTypeReferences(FileDescriptorSet set, string oldFullName, string newFullName)
    {
        foreach (var message in EnumerateMessages(set))
        {
            foreach (var field in message.Message.Field)
            {
                if (field.Type == ProtoFieldType.Enum
                    && string.Equals(field.TypeName.TrimStart('.'), oldFullName, StringComparison.Ordinal))
                {
                    field.TypeName = $".{newFullName}";
                }

                var options = GetFieldOptions(field);
                if (options is not null
                    && string.Equals(options.MapKeyEnum.TrimStart('.'), oldFullName, StringComparison.Ordinal))
                {
                    options.MapKeyEnum = $".{newFullName}";
                }
            }
        }
    }

    private sealed record EnumLocation(
        FileDescriptorProto File,
        EnumDescriptorProto Descriptor,
        string Prefix,
        string FullName);
}
