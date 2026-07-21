namespace LibraDex.FileSearch;

internal sealed class FileSearchUiState
{
    public int X { get; set; }

    public int Y { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    public bool IsMaximized { get; set; }

    public int TopSplitterDistance { get; set; }

    public int UpperSplitterDistance { get; set; }

    public int SelectedTabIndex { get; set; }

    public bool ConfigPanelExpanded { get; set; } = true;

    public bool ConfigPanelPinned { get; set; } = true;

    public int ConfigPanelWidth { get; set; } = 340;

    public string SelectedIndexField { get; set; } = string.Empty;

    public int Skip { get; set; }

    public int Length { get; set; } = 5000;

    public string IdentityMode { get; set; } = string.Empty;

    public string IdentityRenderMode { get; set; } = string.Empty;

    public string IdentityDisplayMode { get; set; } = string.Empty;

    public string KeyDisplayMode { get; set; } = string.Empty;

    public string RowHydrationMode { get; set; } = string.Empty;

    public string DisplayFieldMode { get; set; } = string.Empty;

    public bool UseGroupBatching { get; set; }

    public int BatchCommitFileCount { get; set; } = 10000;

    public bool IndexExtension { get; set; }

    public bool IndexStringFolded { get; set; }

    public bool IndexStringSortKey { get; set; }

    public bool IndexStringReversed { get; set; }

    public string InsertLayout { get; set; } = string.Empty;

    public string WriteOrder { get; set; } = string.Empty;

    public string WriteVolume { get; set; } = string.Empty;

    public string WriteLocality { get; set; } = string.Empty;

    public string WritePriority { get; set; } = string.Empty;

    public int[] QueryColumnWidths { get; set; } = Array.Empty<int>();

    public int[] IndexColumnWidths { get; set; } = Array.Empty<int>();

    public int[] ResultsColumnWidths { get; set; } = Array.Empty<int>();
}
