using ExcelDb.Schema.Descriptors;

namespace ExcelDb.Workbooks.Formatting;

/// <summary>
/// The complete context available to an authoring-only cell format. Implementations must be
/// deterministic and must not perform I/O; runtime data never depends on the physical cell text.
/// </summary>
public sealed record CellFormatContext(
    CanonicalFieldDescriptor Field,
    CanonicalSchemaDescriptor Schema);

/// <summary>
/// Converts between a physical workbook cell and the field's canonical value text.
/// Complex values use deterministic JSON as their canonical representation.
/// </summary>
public interface ICellFormat
{
    /// <summary>A stable, versioned identity, for example <c>duration-seconds:v1</c>.</summary>
    string Identity { get; }

    bool TryParse(
        string physicalText,
        CellFormatContext context,
        out string canonicalValue,
        out string? error);

    bool TryWrite(
        string canonicalValue,
        CellFormatContext context,
        out string physicalText,
        out string? error);

    string Describe(CellFormatContext context);
}

/// <summary>Host-owned registry for <c>codec</c> CellFormat declarations.</summary>
public sealed class CellFormatRegistry
{
    private readonly Dictionary<string, ICellFormat> codecs = new(StringComparer.Ordinal);

    public CellFormatRegistry()
    {
    }

    public CellFormatRegistry(IEnumerable<ICellFormat> formats)
    {
        Guard.NotNull(formats);
        foreach (var format in formats)
            Register(format);
    }

    public IReadOnlyCollection<string> CodecIdentities => codecs.Keys;

    public void Register(ICellFormat format)
    {
        Guard.NotNull(format);
        Guard.NotNullOrWhiteSpace(format.Identity);
        if (!codecs.TryAdd(format.Identity, format))
            throw new InvalidOperationException($"Cell format codec '{format.Identity}' is already registered.");
    }

    public bool TryGetCodec(string identity, out ICellFormat? format)
    {
        Guard.NotNullOrWhiteSpace(identity);
        return codecs.TryGetValue(identity, out format);
    }

    internal bool TryResolve(
        CanonicalCellFormat declaration,
        out ICellFormat? format,
        out string? error)
    {
        Guard.NotNull(declaration);
        error = null;
        switch (declaration.Kind)
        {
            case "join":
                if (declaration.Parameters.IsDefaultOrEmpty
                    || declaration.Parameters.Length > 2
                    || declaration.Parameters.Any(string.IsNullOrEmpty))
                {
                    format = null;
                    error = "join CellFormat requires one or two non-empty separators.";
                    return false;
                }

                format = new JoinCellFormat(declaration.Parameters);
                return true;
            case "named":
                if (declaration.Parameters.Length != 2
                    || declaration.Parameters.Any(string.IsNullOrEmpty))
                {
                    format = null;
                    error = "named CellFormat requires non-empty pair and key/value separators.";
                    return false;
                }

                format = new NamedCellFormat(declaration.Parameters[0], declaration.Parameters[1]);
                return true;
            case "codec":
                if (declaration.Parameters.Length != 1)
                {
                    format = null;
                    error = "codec CellFormat requires exactly one registered identity.";
                    return false;
                }

                if (codecs.TryGetValue(declaration.Parameters[0], out format))
                    return true;
                error = $"Cell format codec '{declaration.Parameters[0]}' is not registered.";
                return false;
            default:
                format = null;
                error = $"Unknown CellFormat kind '{declaration.Kind}'.";
                return false;
        }
    }
}
