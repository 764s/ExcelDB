using ExcelDb.Pipeline;
using ExcelDb.Schema.Authoring;
using Xunit;

namespace ExcelDb.ProjectHub.Windows.Tests;

public sealed class HubDialogTests
{
    [Fact]
    public void Create_model_projects_internal_package_keys_and_explicit_targets()
    {
        var intent = TableDialogModel.CreateIntent(
            "Hero",
            "Excel/game.xlsx",
            autoKey: true,
            DialogTargetChoice.ClientAndServer,
            [
                new DialogFieldInput("id", SimpleFieldType.String, IsKey: true, Target: DialogTargetChoice.Server),
                new DialogFieldInput("name", SimpleFieldType.String),
                new DialogFieldInput("client_note", SimpleFieldType.String, Target: DialogTargetChoice.Client),
                new DialogFieldInput("rarity", SimpleFieldType.Enum, "game.configs.Rarity"),
            ]);

        Assert.Equal("game.configs", intent.Package);
        Assert.Equal("Excel/game.xlsx", intent.WorkbookPath);
        Assert.False(intent.AutoKey);
        Assert.True(intent.Fields[0].IsKey);
        Assert.Equal("game.configs.Rarity", intent.Fields.Single(static field => field.Name == "rarity").EnumType);
        Assert.Equal(new[] { "client", "server" }, intent.TableTargets!.Targets);
        var fieldTargets = intent.FieldTargets!;
        Assert.Equal(new[] { "server" }, fieldTargets["id"].Targets);
        Assert.Equal(new[] { "client" }, fieldTargets["client_note"].Targets);
        Assert.False(fieldTargets.ContainsKey("name"));
    }

    [Fact]
    public void Automatic_targets_remain_unset_for_the_csharp_strategy()
    {
        var intent = TableDialogModel.CreateIntent(
            "Hero",
            "Excel/game.xlsx",
            autoKey: true,
            DialogTargetChoice.Automatic,
            [new DialogFieldInput("name", SimpleFieldType.String)]);

        Assert.True(intent.AutoKey);
        Assert.Null(intent.TableTargets);
        Assert.NotNull(intent.FieldTargets);
        Assert.Empty(intent.FieldTargets!);
    }

    [Fact]
    public void Edit_model_creates_typed_additions_without_raw_field_declarations()
    {
        var additions = TableDialogModel.CreateAdditions(
        [
            new DialogFieldInput("power", SimpleFieldType.Int32),
            new DialogFieldInput("enabled", SimpleFieldType.Boolean),
            new DialogFieldInput("rarity", SimpleFieldType.Enum, "game.configs.Rarity"),
        ]);

        Assert.Collection(
            additions,
            addition =>
            {
                Assert.Equal("power", addition.Definition.Name);
                Assert.Equal(SimpleFieldType.Int32, addition.Definition.Type);
                Assert.Null(addition.ExportTargets);
            },
            addition =>
            {
                Assert.Equal("enabled", addition.Definition.Name);
                Assert.Equal(SimpleFieldType.Boolean, addition.Definition.Type);
                Assert.Null(addition.ExportTargets);
            },
            addition =>
            {
                Assert.Equal("rarity", addition.Definition.Name);
                Assert.Equal(SimpleFieldType.Enum, addition.Definition.Type);
                Assert.Equal("game.configs.Rarity", addition.Definition.EnumType);
                Assert.Null(addition.ExportTargets);
            });
    }

    [Fact]
    public void Enum_type_metadata_is_required_only_for_enum_fields()
    {
        Assert.Throws<ArgumentException>(() => TableDialogModel.CreateIntent(
            "Hero",
            "Excel/game.xlsx",
            autoKey: true,
            DialogTargetChoice.Automatic,
            [new DialogFieldInput("rarity", SimpleFieldType.Enum)]));
        Assert.Throws<ArgumentException>(() => TableDialogModel.CreateIntent(
            "Hero",
            "Excel/game.xlsx",
            autoKey: true,
            DialogTargetChoice.Automatic,
            [new DialogFieldInput("name", SimpleFieldType.String, "game.configs.Rarity")]));
    }

    [Fact]
    public void Create_dialog_uses_a_structured_grid_and_links_explicit_key_to_auto_key()
    {
        RunSta(() =>
        {
            using var dialog = new CreateTableDialog("Excel/game.xlsx");
            var grid = Assert.Single(Descendants(dialog).OfType<DataGridView>());
            Assert.Equal(new[] { "字段名", "类型", "枚举类型", "Key", "Target" }, grid.Columns.Cast<DataGridViewColumn>().Select(static column => column.HeaderText));
            var textBoxes = Descendants(dialog).OfType<TextBox>().ToArray();
            Assert.DoesNotContain(textBoxes, static textBox => textBox.Multiline);
            Assert.Equal(2, textBoxes.Length);
            Assert.Contains(textBoxes, static textBox => textBox.Text == "Excel/game.xlsx");
            Assert.DoesNotContain(
                Descendants(dialog),
                static control => control.Text.Contains("Proto package", StringComparison.OrdinalIgnoreCase));
            var tableTarget = Assert.Single(Descendants(dialog).OfType<ComboBox>());
            Assert.Equal(new[] { "自动", "client", "server", "client+server" }, tableTarget.Items.Cast<string>());
            var targetColumn = Assert.IsType<DataGridViewComboBoxColumn>(grid.Columns["Target"]);
            Assert.Equal(new[] { "自动", "client", "server", "client+server" }, targetColumn.Items.Cast<string>());
            var typeColumn = Assert.IsType<DataGridViewComboBoxColumn>(grid.Columns["FieldType"]);
            Assert.Contains("enum", typeColumn.Items.Cast<string>());

            var row = grid.Rows.Cast<DataGridViewRow>().Single(static item => !item.IsNewRow);
            var autoKey = Assert.Single(Descendants(dialog).OfType<CheckBox>());
            Assert.True(autoKey.Checked);
            textBoxes.Single(static textBox => textBox.Name == "CreateTableName").Text = "Hero";
            tableTarget.SelectedItem = "client+server";
            row.Cells["Target"].Value = "server";
            Assert.True(row.Cells["EnumType"].ReadOnly);
            row.Cells["FieldType"].Value = "enum";
            Assert.False(row.Cells["EnumType"].ReadOnly);
            row.Cells["EnumType"].Value = "game.configs.Rarity";
            row.Cells["FieldType"].Value = "string";
            Assert.True(row.Cells["EnumType"].ReadOnly);
            Assert.Null(row.Cells["EnumType"].Value);
            row.Cells["Key"].Value = true;
            Assert.False(autoKey.Checked);
            var intent = dialog.CreateIntent();
            Assert.Equal("game.configs", intent.Package);
            Assert.False(intent.AutoKey);
            Assert.Equal(new[] { "client", "server" }, intent.TableTargets!.Targets);
            Assert.Equal(new[] { "server" }, intent.FieldTargets!["name"].Targets);
            autoKey.Checked = true;
            Assert.False((bool)row.Cells["Key"].Value!);
        });
    }

    [Fact]
    public void Create_dialog_uses_the_workbook_default_projected_from_the_current_excel_directory()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "exceldb-hub-default"));
        var defaultWorkbook = ProjectArtifactPaths.GetDefaultWorkbookPath(root, Path.Combine(root, "Tables"));

        Assert.Equal("Tables/game.xlsx", defaultWorkbook);
        RunSta(() =>
        {
            using var dialog = new CreateTableDialog(defaultWorkbook);
            var workbook = Descendants(dialog).OfType<TextBox>()
                .Single(static textBox => textBox.Name == "CreateWorkbook");
            Assert.Equal("Tables/game.xlsx", workbook.Text);
        });
    }

    [Fact]
    public void Edit_dialog_uses_a_structured_name_and_type_grid()
    {
        RunSta(() =>
        {
            using var dialog = new EditTableDialog();
            var grid = Assert.Single(Descendants(dialog).OfType<DataGridView>());
            Assert.Equal(new[] { "字段名", "类型", "枚举类型" }, grid.Columns.Cast<DataGridViewColumn>().Select(static column => column.HeaderText));
            Assert.DoesNotContain(Descendants(dialog).OfType<TextBox>(), static textBox => textBox.Multiline);
            var typeColumn = Assert.IsType<DataGridViewComboBoxColumn>(grid.Columns["FieldType"]);
            Assert.Contains("enum", typeColumn.Items.Cast<string>());
            var textBoxes = Descendants(dialog).OfType<TextBox>().ToArray();
            Assert.Equal(3, textBoxes.Length);
            textBoxes.Single(static textBox => textBox.Name == "EditTableFullName").Text = "game.configs.Hero";
            grid.Rows.Add("rarity", "enum", "game.configs.Rarity");

            var intent = dialog.CreateIntent();

            var addition = Assert.Single(intent.AddFields);
            Assert.Equal(SimpleFieldType.Enum, addition.Definition.Type);
            Assert.Equal("game.configs.Rarity", addition.Definition.EnumType);
        });
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child))
                yield return descendant;
        }
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "STA dialog test timed out.");
        if (failure is not null)
            throw new AggregateException(failure);
    }
}
