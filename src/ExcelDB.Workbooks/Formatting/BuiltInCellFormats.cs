using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using ExcelDb.Schema.Descriptors;

namespace ExcelDb.Workbooks.Formatting;

/// <summary>Positional format whose separators are declared from outermost to innermost.</summary>
public sealed class JoinCellFormat : ICellFormat
{
    private readonly ImmutableArray<string> separators;

    public JoinCellFormat(IEnumerable<string> separators)
    {
        ArgumentNullException.ThrowIfNull(separators);
        this.separators = separators.ToImmutableArray();
        if (this.separators.IsDefaultOrEmpty || this.separators.Any(string.IsNullOrEmpty))
            throw new ArgumentException("join requires one or two non-empty separators.", nameof(separators));
        if (this.separators.Length > 2)
            throw new ArgumentException("join supports at most two separator layers.", nameof(separators));
    }

    public string Identity => $"join:{string.Join("|", separators.Select(EscapeIdentityPart))}";

    public string Describe(CellFormatContext context) =>
        $"positional join ({string.Join(" then ", separators.Select(static value => $"'{value}'"))})";

    public bool TryParse(
        string physicalText,
        CellFormatContext context,
        out string canonicalValue,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(physicalText);
        ArgumentNullException.ThrowIfNull(context);
        JsonNode? value;
        var succeeded = context.Field.Shape switch
        {
            CanonicalFieldShape.RepeatedScalar or CanonicalFieldShape.RepeatedEnum =>
                TryParseRepeatedScalar(physicalText, context, out value, out error),
            CanonicalFieldShape.Message =>
                TryParseMessage(physicalText, context.Field.Children, context, out value, out error),
            CanonicalFieldShape.RepeatedMessage =>
                TryParseRepeatedMessage(physicalText, context, out value, out error),
            CanonicalFieldShape.Map =>
                TryParseMap(physicalText, context, out value, out error),
            _ => Unsupported(context.Field.Shape, out value, out error),
        };
        canonicalValue = succeeded ? CanonicalJson.Serialize(value) : string.Empty;
        return succeeded;
    }

    public bool TryWrite(
        string canonicalValue,
        CellFormatContext context,
        out string physicalText,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(canonicalValue);
        ArgumentNullException.ThrowIfNull(context);
        if (!TryReadCanonicalJson(canonicalValue, out var value, out error))
        {
            physicalText = string.Empty;
            return false;
        }

        return context.Field.Shape switch
        {
            CanonicalFieldShape.RepeatedScalar or CanonicalFieldShape.RepeatedEnum =>
                TryWriteRepeatedScalar(value, out physicalText, out error),
            CanonicalFieldShape.Message =>
                TryWriteMessage(value, context.Field.Children, out physicalText, out error),
            CanonicalFieldShape.RepeatedMessage =>
                TryWriteRepeatedMessage(value, context.Field.Children, out physicalText, out error),
            CanonicalFieldShape.Map =>
                TryWriteMap(value, out physicalText, out error),
            _ => UnsupportedWrite(context.Field.Shape, out physicalText, out error),
        };
    }

    private bool TryParseRepeatedScalar(
        string text,
        CellFormatContext context,
        out JsonNode? value,
        out string? error)
    {
        value = null;
        if (separators.Length != 1)
        {
            error = "A repeated scalar join requires exactly one separator layer.";
            return false;
        }

        if (!DelimitedCellText.TrySplit(text, separators[0], out var parts, out error))
            return false;
        var array = new JsonArray();
        foreach (var part in parts)
        {
            // An empty segment is missing, not an empty string. A quoted empty segment is a value.
            if (part.Length == 0)
                continue;
            if (!CellScalarValueCodec.TryParse(
                    part,
                    context.Field.TypeName,
                    context.Field.Shape,
                    context.Schema,
                    out var element,
                    out error))
                return false;
            array.Add(element);
        }

        value = array;
        error = null;
        return true;
    }

    private bool TryParseMessage(
        string text,
        ImmutableArray<CanonicalFieldDescriptor> children,
        CellFormatContext context,
        out JsonNode? value,
        out string? error)
    {
        value = null;
        if (separators.Length is < 1 or > 2)
        {
            error = "A single-cell message join requires one or two separator layers.";
            return false;
        }

        if (children.IsDefaultOrEmpty)
        {
            error = $"Message field '{context.Field.PropertyPath}' has no canonical child fields.";
            return false;
        }

        if (!DelimitedCellText.TrySplit(text, separators[0], out var parts, out error))
            return false;
        var orderedChildren = children.OrderBy(static item => item.Id).ToArray();
        if (parts.Count > orderedChildren.Length)
        {
            error = $"Cell contains {parts.Count} positional segments, but message '{context.Field.TypeName}' has only {orderedChildren.Length} fields.";
            return false;
        }

        var result = new JsonObject();
        for (var index = 0; index < parts.Count; index++)
        {
            var part = parts[index];
            if (part.Length == 0)
                continue;
            var child = orderedChildren[index];
            if (!child.Children.IsDefaultOrEmpty)
            {
                if (separators.Length != 2)
                {
                    error = $"Nested field '{child.PropertyPath}' requires a second join separator.";
                    return false;
                }

                if (!TryParseFlatMessage(part, child.Children, separators[1], context, out var nested, out error))
                    return false;
                result[child.Name] = nested;
            }
            else
            {
                if (!CellScalarValueCodec.TryParse(
                        part,
                        child.TypeName,
                        child.Shape,
                        context.Schema,
                        out var childValue,
                        out error))
                    return false;
                result[child.Name] = childValue;
            }
        }

        value = result;
        error = null;
        return true;
    }

    private static bool TryParseFlatMessage(
        string text,
        ImmutableArray<CanonicalFieldDescriptor> children,
        string separator,
        CellFormatContext context,
        out JsonObject value,
        out string? error)
    {
        value = new JsonObject();
        if (!DelimitedCellText.TrySplit(text, separator, out var parts, out error))
            return false;
        var ordered = children.OrderBy(static item => item.Id).ToArray();
        if (parts.Count > ordered.Length)
        {
            error = $"Nested cell contains {parts.Count} segments, but the value has only {ordered.Length} fields.";
            return false;
        }

        for (var index = 0; index < parts.Count; index++)
        {
            if (parts[index].Length == 0)
                continue;
            var child = ordered[index];
            if (!child.Children.IsDefaultOrEmpty)
            {
                error = $"Field '{child.PropertyPath}' exceeds the supported two CellFormat layers.";
                return false;
            }

            if (!CellScalarValueCodec.TryParse(
                    parts[index], child.TypeName, child.Shape, context.Schema, out var childValue, out error))
                return false;
            value[child.Name] = childValue;
        }

        error = null;
        return true;
    }

    private bool TryParseRepeatedMessage(
        string text,
        CellFormatContext context,
        out JsonNode? value,
        out string? error)
    {
        value = null;
        if (separators.Length != 2)
        {
            error = "A repeated message join requires outer element and inner field separators.";
            return false;
        }

        if (context.Field.Children.IsDefaultOrEmpty)
        {
            error = $"Repeated message field '{context.Field.PropertyPath}' has no canonical child fields.";
            return false;
        }

        if (!DelimitedCellText.TrySplit(text, separators[0], out var elements, out error))
            return false;
        var array = new JsonArray();
        foreach (var element in elements)
        {
            if (element.Length == 0)
                continue;
            if (!TryParseFlatMessage(element, context.Field.Children, separators[1], context, out var item, out error))
                return false;
            array.Add(item);
        }

        value = array;
        error = null;
        return true;
    }

    private bool TryParseMap(
        string text,
        CellFormatContext context,
        out JsonNode? value,
        out string? error)
    {
        value = null;
        if (separators.Length != 2)
        {
            error = "A map join requires outer entry and inner key/value separators.";
            return false;
        }

        var map = context.Field.Map;
        if (map is null)
        {
            error = $"Map field '{context.Field.PropertyPath}' has no map descriptor.";
            return false;
        }

        if (!DelimitedCellText.TrySplit(text, separators[0], out var entries, out error))
            return false;
        var result = new JsonObject();
        foreach (var entry in entries)
        {
            if (entry.Length == 0)
                continue;
            if (!DelimitedCellText.TrySplit(entry, separators[1], out var pair, out error))
                return false;
            if (pair.Count != 2 || pair[0].Length == 0 || pair[1].Length == 0)
            {
                error = $"Map entry '{entry}' must contain exactly one key/value separator and two values.";
                return false;
            }

            var keyType = map.KeyEnumType ?? map.KeyTypeName;
            var keyShape = map.KeyEnumType is null ? CanonicalFieldShape.Scalar : CanonicalFieldShape.Enum;
            if (!CellScalarValueCodec.TryParse(pair[0], keyType, keyShape, context.Schema, out var keyNode, out error))
                return false;
            if (!TryMapKey(keyNode, out var key, out error))
                return false;
            if (!CellScalarValueCodec.TryParse(
                    pair[1], map.ValueTypeName, map.ValueShape, context.Schema, out var itemValue, out error))
                return false;
            if (result.ContainsKey(key))
            {
                error = $"Map key '{key}' occurs more than once.";
                return false;
            }

            result[key] = itemValue;
        }

        value = result;
        error = null;
        return true;
    }

    private bool TryWriteRepeatedScalar(JsonNode? value, out string physicalText, out string? error)
    {
        if (separators.Length != 1 || value is not JsonArray array)
        {
            physicalText = string.Empty;
            error = "Canonical repeated scalar value must be a JSON array and use one separator.";
            return false;
        }

        var parts = new List<string>(array.Count);
        foreach (var element in array)
        {
            if (!CellScalarValueCodec.TryWrite(element, separators, out var part, out error))
            {
                physicalText = string.Empty;
                return false;
            }
            parts.Add(part);
        }

        physicalText = string.Join(separators[0], parts);
        error = null;
        return true;
    }

    private bool TryWriteMessage(
        JsonNode? value,
        ImmutableArray<CanonicalFieldDescriptor> children,
        out string physicalText,
        out string? error)
    {
        error = null;
        if (value is not JsonObject message)
        {
            physicalText = string.Empty;
            error = "Canonical message value must be a JSON object.";
            return false;
        }

        var parts = new List<string>();
        foreach (var child in children.OrderBy(static item => item.Id))
        {
            if (!message.TryGetPropertyValue(child.Name, out var childValue))
            {
                parts.Add(string.Empty);
                continue;
            }

            if (!child.Children.IsDefaultOrEmpty)
            {
                if (separators.Length != 2
                    || !TryWriteFlatMessage(childValue, child.Children, separators[1], out var nested, out error))
                {
                    physicalText = string.Empty;
                    error ??= $"Nested field '{child.PropertyPath}' requires a second separator.";
                    return false;
                }
                parts.Add(nested);
            }
            else
            {
                if (!CellScalarValueCodec.TryWrite(childValue, separators, out var part, out error))
                {
                    physicalText = string.Empty;
                    return false;
                }
                parts.Add(part);
            }
        }

        TrimMissingTail(parts);
        physicalText = string.Join(separators[0], parts);
        error = null;
        return true;
    }

    private bool TryWriteFlatMessage(
        JsonNode? value,
        ImmutableArray<CanonicalFieldDescriptor> children,
        string separator,
        out string physicalText,
        out string? error)
    {
        if (value is not JsonObject message)
        {
            physicalText = string.Empty;
            error = "Canonical nested message value must be a JSON object.";
            return false;
        }

        var parts = new List<string>();
        foreach (var child in children.OrderBy(static item => item.Id))
        {
            if (!message.TryGetPropertyValue(child.Name, out var childValue))
            {
                parts.Add(string.Empty);
                continue;
            }

            if (!child.Children.IsDefaultOrEmpty)
            {
                physicalText = string.Empty;
                error = $"Field '{child.PropertyPath}' exceeds the supported two CellFormat layers.";
                return false;
            }

            if (!CellScalarValueCodec.TryWrite(childValue, separators, out var part, out error))
            {
                physicalText = string.Empty;
                return false;
            }
            parts.Add(part);
        }

        TrimMissingTail(parts);
        physicalText = string.Join(separator, parts);
        error = null;
        return true;
    }

    private bool TryWriteRepeatedMessage(
        JsonNode? value,
        ImmutableArray<CanonicalFieldDescriptor> children,
        out string physicalText,
        out string? error)
    {
        if (separators.Length != 2 || value is not JsonArray array)
        {
            physicalText = string.Empty;
            error = "Canonical repeated message value must be a JSON array and use two separators.";
            return false;
        }

        var entries = new List<string>(array.Count);
        foreach (var item in array)
        {
            if (!TryWriteFlatMessage(item, children, separators[1], out var entry, out error))
            {
                physicalText = string.Empty;
                return false;
            }
            entries.Add(entry);
        }

        physicalText = string.Join(separators[0], entries);
        error = null;
        return true;
    }

    private bool TryWriteMap(JsonNode? value, out string physicalText, out string? error)
    {
        if (separators.Length != 2 || value is not JsonObject map)
        {
            physicalText = string.Empty;
            error = "Canonical map value must be a JSON object and use two separators.";
            return false;
        }

        var entries = new List<string>(map.Count);
        foreach (var item in map.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            var key = DelimitedCellText.EncodeAtom(item.Key, separators);
            if (!CellScalarValueCodec.TryWrite(item.Value, separators, out var itemValue, out error))
            {
                physicalText = string.Empty;
                return false;
            }
            entries.Add($"{key}{separators[1]}{itemValue}");
        }

        physicalText = string.Join(separators[0], entries);
        error = null;
        return true;
    }

    private static bool TryReadCanonicalJson(string text, out JsonNode? value, out string? error)
    {
        try
        {
            value = JsonNode.Parse(text);
            error = null;
            return true;
        }
        catch (JsonException exception)
        {
            value = null;
            error = $"Invalid canonical JSON value: {exception.Message}";
            return false;
        }
    }

    private static bool TryMapKey(JsonNode? value, out string key, out string? error)
    {
        if (value is JsonValue json && json.TryGetValue<string>(out var stringValue))
        {
            key = stringValue;
            error = null;
            return true;
        }

        if (value is JsonValue)
        {
            key = value.ToJsonString();
            error = null;
            return true;
        }

        key = string.Empty;
        error = "A map key must be a scalar value.";
        return false;
    }

    private static void TrimMissingTail(List<string> values)
    {
        while (values.Count > 0 && values[^1].Length == 0)
            values.RemoveAt(values.Count - 1);
    }

    private static bool Unsupported(
        CanonicalFieldShape shape,
        out JsonNode? value,
        out string? error)
    {
        value = null;
        error = $"join CellFormat does not support field shape {shape}.";
        return false;
    }

    private static bool UnsupportedWrite(
        CanonicalFieldShape shape,
        out string value,
        out string? error)
    {
        value = string.Empty;
        error = $"join CellFormat does not support field shape {shape}.";
        return false;
    }

    private static string EscapeIdentityPart(string value) =>
        Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(value));
}

/// <summary>Named key/value format for single-cell messages and scalar maps.</summary>
public sealed class NamedCellFormat : ICellFormat
{
    private readonly string pairSeparator;
    private readonly string keyValueSeparator;
    private readonly string[] separators;

    public NamedCellFormat(string pairSeparator, string keyValueSeparator)
    {
        ArgumentException.ThrowIfNullOrEmpty(pairSeparator);
        ArgumentException.ThrowIfNullOrEmpty(keyValueSeparator);
        this.pairSeparator = pairSeparator;
        this.keyValueSeparator = keyValueSeparator;
        separators = [pairSeparator, keyValueSeparator];
    }

    public string Identity =>
        $"named:{Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(pairSeparator))}:" +
        Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(keyValueSeparator));

    public string Describe(CellFormatContext context) =>
        $"named pairs ('{pairSeparator}' between pairs, '{keyValueSeparator}' between key and value)";

    public bool TryParse(
        string physicalText,
        CellFormatContext context,
        out string canonicalValue,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(physicalText);
        ArgumentNullException.ThrowIfNull(context);
        JsonObject? value;
        var succeeded = context.Field.Shape switch
        {
            CanonicalFieldShape.Message => TryParseMessage(physicalText, context, out value, out error),
            CanonicalFieldShape.Map => TryParseMap(physicalText, context, out value, out error),
            _ => Unsupported(context.Field.Shape, out value, out error),
        };
        canonicalValue = succeeded ? CanonicalJson.Serialize(value) : string.Empty;
        return succeeded;
    }

    public bool TryWrite(
        string canonicalValue,
        CellFormatContext context,
        out string physicalText,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(canonicalValue);
        ArgumentNullException.ThrowIfNull(context);
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(canonicalValue);
        }
        catch (JsonException exception)
        {
            physicalText = string.Empty;
            error = $"Invalid canonical JSON value: {exception.Message}";
            return false;
        }

        if (node is not JsonObject value)
        {
            physicalText = string.Empty;
            error = "Canonical named value must be a JSON object.";
            return false;
        }

        return context.Field.Shape switch
        {
            CanonicalFieldShape.Message => TryWriteMessage(value, context, out physicalText, out error),
            CanonicalFieldShape.Map => TryWriteMap(value, out physicalText, out error),
            _ => UnsupportedWrite(context.Field.Shape, out physicalText, out error),
        };
    }

    private bool TryParseMessage(
        string text,
        CellFormatContext context,
        out JsonObject? value,
        out string? error)
    {
        value = new JsonObject();
        if (context.Field.Children.IsDefaultOrEmpty)
        {
            error = $"Message field '{context.Field.PropertyPath}' has no canonical child fields.";
            return false;
        }

        var children = context.Field.Children
            .SelectMany(static child => new[]
            {
                new KeyValuePair<string, CanonicalFieldDescriptor>(child.Name, child),
                new KeyValuePair<string, CanonicalFieldDescriptor>(child.PropertyPath.Split('.').Last(), child),
            })
            .GroupBy(static pair => pair.Key, StringComparer.Ordinal)
            .Where(static group => group.Select(static pair => pair.Value.Id).Distinct().Count() == 1)
            .ToDictionary(static group => group.Key, static group => group.First().Value, StringComparer.Ordinal);
        if (!TryPairs(text, out var pairs, out error))
            return false;
        foreach (var pair in pairs)
        {
            if (!DelimitedCellText.TryDecodeAtom(pair.Key, out var name, out error))
                return false;
            if (!children.TryGetValue(name, out var child))
            {
                error = $"Named field '{name}' is absent from message '{context.Field.TypeName}'.";
                return false;
            }
            if (!child.Children.IsDefaultOrEmpty)
            {
                error = $"Named field '{child.PropertyPath}' is nested; use a codec or a two-layer join.";
                return false;
            }
            if (value.ContainsKey(child.Name))
            {
                error = $"Named field '{name}' occurs more than once.";
                return false;
            }
            if (!CellScalarValueCodec.TryParse(
                    pair.Value, child.TypeName, child.Shape, context.Schema, out var itemValue, out error))
                return false;
            value[child.Name] = itemValue;
        }

        error = null;
        return true;
    }

    private bool TryParseMap(
        string text,
        CellFormatContext context,
        out JsonObject? value,
        out string? error)
    {
        value = new JsonObject();
        var map = context.Field.Map;
        if (map is null)
        {
            error = $"Map field '{context.Field.PropertyPath}' has no map descriptor.";
            return false;
        }
        if (!TryPairs(text, out var pairs, out error))
            return false;
        foreach (var pair in pairs)
        {
            var keyType = map.KeyEnumType ?? map.KeyTypeName;
            var keyShape = map.KeyEnumType is null ? CanonicalFieldShape.Scalar : CanonicalFieldShape.Enum;
            if (!CellScalarValueCodec.TryParse(pair.Key, keyType, keyShape, context.Schema, out var keyNode, out error))
                return false;
            if (!TryMapKey(keyNode, out var key, out error))
                return false;
            if (value.ContainsKey(key))
            {
                error = $"Map key '{key}' occurs more than once.";
                return false;
            }
            if (!CellScalarValueCodec.TryParse(
                    pair.Value, map.ValueTypeName, map.ValueShape, context.Schema, out var itemValue, out error))
                return false;
            value[key] = itemValue;
        }

        error = null;
        return true;
    }

    private bool TryPairs(
        string text,
        out List<KeyValuePair<string, string>> pairs,
        out string? error)
    {
        pairs = [];
        if (!DelimitedCellText.TrySplit(text, pairSeparator, out var physicalPairs, out error))
            return false;
        foreach (var physicalPair in physicalPairs)
        {
            if (physicalPair.Length == 0)
                continue;
            if (!DelimitedCellText.TrySplit(physicalPair, keyValueSeparator, out var parts, out error))
                return false;
            if (parts.Count != 2 || parts[0].Length == 0 || parts[1].Length == 0)
            {
                error = $"Named pair '{physicalPair}' must contain exactly one key/value separator and two values.";
                return false;
            }
            pairs.Add(new KeyValuePair<string, string>(parts[0], parts[1]));
        }

        error = null;
        return true;
    }

    private bool TryWriteMessage(
        JsonObject value,
        CellFormatContext context,
        out string physicalText,
        out string? error)
    {
        var pairs = new List<string>();
        foreach (var child in context.Field.Children.OrderBy(static item => item.Id))
        {
            if (!value.TryGetPropertyValue(child.Name, out var itemValue))
                continue;
            if (!child.Children.IsDefaultOrEmpty)
            {
                physicalText = string.Empty;
                error = $"Named field '{child.PropertyPath}' is nested; use a codec or a two-layer join.";
                return false;
            }
            var key = DelimitedCellText.EncodeAtom(child.Name, separators);
            if (!CellScalarValueCodec.TryWrite(itemValue, separators, out var item, out error))
            {
                physicalText = string.Empty;
                return false;
            }
            pairs.Add($"{key}{keyValueSeparator}{item}");
        }

        physicalText = string.Join(pairSeparator, pairs);
        error = null;
        return true;
    }

    private bool TryWriteMap(JsonObject value, out string physicalText, out string? error)
    {
        var pairs = new List<string>();
        foreach (var pair in value.OrderBy(static item => item.Key, StringComparer.Ordinal))
        {
            var key = DelimitedCellText.EncodeAtom(pair.Key, separators);
            if (!CellScalarValueCodec.TryWrite(pair.Value, separators, out var item, out error))
            {
                physicalText = string.Empty;
                return false;
            }
            pairs.Add($"{key}{keyValueSeparator}{item}");
        }

        physicalText = string.Join(pairSeparator, pairs);
        error = null;
        return true;
    }

    private static bool TryMapKey(JsonNode? value, out string key, out string? error)
    {
        if (value is JsonValue json && json.TryGetValue<string>(out var text))
        {
            key = text;
            error = null;
            return true;
        }
        if (value is JsonValue)
        {
            key = value.ToJsonString();
            error = null;
            return true;
        }

        key = string.Empty;
        error = "A map key must be a scalar value.";
        return false;
    }

    private static bool Unsupported(
        CanonicalFieldShape shape,
        out JsonObject? value,
        out string? error)
    {
        value = null;
        error = $"named CellFormat does not support field shape {shape}.";
        return false;
    }

    private static bool UnsupportedWrite(
        CanonicalFieldShape shape,
        out string value,
        out string? error)
    {
        value = string.Empty;
        error = $"named CellFormat does not support field shape {shape}.";
        return false;
    }
}
