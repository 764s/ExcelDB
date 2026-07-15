using System.Collections.Immutable;
using System.Text;
using ExcelDb.Pipeline;
using ExcelDb.Schema.Authoring;
using ExcelDb.Schema.Mutation;
using ExcelDb.Tooling.Plans;

namespace ExcelDb.ProjectHub.Windows;

public sealed class PlanPreviewDialog : Form
{
    private PlanPreviewDialog(MutationPlan plan)
    {
        Text = "确认 ExcelDB 操作计划";
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        MinimumSize = new Size(720, 520);
        Size = new Size(820, 620);

        var external = IsExternal(plan.ProjectRoot, plan.GeneratedCSharpRoot);
        var heading = new Label
        {
            AutoSize = true,
            Text = $"操作：{plan.Operation}",
            Font = new Font(Font, FontStyle.Bold),
            Padding = new Padding(0, 0, 0, 6),
        };
        var externalNotice = new Label
        {
            AutoSize = true,
            Visible = external,
            ForeColor = Color.DarkOrange,
            Text = external ? $"注意：Generated C# 将写入项目外目录：{plan.GeneratedCSharpRoot}" : string.Empty,
            Padding = new Padding(0, 0, 0, 6),
        };
        var preview = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Dock = DockStyle.Fill,
            Font = new Font(FontFamily.GenericMonospace, 9),
            Text = FormatPlan(plan),
        };
        var failure = plan.Diagnostics.Any(static diagnostic => diagnostic.IsFailure);
        var note = new Label
        {
            AutoSize = true,
            ForeColor = failure ? Color.Firebrick : SystemColors.ControlText,
            Text = failure
                ? "计划包含阻断性诊断，不能应用。"
                : "应用的是上方显示的同一个冻结计划；取消不会写入项目。",
            Padding = new Padding(0, 6, 0, 0),
        };
        var apply = new Button
        {
            Text = "应用计划",
            DialogResult = DialogResult.OK,
            AutoSize = true,
            Enabled = !failure,
        };
        var cancel = new Button
        {
            Text = "取消",
            DialogResult = DialogResult.Cancel,
            AutoSize = true,
        };
        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(apply);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(14),
            ColumnCount = 1,
            RowCount = 5,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(heading, 0, 0);
        layout.Controls.Add(externalNotice, 0, 1);
        layout.Controls.Add(preview, 0, 2);
        layout.Controls.Add(note, 0, 3);
        layout.Controls.Add(buttons, 0, 4);
        Controls.Add(layout);
        AcceptButton = apply;
        CancelButton = cancel;
    }

    public static bool Confirm(IWin32Window owner, MutationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(plan);
        using var dialog = new PlanPreviewDialog(plan);
        return dialog.ShowDialog(owner) == DialogResult.OK;
    }

    private static string FormatPlan(MutationPlan plan)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Plan hash: {plan.PlanHash}");
        builder.AppendLine($"Project root: {plan.ProjectRoot}");
        if (plan.GeneratedCSharpRoot is not null)
            builder.AppendLine($"Generated C# root: {plan.GeneratedCSharpRoot}");
        builder.AppendLine($"System catalog: {plan.SystemCatalogHash}");
        if (!plan.Risks.IsEmpty)
        {
            builder.AppendLine();
            builder.AppendLine("Risks:");
            foreach (var risk in plan.Risks)
                builder.AppendLine($"  - {risk}");
        }
        if (!plan.Diagnostics.IsEmpty)
        {
            builder.AppendLine();
            builder.AppendLine("Diagnostics:");
            foreach (var diagnostic in plan.Diagnostics)
                builder.AppendLine($"  {diagnostic.Severity} {diagnostic.Code} {diagnostic.Location}: {diagnostic.Message}");
        }
        builder.AppendLine();
        builder.AppendLine($"Changes ({plan.Mutations.Length}):");
        foreach (var mutation in plan.Mutations)
            builder.AppendLine($"  [{mutation.Root}] {mutation.Kind} {mutation.RelativePath}");
        return builder.ToString();
    }

    private static bool IsExternal(string projectRoot, string? generatedCSharpRoot)
    {
        if (generatedCSharpRoot is null)
            return false;
        var project = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
        var generated = Path.TrimEndingDirectorySeparator(Path.GetFullPath(generatedCSharpRoot));
        if (string.Equals(project, generated, StringComparison.OrdinalIgnoreCase))
            return false;
        return !generated.StartsWith(project + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class InitializeProjectDialog : Form
{
    private readonly TextBox _generatedCSharp = new();

    public InitializeProjectDialog(string projectRoot)
    {
        ProjectRoot = Path.GetFullPath(projectRoot);
        Text = "初始化 ExcelDB 项目";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(650, 205);

        _generatedCSharp.Text = Path.Combine(ProjectRoot, "Generated", "CSharp");
        _generatedCSharp.Dock = DockStyle.Fill;
        var browse = new Button { Text = "选择…", AutoSize = true };
        browse.Click += (_, _) => BrowseForFolder(_generatedCSharp, "选择 Generated C# 输出目录");
        var ok = new Button { Text = "预览初始化计划", AutoSize = true, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "取消", AutoSize = true, DialogResult = DialogResult.Cancel };
        ok.Click += ValidateGeneratedDirectory;

        Controls.Add(DialogLayout.Create(
            [
                ("项目目录", DialogLayout.ReadOnlyText(ProjectRoot), (Control?)null),
                ("Generated C#", _generatedCSharp, browse),
            ],
            "这里只选择四类工件的位置；配置和工具状态会放入隐藏的 .exceldb 目录。",
            ok,
            cancel));
        AcceptButton = ok;
        CancelButton = cancel;
    }

    public string ProjectRoot { get; }

    public string GeneratedCSharpDirectory => Path.GetFullPath(_generatedCSharp.Text, ProjectRoot);

    private void ValidateGeneratedDirectory(object? sender, EventArgs eventArgs)
    {
        try
        {
            _ = GeneratedCSharpDirectory;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            DialogResult = DialogResult.None;
            MessageBox.Show(this, exception.Message, "无效目录", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    internal static void BrowseForFolder(TextBox target, string description)
    {
        using var picker = new FolderBrowserDialog
        {
            Description = description,
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            SelectedPath = Directory.Exists(target.Text) ? target.Text : string.Empty,
        };
        if (picker.ShowDialog(target.FindForm()) == DialogResult.OK)
            target.Text = picker.SelectedPath;
    }
}

public sealed class ProjectSettingsDialog : Form
{
    private readonly TextBox _schema;
    private readonly TextBox _excel;
    private readonly TextBox _csharp;
    private readonly TextBox _bytes;

    public ProjectSettingsDialog(ProjectInspection inspection)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        Text = "ExcelDB 项目设置";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(720, 300);

        _schema = DialogLayout.EditableText(inspection.Schema.Path);
        _excel = DialogLayout.EditableText(inspection.Excel.Path);
        _csharp = DialogLayout.EditableText(inspection.GeneratedCSharp.Path);
        _bytes = DialogLayout.EditableText(inspection.GeneratedBytes.Path);
        var browseCSharp = new Button { Text = "选择…", AutoSize = true };
        browseCSharp.Click += (_, _) => InitializeProjectDialog.BrowseForFolder(_csharp, "选择 Generated C# 输出目录");
        var ok = new Button { Text = "预览设置计划", AutoSize = true, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "取消", AutoSize = true, DialogResult = DialogResult.Cancel };
        ok.Click += ValidatePaths;

        Controls.Add(DialogLayout.Create(
            [
                ("Schema", _schema, (Control?)null),
                ("Excel", _excel, (Control?)null),
                ("Generated C#", _csharp, browseCSharp),
                ("Generated Bytes", _bytes, (Control?)null),
            ],
            "Generated C# 可以位于项目外；其他目录必须由项目服务验证为项目内目录。保存设置不会搬移旧输出。",
            ok,
            cancel));
        AcceptButton = ok;
        CancelButton = cancel;
    }

    public string SchemaDirectory => Path.GetFullPath(_schema.Text);

    public string ExcelDirectory => Path.GetFullPath(_excel.Text);

    public string GeneratedCSharpDirectory => Path.GetFullPath(_csharp.Text);

    public string GeneratedBytesDirectory => Path.GetFullPath(_bytes.Text);

    private void ValidatePaths(object? sender, EventArgs eventArgs)
    {
        try
        {
            _ = SchemaDirectory;
            _ = ExcelDirectory;
            _ = GeneratedCSharpDirectory;
            _ = GeneratedBytesDirectory;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            DialogResult = DialogResult.None;
            MessageBox.Show(this, exception.Message, "无效目录", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}

public sealed class CreateTableDialog : Form
{
    private readonly TextBox _tableName = DialogLayout.EditableText(string.Empty);
    private readonly TextBox _workbook;
    private readonly DataGridView _fields = TableDialogControls.CreateFieldGrid(includeKeyAndTarget: true);
    private readonly CheckBox _autoKey = new() { Text = "自动创建 id:string key", Checked = true, AutoSize = true };
    private readonly ComboBox _tableTarget = TableDialogControls.CreateTargetComboBox();
    private bool _synchronizingKey;

    public CreateTableDialog(string defaultWorkbookPath)
    {
        _workbook = DialogLayout.EditableText(CreateTableDialog.Required(defaultWorkbookPath, "Excel 文件"));
        Text = "创建表结构";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        ShowInTaskbar = false;
        MinimumSize = new Size(760, 520);
        Size = new Size(840, 600);
        var ok = new Button { Text = "预览建表计划", AutoSize = true, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "取消", AutoSize = true, DialogResult = DialogResult.Cancel };
        _tableName.Name = "CreateTableName";
        _workbook.Name = "CreateWorkbook";
        _tableTarget.Name = "CreateTableTarget";
        _fields.Name = "CreateFieldsGrid";
        _fields.Height = 210;
        _fields.Rows.Add("name", "string", string.Empty, false, TableDialogControls.AutomaticTargetLabel);
        TableDialogControls.UpdateEnumTypeCell(_fields.Rows[0]);
        _fields.CellValueChanged += ExplicitKeyChanged;
        _autoKey.CheckedChanged += AutoKeyChanged;
        ok.Click += ValidateInput;
        Controls.Add(DialogLayout.Create(
            [
                ("表名", _tableName, (Control?)null),
                ("Excel 文件", _workbook, (Control?)null),
                ("简单字段", _fields, (Control?)null),
                ("Key", _autoKey, (Control?)null),
                ("表 Target", _tableTarget, (Control?)null),
            ],
            "字段使用结构化选项。Target 选择“自动”时由 C# 策略设置；显式 Key 会自动关闭 AutoKey。内部代码命名使用默认设置。",
            ok,
            cancel));
        AcceptButton = ok;
        CancelButton = cancel;
    }

    public TableCreateIntent CreateIntent()
    {
        _fields.EndEdit();
        var intent = TableDialogModel.CreateIntent(
            Required(_tableName.Text, "表名"),
            Required(_workbook.Text, "Excel 文件"),
            _autoKey.Checked,
            TableDialogControls.ParseTarget(_tableTarget.SelectedItem),
            TableDialogControls.ReadFields(_fields, includeKeyAndTarget: true));
        _autoKey.Checked = intent.AutoKey;
        return intent;
    }

    private void ExplicitKeyChanged(object? sender, DataGridViewCellEventArgs eventArgs)
    {
        if (_synchronizingKey
            || eventArgs.RowIndex < 0
            || eventArgs.ColumnIndex != _fields.Columns[TableDialogControls.KeyColumnName]!.Index
            || _fields.Rows[eventArgs.RowIndex].Cells[eventArgs.ColumnIndex].Value is not true)
        {
            return;
        }

        _synchronizingKey = true;
        try
        {
            _autoKey.Checked = false;
            foreach (DataGridViewRow row in _fields.Rows)
            {
                if (!row.IsNewRow && row.Index != eventArgs.RowIndex)
                    row.Cells[TableDialogControls.KeyColumnName].Value = false;
            }
        }
        finally
        {
            _synchronizingKey = false;
        }
    }

    private void AutoKeyChanged(object? sender, EventArgs eventArgs)
    {
        if (_synchronizingKey || !_autoKey.Checked)
            return;
        _synchronizingKey = true;
        try
        {
            foreach (DataGridViewRow row in _fields.Rows)
            {
                if (!row.IsNewRow)
                    row.Cells[TableDialogControls.KeyColumnName].Value = false;
            }
        }
        finally
        {
            _synchronizingKey = false;
        }
    }

    private void ValidateInput(object? sender, EventArgs eventArgs)
    {
        try
        {
            _ = CreateIntent();
        }
        catch (ArgumentException exception)
        {
            DialogResult = DialogResult.None;
            MessageBox.Show(this, exception.Message, "建表输入不完整", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    internal static string Required(string value, string label) =>
        !string.IsNullOrWhiteSpace(value) ? value.Trim() : throw new ArgumentException($"{label}不能为空。");
}

public sealed class EditTableDialog : Form
{
    private readonly TextBox _tableName = DialogLayout.EditableText(string.Empty);
    private readonly TextBox _newName = DialogLayout.EditableText(string.Empty);
    private readonly TextBox _workbook = DialogLayout.EditableText(string.Empty);
    private readonly DataGridView _addFields = TableDialogControls.CreateFieldGrid(includeKeyAndTarget: false);

    public EditTableDialog()
    {
        Text = "编辑表结构";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        ShowInTaskbar = false;
        MinimumSize = new Size(700, 430);
        Size = new Size(780, 520);
        var ok = new Button { Text = "预览编辑计划", AutoSize = true, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "取消", AutoSize = true, DialogResult = DialogResult.Cancel };
        _tableName.Name = "EditTableFullName";
        _newName.Name = "EditTableNewName";
        _workbook.Name = "EditWorkbook";
        _addFields.Name = "EditFieldsGrid";
        _addFields.Height = 170;
        ok.Click += ValidateInput;
        Controls.Add(DialogLayout.Create(
            [
                ("表全名", _tableName, (Control?)null),
                ("新表名（可选）", _newName, (Control?)null),
                ("Excel 文件（可选）", _workbook, (Control?)null),
                ("新增简单字段", _addFields, (Control?)null),
            ],
            "新增字段使用结构化名称和类型。至少填写新表名或一个新增字段；target、复杂重命名和删除可使用 CLI。",
            ok,
            cancel));
        AcceptButton = ok;
        CancelButton = cancel;
    }

    public TableEditIntent CreateIntent()
    {
        _addFields.EndEdit();
        var additions = TableDialogModel.CreateAdditions(
            TableDialogControls.ReadFields(_addFields, includeKeyAndTarget: false));
        var newName = NullIfWhiteSpace(_newName.Text);
        if (additions.IsEmpty && newName is null)
            throw new ArgumentException("至少填写新表名或一个新增字段。");
        return new TableEditIntent(
            CreateTableDialog.Required(_tableName.Text, "表全名"),
            additions,
            [],
            [],
            NewName: newName,
            WorkbookPath: NullIfWhiteSpace(_workbook.Text));
    }

    private void ValidateInput(object? sender, EventArgs eventArgs)
    {
        try
        {
            _ = CreateIntent();
        }
        catch (ArgumentException exception)
        {
            DialogResult = DialogResult.None;
            MessageBox.Show(this, exception.Message, "编辑输入不完整", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static string? NullIfWhiteSpace(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed class TargetDialog : Form
{
    private readonly TextBox _target = DialogLayout.EditableText("client");

    public TargetDialog()
    {
        Text = "生成运行时 bytes";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(520, 175);
        var ok = new Button { Text = "生成", AutoSize = true, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "取消", AutoSize = true, DialogResult = DialogResult.Cancel };
        ok.Click += ValidateTarget;
        Controls.Add(DialogLayout.Create(
            [("Target", _target, (Control?)null)],
            "输出固定派生到 Generated Bytes/<target-id>/config.bytes；此处不需要填写输出路径。",
            ok,
            cancel));
        AcceptButton = ok;
        CancelButton = cancel;
    }

    public string Target => _target.Text.Trim();

    private void ValidateTarget(object? sender, EventArgs eventArgs)
    {
        if (System.Text.RegularExpressions.Regex.IsMatch(Target, "^[a-z][a-z0-9-]*$", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            return;
        DialogResult = DialogResult.None;
        MessageBox.Show(this, "Target 必须匹配 ^[a-z][a-z0-9-]*$。", "无效 target", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }
}

internal enum DialogTargetChoice
{
    Automatic,
    Client,
    Server,
    ClientAndServer,
}

internal sealed record DialogFieldInput(
    string Name,
    SimpleFieldType Type,
    string? EnumType = null,
    bool IsKey = false,
    DialogTargetChoice Target = DialogTargetChoice.Automatic);

internal static class TableDialogModel
{
    public const string DefaultPackage = "game.configs";

    public static TableCreateIntent CreateIntent(
        string tableName,
        string workbookPath,
        bool autoKey,
        DialogTargetChoice tableTarget,
        IEnumerable<DialogFieldInput> fieldInputs)
    {
        ArgumentNullException.ThrowIfNull(fieldInputs);
        var inputs = Normalize(fieldInputs);
        var explicitKeys = inputs.Count(static field => field.IsKey);
        if (explicitKeys > 1)
            throw new ArgumentException("只能声明一个显式 key 字段。", nameof(fieldInputs));

        var fields = inputs
            .Select(static field => new SimpleFieldDefinition(
                field.Name,
                field.Type,
                field.EnumType,
                field.IsKey))
            .ToImmutableArray();
        var fieldTargets = inputs
            .Where(static field => field.Target != DialogTargetChoice.Automatic)
            .ToImmutableDictionary(
                static field => field.Name,
                static field => ToSelection(field.Target)!,
                StringComparer.Ordinal);
        return new TableCreateIntent(
            CreateTableDialog.Required(tableName, "表名"),
            CreateTableDialog.Required(workbookPath, "Excel 文件"),
            fields,
            AutoKey: explicitKeys == 0 && autoKey,
            Package: DefaultPackage,
            FieldTargets: fieldTargets,
            TableTargets: ToSelection(tableTarget));
    }

    public static ImmutableArray<AddSimpleFieldMutation> CreateAdditions(
        IEnumerable<DialogFieldInput> fieldInputs)
    {
        ArgumentNullException.ThrowIfNull(fieldInputs);
        return Normalize(fieldInputs)
            .Select(static field => new AddSimpleFieldMutation(
                new SimpleFieldDefinition(field.Name, field.Type, field.EnumType),
                ExportTargets: ToSelection(field.Target)))
            .ToImmutableArray();
    }

    public static ExportTargetSelection? ToSelection(DialogTargetChoice target) => target switch
    {
        DialogTargetChoice.Automatic => null,
        DialogTargetChoice.Client => ExportTargetSelection.Explicit(["client"]),
        DialogTargetChoice.Server => ExportTargetSelection.Explicit(["server"]),
        DialogTargetChoice.ClientAndServer => ExportTargetSelection.Explicit(["client", "server"]),
        _ => throw new ArgumentOutOfRangeException(nameof(target), target, "Unknown dialog target choice."),
    };

    private static ImmutableArray<DialogFieldInput> Normalize(IEnumerable<DialogFieldInput> fieldInputs)
    {
        var result = ImmutableArray.CreateBuilder<DialogFieldInput>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var input in fieldInputs)
        {
            var name = CreateTableDialog.Required(input.Name, "字段名");
            var enumType = string.IsNullOrWhiteSpace(input.EnumType) ? null : input.EnumType.Trim();
            if (input.Type == SimpleFieldType.Enum && enumType is null)
                throw new ArgumentException($"枚举字段 '{name}' 必须填写枚举类型。", nameof(fieldInputs));
            if (input.Type != SimpleFieldType.Enum && enumType is not null)
                throw new ArgumentException($"非枚举字段 '{name}' 不能填写枚举类型。", nameof(fieldInputs));
            if (!names.Add(name))
                throw new ArgumentException($"字段名 '{name}' 重复。", nameof(fieldInputs));
            result.Add(input with { Name = name, EnumType = enumType });
        }
        return result.ToImmutable();
    }
}

internal static class TableDialogControls
{
    public const string KeyColumnName = "Key";
    public const string AutomaticTargetLabel = "自动";

    private static readonly IReadOnlyDictionary<string, SimpleFieldType> FieldTypes =
        new Dictionary<string, SimpleFieldType>(StringComparer.Ordinal)
        {
            ["string"] = SimpleFieldType.String,
            ["int32"] = SimpleFieldType.Int32,
            ["int64"] = SimpleFieldType.Int64,
            ["uint32"] = SimpleFieldType.UInt32,
            ["uint64"] = SimpleFieldType.UInt64,
            ["float"] = SimpleFieldType.Float,
            ["double"] = SimpleFieldType.Double,
            ["boolean"] = SimpleFieldType.Boolean,
            ["enum"] = SimpleFieldType.Enum,
        };

    private static readonly IReadOnlyDictionary<string, DialogTargetChoice> Targets =
        new Dictionary<string, DialogTargetChoice>(StringComparer.Ordinal)
        {
            [AutomaticTargetLabel] = DialogTargetChoice.Automatic,
            ["client"] = DialogTargetChoice.Client,
            ["server"] = DialogTargetChoice.Server,
            ["client+server"] = DialogTargetChoice.ClientAndServer,
        };

    public static DataGridView CreateFieldGrid(bool includeKeyAndTarget)
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = true,
            AllowUserToDeleteRows = true,
            AutoGenerateColumns = false,
            RowHeadersVisible = false,
            EditMode = DataGridViewEditMode.EditOnEnter,
            SelectionMode = DataGridViewSelectionMode.CellSelect,
        };
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "FieldName",
            HeaderText = "字段名",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            FillWeight = 45,
        });
        var type = new DataGridViewComboBoxColumn
        {
            Name = "FieldType",
            HeaderText = "类型",
            FlatStyle = FlatStyle.Flat,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            FillWeight = 30,
        };
        type.Items.AddRange(FieldTypes.Keys.Cast<object>().ToArray());
        grid.Columns.Add(type);
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "EnumType",
            HeaderText = "枚举类型",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            FillWeight = 40,
        });
        if (includeKeyAndTarget)
        {
            grid.Columns.Add(new DataGridViewCheckBoxColumn
            {
                Name = KeyColumnName,
                HeaderText = "Key",
                AutoSizeMode = DataGridViewAutoSizeColumnMode.ColumnHeader,
            });
            var target = new DataGridViewComboBoxColumn
            {
                Name = "Target",
                HeaderText = "Target",
                FlatStyle = FlatStyle.Flat,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                FillWeight = 35,
            };
            target.Items.AddRange(Targets.Keys.Cast<object>().ToArray());
            grid.Columns.Add(target);
        }
        grid.DefaultValuesNeeded += (_, eventArgs) =>
        {
            eventArgs.Row.Cells["FieldType"].Value = "string";
            if (includeKeyAndTarget)
            {
                eventArgs.Row.Cells[KeyColumnName].Value = false;
                eventArgs.Row.Cells["Target"].Value = AutomaticTargetLabel;
            }
        };
        grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (grid.IsCurrentCellDirty)
                grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        grid.CellValueChanged += (_, eventArgs) =>
        {
            if (eventArgs.RowIndex >= 0
                && eventArgs.ColumnIndex == grid.Columns["FieldType"]!.Index)
            {
                UpdateEnumTypeCell(grid.Rows[eventArgs.RowIndex]);
            }
        };
        return grid;
    }

    public static ComboBox CreateTargetComboBox()
    {
        var combo = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
        combo.Items.AddRange(Targets.Keys.Cast<object>().ToArray());
        combo.SelectedItem = AutomaticTargetLabel;
        return combo;
    }

    public static ImmutableArray<DialogFieldInput> ReadFields(
        DataGridView grid,
        bool includeKeyAndTarget)
    {
        ArgumentNullException.ThrowIfNull(grid);
        var result = ImmutableArray.CreateBuilder<DialogFieldInput>();
        foreach (DataGridViewRow row in grid.Rows)
        {
            if (row.IsNewRow)
                continue;
            var name = Convert.ToString(row.Cells["FieldName"].Value)?.Trim() ?? string.Empty;
            var typeLabel = Convert.ToString(row.Cells["FieldType"].Value);
            var enumType = Convert.ToString(row.Cells["EnumType"].Value);
            var isKey = includeKeyAndTarget && row.Cells[KeyColumnName].Value is true;
            var targetLabel = includeKeyAndTarget ? row.Cells["Target"].Value : null;
            if (name.Length == 0 && typeLabel is null && enumType is null && !isKey && targetLabel is null)
                continue;
            if (name.Length == 0)
                throw new ArgumentException("字段名不能为空。");
            result.Add(new DialogFieldInput(
                name,
                ParseType(typeLabel),
                enumType,
                isKey,
                includeKeyAndTarget ? ParseTarget(targetLabel) : DialogTargetChoice.Automatic));
        }
        return result.ToImmutable();
    }

    public static DialogTargetChoice ParseTarget(object? value)
    {
        var label = Convert.ToString(value) ?? AutomaticTargetLabel;
        return Targets.TryGetValue(label, out var target)
            ? target
            : throw new ArgumentException($"不支持 Target 选项 '{label}'。");
    }

    private static SimpleFieldType ParseType(string? label)
    {
        if (label is not null && FieldTypes.TryGetValue(label, out var type))
            return type;
        throw new ArgumentException("请选择字段类型。");
    }

    public static void UpdateEnumTypeCell(DataGridViewRow row)
    {
        var isEnum = string.Equals(
            Convert.ToString(row.Cells["FieldType"].Value),
            "enum",
            StringComparison.Ordinal);
        var cell = row.Cells["EnumType"];
        cell.ReadOnly = !isEnum;
        cell.Style.BackColor = isEnum ? SystemColors.Window : SystemColors.Control;
        if (!isEnum)
            cell.Value = null;
    }
}

internal static class DialogLayout
{
    public static Control Create(
        IReadOnlyList<(string Label, Control Input, Control? Extra)> rows,
        string note,
        Button accept,
        Button cancel)
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(14),
            ColumnCount = 3,
            RowCount = rows.Count + 2,
            AutoScroll = true,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            var label = new Label { Text = row.Label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 7, 12, 3) };
            row.Input.Margin = new Padding(0, 3, 6, 3);
            layout.Controls.Add(label, 0, index);
            layout.Controls.Add(row.Input, 1, index);
            if (row.Extra is not null)
                layout.Controls.Add(row.Extra, 2, index);
        }
        var noteLabel = new Label { Text = note, AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(620, 0), Margin = new Padding(0, 8, 0, 8) };
        layout.Controls.Add(noteLabel, 0, rows.Count);
        layout.SetColumnSpan(noteLabel, 3);
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(accept);
        layout.Controls.Add(buttons, 0, rows.Count + 1);
        layout.SetColumnSpan(buttons, 3);
        return layout;
    }

    public static TextBox EditableText(string value) => new() { Text = value, Dock = DockStyle.Fill };

    public static TextBox ReadOnlyText(string value) => new() { Text = value, ReadOnly = true, Dock = DockStyle.Fill };
}
