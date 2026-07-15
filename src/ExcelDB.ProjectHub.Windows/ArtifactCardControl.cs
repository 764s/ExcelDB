using ExcelDb.Pipeline;

namespace ExcelDb.ProjectHub.Windows;

public sealed class ArtifactCardControl : GroupBox
{
    private readonly Label _status = new();
    private readonly Label _path = new();
    private readonly Label _count = new();
    private readonly Label _external = new();
    private readonly Button _open = new();
    private string? _artifactPath;

    public ArtifactCardControl(string artifactName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactName);
        ArtifactName = artifactName;
        Text = artifactName;
        AccessibleName = "ArtifactCard";
        Padding = new Padding(12);
        Margin = new Padding(8);
        Dock = DockStyle.Fill;
        MinimumSize = new Size(300, 140);

        _status.AutoSize = true;
        _status.Font = new Font(Font, FontStyle.Bold);
        _path.AutoEllipsis = true;
        _path.Dock = DockStyle.Fill;
        _count.AutoSize = true;
        _external.AutoSize = true;
        _external.ForeColor = Color.DarkOrange;
        _open.Text = "打开目录";
        _open.AutoSize = true;
        _open.Enabled = false;
        _open.Click += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 4,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.Controls.Add(_status, 0, 0);
        layout.SetColumnSpan(_status, 2);
        layout.Controls.Add(_path, 0, 1);
        layout.SetColumnSpan(_path, 2);
        layout.Controls.Add(_count, 0, 2);
        layout.Controls.Add(_external, 1, 2);
        layout.Controls.Add(_open, 1, 3);
        Controls.Add(layout);
        ResetInspection();
    }

    public string ArtifactName { get; }

    public string? ArtifactPath => _artifactPath;

    public event EventHandler? OpenRequested;

    public void UpdateInspection(ProjectArtifactInspection inspection)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        _artifactPath = inspection.Path;
        _status.Text = inspection.Status;
        _status.ForeColor = StatusColor(inspection.Status);
        _path.Text = inspection.Path;
        _path.AccessibleDescription = inspection.Path;
        _count.Text = $"文件数：{inspection.ItemCount}";
        _external.Text = inspection.IsExternal ? "外部目录" : string.Empty;
        _open.Enabled = Directory.Exists(inspection.Path);
    }

    public void ResetInspection()
    {
        _artifactPath = null;
        _status.Text = "尚未选择项目";
        _status.ForeColor = SystemColors.GrayText;
        _path.Text = "—";
        _count.Text = "文件数：0";
        _external.Text = string.Empty;
        _open.Enabled = false;
    }

    private static Color StatusColor(string status)
    {
        if (status.Contains("error", StringComparison.OrdinalIgnoreCase)
            || status.Contains("conflict", StringComparison.OrdinalIgnoreCase)
            || status.Contains("invalid", StringComparison.OrdinalIgnoreCase))
        {
            return Color.Firebrick;
        }
        if (status.Contains("ready", StringComparison.OrdinalIgnoreCase)
            || status.Contains("ok", StringComparison.OrdinalIgnoreCase))
        {
            return Color.DarkGreen;
        }
        return Color.DarkGoldenrod;
    }
}
