using ExcelDb.Schema.Authoring;
using ExcelDb.Pipeline;
using ExcelDb.Workbooks.Formatting;
using ExcelDb.Workbooks.Importing;

namespace ExcelDb.Cli;

public sealed class ToolBuilder
{
    internal ToolBuilder()
    {
        TableInitializer = new DefaultTableInitializer();
        ExportTargetStrategy = new StandardClientServerExportTargetStrategy();
        CellFormats = new CellFormatRegistry();
        WorkbookValidators = [];
    }

    internal ITableInitializer TableInitializer { get; private set; }

    internal IExportTargetStrategy ExportTargetStrategy { get; private set; }

    internal CellFormatRegistry CellFormats { get; }

    internal List<IWorkbookValidator> WorkbookValidators { get; }

    public ToolBuilder UseTableInitializer(ITableInitializer initializer)
    {
        TableInitializer = initializer ?? throw new ArgumentNullException(nameof(initializer));
        return this;
    }

    public ToolBuilder UseExportTargetStrategy(IExportTargetStrategy strategy)
    {
        ExportTargetStrategy = strategy ?? throw new ArgumentNullException(nameof(strategy));
        return this;
    }

    /// <summary>
    /// Registers a pure C# authoring cell codec for this customized tool distribution.
    /// The same registry is used by normalize, check, convert and other workbook operations.
    /// </summary>
    public ToolBuilder UseCellFormat(ICellFormat format)
    {
        CellFormats.Register(format);
        return this;
    }

    /// <summary>
    /// Registers deterministic host code for a table validator id declared in proto.
    /// The same registry gates generate, normalize, check and convert imports.
    /// </summary>
    public ToolBuilder UseWorkbookValidator(IWorkbookValidator validator)
    {
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentException.ThrowIfNullOrWhiteSpace(validator.Id);
        if (WorkbookValidators.Any(item => string.Equals(item.Id, validator.Id, StringComparison.Ordinal)))
            throw new InvalidOperationException($"Workbook validator '{validator.Id}' is already registered.");
        WorkbookValidators.Add(validator);
        return this;
    }

    internal WorkbookValidatorRegistry CreateWorkbookValidatorRegistry() => new(WorkbookValidators);

    internal IExcelDbProjectService CreateProjectService(string toolVersion) => new ExcelDbProjectService(
        toolVersion,
        TableInitializer,
        ExportTargetStrategy,
        CellFormats,
        WorkbookValidators);
}
