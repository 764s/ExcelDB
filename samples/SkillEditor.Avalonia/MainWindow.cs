using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using ExcelDb;
using SkillEditor.Avalonia.ViewModels;

namespace SkillEditor.Avalonia;

public sealed class MainWindow : Window
{
    const double NodeWidth = 180;
    const double NodeHeight = 78;

    readonly BehaviorTreeDocument _document = new();
    readonly TextBox _pathBox = new();
    readonly ListBox _treeList = new();
    readonly Canvas _canvas = new();
    readonly TextBox _searchBox = new();
    readonly StackPanel _inspector = new();
    readonly ListBox _issues = new();
    readonly TextBlock _status = new();

    Point? _dragStart;
    BehaviorNodeViewModel? _dragNode;
    Point _dragNodeStart;
    double _zoom = 1;
    bool _dragMoved;
    bool _forceClose;

    public MainWindow()
    {
        Title = "ExcelDB Behavior Tree Editor";
        Width = 1320;
        Height = 820;
        MinWidth = 1040;
        MinHeight = 680;
        Background = Brush.Parse("#1f2329");

        _document.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(BehaviorTreeDocument.Status))
                _status.Text = _document.Status;
            if (e.PropertyName == nameof(BehaviorTreeDocument.SelectedNode))
                RebuildInspector();
        };
        _document.Nodes.CollectionChanged += (_, _) => RebuildCanvas();
        _document.Trees.CollectionChanged += (_, _) => _treeList.SelectedItem = _document.SelectedTree;
        _document.Issues.CollectionChanged += (_, _) => _issues.ItemsSource = _document.Issues;
        Closing += async (_, e) =>
        {
            if (_forceClose || !_document.HasDirtyTables)
                return;

            e.Cancel = true;
            if (await ConfirmDiscardUnsavedAsync())
            {
                _forceClose = true;
                Close();
            }
        };

        Content = BuildLayout();
        _status.Text = _document.Status;
        RebuildCanvas();
        RebuildInspector();
    }

    Control BuildLayout()
    {
        var root = new DockPanel();

        var toolbar = BuildToolbar();
        DockPanel.SetDock(toolbar, Dock.Top);
        root.Children.Add(toolbar);

        var footer = BuildFooter();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        var body = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("260,*,330"),
        };

        body.Children.Add(BuildSidebar());

        var canvasPanel = BuildCanvasPanel();
        Grid.SetColumn(canvasPanel, 1);
        body.Children.Add(canvasPanel);

        var inspectorPanel = BuildInspectorPanel();
        Grid.SetColumn(inspectorPanel, 2);
        body.Children.Add(inspectorPanel);

        root.Children.Add(body);
        return root;
    }

    Control BuildToolbar()
    {
        _pathBox.Text = _document.WorkbookPath;
        _pathBox.Width = 380;
        _pathBox.PlaceholderText = "Workbook path";
        _pathBox.Foreground = Brush.Parse("#e5e7eb");
        _pathBox.Background = Brush.Parse("#111827");
        _pathBox.TextChanged += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(_pathBox.Text))
                _document.WorkbookPath = _pathBox.Text;
        };
        _searchBox.Width = 180;
        _searchBox.PlaceholderText = "Find node";
        _searchBox.Foreground = Brush.Parse("#e5e7eb");
        _searchBox.Background = Brush.Parse("#111827");
        _searchBox.KeyUp += (_, e) =>
        {
            if (e.Key == Key.Enter)
                SearchNode();
        };

        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(10),
            VerticalAlignment = VerticalAlignment.Center,
        };

        toolbar.Children.Add(Label("Behavior Tree"));
        toolbar.Children.Add(_pathBox);
        toolbar.Children.Add(Button("Browse", () => RunAsync(BrowseWorkbook)));
        toolbar.Children.Add(Button("New Sample", () => RunAsync(CreateSampleWorkbook)));
        toolbar.Children.Add(Button("Open", () => RunAsync(OpenWorkbookFromPath)));
        toolbar.Children.Add(Button("Save", () => Run(_document.Save)));
        toolbar.Children.Add(Separator());
        toolbar.Children.Add(Button("Undo", () => Run(_document.Undo)));
        toolbar.Children.Add(Button("Redo", () => Run(_document.Redo)));
        toolbar.Children.Add(Separator());
        toolbar.Children.Add(Button("Validate", () => Run(_document.Validate)));
        toolbar.Children.Add(Separator());
        toolbar.Children.Add(_searchBox);
        toolbar.Children.Add(Button("Find", SearchNode));
        toolbar.Children.Add(Separator());
        toolbar.Children.Add(Button("Zoom -", () => AdjustZoom(-0.1)));
        toolbar.Children.Add(Button("Zoom +", () => AdjustZoom(0.1)));

        return new Border
        {
            Background = Brush.Parse("#2b3038"),
            BorderBrush = Brush.Parse("#3f4652"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = toolbar,
        };
    }

    Control BuildSidebar()
    {
        _treeList.ItemsSource = _document.Trees;
        _treeList.Background = Brush.Parse("#252a31");
        _treeList.Foreground = Brush.Parse("#e5e7eb");
        _treeList.SelectionChanged += (_, _) =>
        {
            _document.SelectedTree = _treeList.SelectedItem as BehaviorTreeAssetViewModel;
            RebuildCanvas();
            RebuildInspector();
        };

        var panel = new StackPanel
        {
            Margin = new Thickness(10, 10, 6, 10),
            Spacing = 12,
        };

        panel.Children.Add(PanelCard("Trees", _treeList, 220));
        panel.Children.Add(PanelCard("Tree Tools", BuildTreeTools(), 140));
        panel.Children.Add(PanelCard("Create Node", BuildPalette(), 180));
        return panel;
    }

    Control BuildTreeTools()
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(Button("New Tree", () => Run(_document.CreateTree)));
        panel.Children.Add(Button("Duplicate Tree", () => Run(_document.DuplicateSelectedTree)));
        panel.Children.Add(Button("Delete Tree", () => RunAsync(DeleteSelectedTreeConfirmed)));
        return panel;
    }

    Control BuildPalette()
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(PaletteButton("Selector", "#60a5fa"));
        panel.Children.Add(PaletteButton("Sequence", "#a78bfa"));
        panel.Children.Add(PaletteButton("Condition", "#fbbf24"));
        panel.Children.Add(PaletteButton("Action", "#34d399"));
        panel.Children.Add(PaletteButton("Decorator", "#94a3b8"));
        return panel;
    }

    Control BuildCanvasPanel()
    {
        _canvas.Width = 1200;
        _canvas.Height = 900;
        _canvas.Background = Brush.Parse("#1b1f25");
        _canvas.RenderTransformOrigin = RelativePoint.TopLeft;
        ApplyZoom();

        return new Border
        {
            Margin = new Thickness(0, 10, 6, 10),
            BorderBrush = Brush.Parse("#3f4652"),
            BorderThickness = new Thickness(1),
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = _canvas,
            },
        };
    }

    Control BuildInspectorPanel()
    {
        var panel = new DockPanel
        {
            Margin = new Thickness(0, 10, 10, 10),
            Background = Brush.Parse("#252a31"),
        };

        var issuesPanel = new DockPanel
        {
            MinHeight = 170,
            LastChildFill = true,
        };
        DockPanel.SetDock(issuesPanel, Dock.Bottom);
        issuesPanel.Children.Add(Header("Validation"));
        _issues.ItemsSource = _document.Issues;
        _issues.Background = Brush.Parse("#1f2329");
        _issues.Foreground = Brush.Parse("#fca5a5");
        issuesPanel.Children.Add(_issues);

        panel.Children.Add(issuesPanel);
        panel.Children.Add(new ScrollViewer
        {
            Content = _inspector,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        });

        return panel;
    }

    Control BuildFooter()
    {
        _status.Margin = new Thickness(10, 6);
        _status.Foreground = Brush.Parse("#d1d5db");
        return new Border
        {
            Background = Brush.Parse("#171a20"),
            BorderBrush = Brush.Parse("#3f4652"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = _status,
        };
    }

    void RebuildCanvas()
    {
        _canvas.Children.Clear();
        ResizeCanvasToContent();
        AddGridBackground();

        foreach (var edge in _document.Edges())
            _canvas.Children.Add(EdgeLine(edge.Parent, edge.Child));

        foreach (var node in _document.Nodes)
            _canvas.Children.Add(NodeCard(node));

        if (_document.Nodes.Count == 0)
        {
            _canvas.Children.Add(new TextBlock
            {
                Text = _document.HasWorkbook
                    ? "No behavior nodes in this tree."
                    : "Create or open a workbook to load behavior tree nodes.",
                Foreground = Brush.Parse("#9ca3af"),
                FontSize = 18,
            });
            Canvas.SetLeft(_canvas.Children[^1], 40);
            Canvas.SetTop(_canvas.Children[^1], 40);
        }
    }

    void ResizeCanvasToContent()
    {
        var maxX = _document.Nodes.Count == 0
            ? 1200
            : _document.Nodes.Max(node => node.X + NodeWidth + 260);
        var maxY = _document.Nodes.Count == 0
            ? 900
            : _document.Nodes.Max(node => node.Y + NodeHeight + 220);
        _canvas.Width = Math.Max(1200, maxX);
        _canvas.Height = Math.Max(900, maxY);
    }

    void AddGridBackground()
    {
        for (var x = 0; x <= _canvas.Width; x += 40)
        {
            _canvas.Children.Add(new Line
            {
                StartPoint = new Point(x, 0),
                EndPoint = new Point(x, _canvas.Height),
                Stroke = Brush.Parse("#252a31"),
                StrokeThickness = 1,
            });
        }

        for (var y = 0; y <= _canvas.Height; y += 40)
        {
            _canvas.Children.Add(new Line
            {
                StartPoint = new Point(0, y),
                EndPoint = new Point(_canvas.Width, y),
                Stroke = Brush.Parse("#252a31"),
                StrokeThickness = 1,
            });
        }
    }

    Control NodeCard(BehaviorNodeViewModel node)
    {
        var headerBrush = Brush.Parse(NodeColor(node.Kind));
        var borderBrush = node.IsSelected ? Brush.Parse("#f59e0b") : Brush.Parse("#4b5563");
        var card = new Border
        {
            Width = NodeWidth,
            Height = NodeHeight,
            CornerRadius = new CornerRadius(7),
            Background = Brush.Parse("#2d333d"),
            BorderBrush = borderBrush,
            BorderThickness = node.IsSelected ? new Thickness(2) : new Thickness(1),
            Child = new StackPanel
            {
                Children =
                {
                    new Border
                    {
                        Height = 24,
                        CornerRadius = new CornerRadius(7, 7, 0, 0),
                        Background = headerBrush,
                        Child = new TextBlock
                        {
                            Text = node.Kind,
                            Foreground = Brushes.White,
                            FontWeight = FontWeight.SemiBold,
                            Margin = new Thickness(8, 3),
                        },
                    },
                    new TextBlock
                    {
                        Text = node.Title,
                        Foreground = Brush.Parse("#f9fafb"),
                        FontWeight = FontWeight.SemiBold,
                        Margin = new Thickness(8, 7, 8, 0),
                    },
                    new TextBlock
                    {
                        Text = string.IsNullOrWhiteSpace(node.Action) ? node.Key : node.Action,
                        Foreground = Brush.Parse("#9ca3af"),
                        FontSize = 12,
                        Margin = new Thickness(8, 1, 8, 0),
                    },
                },
            },
        };

        card.PointerPressed += (sender, e) =>
        {
            _document.SelectedNode = node;
            _dragStart = e.GetPosition(_canvas);
            _dragNodeStart = new Point(node.X, node.Y);
            _dragNode = node;
            _dragMoved = false;
            e.Pointer.Capture((Control)sender!);
            card.BorderBrush = Brush.Parse("#f59e0b");
            card.BorderThickness = new Thickness(2);
            RebuildInspector();
        };
        card.PointerMoved += (_, e) =>
        {
            if (_dragStart == null || _dragNode != node)
                return;

            var current = e.GetPosition(_canvas);
            var delta = current - _dragStart.Value;
            if (Math.Abs(delta.X) > 2 || Math.Abs(delta.Y) > 2)
                _dragMoved = true;
            node.X = Math.Max(20, _dragNodeStart.X + delta.X);
            node.Y = Math.Max(20, _dragNodeStart.Y + delta.Y);
            Canvas.SetLeft(card, node.X);
            Canvas.SetTop(card, node.Y);
        };
        card.PointerReleased += (sender, e) =>
        {
            if (_dragNode == node && _dragMoved)
                Run(() => _document.CommitNodePosition(node.Id, node.X, node.Y));

            _dragStart = null;
            _dragNode = null;
            _dragMoved = false;
            e.Pointer.Capture(null);
            RebuildCanvas();
        };

        Canvas.SetLeft(card, node.X);
        Canvas.SetTop(card, node.Y);
        return card;
    }

    static Line EdgeLine(BehaviorNodeViewModel parent, BehaviorNodeViewModel child) =>
        new()
        {
            StartPoint = new Point(parent.X + NodeWidth / 2, parent.Y + NodeHeight),
            EndPoint = new Point(child.X + NodeWidth / 2, child.Y),
            Stroke = Brush.Parse("#6b7280"),
            StrokeThickness = 2,
        };

    void RebuildInspector()
    {
        _inspector.Children.Clear();
        _inspector.Margin = new Thickness(12);
        _inspector.Spacing = 10;

        _inspector.Children.Add(Header("Inspector"));

        var tree = _document.SelectedTree;
        if (tree != null)
        {
            _inspector.Children.Add(Field("Tree Key", tree.Key, readOnly: true, _ => { }));
            _inspector.Children.Add(Field("Tree Title", tree.Title, readOnly: false, _document.RenameSelectedTree));
        }

        var node = _document.SelectedNode;
        if (node == null)
        {
            _inspector.Children.Add(HelpText("Select a node on the graph to edit its behavior-tree properties."));
            return;
        }

        var nodeId = node.Id;
        _inspector.Children.Add(Field("Key", node.Key, readOnly: true, _ => { }));
        _inspector.Children.Add(Field("Title", node.Title, readOnly: false, value => _document.SetNodeTitle(nodeId, value)));
        _inspector.Children.Add(ComboField("Kind", _document.NodeKinds, node.Kind, value => _document.SetNodeKind(nodeId, value)));
        _inspector.Children.Add(ComboField("Action", ActionChoices(node), node.Action, value => _document.SetNodeAction(nodeId, value)));
        _inspector.Children.Add(ParentField(node));
        _inspector.Children.Add(Field("Position", ((int)node.X) + ", " + ((int)node.Y), readOnly: true, _ => { }));
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(Button("Up", () => Run(() => _document.MoveSelectedNodeInOrder(-1))));
        row.Children.Add(Button("Down", () => Run(() => _document.MoveSelectedNodeInOrder(1))));
        _inspector.Children.Add(row);
        _inspector.Children.Add(Button("Duplicate Subtree", () => Run(_document.DuplicateSelectedSubtree)));
        _inspector.Children.Add(Button("Detach Node", () => Run(_document.DetachSelectedNode)));
        _inspector.Children.Add(Button("Delete Subtree", () => Run(_document.DeleteSelectedSubtree)));
    }

    IReadOnlyList<string> ActionChoices(BehaviorNodeViewModel node)
    {
        var choices = _document.ActionOptionsFor(node.Kind).ToList();
        if (!string.IsNullOrWhiteSpace(node.Action) && !choices.Contains(node.Action, StringComparer.Ordinal))
            choices.Insert(0, node.Action);
        if (!choices.Contains(string.Empty, StringComparer.Ordinal))
            choices.Insert(0, string.Empty);
        return choices;
    }

    Control Field(string label, string value, bool readOnly, Action<string> apply)
    {
        var box = new TextBox
        {
            Text = value,
            IsReadOnly = readOnly,
            Foreground = Brush.Parse("#f9fafb"),
            Background = readOnly ? Brush.Parse("#1f2329") : Brush.Parse("#111827"),
            BorderBrush = Brush.Parse("#4b5563"),
        };
        box.LostFocus += (_, _) =>
        {
            if (!readOnly)
                Run(() => apply(box.Text ?? string.Empty));
        };

        return new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock
                {
                    Text = label,
                    Foreground = Brush.Parse("#9ca3af"),
                    FontSize = 12,
                },
                box,
            },
        };
    }

    Control ComboField(string label, IReadOnlyList<string> values, string value, Action<string> apply)
    {
        var combo = new ComboBox
        {
            ItemsSource = values,
            SelectedItem = value,
            Foreground = Brush.Parse("#f9fafb"),
            Background = Brush.Parse("#111827"),
            BorderBrush = Brush.Parse("#4b5563"),
            MinHeight = 30,
        };
        var ready = false;
        combo.SelectionChanged += (_, _) =>
        {
            if (ready && combo.SelectedItem is string selected)
                Run(() => apply(selected));
        };
        ready = true;

        return new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock
                {
                    Text = label,
                    Foreground = Brush.Parse("#9ca3af"),
                    FontSize = 12,
                },
                combo,
            },
        };
    }

    Control ParentField(BehaviorNodeViewModel node)
    {
        var currentParent = _document.ParentOf(node.Id);
        var options = _document.ParentCandidates(node.Id)
            .Select(candidate => new ParentOption(candidate))
            .ToList();
        var selected = options.FirstOrDefault(option => currentParent.HasValue && option.Node.Id == currentParent.Value);
        var combo = new ComboBox
        {
            ItemsSource = options,
            SelectedItem = selected,
            Foreground = Brush.Parse("#f9fafb"),
            Background = Brush.Parse("#111827"),
            BorderBrush = Brush.Parse("#4b5563"),
            MinHeight = 30,
            IsEnabled = options.Count > 0,
        };
        var ready = false;
        combo.SelectionChanged += (_, _) =>
        {
            if (ready && combo.SelectedItem is ParentOption option)
                Run(() => _document.ReparentSelectedNode(option.Node.Id));
        };
        ready = true;

        return new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock
                {
                    Text = "Parent",
                    Foreground = Brush.Parse("#9ca3af"),
                    FontSize = 12,
                },
                combo,
            },
        };
    }

    async Task BrowseWorkbook()
    {
        if (!await ConfirmDiscardUnsavedAsync())
            return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open behavior tree workbook",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Excel workbook")
                {
                    Patterns = new[] { "*.xlsx" },
                    AppleUniformTypeIdentifiers = new[] { "org.openxmlformats.spreadsheetml.sheet" },
                    MimeTypes = new[] { "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" },
                },
            },
        });

        if (files.Count == 0)
            return;

        _pathBox.Text = files[0].Path.LocalPath;
        _document.WorkbookPath = files[0].Path.LocalPath;
        _document.OpenWorkbook();
    }

    async Task OpenWorkbookFromPath()
    {
        if (!await ConfirmDiscardUnsavedAsync())
            return;

        _document.OpenWorkbook();
    }

    async Task CreateSampleWorkbook()
    {
        if (!await ConfirmDiscardUnsavedAsync())
            return;

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Create behavior tree workbook",
            SuggestedFileName = "behavior-trees.xlsx",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("Excel workbook")
                {
                    Patterns = new[] { "*.xlsx" },
                    AppleUniformTypeIdentifiers = new[] { "org.openxmlformats.spreadsheetml.sheet" },
                    MimeTypes = new[] { "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" },
                },
            },
        });

        if (file == null)
            return;

        _pathBox.Text = file.Path.LocalPath;
        _document.WorkbookPath = file.Path.LocalPath;
        _document.CreateSampleWorkbook();
    }

    async Task DeleteSelectedTreeConfirmed()
    {
        if (!await ConfirmAsync("Delete Tree", "Delete the selected behavior tree and all of its owned nodes?", "Delete", "Cancel"))
            return;

        _document.DeleteSelectedTree();
    }

    async Task<bool> ConfirmDiscardUnsavedAsync()
    {
        if (!_document.HasDirtyTables)
            return true;

        return await ConfirmAsync("Unsaved Changes", "Discard unsaved workbook changes?", "Discard", "Cancel");
    }

    async Task<bool> ConfirmAsync(string title, string message, string acceptText, string cancelText)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 420,
            Height = 170,
            MinWidth = 420,
            MinHeight = 170,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brush.Parse("#252a31"),
            Content = new DockPanel
            {
                Margin = new Thickness(18),
                LastChildFill = true,
                Children =
                {
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children =
                        {
                            Button(cancelText, () => { }),
                            Button(acceptText, () => { }),
                        },
                    },
                    new TextBlock
                    {
                        Text = message,
                        Foreground = Brush.Parse("#f9fafb"),
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 15,
                    },
                },
            },
        };

        var buttons = ((StackPanel)((DockPanel)dialog.Content!).Children[0]);
        ((Button)buttons.Children[0]).Click += (_, _) => dialog.Close(false);
        ((Button)buttons.Children[1]).Click += (_, _) => dialog.Close(true);
        return await dialog.ShowDialog<bool>(this);
    }

    void SearchNode()
    {
        var query = _searchBox.Text;
        if (string.IsNullOrWhiteSpace(query))
            return;

        var match = _document.Nodes.FirstOrDefault(node =>
            node.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            node.Key.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            node.Action.Contains(query, StringComparison.OrdinalIgnoreCase));
        if (match == null)
            return;

        _document.SelectedNode = match;
        RebuildCanvas();
        RebuildInspector();
    }

    void AdjustZoom(double delta)
    {
        _zoom = Math.Max(0.5, Math.Min(1.75, _zoom + delta));
        ApplyZoom();
    }

    void ApplyZoom()
    {
        _canvas.RenderTransform = new ScaleTransform(_zoom, _zoom);
    }

    void Run(Action action)
    {
        try
        {
            action();
            _treeList.ItemsSource = _document.Trees;
            _treeList.SelectedItem = _document.SelectedTree;
            RebuildCanvas();
            RebuildInspector();
        }
        catch (Exception ex)
        {
            _document.ReportError(ex);
        }
    }

    async void RunAsync(Func<Task> action)
    {
        try
        {
            await action();
            _treeList.ItemsSource = _document.Trees;
            _treeList.SelectedItem = _document.SelectedTree;
            RebuildCanvas();
            RebuildInspector();
        }
        catch (Exception ex)
        {
            _document.ReportError(ex);
        }
    }

    static Control PanelCard(string title, Control child, double minHeight) =>
        new Border
        {
            Background = Brush.Parse("#252a31"),
            BorderBrush = Brush.Parse("#3f4652"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10),
            Child = new DockPanel
            {
                MinHeight = minHeight,
                LastChildFill = true,
                Children =
                {
                    Header(title),
                    child,
                },
            },
        };

    static TextBlock Header(string text)
    {
        var header = new TextBlock
        {
            Text = text,
            Foreground = Brush.Parse("#f9fafb"),
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 0, 0, 10),
        };
        DockPanel.SetDock(header, Dock.Top);
        return header;
    }

    static TextBlock Label(string text) =>
        new()
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush.Parse("#f9fafb"),
            FontWeight = FontWeight.SemiBold,
        };

    static TextBlock HelpText(string text) =>
        new()
        {
            Text = text,
            Foreground = Brush.Parse("#9ca3af"),
            TextWrapping = TextWrapping.Wrap,
        };

    Button PaletteButton(string kind, string color)
    {
        var button = Button("+ " + kind, () => Run(() => _document.AddNode(kind)));
        button.BorderBrush = Brush.Parse(color);
        return button;
    }

    static Button Button(string text, Action action)
    {
        var button = new Button
        {
            Content = text,
            MinHeight = 30,
            Padding = new Thickness(10, 4),
            Foreground = Brush.Parse("#f9fafb"),
            Background = Brush.Parse("#374151"),
            BorderBrush = Brush.Parse("#4b5563"),
        };
        button.Click += (_, _) => action();
        return button;
    }

    static Separator Separator() =>
        new()
        {
            Width = 1,
            Margin = new Thickness(4, 2),
            Background = Brush.Parse("#4b5563"),
        };

    static string NodeColor(string kind) =>
        kind switch
        {
            "Selector" => "#2563eb",
            "Sequence" => "#7c3aed",
            "Condition" => "#d97706",
            "Action" => "#059669",
            "Decorator" => "#64748b",
            _ => "#4b5563",
        };

    sealed class ParentOption
    {
        public ParentOption(BehaviorNodeViewModel node) => Node = node;

        public BehaviorNodeViewModel Node { get; }

        public override string ToString() => Node.Title + "  (" + Node.Key + ")";
    }
}
