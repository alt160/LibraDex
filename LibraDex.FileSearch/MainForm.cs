using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace LibraDex.FileSearch;

internal sealed class MainForm : Form
{
    private const int LogViewerMaxBytes = 256 * 1024;
    private const int BusyLogRefreshIntervalMs = 1000;
    private const int BusyLogUiUpdateIntervalMs = 500;
    private const int ReindexProgressUiUpdateIntervalMs = 250;
    private const int VirtualGridThreshold = 3000;
    private const int EmGetFirstVisibleLine = 0x00CE;
    private const int EmLineScroll = 0x00B6;

    private static readonly JsonSerializerOptions UiJsonOptions = new(JsonSerializerDefaults.General)
    {
        WriteIndented = true
    };

    private readonly BindingList<QueryRow> queryRows = new();
    private readonly BindingList<FileRecord> resultRows = new();
    private readonly BindingList<FileSearchTupleRow> resultTupleRows = new();
    private readonly BindingList<FileSearchIdentityRow> resultIdentityRows = new();
    private readonly BindingList<FileRecord> indexFileRows = new();
    private readonly BindingList<FileSearchTupleRow> indexTupleRows = new();
    private IReadOnlyList<FileRecord>? virtualIndexFileRows;
    private IReadOnlyList<FileRecord>? virtualResultFileRows;
    private readonly ComboBox catalogCombo = new();
    private readonly ListBox rootsList = new();
    private readonly DataGridView queryGrid = new();
    private readonly DataGridView resultsGrid = new();
    private readonly DataGridView indexGrid = new();
    private readonly ComboBox indexBrowseCombo = new();
    private readonly TextBox logViewer = new();
    private readonly TextBox conditionPreview = new();
    private readonly Label activeInstanceLabel = new();
    private readonly Label metricsLabel = new();
    private readonly Label statusLabel = new();
    private readonly Label resourceLabel = new();
    private readonly Label lastLogLabel = new();
    private readonly ProgressBar progressBar = new();
    private readonly System.Windows.Forms.Timer resourceTimer = new() { Interval = 1000 };
    private readonly object logUiGate = new();
    private readonly NumericUpDown skipInput = new();
    private readonly NumericUpDown lengthInput = new();
    private readonly ComboBox identityModeInput = new();
    private readonly ComboBox identityDisplayInput = new();
    private readonly ComboBox keyDisplayInput = new();
    private readonly ComboBox rowHydrationInput = new();
    private readonly ComboBox displayFieldModeInput = new();
    private readonly CheckBox useGroupBatchingInput = new() { Text = "Group batching", AutoSize = true };
    private readonly CheckBox indexExtensionInput = new() { Text = "Index Rile Extensions", AutoSize = true };
    private readonly CheckBox indexStringFoldedInput = new() { Text = "Index folded string keys", AutoSize = true };
    private readonly CheckBox indexStringSortKeyInput = new() { Text = "Index string sort keys", AutoSize = true };
    private readonly CheckBox indexStringReversedInput = new() { Text = "Index reversed string keys", AutoSize = true };
    private readonly NumericUpDown batchCommitFileCountInput = new();
    private readonly ComboBox insertLayoutInput = new();
    private readonly ComboBox writeOrderInput = new();
    private readonly ComboBox writeVolumeInput = new();
    private readonly ComboBox writeLocalityInput = new();
    private readonly ComboBox writePriorityInput = new();
    private readonly CheckBox configPinnedInput = new() { Text = "Pinned", AutoSize = true, Checked = true };
    private readonly List<string> catalogPaths = new();
    private readonly ToolStripDropDownButton newCatalog = new("New") { DisplayStyle = ToolStripItemDisplayStyle.Text };
    private readonly ToolStripButton openCatalog = new("Open") { DisplayStyle = ToolStripItemDisplayStyle.Text };
    private readonly ToolStripButton closeCatalog = new("Close") { DisplayStyle = ToolStripItemDisplayStyle.Text };
    private readonly ToolStripButton switchCatalog = new("Switch") { DisplayStyle = ToolStripItemDisplayStyle.Text };
    private readonly ToolStripButton toggleConfig = new("Config") { DisplayStyle = ToolStripItemDisplayStyle.Text };
    private readonly ToolStripButton addRoot = new("Add Folder") { DisplayStyle = ToolStripItemDisplayStyle.Text };
    private readonly ToolStripButton removeRoot = new("Remove Folder") { DisplayStyle = ToolStripItemDisplayStyle.Text };
    private readonly ToolStripButton resetRoots = new("Reset Folders") { DisplayStyle = ToolStripItemDisplayStyle.Text };
    private readonly ToolStripButton reindex = new("Reindex") { DisplayStyle = ToolStripItemDisplayStyle.Text };
    private readonly ToolStripButton resetDisabledFields = new("Reset Disabled Fields") { DisplayStyle = ToolStripItemDisplayStyle.Text };
    private readonly ToolStripButton addCondition = new("Add Query Row") { DisplayStyle = ToolStripItemDisplayStyle.Text };
    private readonly ToolStripButton runQuery = new("Run Query") { DisplayStyle = ToolStripItemDisplayStyle.Text };
    private readonly ToolStripButton enumerateIndex = new("Enumerate Index") { DisplayStyle = ToolStripItemDisplayStyle.Text };
    private readonly ToolStripButton clearLogAction = new("Clear Log") { DisplayStyle = ToolStripItemDisplayStyle.Text };
    private readonly ToolStripButton runStressCertification = new("Stress Check") { DisplayStyle = ToolStripItemDisplayStyle.Text };
    private readonly Label reindexMetaLabel = CreateMetaLabel();
    private readonly Label resetDisabledMetaLabel = CreateMetaLabel();
    private readonly Label enumerateMetaLabel = CreateMetaLabel();
    private readonly Label queryMetaLabel = CreateMetaLabel();
    private readonly Label stressMetaLabel = CreateMetaLabel();
    private readonly Label clearLogMetaLabel = CreateMetaLabel();
    private readonly Panel configPanel = new() { Dock = DockStyle.Right, Width = 340, Padding = new Padding(8), BackColor = SystemColors.Control };
    private readonly SplitContainer topSplit = new() { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 260 };
    private readonly SplitContainer upperSplit = new() { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterDistance = 360 };
    private readonly TabControl tabs = new() { Dock = DockStyle.Fill };
    private FileSearchCatalog? activeCatalog;
    private CancellationTokenSource? reindexCancellation;
    private readonly Stopwatch resourceWatch = Stopwatch.StartNew();
    private TimeSpan lastResourceCpuTime;
    private long lastResourceTimestamp;
    private long peakWorkingSetBytes;
    private long peakPrivateBytes;
    private long peakGcBytes;
    private long lastBusyLogRefreshTimestamp;
    private long lastBusyLogUiDispatchTimestamp;
    private string lastResourceText = "CPU n/a | RAM n/a";
    private string? pendingBusyLogEntry;
    private bool busy;
    private bool logAutoScroll = true;
    private bool deferredBusyLogRefresh;

    private enum VirtualGridKind
    {
        File,
        Tuple,
        Identity
    }

    private sealed class VirtualGridSource
    {
        internal VirtualGridSource(VirtualGridKind kind, object rows)
        {
            Kind = kind;
            Rows = rows;
        }

        internal VirtualGridKind Kind { get; }

        internal object Rows { get; }
    }

    public MainForm()
    {
        Text = "LibraDex File Search";
        Width = 1280;
        Height = 820;
        StartPosition = FormStartPosition.CenterScreen;
        BuildLayout();
        BindGrids();
        LoadUiState();
        FileSearchLog.EntryWritten += OnLogEntryWritten;
        resourceTimer.Tick += (_, _) => UpdateResourceMetrics();
        InitializeResourceMetrics();
        resourceTimer.Start();
        NewMemoryCatalog();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        SaveUiState();
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        activeCatalog?.Dispose();
        resourceTimer.Stop();
        FileSearchLog.EntryWritten -= OnLogEntryWritten;
        base.OnFormClosed(e);
    }

    private void BuildLayout()
    {
        ToolStrip strip = new() { GripStyle = ToolStripGripStyle.Hidden };
        ToolStripMenuItem newFileCatalog = new("File-backed catalog");
        ToolStripMenuItem newMemoryCatalog = new("Memory-backed catalog");
        newCatalog.DropDownItems.Add(newFileCatalog);
        newCatalog.DropDownItems.Add(newMemoryCatalog);
        newFileCatalog.Click += (_, _) => NewFileCatalog();
        newMemoryCatalog.Click += (_, _) => NewMemoryCatalog();
        openCatalog.Click += (_, _) => OpenCatalog();
        closeCatalog.Click += (_, _) => CloseActiveCatalog();
        switchCatalog.Click += (_, _) => SwitchCatalog();
        toggleConfig.Click += (_, _) => ToggleConfigPanel();
        configPanel.SizeChanged += (_, _) => ResizeConfigPanelChildren();
        addRoot.Click += (_, _) => AddRoot();
        removeRoot.Click += (_, _) => RemoveRoot();
        resetRoots.Click += (_, _) => ResetRoots();
        reindex.Click += async (_, _) => await Reindex();
        resetDisabledFields.Click += (_, _) => ResetDisabledFields();
        addCondition.Click += (_, _) => AddQueryRow();
        runQuery.Click += (_, _) => RunQuery();
        enumerateIndex.Click += (_, _) => EnumerateIndex();
        clearLogAction.Click += (_, _) => ClearLog();

        strip.Items.AddRange(new ToolStripItem[]
        {
            newCatalog,
            openCatalog,
            closeCatalog,
            switchCatalog,
            new ToolStripControlHost(catalogCombo) { Width = 360 },
            new ToolStripSeparator(),
            toggleConfig
        });

        BuildConfigPanel();

        Panel activePanel = new() { Dock = DockStyle.Top, Height = 30, Padding = new Padding(8, 4, 8, 4), BackColor = SystemColors.ControlLight };
        metricsLabel.Dock = DockStyle.Right;
        metricsLabel.Width = 560;
        metricsLabel.TextAlign = ContentAlignment.MiddleRight;
        activeInstanceLabel.Dock = DockStyle.Fill;
        activeInstanceLabel.TextAlign = ContentAlignment.MiddleLeft;
        activePanel.Controls.Add(metricsLabel);
        activePanel.Controls.Add(activeInstanceLabel);
        topSplit.FixedPanel = FixedPanel.Panel1;

        GroupBox rootsBox = new() { Text = "Source folder subtrees", Dock = DockStyle.Fill };
        rootsList.Dock = DockStyle.Fill;
        rootsList.DrawMode = DrawMode.OwnerDrawFixed;
        rootsList.DrawItem += DrawRootItem;
        rootsList.SelectedIndexChanged += (_, _) => RootSelectionChanged();
        rootsBox.Controls.Add(rootsList);

        GroupBox queryBox = new() { Text = "AND query rows", Dock = DockStyle.Fill };
        queryGrid.Dock = DockStyle.Fill;
        queryGrid.AutoGenerateColumns = false;
        queryGrid.AllowUserToAddRows = false;
        queryGrid.AllowUserToDeleteRows = true;
        queryBox.Controls.Add(queryGrid);

        upperSplit.Panel1.Controls.Add(rootsBox);
        upperSplit.Panel2.Controls.Add(queryBox);

        TabPage indexTab = new("Index enumeration");
        TabPage resultsTab = new("Search results");
        TabPage logTab = new("Log");
        indexGrid.Dock = DockStyle.Fill;
        indexGrid.AutoGenerateColumns = false;
        indexGrid.ReadOnly = true;
        indexGrid.AllowUserToAddRows = false;
        indexGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        indexGrid.CellValueNeeded += OnGridCellValueNeeded;
        indexGrid.CellDoubleClick += (_, args) => OpenRecordFromGrid(indexRows: true, args.RowIndex);
        indexTab.Controls.Add(indexGrid);

        resultsGrid.Dock = DockStyle.Fill;
        resultsGrid.AutoGenerateColumns = false;
        resultsGrid.ReadOnly = true;
        resultsGrid.AllowUserToAddRows = false;
        resultsGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        resultsGrid.CellValueNeeded += OnGridCellValueNeeded;
        resultsGrid.CellDoubleClick += (_, args) => OpenRecordFromGrid(indexRows: false, args.RowIndex);
        resultsTab.Controls.Add(resultsGrid);
        logViewer.Dock = DockStyle.Fill;
        logViewer.Multiline = true;
        logViewer.ReadOnly = true;
        logViewer.ScrollBars = ScrollBars.Both;
        logViewer.WordWrap = false;
        logViewer.MouseWheel += (_, _) => UpdateLogAutoScroll();
        logViewer.MouseUp += (_, _) => UpdateLogAutoScroll();
        logViewer.KeyUp += (_, _) => UpdateLogAutoScroll();
        logTab.Controls.Add(logViewer);
        tabs.TabPages.Add(indexTab);
        tabs.TabPages.Add(resultsTab);
        tabs.TabPages.Add(logTab);
        tabs.SelectedIndexChanged += (_, _) =>
        {
            if (tabs.SelectedTab == logTab)
            {
                RefreshLogViewer();
            }
        };

        Panel bottom = new() { Dock = DockStyle.Bottom, Height = 122 };
        conditionPreview.Dock = DockStyle.Fill;
        conditionPreview.Multiline = true;
        conditionPreview.ScrollBars = ScrollBars.Vertical;
        conditionPreview.ReadOnly = true;
        Panel statusPanel = new() { Dock = DockStyle.Bottom, Height = 28 };
        statusLabel.Dock = DockStyle.Fill;
        resourceLabel.Dock = DockStyle.Right;
        resourceLabel.Width = 430;
        resourceLabel.TextAlign = ContentAlignment.MiddleRight;
        resourceLabel.ForeColor = SystemColors.GrayText;
        progressBar.Dock = DockStyle.Right;
        progressBar.Width = 220;
        progressBar.Visible = false;
        statusPanel.Controls.Add(statusLabel);
        statusPanel.Controls.Add(resourceLabel);
        statusPanel.Controls.Add(progressBar);
        Panel logStatusPanel = new() { Dock = DockStyle.Bottom, Height = 24, Padding = new Padding(0, 2, 0, 0) };
        lastLogLabel.Dock = DockStyle.Fill;
        lastLogLabel.AutoEllipsis = true;
        lastLogLabel.ForeColor = SystemColors.GrayText;
        logStatusPanel.Controls.Add(lastLogLabel);
        bottom.Controls.Add(conditionPreview);
        bottom.Controls.Add(logStatusPanel);
        bottom.Controls.Add(statusPanel);

        Panel lowerPanel = new() { Dock = DockStyle.Fill };
        lowerPanel.Controls.Add(tabs);
        lowerPanel.Controls.Add(bottom);

        topSplit.Panel1.Controls.Add(upperSplit);
        topSplit.Panel2.Controls.Add(lowerPanel);

        Panel bodyPanel = new() { Dock = DockStyle.Fill };
        bodyPanel.Controls.Add(topSplit);
        bodyPanel.Controls.Add(configPanel);
        Controls.Add(bodyPanel);
        Controls.Add(activePanel);
        Controls.Add(strip);
        strip.Dock = DockStyle.Top;

        indexBrowseCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        indexBrowseCombo.Items.AddRange(Enum.GetNames<SearchField>());
        indexBrowseCombo.SelectedItem = SearchField.FileName.ToString();
        indexBrowseCombo.SelectedIndexChanged += (_, _) =>
        {
            UpdateKeyDisplayChoices(null);
            UpdateMetrics();
        };
        skipInput.Minimum = 0;
        skipInput.Maximum = int.MaxValue;
        skipInput.ThousandsSeparator = true;
        lengthInput.Minimum = 0;
        lengthInput.Maximum = int.MaxValue;
        lengthInput.ThousandsSeparator = true;
        lengthInput.Value = 5000;
        identityModeInput.DropDownStyle = ComboBoxStyle.DropDownList;
        identityModeInput.Items.Add(FileSearchIdentityMode.SyntheticUInt64.ToString());
        identityModeInput.Items.Add(FileSearchIdentityMode.PathString.ToString());
        identityModeInput.SelectedItem = FileSearchIdentityMode.SyntheticUInt64.ToString();
        identityModeInput.SelectedIndexChanged += (_, _) => UpdateIdentityDisplayChoices(null);
        identityDisplayInput.DropDownStyle = ComboBoxStyle.DropDownList;
        keyDisplayInput.DropDownStyle = ComboBoxStyle.DropDownList;
        rowHydrationInput.DropDownStyle = ComboBoxStyle.DropDownList;
        rowHydrationInput.Items.Add(FileSearchRowHydrationMode.SidecarCache.ToString());
        rowHydrationInput.Items.Add(FileSearchRowHydrationMode.FileSystem.ToString());
        rowHydrationInput.SelectedItem = FileSearchRowHydrationMode.SidecarCache.ToString();
        displayFieldModeInput.DropDownStyle = ComboBoxStyle.DropDownList;
        displayFieldModeInput.Items.AddRange(Enum.GetNames<FileSearchDisplayFieldMode>());
        displayFieldModeInput.SelectedItem = FileSearchDisplayFieldMode.AllKnownFields.ToString();
        displayFieldModeInput.SelectedIndexChanged += (_, _) => UpdateGridColumnVisibility();
        batchCommitFileCountInput.Minimum = 0;
        batchCommitFileCountInput.Maximum = int.MaxValue;
        batchCommitFileCountInput.ThousandsSeparator = true;
        batchCommitFileCountInput.Value = 10000;
        insertLayoutInput.DropDownStyle = ComboBoxStyle.DropDownList;
        insertLayoutInput.Items.AddRange(Enum.GetNames<FileSearchInsertLayout>());
        insertLayoutInput.SelectedItem = FileSearchInsertLayout.RecordMajor.ToString();
        writeOrderInput.DropDownStyle = ComboBoxStyle.DropDownList;
        writeOrderInput.Items.AddRange(Enum.GetNames<LibraDexWriteOrder>());
        writeOrderInput.SelectedItem = LibraDexWriteOrder.Default.ToString();
        writeVolumeInput.DropDownStyle = ComboBoxStyle.DropDownList;
        writeVolumeInput.Items.AddRange(Enum.GetNames<LibraDexWriteVolume>());
        writeVolumeInput.SelectedItem = LibraDexWriteVolume.Thousands.ToString();
        writeLocalityInput.DropDownStyle = ComboBoxStyle.DropDownList;
        writeLocalityInput.Items.AddRange(Enum.GetNames<LibraDexWriteLocality>());
        writeLocalityInput.SelectedItem = LibraDexWriteLocality.Clustered.ToString();
        writePriorityInput.DropDownStyle = ComboBoxStyle.DropDownList;
        writePriorityInput.Items.AddRange(Enum.GetNames<LibraDexWritePriority>());
        writePriorityInput.SelectedItem = LibraDexWritePriority.WriteSpeed.ToString();
        UpdateIdentityDisplayChoices(null);
        UpdateKeyDisplayChoices(null);
        UpdateUiState();
    }

    private void BuildConfigPanel()
    {
        Panel header = new()
        {
            Dock = DockStyle.Top,
            Height = 32
        };
        Label title = new()
        {
            Text = "Test Config",
            Dock = DockStyle.Fill,
            Font = new Font(Font, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft
        };
        Button close = new()
        {
            Text = "X",
            Dock = DockStyle.Right,
            Width = 32
        };
        close.Click += (_, _) =>
        {
            configPanel.Visible = false;
            UpdateConfigToggleText();
        };
        configPinnedInput.Dock = DockStyle.Right;
        configPinnedInput.Width = 76;
        header.Controls.Add(title);
        header.Controls.Add(configPinnedInput);
        header.Controls.Add(close);

        FlowLayoutPanel layout = new()
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true
        };

        Button addFolderButton = CreateConfigButton("Add Folder", () => AddRoot());
        Button removeFolderButton = CreateConfigButton("Remove Folder", () => RemoveRoot());
        Button resetFoldersButton = CreateConfigButton("Reset Folders", () => ResetRoots());
        Button reindexButton = CreateConfigButton("Reindex", async () => await Reindex());
        Button resetDisabledButton = CreateConfigButton("Reset Disabled Fields", () => ResetDisabledFields());
        Button enumerateButton = CreateConfigButton("Enumerate Index", () => EnumerateIndex());
        Button addQueryButton = CreateConfigButton("Add Query Row", () => AddQueryRow());
        Button runQueryButton = CreateConfigButton("Run Query", () => RunQuery());
        Button stressButton = CreateConfigButton("Stress Check", async () => await RunStressCertification());
        Button clearLogButton = CreateConfigButton("Clear Log File", () => ClearLog());
        addRoot.Tag = addFolderButton;
        removeRoot.Tag = removeFolderButton;
        resetRoots.Tag = resetFoldersButton;
        reindex.Tag = reindexButton;
        resetDisabledFields.Tag = resetDisabledButton;
        enumerateIndex.Tag = enumerateButton;
        addCondition.Tag = addQueryButton;
        runQuery.Tag = runQueryButton;
        runStressCertification.Tag = stressButton;
        clearLogAction.Tag = clearLogButton;

        layout.Controls.Add(CreateSectionHeader("Sources"));
        layout.Controls.Add(addFolderButton);
        layout.Controls.Add(removeFolderButton);
        layout.Controls.Add(resetFoldersButton);
        layout.Controls.Add(CreateSectionHeader("Identity / Rows"));
        layout.Controls.Add(CreateLabeledControl("Identity", identityModeInput));
        layout.Controls.Add(CreateLabeledControl("Identity Display Mode", identityDisplayInput));
        layout.Controls.Add(CreateLabeledControl("Key Display Mode", keyDisplayInput));
        layout.Controls.Add(CreateLabeledControl("Hydration", rowHydrationInput));
        layout.Controls.Add(CreateLabeledControl("Display", displayFieldModeInput));
        layout.Controls.Add(CreateSectionHeader("Reindex"));
        layout.Controls.Add(useGroupBatchingInput);
        layout.Controls.Add(indexExtensionInput);
        layout.Controls.Add(indexStringFoldedInput);
        layout.Controls.Add(indexStringSortKeyInput);
        layout.Controls.Add(indexStringReversedInput);
        layout.Controls.Add(CreateLabeledControl("Commit interval", batchCommitFileCountInput));
        layout.Controls.Add(CreateLabeledControl("Layout", insertLayoutInput));
        layout.Controls.Add(CreateLabeledControl("Order", writeOrderInput));
        layout.Controls.Add(CreateLabeledControl("Volume", writeVolumeInput));
        layout.Controls.Add(CreateLabeledControl("Locality", writeLocalityInput));
        layout.Controls.Add(CreateLabeledControl("Priority", writePriorityInput));
        layout.Controls.Add(reindexButton);
        layout.Controls.Add(reindexMetaLabel);
        layout.Controls.Add(resetDisabledButton);
        layout.Controls.Add(resetDisabledMetaLabel);
        layout.Controls.Add(CreateSectionHeader("Enumeration"));
        layout.Controls.Add(CreateLabeledControl("Index", indexBrowseCombo));
        layout.Controls.Add(CreateLabeledControl("Skip", skipInput));
        layout.Controls.Add(CreateLabeledControl("Length", lengthInput));
        layout.Controls.Add(enumerateButton);
        layout.Controls.Add(enumerateMetaLabel);
        layout.Controls.Add(CreateSectionHeader("Query"));
        layout.Controls.Add(addQueryButton);
        layout.Controls.Add(runQueryButton);
        layout.Controls.Add(queryMetaLabel);
        layout.Controls.Add(CreateSectionHeader("Stress"));
        layout.Controls.Add(stressButton);
        layout.Controls.Add(stressMetaLabel);
        layout.Controls.Add(CreateSectionHeader("Log"));
        layout.Controls.Add(clearLogButton);
        layout.Controls.Add(clearLogMetaLabel);
        configPanel.Controls.Add(layout);
        configPanel.Controls.Add(header);
    }

    /// <summary>
    /// Creates the compact action-metadata label used under config-panel command buttons.<br/>
    /// These labels keep the most recent count/duration result close to the button that produced it without consuming status-bar space.<br/>
    /// </summary>
    private static Label CreateMetaLabel()
    {
        return new Label
        {
            AutoSize = false,
            Height = 46,
            Margin = new Padding(0, -2, 0, 8),
            ForeColor = SystemColors.GrayText,
            TextAlign = ContentAlignment.TopLeft
        };
    }

    private Label CreateSectionHeader(string text)
    {
        return new Label
        {
            Text = text,
            Width = configPanel.Width - 28,
            Height = 24,
            Margin = new Padding(0, 10, 0, 4),
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font(Font, FontStyle.Bold),
            ForeColor = SystemColors.ControlDarkDark
        };
    }

    private Button CreateConfigButton(string text, Action action)
    {
        Button button = new()
        {
            Text = text,
            Width = configPanel.Width - 32,
            Height = 28,
            Margin = new Padding(0, 0, 0, 6)
        };
        button.Click += (_, _) =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                ShowError(ex);
            }
            finally
            {
                HideUnpinnedConfigPanel();
            }
        };
        return button;
    }

    private Button CreateConfigButton(string text, Func<Task> action)
    {
        Button button = new()
        {
            Text = text,
            Width = configPanel.Width - 32,
            Height = 28,
            Margin = new Padding(0, 0, 0, 6)
        };
        button.Click += async (_, _) =>
        {
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                ShowError(ex);
            }
            finally
            {
                HideUnpinnedConfigPanel();
            }
        };
        return button;
    }

    private Control CreateLabeledControl(string text, Control control)
    {
        TableLayoutPanel panel = new()
        {
            Width = configPanel.Width - 32,
            Height = 28,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0, 0, 0, 6)
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 138));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Label label = new()
        {
            Text = text,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        };
        control.Dock = DockStyle.Fill;
        panel.Controls.Add(label, 0, 0);
        panel.Controls.Add(control, 1, 0);
        return panel;
    }

    private void HideUnpinnedConfigPanel()
    {
        if (configPinnedInput.Checked)
        {
            return;
        }

        configPanel.Visible = false;
        UpdateConfigToggleText();
    }

    private void ToggleConfigPanel()
    {
        configPanel.Visible = !configPanel.Visible;
        UpdateConfigToggleText();
    }

    private void UpdateConfigToggleText()
    {
        toggleConfig.Text = configPanel.Visible ? "Config -" : "Config +";
    }

    private void ResizeConfigPanelChildren()
    {
        int width = Math.Max(120, configPanel.Width - 32);
        foreach (Control child in configPanel.Controls)
        {
            if (child is not FlowLayoutPanel layout)
            {
                continue;
            }

            foreach (Control item in layout.Controls)
            {
                if (item is Button or Label or TableLayoutPanel or CheckBox)
                {
                    item.Width = width;
                }
            }
        }
    }

    private void BindGrids()
    {
        queryGrid.Columns.Add(new DataGridViewComboBoxColumn
        {
            HeaderText = "Field",
            DataPropertyName = nameof(QueryRow.Field),
            DataSource = Enum.GetValues<SearchField>(),
            Width = 130
        });
        queryGrid.Columns.Add(new DataGridViewComboBoxColumn
        {
            HeaderText = "Operator",
            DataPropertyName = nameof(QueryRow.Operator),
            DataSource = Enum.GetValues<SearchOperator>(),
            Width = 130
        });
        queryGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Value 1", DataPropertyName = nameof(QueryRow.Value1), Width = 180 });
        queryGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Value 2", DataPropertyName = nameof(QueryRow.Value2), Width = 180 });
        queryGrid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "Ignore Case", DataPropertyName = nameof(QueryRow.IgnoreCase), Width = 90 });
        queryGrid.DataSource = queryRows;

        ConfigureFileGrid(resultsGrid, resultRows);
        ConfigureFileGrid(indexGrid, indexFileRows);
        UpdateGridColumnVisibility();
    }

    private void NewFileCatalog()
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        using SaveFileDialog dialog = new()
        {
            Filter = "LibraDex catalogs (*.lbdx)|*.lbdx|All files (*.*)|*.*",
            DefaultExt = "lbdx",
            AddExtension = true,
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        SetActiveCatalog(FileSearchCatalog.CreateFile(dialog.FileName, overwrite: true));
        AddCatalogPath(dialog.FileName);
        CompleteAction("Created file-backed catalog", stopwatch);
    }

    private void NewMemoryCatalog()
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        SetActiveCatalog(FileSearchCatalog.CreateMemory());
        CompleteAction("Created memory catalog", stopwatch);
    }

    private void OpenCatalog()
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        using OpenFileDialog dialog = new()
        {
            Filter = "LibraDex catalogs (*.lbdx)|*.lbdx|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        SetActiveCatalog(FileSearchCatalog.CreateFile(dialog.FileName, overwrite: false));
        AddCatalogPath(dialog.FileName);
        CompleteAction("Opened file-backed catalog", stopwatch);
    }

    private void SwitchCatalog()
    {
        if (catalogCombo.SelectedItem is not string path || path.Length == 0)
        {
            return;
        }

        Stopwatch stopwatch = Stopwatch.StartNew();
        SetActiveCatalog(FileSearchCatalog.CreateFile(path, overwrite: false));
        CompleteAction("Switched active catalog", stopwatch);
    }

    private void CloseActiveCatalog()
    {
        FileSearchCatalog? closingCatalog = activeCatalog;
        if (closingCatalog is null)
        {
            return;
        }

        Stopwatch stopwatch = Stopwatch.StartNew();
        string displayName = closingCatalog.DisplayName;
        string? closedPath = closingCatalog.CatalogPath;
        closingCatalog.Dispose();
        activeCatalog = null;
        RemoveCatalogPath(closedPath);
        ClearCatalogUiState(clearQueryRows: true);
        FileSearchLog.Info($"Closed catalog '{displayName}'.");

        if (catalogPaths.Count > 0)
        {
            SetActiveCatalog(FileSearchCatalog.CreateFile(catalogPaths[0], overwrite: false));
            CompleteAction($"Closed catalog and focused {activeCatalog!.DisplayName}", stopwatch);
            return;
        }

        statusLabel.Text = "No active catalog.";
        activeInstanceLabel.Text = "No active LibraDex instance";
        metricsLabel.Text = "Catalog closed.";
        UpdateUiState();
        CompleteAction("Closed active catalog", stopwatch);
    }

    private void AddRoot()
    {
        FileSearchCatalog catalog = RequireCatalog();
        using FolderBrowserDialog dialog = new() { Description = "Select a folder subtree to index" };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        catalog.AddRoot(dialog.SelectedPath);
        RefreshRoots();
        rootsList.SelectedItem = System.IO.Path.GetFullPath(dialog.SelectedPath);
        statusLabel.Text = $"Added source folder. Click Reindex to populate this {catalog.DisplayName}.";
        FileSearchLog.Info($"Added source folder '{dialog.SelectedPath}'.");
    }

    private void RemoveRoot()
    {
        FileSearchCatalog catalog = RequireCatalog();
        if (rootsList.SelectedItem is not string path)
        {
            return;
        }

        catalog.RemoveRoot(path);
        RefreshRoots();
        ClearDisplayedRows();
        queryMetaLabel.Text = string.Empty;
        enumerateMetaLabel.Text = string.Empty;
        statusLabel.Text = "Removed source folder. Click Reindex to rebuild indexed data.";
        FileSearchLog.Info($"Removed source folder '{path}'.");
    }

    private void ResetRoots()
    {
        FileSearchCatalog catalog = RequireCatalog();
        if (MessageBox.Show(this, "Remove all source folder subtrees from this catalog?", "Reset folders", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }

        catalog.ResetRoots();
        RefreshRoots();
        ClearDisplayedRows();
        queryMetaLabel.Text = string.Empty;
        enumerateMetaLabel.Text = string.Empty;
        statusLabel.Text = "Reset source folders. Click Add Folder to choose new roots.";
        FileSearchLog.Info("Reset source folders.");
    }

    private void ResetDisabledFields()
    {
        FileSearchCatalog catalog = RequireCatalog();
        int count = catalog.ClearDisabledSecondaryFields();
        statusLabel.Text = count == 0
            ? "No disabled secondary fields were stored."
            : $"Reset {count:n0} disabled secondary fields. Next reindex will try them again.";
        resetDisabledMetaLabel.Text = count == 0 ? "No disabled fields." : $"Reset {count:n0} fields.";
        FileSearchLog.Info($"Reset disabled secondary fields. Count={count:n0}; Catalog='{catalog.DisplayName}'.");
        UpdateMetrics();
    }

    private void ClearLog()
    {
        FileSearchLog.Clear();
        logViewer.Clear();
        lastLogLabel.Text = "Log cleared.";
        statusLabel.Text = $"Cleared log file: {FileSearchLog.Path}";
        clearLogMetaLabel.Text = $"Cleared {DateTime.Now:T}.";
    }

    private async Task Reindex()
    {
        if (busy)
        {
            reindexCancellation?.Cancel();
            statusLabel.Text = "Cancel requested; waiting for current reindex step to stop.";
            FileSearchLog.Info("Reindex cancellation requested.");
            return;
        }

        FileSearchCatalog catalog = RequireCatalog();
        if (catalog.Roots.Count == 0)
        {
            MessageBox.Show(this, "Add at least one source folder subtree first.", "Reindex", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Cursor = Cursors.WaitCursor;
        reindexCancellation = new CancellationTokenSource();
        SetBusy(true);
        BeginProgress("Indexing...");
        Stopwatch stopwatch = Stopwatch.StartNew();
        long reindexAllocatedStartBytes = GC.GetTotalAllocatedBytes(precise: true);
        ResetResourcePeaks();
        UpdateResourceMetrics();
        FileSearchLog.Info($"Reindex resource start: {lastResourceText}; Allocated={FormatBytes(reindexAllocatedStartBytes)}.");
        FileSearchLog.Info($"Reindex catalog storage start: {catalog.GetCatalogStorageStatusText()}.");
        try
        {
            FileSearchReindexOptions options = GetReindexOptions();
            CancellationToken cancellationToken = reindexCancellation.Token;
            ThrottledUiProgress throttledProgress = new(this, message => statusLabel.Text = AppendCatalogStorageStatus(catalog, message), ReindexProgressUiUpdateIntervalMs);
            ReindexResult result = await Task.Run(() => catalog.Reindex(options, throttledProgress, cancellationToken), cancellationToken);
            stopwatch.Stop();
            string elapsed = FormatDuration(stopwatch.Elapsed);
            long allocatedDelta = GC.GetTotalAllocatedBytes(precise: false) - reindexAllocatedStartBytes;
            string storageStatus = catalog.GetCatalogStorageStatusText();
            statusLabel.Text = $"Indexed {result.Indexed:n0} files in {elapsed}. Collect {FormatDuration(result.Phases.Collect)}, sort {FormatDuration(result.Phases.Sort)}, insert {FormatDuration(result.Phases.Insert)}. {storageStatus}.";
            reindexMetaLabel.Text = $"Total {elapsed}; FS {FormatDuration(result.Phases.Collect)}; sort {FormatDuration(result.Phases.Sort)}; index {FormatDuration(result.Phases.Insert)}; {result.Indexed:n0}/{result.Scanned:n0}; {storageStatus}.";
            FileSearchLog.Info($"Reindex completed in {elapsed}. Scanned={result.Scanned:n0}; Indexed={result.Indexed:n0}; FailedFiles={result.FailedFiles:n0}; FailedFieldAttempts={result.FailedFieldAttempts:n0}; SkippedFields={result.SkippedFields:n0}; DisabledFields={result.DisabledFields}; Collect={FormatDuration(result.Phases.Collect)}; Sort={FormatDuration(result.Phases.Sort)}; Insert={FormatDuration(result.Phases.Insert)}; AllocatedDelta={FormatBytes(allocatedDelta)}; Batch={options.UseGroupBatching}; CommitInterval={options.BatchCommitFileCount:n0}; IndexExtension={options.IndexExtension}; StringFolded={options.IndexStringFolded}; StringSortKey={options.IndexStringSortKey}; StringReversed={options.IndexStringReversed}; Layout={options.InsertLayout}; Intent={options.WriteOrder}/{options.WriteVolume}/{options.WriteLocality}/{options.WritePriority}; Catalog='{catalog.DisplayName}'.");
            FileSearchLog.Info($"Reindex catalog storage end: {storageStatus}.");
            FileSearchLog.Info($"Catalog memory diagnostics: {catalog.GetCatalogMemoryDiagnosticsText()}.");
            UpdateResourceMetrics();
            FileSearchLog.Info($"Reindex resource end: {lastResourceText}.");
            FileSearchLog.Info($"Reindex insert field telemetry: {result.Telemetry.CreateFieldSummary()}.");
            FileSearchLog.Info($"Reindex batch telemetry: Commits={result.Telemetry.BatchCommits.Count:n0}; FinalAttempted={result.Telemetry.FinalCommitAttempted:n0}; FinalInserted={result.Telemetry.FinalCommitInserted:n0}; FinalDeferredCommits={result.Telemetry.FinalCommitDeferredCommits:n0}; FinalCommit={FormatDuration(result.Telemetry.FinalCommitDuration)}.");
            UpdateMetrics();
            RefreshLogViewer();
            RunQuery();
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            string elapsed = FormatDuration(stopwatch.Elapsed);
            long allocatedDelta = GC.GetTotalAllocatedBytes(precise: false) - reindexAllocatedStartBytes;
            string storageStatus = catalog.GetCatalogStorageStatusText();
            statusLabel.Text = $"Reindex canceled after {elapsed}. Catalog storage was reset. {storageStatus}.";
            reindexMetaLabel.Text = $"Canceled after {elapsed}; {storageStatus}.";
            FileSearchLog.Info($"Reindex canceled after {elapsed}. AllocatedDelta={FormatBytes(allocatedDelta)}; Catalog='{catalog.DisplayName}'.");
            FileSearchLog.Info($"Reindex catalog storage cancel: {storageStatus}.");
            UpdateResourceMetrics();
            FileSearchLog.Info($"Reindex resource cancel: {lastResourceText}.");
            ClearDisplayedRows();
            UpdateMetrics();
            RefreshLogViewer();
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            EndProgress();
            SetBusy(false);
            FlushPendingBusyLogEntry();
            FlushDeferredBusyLogRefresh();
            reindexCancellation?.Dispose();
            reindexCancellation = null;
            Cursor = Cursors.Default;
        }
    }

    private void SetBusy(bool value)
    {
        busy = value;
        newCatalog.Enabled = !value;
        openCatalog.Enabled = !value;
        closeCatalog.Enabled = !value;
        switchCatalog.Enabled = !value;
        catalogCombo.Enabled = !value;
        addRoot.Enabled = !value;
        removeRoot.Enabled = !value;
        resetRoots.Enabled = !value;
        reindex.Enabled = true;
        reindex.Text = value ? "Cancel" : "Reindex";
        if (reindex.Tag is Button reindexButton)
        {
            reindexButton.Text = reindex.Text;
        }
        resetDisabledFields.Enabled = !value;
        addCondition.Enabled = !value;
        runQuery.Enabled = !value;
        runStressCertification.Enabled = !value;
        enumerateIndex.Enabled = !value;
        queryGrid.Enabled = !value;
        rootsList.Enabled = !value;
        indexBrowseCombo.Enabled = !value;
        skipInput.Enabled = !value;
        lengthInput.Enabled = !value;
        identityModeInput.Enabled = !value;
        identityDisplayInput.Enabled = !value;
        keyDisplayInput.Enabled = !value;
        rowHydrationInput.Enabled = !value;
        displayFieldModeInput.Enabled = !value;
        useGroupBatchingInput.Enabled = !value;
        indexExtensionInput.Enabled = !value;
        indexStringFoldedInput.Enabled = !value;
        indexStringSortKeyInput.Enabled = !value;
        indexStringReversedInput.Enabled = !value;
        batchCommitFileCountInput.Enabled = !value;
        insertLayoutInput.Enabled = !value;
        writeOrderInput.Enabled = !value;
        writeVolumeInput.Enabled = !value;
        writeLocalityInput.Enabled = !value;
        writePriorityInput.Enabled = !value;
        SetConfigButtonEnabled(addRoot, !value);
        SetConfigButtonEnabled(removeRoot, !value);
        SetConfigButtonEnabled(resetRoots, !value);
        SetConfigButtonEnabled(reindex, true);
        SetConfigButtonEnabled(resetDisabledFields, !value);
        SetConfigButtonEnabled(addCondition, !value);
        SetConfigButtonEnabled(runQuery, !value);
        SetConfigButtonEnabled(runStressCertification, !value);
        SetConfigButtonEnabled(enumerateIndex, !value);
        SetConfigButtonEnabled(clearLogAction, !value);
    }

    private void AddQueryRow()
    {
        queryRows.Add(new QueryRow
        {
            Field = SearchField.FileName,
            Operator = SearchOperator.Contains,
            IgnoreCase = true
        });
    }

    private void RunQuery()
    {
        FileSearchCatalog catalog = RequireCatalog();
        queryGrid.EndEdit();
        Cursor = Cursors.WaitCursor;
        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            int skip = GetSkip();
            int length = GetLength();
            FileSearchReadOptions options = GetReadOptions(catalog);
            QueryRow[] rows = queryRows.ToArray();
            if (options.DisplayFieldMode == FileSearchDisplayFieldMode.IdentityOnly)
            {
                IdentityQueryResult identityResult = catalog.ExecuteIdentities(rows, skip, length, options);
                stopwatch.Stop();
                Stopwatch identityGridStopwatch = Stopwatch.StartNew();
                ConfigureIdentityGrid(resultsGrid, resultIdentityRows);
                BindIdentityRows(resultsGrid, resultIdentityRows, identityResult.Rows);
                identityGridStopwatch.Stop();
                conditionPreview.Text = identityResult.ConditionPreview;
                string identityElapsed = FormatDuration(stopwatch.Elapsed);
                string identityGridElapsed = FormatDuration(identityGridStopwatch.Elapsed);
                statusLabel.Text = $"Returned {identityResult.Rows.Count:n0} identities. Query={identityElapsed}; Grid={identityGridElapsed}; Indexed={catalog.FileCount:n0}; Skip={skip:n0}; Length={DescribeLength(length)}.";
                queryMetaLabel.Text = $"{identityResult.Rows.Count:n0} identities; query {identityElapsed}; grid {identityGridElapsed}.";
                FileSearchLog.Info($"Query identity discovery completed. Query={identityElapsed}; Grid={identityGridElapsed}; Rows={identityResult.Rows.Count:n0}; IndexedFiles={catalog.FileCount:n0}; Skip={skip:n0}; Length={DescribeLength(length)}; Identity={options.IdentityMode}; IdentityDisplay={options.IdentityDisplayMode}; Display={options.DisplayFieldMode}.");
                if (rows.Length > 0)
                {
                    FileSearchLog.Info($"Query condition:{Environment.NewLine}{identityResult.ConditionPreview}");
                }

                UpdateMetrics();
                RefreshLogViewer();
                return;
            }

            if (options.DisplayFieldMode == FileSearchDisplayFieldMode.SelectedFields)
            {
                if (rows.Length != 1)
                {
                    IdentityQueryResult identityResult = catalog.ExecuteIdentities(rows, skip, length, options);
                    stopwatch.Stop();
                    Stopwatch fallbackGridStopwatch = Stopwatch.StartNew();
                    ConfigureIdentityGrid(resultsGrid, resultIdentityRows);
                    BindIdentityRows(resultsGrid, resultIdentityRows, identityResult.Rows);
                    fallbackGridStopwatch.Stop();
                    conditionPreview.Text = identityResult.ConditionPreview;
                    string identityElapsed = FormatDuration(stopwatch.Elapsed);
                    string fallbackGridElapsed = FormatDuration(fallbackGridStopwatch.Elapsed);
                    statusLabel.Text = $"Returned {identityResult.Rows.Count:n0} identities. Query={identityElapsed}; Grid={fallbackGridElapsed}; selected-fields view needs exactly one condition row to have one natural key column.";
                    queryMetaLabel.Text = $"{identityResult.Rows.Count:n0} identities; query {identityElapsed}; grid {fallbackGridElapsed}; no single key.";
                    FileSearchLog.Info($"Query selected-fields fell back to identity discovery. Query={identityElapsed}; Grid={fallbackGridElapsed}; Rows={identityResult.Rows.Count:n0}; IndexedFiles={catalog.FileCount:n0}; Skip={skip:n0}; Length={DescribeLength(length)}; Identity={options.IdentityMode}; IdentityDisplay={options.IdentityDisplayMode}; Display={options.DisplayFieldMode}; Reason=NoSingleNaturalKey.");
                    if (rows.Length > 0)
                    {
                        FileSearchLog.Info($"Query condition:{Environment.NewLine}{identityResult.ConditionPreview}");
                    }

                    UpdateMetrics();
                    RefreshLogViewer();
                    return;
                }

                TupleQueryResult tupleResult = catalog.ExecuteSelectedIndexTuples(rows, skip, length, options);
                stopwatch.Stop();
                Stopwatch tupleGridStopwatch = Stopwatch.StartNew();
                ConfigureTupleGrid(resultsGrid, resultTupleRows, rows.Length == 1 ? rows[0].Field.ToString() : "Key");
                BindTupleRows(resultsGrid, resultTupleRows, tupleResult.Rows);
                tupleGridStopwatch.Stop();
                conditionPreview.Text = tupleResult.ConditionPreview;
                string tupleElapsed = FormatDuration(stopwatch.Elapsed);
                string tupleGridElapsed = FormatDuration(tupleGridStopwatch.Elapsed);
                statusLabel.Text = $"Returned {tupleResult.Rows.Count:n0} selected rows. Query={tupleElapsed}; Grid={tupleGridElapsed}; Indexed={catalog.FileCount:n0}; Skip={skip:n0}; Length={DescribeLength(length)}.";
                queryMetaLabel.Text = $"{tupleResult.Rows.Count:n0} tuples; query {tupleElapsed}; grid {tupleGridElapsed}; key {rows[0].Field}.";
                FileSearchLog.Info($"Query selected-fields completed. Query={tupleElapsed}; Grid={tupleGridElapsed}; Rows={tupleResult.Rows.Count:n0}; IndexedFiles={catalog.FileCount:n0}; Skip={skip:n0}; Length={DescribeLength(length)}; Identity={options.IdentityMode}; IdentityDisplay={options.IdentityDisplayMode}; KeyDisplay={options.KeyDisplayMode}; Display={options.DisplayFieldMode}.");
                if (rows.Length > 0)
                {
                    FileSearchLog.Info($"Query condition:{Environment.NewLine}{tupleResult.ConditionPreview}");
                }

                UpdateMetrics();
                RefreshLogViewer();
                return;
            }

            QueryResult result = catalog.Execute(rows, skip, length, options);
            stopwatch.Stop();
            Stopwatch gridStopwatch = Stopwatch.StartNew();
            ConfigureFileGrid(resultsGrid, resultRows);
            BindFileRows(resultsGrid, resultRows, result.Files);
            UpdateGridColumnVisibility();
            gridStopwatch.Stop();
            conditionPreview.Text = result.ConditionPreview;
            string elapsed = FormatDuration(stopwatch.Elapsed);
            string fileGridElapsed = FormatDuration(gridStopwatch.Elapsed);
            statusLabel.Text = $"Returned {result.Files.Count:n0} rows. Query={elapsed}; Grid={fileGridElapsed}; Indexed={catalog.FileCount:n0}; Skip={skip:n0}; Length={DescribeLength(length)}.";
            queryMetaLabel.Text = $"{result.Files.Count:n0} records; query {elapsed}; grid {fileGridElapsed}.";
            FileSearchLog.Info($"Query completed. Query={elapsed}; Grid={fileGridElapsed}; Rows={result.Files.Count:n0}; IndexedFiles={catalog.FileCount:n0}; Skip={skip:n0}; Length={DescribeLength(length)}; Identity={options.IdentityMode}; IdentityDisplay={options.IdentityDisplayMode}; Hydration={options.RowHydrationMode}; Display={options.DisplayFieldMode}.");
            if (rows.Length > 0)
            {
                FileSearchLog.Info($"Query condition:{Environment.NewLine}{result.ConditionPreview}");
            }
            UpdateMetrics();
            RefreshLogViewer();
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            Cursor = Cursors.Default;
        }
    }

    /// <summary>
    /// Runs the loaded-catalog stress certification pass from the workbench panel.<br/>
    /// The work is performed off the UI thread and avoids grid population so the resulting log focuses on LibraDex enumeration, query formation, and hydration behavior rather than presentation cost.<br/>
    /// </summary>
    private async Task RunStressCertification()
    {
        if (busy)
        {
            return;
        }

        FileSearchCatalog catalog = RequireCatalog();
        if (catalog.FileCount == 0)
        {
            MessageBox.Show(this, "Reindex at least one source folder before running stress certification.", "Stress Check", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Cursor = Cursors.WaitCursor;
        SetBusy(true);
        BeginProgress("Stress checking...");
        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            FileSearchReadOptions options = GetReadOptions(catalog);
            Progress<string> progress = new(message => statusLabel.Text = message);
            FileSearchStressResult result = await Task.Run(() => catalog.RunStressCertification(options, progress, CancellationToken.None));
            stopwatch.Stop();
            string elapsed = FormatDuration(stopwatch.Elapsed);
            string text = $"{result.Summary}; wall={elapsed}.";
            statusLabel.Text = text;
            stressMetaLabel.Text = text;
            FileSearchLog.Info($"Stress certification UI completed. {text}");
            UpdateMetrics();
            RefreshLogViewer();
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            EndProgress();
            SetBusy(false);
            FlushPendingBusyLogEntry();
            FlushDeferredBusyLogRefresh();
            Cursor = Cursors.Default;
        }
    }

    private void EnumerateIndex()
    {
        FileSearchCatalog catalog = RequireCatalog();
        Stopwatch stopwatch = Stopwatch.StartNew();
        SearchField field = GetSelectedIndexField();

        int skip = GetSkip();
        int length = GetLength();
        FileSearchReadOptions options = GetReadOptions(catalog);
        if (options.DisplayFieldMode == FileSearchDisplayFieldMode.SelectedFields)
        {
            IReadOnlyList<FileSearchTupleRow> tupleRows = catalog.EnumerateTuples(field, skip, length, options);
            stopwatch.Stop();
            Stopwatch gridStopwatch = Stopwatch.StartNew();
            ConfigureTupleGrid(indexGrid, indexTupleRows, field.ToString());
            BindTupleRows(indexGrid, indexTupleRows, tupleRows);
            gridStopwatch.Stop();
            string tupleElapsed = FormatDuration(stopwatch.Elapsed);
            string gridElapsed = FormatDuration(gridStopwatch.Elapsed);
            bool fullEnumeration = skip == 0 && length == 0;
            bool countMismatch = fullEnumeration && tupleRows.Count != catalog.FileCount;
            string countWarning = countMismatch ? " Count mismatch; see log." : string.Empty;
            statusLabel.Text = $"Enumerated {tupleRows.Count:n0} key/identity rows by {field}. Enumerate={tupleElapsed}; Grid={gridElapsed}; Indexed={catalog.FileCount:n0}; Skip={skip:n0}; Length={DescribeLength(length)}.{countWarning}";
            enumerateMetaLabel.Text = countMismatch
                ? $"{field}: {tupleRows.Count:n0}/{catalog.FileCount:n0} tuples; enum {tupleElapsed}; grid {gridElapsed}; mismatch."
                : $"{field}: {tupleRows.Count:n0} tuples; enum {tupleElapsed}; grid {gridElapsed}.";
            FileSearchLog.Info($"Index tuple enumeration completed. Enumerate={tupleElapsed}; Grid={gridElapsed}; Field={field}; Rows={tupleRows.Count:n0}; IndexedFiles={catalog.FileCount:n0}; Skip={skip:n0}; Length={DescribeLength(length)}; Identity={options.IdentityMode}; IdentityDisplay={options.IdentityDisplayMode}; KeyDisplay={options.KeyDisplayMode}; Display={options.DisplayFieldMode}.");
            if (countMismatch)
            {
                FileSearchLog.Info($"WARNING: Index tuple enumeration count mismatch. Field={field}; Rows={tupleRows.Count:n0}; IndexedFiles={catalog.FileCount:n0}; Skip={skip:n0}; Length={DescribeLength(length)}; Identity={options.IdentityMode}; KeyDisplay={options.KeyDisplayMode}; IdentityDisplay={options.IdentityDisplayMode}. Expected one tuple per indexed file for a full workbench enumeration.");
            }

            UpdateMetrics();
            RefreshLogViewer();
            return;
        }

        IReadOnlyList<FileRecord> rows = catalog.Enumerate(field, skip, length, options);
        stopwatch.Stop();
        Stopwatch fileGridStopwatch = Stopwatch.StartNew();
        ConfigureFileGrid(indexGrid, indexFileRows);
        BindFileRows(indexGrid, indexFileRows, rows);
        UpdateGridColumnVisibility();
        fileGridStopwatch.Stop();
        string elapsed = FormatDuration(stopwatch.Elapsed);
        string fileGridElapsed = FormatDuration(fileGridStopwatch.Elapsed);
        statusLabel.Text = $"Enumerated {rows.Count:n0} rows by {field}. Enumerate={elapsed}; Grid={fileGridElapsed}; Indexed={catalog.FileCount:n0}; Skip={skip:n0}; Length={DescribeLength(length)}.";
        enumerateMetaLabel.Text = $"{field}: {rows.Count:n0} records; enum {elapsed}; grid {fileGridElapsed}.";
        FileSearchLog.Info($"Index enumeration completed. Enumerate={elapsed}; Grid={fileGridElapsed}; Field={field}; Rows={rows.Count:n0}; IndexedFiles={catalog.FileCount:n0}; Skip={skip:n0}; Length={DescribeLength(length)}; Identity={options.IdentityMode}; IdentityDisplay={options.IdentityDisplayMode}; Hydration={options.RowHydrationMode}; Display={options.DisplayFieldMode}.");
        UpdateMetrics();
        RefreshLogViewer();
    }

    private bool TryHandleUnsupportedPathStringStringFields(
        IReadOnlyList<QueryRow> rows,
        FileSearchReadOptions options,
        Label targetMetaLabel,
        string actionName)
    {
        if (options.IdentityMode != FileSearchIdentityMode.PathString)
        {
            return false;
        }

        for (int i = 0; i < rows.Count; i++)
        {
            SearchField field = rows[i].Field;
            if (field != SearchField.FileName && field != SearchField.Extension)
            {
                continue;
            }

            string message = $"{field} queries are not connected for PathString identity mode yet. Choose Size/Created/Modified/Accessed/Attributes, or reindex as SyntheticUInt64 for filename/extension tests.";
            statusLabel.Text = message;
            targetMetaLabel.Text = $"{actionName}: unsupported {field} for PathString.";
            conditionPreview.Text = message;
            FileSearchLog.Info($"{actionName} skipped. Field={field}; Identity={options.IdentityMode}; Reason=PathString string-key VV facade is not connected.");
            return true;
        }

        return false;
    }

    private void OpenRecordFromGrid(bool indexRows, int rowIndex)
    {
        DataGridView grid = indexRows ? indexGrid : resultsGrid;
        IReadOnlyList<FileRecord>? rows = indexRows
            ? grid.VirtualMode && grid.Tag is VirtualGridSource { Kind: VirtualGridKind.File } ? virtualIndexFileRows : grid.DataSource == indexFileRows ? indexFileRows : null
            : grid.VirtualMode && grid.Tag is VirtualGridSource { Kind: VirtualGridKind.File } ? virtualResultFileRows : grid.DataSource == resultRows ? resultRows : null;
        if (rows is null)
        {
            return;
        }

        if (rowIndex < 0 || rowIndex >= rows.Count)
        {
            return;
        }

        string path = rows[rowIndex].Path;
        try
        {
            using Process process = Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = false
            })!;
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private FileSearchCatalog RequireCatalog()
        => activeCatalog ?? throw new InvalidOperationException("No LibraDex file-search catalog is active.");

    private void SetActiveCatalog(FileSearchCatalog catalog)
    {
        activeCatalog?.Dispose();
        activeCatalog = catalog;
        RefreshRoots();
        ClearDisplayedRows();
        statusLabel.Text = $"Active catalog: {catalog.DisplayName}";
        UpdateMetrics();
        UpdateUiState();
    }

    private void RefreshRoots()
    {
        string? previous = rootsList.SelectedItem as string;
        rootsList.Items.Clear();
        if (activeCatalog is null)
        {
            return;
        }

        for (int i = 0; i < activeCatalog.Roots.Count; i++)
        {
            rootsList.Items.Add(activeCatalog.Roots[i]);
        }

        if (previous is not null && rootsList.Items.Contains(previous))
        {
            rootsList.SelectedItem = previous;
        }
        else if (rootsList.Items.Count > 0)
        {
            rootsList.SelectedIndex = 0;
        }
    }

    private void RootSelectionChanged()
    {
        ClearDisplayedRows();
        if (rootsList.SelectedItem is string selected)
        {
            statusLabel.Text = $"Selected source folder: {selected}";
        }
    }

    private void DrawRootItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0)
        {
            return;
        }

        bool selected = (e.State & DrawItemState.Selected) != 0;
        Color backColor = selected ? SystemColors.Highlight : rootsList.BackColor;
        Color foreColor = selected ? SystemColors.HighlightText : rootsList.ForeColor;
        using SolidBrush backBrush = new(backColor);
        e.Graphics.FillRectangle(backBrush, e.Bounds);

        string text = rootsList.Items[e.Index]?.ToString() ?? string.Empty;
        TextRenderer.DrawText(
            e.Graphics,
            text,
            rootsList.Font,
            e.Bounds,
            foreColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        e.DrawFocusRectangle();
    }

    private void ClearDisplayedRows()
    {
        resultRows.Clear();
        resultTupleRows.Clear();
        resultIdentityRows.Clear();
        indexFileRows.Clear();
        indexTupleRows.Clear();
        conditionPreview.Clear();
        ClearGridRows(resultsGrid);
        ClearGridRows(indexGrid);
    }

    /// <summary>
    /// Clears all UI state whose values are owned by the active catalog.<br/>
    /// The close path uses this to make the no-instance state visually unambiguous, while normal catalog switches can keep query rows when desired.<br/>
    /// </summary>
    /// <param name="clearQueryRows">When true, clears the condition-builder rows as part of closing the catalog.<br/></param>
    private void ClearCatalogUiState(bool clearQueryRows)
    {
        rootsList.Items.Clear();
        ClearDisplayedRows();
        conditionPreview.Clear();
        if (clearQueryRows)
        {
            queryRows.Clear();
            queryRows.ResetBindings();
        }
    }

    /// <summary>
    /// Clears a result grid regardless of whether it was bound or virtualized for a large result set.<br/>
    /// Virtual grids keep their row count outside the backing list, so close/reset code must clear both the row count and virtual source tag.<br/>
    /// </summary>
    /// <param name="grid">The grid to clear.<br/></param>
    private static void ClearGridRows(DataGridView grid)
    {
        if (grid.VirtualMode)
        {
            grid.RowCount = 0;
            grid.VirtualMode = false;
            grid.Tag = null;
        }

        grid.Refresh();
    }

    private void AddCatalogPath(string path)
    {
        if (!catalogPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            catalogPaths.Add(path);
            catalogCombo.Items.Add(path);
        }

        catalogCombo.SelectedItem = path;
        UpdateUiState();
    }

    /// <summary>
    /// Removes a file-backed catalog path from the workbench switch list after that catalog is closed.<br/>
    /// Memory-backed catalogs do not have a path entry, so null or empty values are ignored.<br/>
    /// </summary>
    /// <param name="path">The closed file-backed catalog path.<br/></param>
    private void RemoveCatalogPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        for (int i = catalogPaths.Count - 1; i >= 0; i--)
        {
            if (string.Equals(catalogPaths[i], path, StringComparison.OrdinalIgnoreCase))
            {
                catalogPaths.RemoveAt(i);
            }
        }

        for (int i = catalogCombo.Items.Count - 1; i >= 0; i--)
        {
            if (catalogCombo.Items[i] is string item &&
                string.Equals(item, path, StringComparison.OrdinalIgnoreCase))
            {
                catalogCombo.Items.RemoveAt(i);
            }
        }

        catalogCombo.SelectedItem = null;
        catalogCombo.Text = string.Empty;
    }

    private void ShowError(Exception ex)
    {
        FileSearchLog.Error("Unhandled UI action error.", ex);
        statusLabel.Text = ex.Message;
        MessageBox.Show(this, $"{ex.Message}{Environment.NewLine}{Environment.NewLine}Log:{Environment.NewLine}{FileSearchLog.Path}", "LibraDex File Search", MessageBoxButtons.OK, MessageBoxIcon.Error);
        RefreshLogViewer();
    }

    private void BeginProgress(string text)
    {
        statusLabel.Text = text;
        progressBar.Style = ProgressBarStyle.Marquee;
        progressBar.Visible = true;
        progressBar.MarqueeAnimationSpeed = 30;
        statusLabel.Refresh();
        progressBar.Refresh();
    }

    private void EndProgress()
    {
        progressBar.MarqueeAnimationSpeed = 0;
        progressBar.Visible = false;
        progressBar.Style = ProgressBarStyle.Blocks;
    }

    private void InitializeResourceMetrics()
    {
        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        lastResourceCpuTime = process.TotalProcessorTime;
        lastResourceTimestamp = Stopwatch.GetTimestamp();
        peakWorkingSetBytes = process.WorkingSet64;
        peakPrivateBytes = process.PrivateMemorySize64;
        peakGcBytes = GC.GetTotalMemory(forceFullCollection: false);
        UpdateResourceMetrics();
    }

    private void ResetResourcePeaks()
    {
        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        peakWorkingSetBytes = process.WorkingSet64;
        peakPrivateBytes = process.PrivateMemorySize64;
        peakGcBytes = GC.GetTotalMemory(forceFullCollection: false);
        lastResourceCpuTime = process.TotalProcessorTime;
        lastResourceTimestamp = Stopwatch.GetTimestamp();
    }

    private void UpdateResourceMetrics()
    {
        try
        {
            using Process process = Process.GetCurrentProcess();
            process.Refresh();
            long nowTimestamp = Stopwatch.GetTimestamp();
            TimeSpan nowCpu = process.TotalProcessorTime;
            double cpuPercent = 0;
            long elapsedTicks = nowTimestamp - lastResourceTimestamp;
            if (elapsedTicks > 0)
            {
                double elapsedSeconds = elapsedTicks / (double)Stopwatch.Frequency;
                double cpuSeconds = (nowCpu - lastResourceCpuTime).TotalSeconds;
                cpuPercent = Math.Max(0, cpuSeconds / elapsedSeconds / Environment.ProcessorCount * 100);
            }

            lastResourceTimestamp = nowTimestamp;
            lastResourceCpuTime = nowCpu;

            long workingSet = process.WorkingSet64;
            long privateBytes = process.PrivateMemorySize64;
            long gcBytes = GC.GetTotalMemory(forceFullCollection: false);
            peakWorkingSetBytes = Math.Max(peakWorkingSetBytes, workingSet);
            peakPrivateBytes = Math.Max(peakPrivateBytes, privateBytes);
            peakGcBytes = Math.Max(peakGcBytes, gcBytes);

            lastResourceText = string.Create(
                CultureInfo.InvariantCulture,
                $"CPU {cpuPercent:n0}% | RAM {FormatBytes(workingSet)} | Private {FormatBytes(privateBytes)} | Peak {FormatBytes(peakWorkingSetBytes)} | GC {FormatBytes(gcBytes)} / {FormatBytes(peakGcBytes)}");
            resourceLabel.Text = lastResourceText;
        }
        catch
        {
            lastResourceText = "CPU n/a | RAM n/a";
            resourceLabel.Text = lastResourceText;
        }
    }

    private static string FormatBytes(long bytes)
    {
        double value = bytes;
        string suffix = "B";
        if (value >= 1024)
        {
            value /= 1024;
            suffix = "KB";
        }

        if (value >= 1024)
        {
            value /= 1024;
            suffix = "MB";
        }

        if (value >= 1024)
        {
            value /= 1024;
            suffix = "GB";
        }

        return value >= 100
            ? string.Create(CultureInfo.InvariantCulture, $"{value:n0} {suffix}")
            : string.Create(CultureInfo.InvariantCulture, $"{value:n1} {suffix}");
    }

    /// <summary>
    /// Appends visible file-backed catalog storage state to a workbench status message.<br/>
    /// Memory catalogs intentionally keep the normal concise progress text because their arena diagnostics are already shown in the resource/status area and would make every progress update noisy.<br/>
    /// </summary>
    /// <param name="catalog">The catalog whose backing storage should be reported.<br/></param>
    /// <param name="message">The base status message produced by the background operation.<br/></param>
    /// <returns>The status message plus current `.lbdx` and sidecar sizes for file-backed catalogs.</returns>
    private static string AppendCatalogStorageStatus(FileSearchCatalog catalog, string message)
    {
        return catalog.IsFileBacked
            ? $"{message} {catalog.GetCatalogStorageStatusText()}."
            : message;
    }

    private void UpdateUiState()
    {
        bool hasCatalog = activeCatalog is not null;
        bool hasSavedCatalogs = catalogCombo.Items.Count > 0;
        closeCatalog.Enabled = hasCatalog;
        switchCatalog.Enabled = hasSavedCatalogs;
        addRoot.Enabled = hasCatalog;
        removeRoot.Enabled = hasCatalog;
        resetRoots.Enabled = hasCatalog;
        reindex.Enabled = hasCatalog;
        resetDisabledFields.Enabled = hasCatalog;
        addCondition.Enabled = hasCatalog;
        runQuery.Enabled = hasCatalog;
        enumerateIndex.Enabled = hasCatalog;
        indexBrowseCombo.Enabled = hasCatalog;
        queryGrid.Enabled = hasCatalog;
        rootsList.Enabled = hasCatalog;
        skipInput.Enabled = hasCatalog;
        lengthInput.Enabled = hasCatalog;
        identityModeInput.Enabled = hasCatalog;
        identityDisplayInput.Enabled = hasCatalog;
        keyDisplayInput.Enabled = hasCatalog;
        rowHydrationInput.Enabled = hasCatalog;
        displayFieldModeInput.Enabled = hasCatalog;
        useGroupBatchingInput.Enabled = hasCatalog;
        indexExtensionInput.Enabled = hasCatalog;
        indexStringFoldedInput.Enabled = hasCatalog;
        indexStringSortKeyInput.Enabled = hasCatalog;
        indexStringReversedInput.Enabled = hasCatalog;
        batchCommitFileCountInput.Enabled = hasCatalog;
        insertLayoutInput.Enabled = hasCatalog;
        writeOrderInput.Enabled = hasCatalog;
        writeVolumeInput.Enabled = hasCatalog;
        writeLocalityInput.Enabled = hasCatalog;
        writePriorityInput.Enabled = hasCatalog;
        SetConfigButtonEnabled(addRoot, hasCatalog);
        SetConfigButtonEnabled(removeRoot, hasCatalog);
        SetConfigButtonEnabled(resetRoots, hasCatalog);
        SetConfigButtonEnabled(reindex, hasCatalog);
        SetConfigButtonEnabled(resetDisabledFields, hasCatalog);
        SetConfigButtonEnabled(addCondition, hasCatalog);
        SetConfigButtonEnabled(runQuery, hasCatalog);
        SetConfigButtonEnabled(runStressCertification, hasCatalog);
        SetConfigButtonEnabled(enumerateIndex, hasCatalog);
        SetConfigButtonEnabled(clearLogAction, true);
        activeInstanceLabel.Text = hasCatalog
            ? $"Active LibraDex instance: {activeCatalog!.DisplayName}"
            : "No active LibraDex instance";
        activeInstanceLabel.Font = new Font(activeInstanceLabel.Font, hasCatalog ? FontStyle.Bold : FontStyle.Regular);
        activeInstanceLabel.ForeColor = hasCatalog ? Color.DarkGreen : SystemColors.GrayText;
        UpdateMetrics();
        UpdateConfigToggleText();
    }

    private static void SetConfigButtonEnabled(ToolStripItem owner, bool enabled)
    {
        if (owner.Tag is Button button)
        {
            button.Enabled = enabled;
        }
    }

    private void UpdateMetrics()
    {
        if (activeCatalog is null)
        {
            metricsLabel.Text = "Catalog: none";
            return;
        }

        SearchField field = GetSelectedIndexField();
        CatalogMetrics metrics = activeCatalog.GetMetrics(field);
        metricsLabel.Text = $"Files {metrics.FileCount:n0} | Roots {metrics.RootCount:n0} | Index {metrics.SelectedField} distinct {metrics.SelectedFieldDistinctKeys:n0} | {(metrics.FileBacked ? "file" : "memory")}";
    }

    private SearchField GetSelectedIndexField()
    {
        return Enum.TryParse(indexBrowseCombo.SelectedItem as string, out SearchField field)
            ? field
            : SearchField.FileName;
    }

    private int GetSkip()
    {
        return decimal.ToInt32(skipInput.Value);
    }

    private int GetLength()
    {
        return decimal.ToInt32(lengthInput.Value);
    }

    private FileSearchReindexOptions GetReindexOptions()
    {
        return new FileSearchReindexOptions
        {
            IdentityMode = GetIdentityMode(),
            UseGroupBatching = useGroupBatchingInput.Checked,
            BatchCommitFileCount = decimal.ToInt32(batchCommitFileCountInput.Value),
            IndexExtension = indexExtensionInput.Checked,
            IndexStringFolded = indexStringFoldedInput.Checked,
            IndexStringSortKey = indexStringSortKeyInput.Checked,
            IndexStringReversed = indexStringReversedInput.Checked,
            InsertLayout = ParseEnum(insertLayoutInput.SelectedItem as string, FileSearchInsertLayout.RecordMajor),
            WriteOrder = ParseEnum(writeOrderInput.SelectedItem as string, LibraDexWriteOrder.Default),
            WriteVolume = ParseEnum(writeVolumeInput.SelectedItem as string, LibraDexWriteVolume.Default),
            WriteLocality = ParseEnum(writeLocalityInput.SelectedItem as string, LibraDexWriteLocality.Default),
            WritePriority = ParseEnum(writePriorityInput.SelectedItem as string, LibraDexWritePriority.Default)
        };
    }

    private FileSearchReadOptions GetReadOptions(FileSearchCatalog? catalog = null)
    {
        FileSearchIdentityMode identityMode = catalog?.IdentityMode ?? GetIdentityMode();
        return new FileSearchReadOptions
        {
            IdentityMode = identityMode,
            RowHydrationMode = ParseEnum(rowHydrationInput.SelectedItem as string, FileSearchRowHydrationMode.SidecarCache),
            DisplayFieldMode = GetDisplayFieldMode(),
            IdentityDisplayMode = GetIdentityDisplayMode(identityMode),
            KeyDisplayMode = GetKeyDisplayMode()
        };
    }

    private FileSearchIdentityMode GetIdentityMode()
        => ParseEnum(identityModeInput.SelectedItem as string, FileSearchIdentityMode.SyntheticUInt64);

    private FileSearchDisplayFieldMode GetDisplayFieldMode()
        => ParseEnum(displayFieldModeInput.SelectedItem as string, FileSearchDisplayFieldMode.AllKnownFields);

    /// <summary>
    /// Reads the current identity display mode from the UI with a type-aware fallback.<br/>
    /// The fallback follows the selected identity value type so old/missing persisted state cannot select an impossible mode.<br/>
    /// </summary>
    private FileSearchValueDisplayMode GetIdentityDisplayMode()
        => GetIdentityDisplayMode(GetIdentityMode());

    private FileSearchValueDisplayMode GetIdentityDisplayMode(FileSearchIdentityMode identityMode)
        => ParseEnum(identityDisplayInput.SelectedItem as string, GetDefaultIdentityDisplayMode(identityMode));

    /// <summary>
    /// Reads the current key display mode from the UI with a selected-index fallback.<br/>
    /// This keeps direct tuple enumeration from accidentally using a date/string/numeric mode from a previous index choice.<br/>
    /// </summary>
    private FileSearchValueDisplayMode GetKeyDisplayMode()
        => ParseEnum(keyDisplayInput.SelectedItem as string, GetDefaultKeyDisplayMode(GetSelectedIndexField()));

    /// <summary>
    /// Rebuilds identity display choices for the currently selected identity value type.<br/>
    /// Synthetic UInt64 identities expose numeric views, while path-string identities expose text/byte presentation views.<br/>
    /// </summary>
    private void UpdateIdentityDisplayChoices(string? preferred)
    {
        FileSearchIdentityMode mode = GetIdentityMode();
        FileSearchValueDisplayMode[] choices = mode == FileSearchIdentityMode.PathString
            ? new[] { FileSearchValueDisplayMode.Text, FileSearchValueDisplayMode.RawBytesText, FileSearchValueDisplayMode.Bytes, FileSearchValueDisplayMode.Both }
            : new[] { FileSearchValueDisplayMode.Value, FileSearchValueDisplayMode.Hex, FileSearchValueDisplayMode.Both };
        ResetDisplayChoices(identityDisplayInput, choices, preferred, GetDefaultIdentityDisplayMode(mode));
    }

    /// <summary>
    /// Rebuilds key display choices for the currently selected index field.<br/>
    /// String keys expose text/byte views, date keys expose timestamp/raw views, and numeric keys expose decimal/hex views.<br/>
    /// </summary>
    private void UpdateKeyDisplayChoices(string? preferred)
    {
        SearchField field = GetSelectedIndexField();
        FileSearchValueDisplayMode[] choices = field switch
        {
            SearchField.FileName or SearchField.Extension => new[] { FileSearchValueDisplayMode.Text, FileSearchValueDisplayMode.RawBytesText, FileSearchValueDisplayMode.Bytes, FileSearchValueDisplayMode.Both },
            SearchField.CreatedUtc or SearchField.ModifiedUtc or SearchField.AccessedUtc => new[] { FileSearchValueDisplayMode.UtcDateTime, FileSearchValueDisplayMode.Value, FileSearchValueDisplayMode.Hex, FileSearchValueDisplayMode.Both },
            _ => new[] { FileSearchValueDisplayMode.Value, FileSearchValueDisplayMode.Hex, FileSearchValueDisplayMode.Both }
        };
        ResetDisplayChoices(keyDisplayInput, choices, preferred, GetDefaultKeyDisplayMode(field));
    }

    /// <summary>
    /// Replaces a display-mode combo's items while preserving a valid previous or persisted selection.<br/>
    /// Invalid selections are dropped immediately so downstream reads can stay branch-light and type-aware.<br/>
    /// </summary>
    private static void ResetDisplayChoices(ComboBox combo, IReadOnlyList<FileSearchValueDisplayMode> choices, string? preferred, FileSearchValueDisplayMode fallback)
    {
        string current = preferred ?? combo.SelectedItem as string ?? string.Empty;
        combo.BeginUpdate();
        combo.Items.Clear();
        for (int i = 0; i < choices.Count; i++)
        {
            combo.Items.Add(choices[i].ToString());
        }

        combo.EndUpdate();
        if (!string.IsNullOrWhiteSpace(current) && combo.Items.Contains(current))
        {
            combo.SelectedItem = current;
            return;
        }

        combo.SelectedItem = fallback.ToString();
    }

    /// <summary>
    /// Gets the default display mode for an identity value type.<br/>
    /// Defaults favor the logical value because that is the least surprising view for interactive tests.<br/>
    /// </summary>
    private static FileSearchValueDisplayMode GetDefaultIdentityDisplayMode(FileSearchIdentityMode mode)
        => mode == FileSearchIdentityMode.PathString ? FileSearchValueDisplayMode.Text : FileSearchValueDisplayMode.Value;

    /// <summary>
    /// Gets the default display mode for a selected index key field.<br/>
    /// Date keys default to UTC timestamps, string keys default to text, and numeric keys default to decimal values.<br/>
    /// </summary>
    private static FileSearchValueDisplayMode GetDefaultKeyDisplayMode(SearchField field)
        => field switch
        {
            SearchField.FileName or SearchField.Extension => FileSearchValueDisplayMode.Text,
            SearchField.CreatedUtc or SearchField.ModifiedUtc or SearchField.AccessedUtc => FileSearchValueDisplayMode.UtcDateTime,
            _ => FileSearchValueDisplayMode.Value
        };

    private void UpdateGridColumnVisibility()
    {
        if (resultsGrid.DataSource == resultRows)
        {
            ApplyFileGridColumnVisibility(resultsGrid, GetDisplayFieldMode());
        }

        if (indexGrid.DataSource == indexFileRows)
        {
            ApplyFileGridColumnVisibility(indexGrid, GetDisplayFieldMode());
        }
    }

    private static void ApplyFileGridColumnVisibility(DataGridView grid, FileSearchDisplayFieldMode mode)
    {
        for (int i = 0; i < grid.Columns.Count; i++)
        {
            DataGridViewColumn column = grid.Columns[i];
            string property = column.DataPropertyName;
            column.Visible = mode switch
            {
                FileSearchDisplayFieldMode.IdentityOnly => property is nameof(FileRecord.Id) or nameof(FileRecord.Path),
                FileSearchDisplayFieldMode.SelectedFields => property is nameof(FileRecord.Id) or nameof(FileRecord.Path) or nameof(FileRecord.FileName) or nameof(FileRecord.Extension) or nameof(FileRecord.Size) or nameof(FileRecord.ModifiedUtc),
                _ => true
            };
        }
    }

    /// <summary>
    /// Configures a grid for hydrated file-record display.<br/>
    /// This is the presentation path that can include sidecar or file-system hydration cost depending on the active read options.<br/>
    /// </summary>
    private static void ConfigureFileGrid(DataGridView grid, object dataSource)
    {
        ResetGrid(grid);
        grid.AutoGenerateColumns = false;
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Id", DataPropertyName = nameof(FileRecord.Id), Width = 80 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "File", DataPropertyName = nameof(FileRecord.FileName), Width = 220 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Ext", DataPropertyName = nameof(FileRecord.Extension), Width = 70 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Size", DataPropertyName = nameof(FileRecord.Size), Width = 100 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Modified UTC", DataPropertyName = nameof(FileRecord.ModifiedUtc), Width = 170 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Attributes", DataPropertyName = nameof(FileRecord.Attributes), Width = 90 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Path", DataPropertyName = nameof(FileRecord.Path), AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        grid.DataSource = dataSource;
    }

    /// <summary>
    /// Configures a grid for selected index key/identity tuple display.<br/>
    /// This is the direct LibraDex index path and does not hydrate file records.<br/>
    /// </summary>
    private static void ConfigureTupleGrid(DataGridView grid, object dataSource, string keyHeaderText)
    {
        ResetGrid(grid);
        grid.AutoGenerateColumns = false;
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = keyHeaderText, DataPropertyName = nameof(FileSearchTupleRow.Key), Width = 220 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Identity", DataPropertyName = nameof(FileSearchTupleRow.Identity), AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        grid.DataSource = dataSource;
    }

    /// <summary>
    /// Configures a grid for identity-only discovery output.<br/>
    /// This path is used for multi-index queries where a single key column would be misleading.<br/>
    /// </summary>
    private static void ConfigureIdentityGrid(DataGridView grid, object dataSource)
    {
        ResetGrid(grid);
        grid.AutoGenerateColumns = false;
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Identity", DataPropertyName = nameof(FileSearchIdentityRow.Identity), AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        grid.DataSource = dataSource;
    }

    private void BindFileRows(DataGridView grid, BindingList<FileRecord> boundRows, IReadOnlyList<FileRecord> rows)
    {
        if (ReferenceEquals(grid, indexGrid))
        {
            virtualIndexFileRows = rows.Count > VirtualGridThreshold ? rows : null;
        }
        else if (ReferenceEquals(grid, resultsGrid))
        {
            virtualResultFileRows = rows.Count > VirtualGridThreshold ? rows : null;
        }

        if (rows.Count > VirtualGridThreshold)
        {
            boundRows.Clear();
            BindVirtualRows(grid, new VirtualGridSource(VirtualGridKind.File, rows), rows.Count);
            return;
        }

        BindBoundRows(grid, boundRows, rows);
    }

    private static void BindTupleRows(DataGridView grid, BindingList<FileSearchTupleRow> boundRows, IReadOnlyList<FileSearchTupleRow> rows)
    {
        if (rows.Count > VirtualGridThreshold)
        {
            boundRows.Clear();
            BindVirtualRows(grid, new VirtualGridSource(VirtualGridKind.Tuple, rows), rows.Count);
            return;
        }

        BindBoundRows(grid, boundRows, rows);
    }

    private static void BindIdentityRows(DataGridView grid, BindingList<FileSearchIdentityRow> boundRows, IReadOnlyList<FileSearchIdentityRow> rows)
    {
        if (rows.Count > VirtualGridThreshold)
        {
            boundRows.Clear();
            BindVirtualRows(grid, new VirtualGridSource(VirtualGridKind.Identity, rows), rows.Count);
            return;
        }

        BindBoundRows(grid, boundRows, rows);
    }

    private static void BindBoundRows<T>(DataGridView grid, BindingList<T> boundRows, IReadOnlyList<T> rows)
    {
        grid.VirtualMode = false;
        grid.Tag = null;
        grid.DataSource = null;
        grid.RowCount = 0;
        grid.DataSource = boundRows;
        boundRows.RaiseListChangedEvents = false;
        boundRows.Clear();
        for (int i = 0; i < rows.Count; i++)
        {
            boundRows.Add(rows[i]);
        }

        boundRows.RaiseListChangedEvents = true;
        boundRows.ResetBindings();
    }

    private static void BindVirtualRows(DataGridView grid, VirtualGridSource source, int count)
    {
        grid.DataSource = null;
        grid.Tag = source;
        grid.VirtualMode = true;
        grid.RowCount = count;
    }

    private static void ResetGrid(DataGridView grid)
    {
        grid.DataSource = null;
        grid.VirtualMode = false;
        grid.Tag = null;
        grid.RowCount = 0;
        grid.Columns.Clear();
    }

    private static void OnGridCellValueNeeded(object? sender, DataGridViewCellValueEventArgs args)
    {
        if (sender is not DataGridView grid ||
            grid.Tag is not VirtualGridSource source ||
            args.RowIndex < 0 ||
            args.ColumnIndex < 0 ||
            args.ColumnIndex >= grid.Columns.Count)
        {
            return;
        }

        string propertyName = grid.Columns[args.ColumnIndex].DataPropertyName;
        args.Value = source.Kind switch
        {
            VirtualGridKind.File => GetVirtualFileValue((IReadOnlyList<FileRecord>)source.Rows, args.RowIndex, propertyName),
            VirtualGridKind.Tuple => GetVirtualTupleValue((IReadOnlyList<FileSearchTupleRow>)source.Rows, args.RowIndex, propertyName),
            VirtualGridKind.Identity => GetVirtualIdentityValue((IReadOnlyList<FileSearchIdentityRow>)source.Rows, args.RowIndex, propertyName),
            _ => null
        };
    }

    private static object? GetVirtualFileValue(IReadOnlyList<FileRecord> rows, int rowIndex, string propertyName)
    {
        if ((uint)rowIndex >= (uint)rows.Count)
        {
            return null;
        }

        FileRecord row = rows[rowIndex];
        return propertyName switch
        {
            nameof(FileRecord.Id) => row.Id,
            nameof(FileRecord.FileName) => row.FileName,
            nameof(FileRecord.Extension) => row.Extension,
            nameof(FileRecord.Size) => row.Size,
            nameof(FileRecord.ModifiedUtc) => row.ModifiedUtc,
            nameof(FileRecord.Attributes) => row.Attributes,
            nameof(FileRecord.Path) => row.Path,
            _ => null
        };
    }

    private static object? GetVirtualTupleValue(IReadOnlyList<FileSearchTupleRow> rows, int rowIndex, string propertyName)
    {
        if ((uint)rowIndex >= (uint)rows.Count)
        {
            return null;
        }

        FileSearchTupleRow row = rows[rowIndex];
        return propertyName switch
        {
            nameof(FileSearchTupleRow.Key) => row.Key,
            nameof(FileSearchTupleRow.Identity) => row.Identity,
            _ => null
        };
    }

    private static object? GetVirtualIdentityValue(IReadOnlyList<FileSearchIdentityRow> rows, int rowIndex, string propertyName)
    {
        if ((uint)rowIndex >= (uint)rows.Count)
        {
            return null;
        }

        FileSearchIdentityRow row = rows[rowIndex];
        return propertyName == nameof(FileSearchIdentityRow.Identity)
            ? row.Identity
            : null;
    }

    private static T ParseEnum<T>(string? value, T fallback)
        where T : struct, Enum
    {
        return Enum.TryParse(value, out T parsed)
            ? parsed
            : fallback;
    }

    private static string DescribeLength(int length)
    {
        return length <= 0 ? "all" : length.ToString("n0");
    }

    private void CompleteAction(string action, Stopwatch stopwatch)
    {
        stopwatch.Stop();
        string elapsed = FormatDuration(stopwatch.Elapsed);
        statusLabel.Text = $"{action} in {elapsed}.";
        FileSearchLog.Info($"{action} in {elapsed}.");
        RefreshLogViewer();
    }

    private void OnLogEntryWritten(string entry)
    {
        if (!IsHandleCreated)
        {
            return;
        }

        string? dispatchedEntry = null;
        if (busy && !ShouldDispatchBusyLogEntry(entry, out dispatchedEntry))
        {
            return;
        }

        DispatchLogEntry(dispatchedEntry ?? entry);
    }

    /// <summary>
    /// Decides whether a busy-operation log entry should be pushed to visible UI immediately.<br/>
    /// File logging remains unthrottled; this only protects the WinForms message pump from high-frequency batch commit labels.<br/>
    /// </summary>
    private bool ShouldDispatchBusyLogEntry(string entry, out string? dispatchedEntry)
    {
        dispatchedEntry = null;
        long now = Stopwatch.GetTimestamp();
        lock (logUiGate)
        {
            pendingBusyLogEntry = entry;
            double elapsedMs = (now - lastBusyLogUiDispatchTimestamp) * 1000.0 / Stopwatch.Frequency;
            if (lastBusyLogUiDispatchTimestamp != 0 && elapsedMs < BusyLogUiUpdateIntervalMs)
            {
                return false;
            }

            lastBusyLogUiDispatchTimestamp = now;
            dispatchedEntry = pendingBusyLogEntry;
            pendingBusyLogEntry = null;
            return true;
        }
    }

    /// <summary>
    /// Pushes the latest pending busy log entry to the visible status label after a busy operation ends.<br/>
    /// This keeps the final visible log line current without replaying every suppressed intermediate commit line.<br/>
    /// </summary>
    private void FlushPendingBusyLogEntry()
    {
        string? entry;
        lock (logUiGate)
        {
            entry = pendingBusyLogEntry;
            pendingBusyLogEntry = null;
            lastBusyLogUiDispatchTimestamp = 0;
        }

        if (!string.IsNullOrEmpty(entry))
        {
            DispatchLogEntry(entry);
        }
    }

    /// <summary>
    /// Marshals one visible log-entry update onto the WinForms UI thread.<br/>
    /// The method intentionally updates only the live labels/log viewer and never writes back to the log file.<br/>
    /// </summary>
    private void DispatchLogEntry(string entry)
    {
        try
        {
            BeginInvoke(new Action(() =>
            {
                lastLogLabel.Text = entry;
                if (tabs.SelectedTab is not null && tabs.SelectedTab.Text == "Log")
                {
                    RefreshLogViewerForLogEntry();
                }
            }));
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void RefreshLogViewerForLogEntry()
    {
        if (!busy)
        {
            RefreshLogViewer();
            deferredBusyLogRefresh = false;
            lastBusyLogRefreshTimestamp = Stopwatch.GetTimestamp();
            return;
        }

        deferredBusyLogRefresh = true;
        long now = Stopwatch.GetTimestamp();
        double elapsedMs = (now - lastBusyLogRefreshTimestamp) * 1000.0 / Stopwatch.Frequency;
        if (elapsedMs < BusyLogRefreshIntervalMs)
        {
            return;
        }

        RefreshLogViewer();
        deferredBusyLogRefresh = false;
        lastBusyLogRefreshTimestamp = now;
    }

    private void FlushDeferredBusyLogRefresh()
    {
        if (!deferredBusyLogRefresh ||
            tabs.SelectedTab is null ||
            tabs.SelectedTab.Text != "Log")
        {
            return;
        }

        RefreshLogViewer();
        deferredBusyLogRefresh = false;
        lastBusyLogRefreshTimestamp = Stopwatch.GetTimestamp();
    }

    private void RefreshLogViewer()
    {
        try
        {
            bool follow = logAutoScroll || IsLogViewerAtBottom();
            int firstVisibleLine = GetFirstVisibleLine(logViewer);
            int selectionStart = logViewer.SelectionStart;
            logViewer.Text = File.Exists(FileSearchLog.Path)
                ? ReadLogTail(FileSearchLog.Path)
                : $"No log file yet.{Environment.NewLine}{FileSearchLog.Path}";

            if (follow)
            {
                ScrollLogViewerToBottom();
                logAutoScroll = true;
            }
            else
            {
                logViewer.SelectionStart = Math.Min(selectionStart, logViewer.TextLength);
                ScrollLogViewerToLine(logViewer, firstVisibleLine);
            }
        }
        catch (Exception ex)
        {
            logViewer.Text = $"Could not read log file: {ex.Message}{Environment.NewLine}{FileSearchLog.Path}";
        }
    }

    private void UpdateLogAutoScroll()
    {
        logAutoScroll = IsLogViewerAtBottom();
    }

    private bool IsLogViewerAtBottom()
    {
        int lineCount = Math.Max(1, logViewer.Lines.Length);
        int visibleLines = Math.Max(1, logViewer.ClientSize.Height / Math.Max(1, logViewer.Font.Height));
        int firstVisibleLine = GetFirstVisibleLine(logViewer);
        return firstVisibleLine + visibleLines >= lineCount - 1;
    }

    private void ScrollLogViewerToBottom()
    {
        logViewer.SelectionStart = logViewer.TextLength;
        logViewer.SelectionLength = 0;
        logViewer.ScrollToCaret();
    }

    private static void ScrollLogViewerToLine(TextBox textBox, int line)
    {
        int current = GetFirstVisibleLine(textBox);
        _ = SendMessage(textBox.Handle, EmLineScroll, IntPtr.Zero, new IntPtr(line - current));
    }

    private static int GetFirstVisibleLine(TextBox textBox)
    {
        return SendMessage(textBox.Handle, EmGetFirstVisibleLine, IntPtr.Zero, IntPtr.Zero).ToInt32();
    }

    private static string ReadLogTail(string path)
    {
        FileInfo info = new(path);
        if (info.Length <= LogViewerMaxBytes)
        {
            return File.ReadAllText(path);
        }

        byte[] buffer = new byte[LogViewerMaxBytes];
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        stream.Position = info.Length - LogViewerMaxBytes;
        int read = stream.Read(buffer, 0, buffer.Length);
        string text = System.Text.Encoding.UTF8.GetString(buffer, 0, read);
        int firstLineBreak = text.IndexOf('\n');
        if (firstLineBreak >= 0 && firstLineBreak + 1 < text.Length)
        {
            text = text[(firstLineBreak + 1)..];
        }

        return $"-- showing last {LogViewerMaxBytes / 1024:n0} KiB of {info.Length / 1024:n0} KiB log --{Environment.NewLine}{text}";
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private static string FormatDuration(TimeSpan elapsed)
    {
        return elapsed.TotalSeconds >= 1
            ? $"{elapsed.TotalSeconds:n2}s"
            : $"{elapsed.TotalMilliseconds:n0}ms";
    }

    private void LoadUiState()
    {
        try
        {
            string path = GetUiStatePath();
            if (!File.Exists(path))
            {
                return;
            }

            using FileStream stream = File.OpenRead(path);
            FileSearchUiState? state = JsonSerializer.Deserialize<FileSearchUiState>(stream, UiJsonOptions);
            if (state is null)
            {
                return;
            }

            ApplyWindowState(state);
            ApplySplitterDistance(topSplit, state.TopSplitterDistance);
            ApplySplitterDistance(upperSplit, state.UpperSplitterDistance);
            if ((uint)state.SelectedTabIndex < (uint)tabs.TabPages.Count)
            {
                tabs.SelectedIndex = state.SelectedTabIndex;
            }

            if (!string.IsNullOrWhiteSpace(state.SelectedIndexField) &&
                indexBrowseCombo.Items.Contains(state.SelectedIndexField))
            {
                indexBrowseCombo.SelectedItem = state.SelectedIndexField;
            }

            configPanel.Visible = state.ConfigPanelExpanded;
            configPinnedInput.Checked = state.ConfigPanelPinned;
            if (state.ConfigPanelWidth >= 260 && state.ConfigPanelWidth <= 640)
            {
                configPanel.Width = state.ConfigPanelWidth;
            }

            SetNumericValue(skipInput, state.Skip);
            SetNumericValue(lengthInput, state.Length);
            SetComboValue(identityModeInput, state.IdentityMode);
            SetComboValue(rowHydrationInput, state.RowHydrationMode);
            SetComboValue(displayFieldModeInput, state.DisplayFieldMode);
            EnsureComboSelection(identityModeInput, FileSearchIdentityMode.SyntheticUInt64.ToString());
            UpdateIdentityDisplayChoices(string.IsNullOrWhiteSpace(state.IdentityDisplayMode) ? state.IdentityRenderMode : state.IdentityDisplayMode);
            UpdateKeyDisplayChoices(state.KeyDisplayMode);
            EnsureComboSelection(rowHydrationInput, FileSearchRowHydrationMode.SidecarCache.ToString());
            EnsureComboSelection(displayFieldModeInput, FileSearchDisplayFieldMode.AllKnownFields.ToString());
            useGroupBatchingInput.Checked = state.UseGroupBatching;
            indexExtensionInput.Checked = state.IndexExtension;
            indexStringFoldedInput.Checked = state.IndexStringFolded;
            indexStringSortKeyInput.Checked = state.IndexStringSortKey;
            indexStringReversedInput.Checked = state.IndexStringReversed;
            SetNumericValue(batchCommitFileCountInput, state.BatchCommitFileCount);
            SetComboValue(insertLayoutInput, state.InsertLayout);
            SetComboValue(writeOrderInput, state.WriteOrder);
            SetComboValue(writeVolumeInput, state.WriteVolume);
            SetComboValue(writeLocalityInput, state.WriteLocality);
            SetComboValue(writePriorityInput, state.WritePriority);
            EnsureComboSelection(insertLayoutInput, FileSearchInsertLayout.RecordMajor.ToString());
            UpdateConfigToggleText();
            UpdateGridColumnVisibility();
            ApplyColumnWidths(queryGrid, state.QueryColumnWidths);
            ApplyColumnWidths(indexGrid, state.IndexColumnWidths);
            ApplyColumnWidths(resultsGrid, state.ResultsColumnWidths);
        }
        catch
        {
            statusLabel.Text = "UI state could not be restored; using defaults.";
        }
    }

    private void SaveUiState()
    {
        try
        {
            Rectangle bounds = WindowState == FormWindowState.Normal
                ? Bounds
                : RestoreBounds;
            FileSearchUiState state = new()
            {
                X = bounds.X,
                Y = bounds.Y,
                Width = bounds.Width,
                Height = bounds.Height,
                IsMaximized = WindowState == FormWindowState.Maximized,
                TopSplitterDistance = topSplit.SplitterDistance,
                UpperSplitterDistance = upperSplit.SplitterDistance,
                SelectedTabIndex = tabs.SelectedIndex,
                ConfigPanelExpanded = configPanel.Visible,
                ConfigPanelPinned = configPinnedInput.Checked,
                ConfigPanelWidth = configPanel.Width,
                SelectedIndexField = indexBrowseCombo.SelectedItem as string ?? string.Empty,
                Skip = GetSkip(),
                Length = GetLength(),
                IdentityMode = identityModeInput.SelectedItem as string ?? string.Empty,
                IdentityRenderMode = identityDisplayInput.SelectedItem as string ?? string.Empty,
                IdentityDisplayMode = identityDisplayInput.SelectedItem as string ?? string.Empty,
                KeyDisplayMode = keyDisplayInput.SelectedItem as string ?? string.Empty,
                RowHydrationMode = rowHydrationInput.SelectedItem as string ?? string.Empty,
                DisplayFieldMode = displayFieldModeInput.SelectedItem as string ?? string.Empty,
                UseGroupBatching = useGroupBatchingInput.Checked,
                BatchCommitFileCount = decimal.ToInt32(batchCommitFileCountInput.Value),
                IndexExtension = indexExtensionInput.Checked,
                IndexStringFolded = indexStringFoldedInput.Checked,
                IndexStringSortKey = indexStringSortKeyInput.Checked,
                IndexStringReversed = indexStringReversedInput.Checked,
                InsertLayout = insertLayoutInput.SelectedItem as string ?? string.Empty,
                WriteOrder = writeOrderInput.SelectedItem as string ?? string.Empty,
                WriteVolume = writeVolumeInput.SelectedItem as string ?? string.Empty,
                WriteLocality = writeLocalityInput.SelectedItem as string ?? string.Empty,
                WritePriority = writePriorityInput.SelectedItem as string ?? string.Empty,
                QueryColumnWidths = CaptureColumnWidths(queryGrid),
                IndexColumnWidths = CaptureColumnWidths(indexGrid),
                ResultsColumnWidths = CaptureColumnWidths(resultsGrid)
            };

            string path = GetUiStatePath();
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using FileStream stream = File.Create(path);
            JsonSerializer.Serialize(stream, state, UiJsonOptions);
        }
        catch
        {
            // UI shape persistence is convenience only; do not block app shutdown.
        }
    }

    private void ApplyWindowState(FileSearchUiState state)
    {
        Rectangle bounds = new(state.X, state.Y, state.Width, state.Height);
        if (state.Width < 640 || state.Height < 480 || !IsVisibleOnAnyScreen(bounds))
        {
            return;
        }

        StartPosition = FormStartPosition.Manual;
        Bounds = bounds;
        if (state.IsMaximized)
        {
            WindowState = FormWindowState.Maximized;
        }
    }

    private static void SetNumericValue(NumericUpDown input, int value)
    {
        if (value < input.Minimum || value > input.Maximum)
        {
            return;
        }

        input.Value = value;
    }

    private static void SetComboValue(ComboBox input, string value)
    {
        if (!string.IsNullOrWhiteSpace(value) && input.Items.Contains(value))
        {
            input.SelectedItem = value;
        }
    }

    private static void EnsureComboSelection(ComboBox input, string fallback)
    {
        if (input.SelectedItem is not null)
        {
            return;
        }

        if (input.Items.Contains(fallback))
        {
            input.SelectedItem = fallback;
        }
    }

    private static void ApplySplitterDistance(SplitContainer split, int distance)
    {
        if (distance <= 0)
        {
            return;
        }

        int maxDistance = split.Orientation == Orientation.Horizontal
            ? split.Height - split.Panel2MinSize
            : split.Width - split.Panel2MinSize;
        if (distance >= split.Panel1MinSize && distance < maxDistance)
        {
            split.SplitterDistance = distance;
        }
    }

    private static void ApplyColumnWidths(DataGridView grid, int[] widths)
    {
        int count = Math.Min(grid.Columns.Count, widths.Length);
        for (int i = 0; i < count; i++)
        {
            if (widths[i] > 24)
            {
                grid.Columns[i].Width = widths[i];
            }
        }
    }

    private static int[] CaptureColumnWidths(DataGridView grid)
    {
        int[] widths = new int[grid.Columns.Count];
        for (int i = 0; i < widths.Length; i++)
        {
            widths[i] = grid.Columns[i].Width;
        }

        return widths;
    }

    private static bool IsVisibleOnAnyScreen(Rectangle bounds)
    {
        for (int i = 0; i < Screen.AllScreens.Length; i++)
        {
            if (Screen.AllScreens[i].WorkingArea.IntersectsWith(bounds))
            {
                return true;
            }
        }

        return false;
    }

    private static string GetUiStatePath()
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(root, "LibraDex", "FileSearch", "ui-state.json");
    }

    /// <summary>
    /// Throttles background reindex progress before it reaches the WinForms message queue.<br/>
    /// The worker can report fine-grained collection, commit, and index-pass messages, but the UI receives only the latest message at the configured interval.<br/>
    /// This avoids the IndexMajor commit path queuing thousands of label updates while preserving cancellation and detailed file logging.<br/>
    /// </summary>
    private sealed class ThrottledUiProgress : IProgress<string>
    {
        private readonly Control owner;
        private readonly Action<string> update;
        private readonly int intervalMs;
        private readonly object gate = new();
        private long lastDispatchTimestamp;
        private string? pendingText;

        /// <summary>
        /// Creates a throttled UI progress bridge for background work.<br/>
        /// The owner control supplies the UI-thread marshal target, while the update action owns the actual visible state mutation.<br/>
        /// </summary>
        public ThrottledUiProgress(Control owner, Action<string> update, int intervalMs)
        {
            this.owner = owner;
            this.update = update;
            this.intervalMs = Math.Max(1, intervalMs);
        }

        /// <summary>
        /// Records the latest progress message and posts it to the UI thread only when the throttle interval has elapsed.<br/>
        /// Intermediate messages are intentionally collapsed because they are diagnostic detail, not state that must be rendered frame-by-frame.<br/>
        /// </summary>
        public void Report(string value)
        {
            long now = Stopwatch.GetTimestamp();
            string? text;
            lock (gate)
            {
                pendingText = value;
                double elapsedMs = (now - lastDispatchTimestamp) * 1000.0 / Stopwatch.Frequency;
                if (lastDispatchTimestamp != 0 && elapsedMs < intervalMs)
                {
                    return;
                }

                lastDispatchTimestamp = now;
                text = pendingText;
                pendingText = null;
            }

            if (string.IsNullOrEmpty(text) || owner.IsDisposed)
            {
                return;
            }

            try
            {
                owner.BeginInvoke(new Action(() => update(text)));
            }
            catch (ObjectDisposedException)
            {
            }
            catch (InvalidOperationException)
            {
            }
        }
    }
}
