using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Microsoft.Win32;
using ProjectChineseTextCollector.Models;
using ProjectChineseTextCollector.Services;
using WinForms = System.Windows.Forms;
using WpfMessageBox = System.Windows.MessageBox;
using WpfSaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace ProjectChineseTextCollector;

public partial class MainWindow : Window
{
    private const string AllTabName = "全部中文文本";
    private const string GarbledTabName = "疑似乱码文本";

    private static readonly string[] PreferredTabOrder =
    [
        AllTabName,
        GarbledTabName,
        "本地化表文本",
        "固定UI文本",
        "资源配置文本",
        "脚本候选文本",
        "需要人工确认",
        "序列化文本",
        "疑似非游戏内文本",
        "其他文本",
        "扫描失败"
    ];

    private readonly UnityChineseTextScanner scanner = new();
    private readonly ExcelExporter exporter = new();
    private List<ChineseTextRecord> allRecords = [];

    public MainWindow()
    {
        InitializeComponent();
        InitializeDefaults();
    }

    private void InitializeDefaults()
    {
        string defaultProject = @"D:\Unity Data\AbyaPB";
        if (Directory.Exists(defaultProject))
            ProjectPathTextBox.Text = defaultProject;

        RefreshResultTabs();
    }

    private void BrowseProjectButton_Click(object sender, RoutedEventArgs e)
    {
        using WinForms.FolderBrowserDialog dialog = new()
        {
            Description = "选择 Unity 项目根目录",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(ProjectPathTextBox.Text) ? ProjectPathTextBox.Text : string.Empty
        };

        if (dialog.ShowDialog() == WinForms.DialogResult.OK)
            ProjectPathTextBox.Text = dialog.SelectedPath;
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        string projectPath = ProjectPathTextBox.Text.Trim();
        if (!Directory.Exists(projectPath))
        {
            WpfMessageBox.Show("项目路径不存在。", "无法扫描", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SetBusy(true);
        allRecords = [];
        ResultsTabControl.Items.Clear();
        SummaryTextBlock.Text = string.Empty;

        Progress<ScanProgress> progress = new(report =>
        {
            UpdateScanProgress(report);
        });

        try
        {
            IReadOnlyList<ChineseTextRecord> result = await scanner.ScanAsync(
                projectPath,
                IncludeThirdPartyCheckBox.IsChecked == true,
                GetExclusionItems(ExcludedFilesListBox),
                GetExclusionItems(ExcludedFoldersListBox),
                progress,
                CancellationToken.None);

            allRecords = result.ToList();
            RefreshResultTabs();

            StatusTextBlock.Text = $"扫描完成：找到 {allRecords.Count} 条中文文本，去重后 {allRecords.Select(record => record.Text).Distinct().Count()} 条。";
            ProgressPercentTextBlock.Text = "100%";
            ProgressFileTextBlock.Text = "文件完成";
            ProgressRecordTextBlock.Text = $"结果 {allRecords.Count}";
            CurrentFileTextBlock.Text = "扫描完成";
            ScanProgressBar.Value = 100;
            ExportButton.IsEnabled = allRecords.Count > 0;
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = "扫描失败。";
            CurrentFileTextBlock.Text = "扫描失败";
            WpfMessageBox.Show(ex.Message, "扫描失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void AddExcludedFilesButton_Click(object sender, RoutedEventArgs e)
    {
        Microsoft.Win32.OpenFileDialog dialog = new()
        {
            Title = "选择要排除的文件",
            Multiselect = true,
            InitialDirectory = GetDialogInitialDirectory()
        };

        if (dialog.ShowDialog(this) != true)
            return;

        foreach (string fileName in dialog.FileNames)
            AddUniqueExclusionItem(ExcludedFilesListBox, ToProjectRelativeItem(fileName));
    }

    private void AddExcludedFoldersButton_Click(object sender, RoutedEventArgs e)
    {
        Microsoft.Win32.OpenFolderDialog dialog = new()
        {
            Title = "选择要排除的文件夹",
            Multiselect = true,
            InitialDirectory = GetDialogInitialDirectory()
        };

        if (dialog.ShowDialog(this) != true)
            return;

        foreach (string folderName in dialog.FolderNames)
            AddUniqueExclusionItem(ExcludedFoldersListBox, ToProjectRelativeItem(folderName));
    }

    private void RemoveExcludedFilesButton_Click(object sender, RoutedEventArgs e)
    {
        RemoveSelectedExclusionItems(ExcludedFilesListBox);
    }

    private void RemoveExcludedFoldersButton_Click(object sender, RoutedEventArgs e)
    {
        RemoveSelectedExclusionItems(ExcludedFoldersListBox);
    }

    private void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (allRecords.Count == 0)
        {
            WpfMessageBox.Show("还没有扫描结果。", "无法导出", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string exportDirectory = Path.Combine(AppContext.BaseDirectory, "Exports");
        Directory.CreateDirectory(exportDirectory);

        WpfSaveFileDialog dialog = new()
        {
            Title = "导出中文文本清单",
            Filter = "Excel 工作簿 (*.xlsx)|*.xlsx",
            InitialDirectory = exportDirectory,
            FileName = $"ChineseTextReport_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx"
        };

        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            exporter.Export(dialog.FileName, allRecords);
            StatusTextBlock.Text = $"导出完成：{dialog.FileName}";
            WpfMessageBox.Show("Excel 已导出完成。", "导出完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            WpfMessageBox.Show(ex.Message, "导出失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
            return;

        RefreshResultTabs();
    }

    private void ResultsTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource != ResultsTabControl)
            return;

        UpdateSummaryForSelectedTab();
    }

    private void RefreshResultTabs()
    {
        string selectedTab = (ResultsTabControl.SelectedItem as TabItem)?.Tag as string ?? AllTabName;
        List<ChineseTextRecord> filtered = FilterRecordsByKeyword(allRecords).ToList();
        if (DeduplicateCheckBox.IsChecked == true)
            filtered = DeduplicateRecords(filtered);

        List<ResultTabData> tabs = BuildTabData(filtered);

        ResultsTabControl.SelectionChanged -= ResultsTabControl_SelectionChanged;
        ResultsTabControl.Items.Clear();

        foreach (ResultTabData tabData in tabs)
        {
            TabItem tab = new()
            {
                Header = $"{tabData.Name} ({tabData.Records.Count})",
                Tag = tabData.Name,
                DataContext = tabData.Records,
                Content = CreateRecordsGrid(tabData.Records)
            };
            ResultsTabControl.Items.Add(tab);
        }

        ResultsTabControl.SelectionChanged += ResultsTabControl_SelectionChanged;

        TabItem? itemToSelect = ResultsTabControl.Items
            .OfType<TabItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, selectedTab, StringComparison.Ordinal));

        ResultsTabControl.SelectedItem = itemToSelect ?? ResultsTabControl.Items.OfType<TabItem>().FirstOrDefault();
        UpdateSummaryForSelectedTab();
    }

    private IEnumerable<ChineseTextRecord> FilterRecordsByKeyword(IEnumerable<ChineseTextRecord> records)
    {
        string keyword = SearchTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(keyword))
            return records;

        return records.Where(record =>
            record.Text.Contains(keyword, StringComparison.OrdinalIgnoreCase)
            || record.SourceType.Contains(keyword, StringComparison.OrdinalIgnoreCase)
            || record.RelativePath.Contains(keyword, StringComparison.OrdinalIgnoreCase)
            || record.Notes.Contains(keyword, StringComparison.OrdinalIgnoreCase)
            || record.Category.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    private static List<ChineseTextRecord> DeduplicateRecords(IEnumerable<ChineseTextRecord> records)
    {
        HashSet<string> seenTexts = new(StringComparer.Ordinal);
        List<ChineseTextRecord> deduplicated = [];
        foreach (ChineseTextRecord record in records)
        {
            if (seenTexts.Add(record.Text))
                deduplicated.Add(record);
        }

        return deduplicated;
    }

    private static IReadOnlyList<string> GetExclusionItems(System.Windows.Controls.ListBox listBox)
    {
        return listBox.Items
            .OfType<string>()
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToList();
    }

    private static void AddUniqueExclusionItem(System.Windows.Controls.ListBox listBox, string item)
    {
        if (string.IsNullOrWhiteSpace(item))
            return;

        bool exists = listBox.Items
            .OfType<string>()
            .Any(existing => string.Equals(existing, item, StringComparison.OrdinalIgnoreCase));
        if (!exists)
            listBox.Items.Add(item);
    }

    private static void RemoveSelectedExclusionItems(System.Windows.Controls.ListBox listBox)
    {
        List<object> selectedItems = listBox.SelectedItems.Cast<object>().ToList();
        foreach (object item in selectedItems)
            listBox.Items.Remove(item);
    }

    private string GetDialogInitialDirectory()
    {
        string projectPath = ProjectPathTextBox.Text.Trim();
        if (Directory.Exists(projectPath))
            return projectPath;

        return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    private string ToProjectRelativeItem(string path)
    {
        string projectPath = ProjectPathTextBox.Text.Trim();
        if (!Directory.Exists(projectPath))
            return path;

        try
        {
            string relativePath = Path.GetRelativePath(projectPath, path);
            if (!relativePath.StartsWith("..", StringComparison.Ordinal)
                && !Path.IsPathRooted(relativePath))
            {
                return relativePath;
            }
        }
        catch (ArgumentException)
        {
            return path;
        }
        catch (NotSupportedException)
        {
            return path;
        }

        return path;
    }

    private static List<ResultTabData> BuildTabData(List<ChineseTextRecord> filtered)
    {
        Dictionary<string, List<ChineseTextRecord>> bySourceType = filtered
            .GroupBy(record => record.SourceType)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

        List<ResultTabData> tabs = [new ResultTabData(AllTabName, filtered)];
        List<ChineseTextRecord> garbledRecords = filtered.Where(record => record.IsGarbled).ToList();
        if (garbledRecords.Count > 0)
            tabs.Add(new ResultTabData(GarbledTabName, garbledRecords));

        foreach (string sourceType in PreferredTabOrder.Skip(2))
        {
            if (bySourceType.Remove(sourceType, out List<ChineseTextRecord>? records))
                tabs.Add(new ResultTabData(sourceType, records));
        }

        foreach (KeyValuePair<string, List<ChineseTextRecord>> remaining in bySourceType.OrderBy(pair => pair.Key))
            tabs.Add(new ResultTabData(remaining.Key, remaining.Value));

        return tabs;
    }

    private static System.Windows.Controls.DataGrid CreateRecordsGrid(IReadOnlyList<ChineseTextRecord> records)
    {
        System.Windows.Controls.DataGrid grid = new()
        {
            AutoGenerateColumns = false,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            EnableColumnVirtualization = true,
            EnableRowVirtualization = true,
            IsReadOnly = true,
            RowHeaderWidth = 0,
            SelectionMode = System.Windows.Controls.DataGridSelectionMode.Extended,
            SelectionUnit = System.Windows.Controls.DataGridSelectionUnit.CellOrRowHeader,
            ClipboardCopyMode = System.Windows.Controls.DataGridClipboardCopyMode.IncludeHeader,
            ItemsSource = records
        };

        ScrollViewer.SetHorizontalScrollBarVisibility(grid, ScrollBarVisibility.Visible);
        ScrollViewer.SetVerticalScrollBarVisibility(grid, ScrollBarVisibility.Auto);
        grid.PreviewKeyDown += RecordsGrid_PreviewKeyDown;
        grid.PreviewMouseRightButtonDown += RecordsGrid_PreviewMouseRightButtonDown;
        grid.CommandBindings.Add(new CommandBinding(
            ApplicationCommands.Copy,
            CopyRecordsGridSelection_Executed,
            CopyRecordsGridSelection_CanExecute));

        System.Windows.Controls.ContextMenu contextMenu = new();
        System.Windows.Controls.MenuItem copyMenuItem = new()
        {
            Header = "复制选中"
        };
        copyMenuItem.Click += (_, _) => CopyRecordsGridToClipboard(grid);
        contextMenu.Items.Add(copyMenuItem);
        grid.ContextMenu = contextMenu;

        grid.Columns.Add(CreateTextColumn("#", nameof(ChineseTextRecord.Id), 56));
        grid.Columns.Add(CreateTextColumn("中文内容", nameof(ChineseTextRecord.Text), 420));
        grid.Columns.Add(CreateTextColumn("来源类型", nameof(ChineseTextRecord.SourceType), 130));
        grid.Columns.Add(CreateTextColumn("分类", nameof(ChineseTextRecord.Category), 150));
        grid.Columns.Add(CreateTextColumn("可信度", nameof(ChineseTextRecord.Confidence), 80));
        grid.Columns.Add(CreateTextColumn("是否乱码", nameof(ChineseTextRecord.GarbledStatus), 86));
        grid.Columns.Add(CreateTextColumn("乱码判断", nameof(ChineseTextRecord.GarbledReason), 180));
        grid.Columns.Add(CreateTextColumn("文件路径", nameof(ChineseTextRecord.RelativePath), 640));
        grid.Columns.Add(CreateTextColumn("行", nameof(ChineseTextRecord.LineNumber), 70));
        grid.Columns.Add(CreateTextColumn("字段", nameof(ChineseTextRecord.FieldName), 130));
        grid.Columns.Add(CreateTextColumn("对象/资源名", nameof(ChineseTextRecord.ObjectPath), 150));
        grid.Columns.Add(CreateTextColumn("备注", nameof(ChineseTextRecord.Notes), 360));

        return grid;
    }

    private static void RecordsGrid_PreviewMouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.DataGrid grid
            || e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        System.Windows.Controls.DataGridCell? cell = FindVisualParent<System.Windows.Controls.DataGridCell>(source);
        if (cell?.DataContext is not ChineseTextRecord record)
            return;

        cell.Focus();
        System.Windows.Controls.DataGridCellInfo cellInfo = new(record, cell.Column);
        grid.CurrentCell = cellInfo;
        if (!grid.SelectedCells.Contains(cellInfo))
        {
            grid.SelectedCells.Clear();
            grid.SelectedCells.Add(cellInfo);
        }
    }

    private static void RecordsGrid_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (sender is not System.Windows.Controls.DataGrid grid
            || e.Key != Key.C
            || (Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control)
        {
            return;
        }

        string text = BuildClipboardText(grid);
        if (string.IsNullOrEmpty(text)
            && grid.CurrentCell.Item is ChineseTextRecord currentRecord
            && grid.CurrentCell.Column is not null)
        {
            text = GetColumnText(grid.CurrentCell.Column, currentRecord);
        }

        if (!string.IsNullOrEmpty(text))
        {
            System.Windows.Clipboard.SetText(text);
            e.Handled = true;
        }
    }

    private static void CopyRecordsGridToClipboard(System.Windows.Controls.DataGrid grid)
    {
        string text = BuildClipboardText(grid);
        if (string.IsNullOrEmpty(text)
            && grid.CurrentCell.Item is ChineseTextRecord currentRecord
            && grid.CurrentCell.Column is not null)
        {
            text = GetColumnText(grid.CurrentCell.Column, currentRecord);
        }

        if (!string.IsNullOrEmpty(text))
            System.Windows.Clipboard.SetText(text);
    }

    private static void CopyRecordsGridSelection_CanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.DataGrid grid)
            return;

        e.CanExecute = grid.SelectedCells.Count > 0
                       || grid.SelectedItems.Count > 0
                       || grid.CurrentCell.Item is ChineseTextRecord;
        e.Handled = true;
    }

    private static void CopyRecordsGridSelection_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.DataGrid grid)
            return;

        string text = BuildClipboardText(grid);
        if (string.IsNullOrEmpty(text)
            && grid.CurrentCell.Item is ChineseTextRecord currentRecord
            && grid.CurrentCell.Column is not null)
        {
            text = GetColumnText(grid.CurrentCell.Column, currentRecord);
        }

        if (!string.IsNullOrEmpty(text))
            System.Windows.Clipboard.SetText(text);

        e.Handled = true;
    }

    private static string BuildClipboardText(System.Windows.Controls.DataGrid grid)
    {
        if (grid.SelectedCells.Count > 0)
        {
            List<System.Windows.Controls.DataGridCellInfo> selectedCells = grid.SelectedCells
                .Where(cell => cell.Item is ChineseTextRecord)
                .OrderBy(cell => GetRecordGridIndex(grid, (ChineseTextRecord)cell.Item))
                .ThenBy(cell => grid.Columns.IndexOf(cell.Column))
                .ToList();

            return BuildClipboardTextFromCells(grid, selectedCells);
        }

        List<ChineseTextRecord> selectedRows = grid.SelectedItems
            .OfType<ChineseTextRecord>()
            .OrderBy(record => GetRecordGridIndex(grid, record))
            .ToList();

        if (selectedRows.Count == 0)
            return string.Empty;

        return string.Join(
            Environment.NewLine,
            selectedRows.Select(record => string.Join("\t", grid.Columns.Select(column => GetColumnText(column, record)))));
    }

    private static string BuildClipboardTextFromCells(
        System.Windows.Controls.DataGrid grid,
        IReadOnlyList<System.Windows.Controls.DataGridCellInfo> selectedCells)
    {
        if (selectedCells.Count == 0)
            return string.Empty;

        List<string> lines = [];
        foreach (IGrouping<ChineseTextRecord, System.Windows.Controls.DataGridCellInfo> rowGroup in selectedCells.GroupBy(cell => (ChineseTextRecord)cell.Item))
        {
            List<string> values = rowGroup
                .OrderBy(cell => grid.Columns.IndexOf(cell.Column))
                .Select(cell => GetColumnText(cell.Column, rowGroup.Key))
                .ToList();
            lines.Add(string.Join("\t", values));
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static int GetRecordGridIndex(System.Windows.Controls.DataGrid grid, ChineseTextRecord record)
    {
        return grid.Items.IndexOf(record);
    }

    private static string GetColumnText(System.Windows.Controls.DataGridColumn column, ChineseTextRecord record)
    {
        if (column is not System.Windows.Controls.DataGridTextColumn textColumn
            || textColumn.Binding is not System.Windows.Data.Binding binding
            || string.IsNullOrWhiteSpace(binding.Path.Path))
        {
            return string.Empty;
        }

        object? value = typeof(ChineseTextRecord).GetProperty(binding.Path.Path)?.GetValue(record);
        return value?.ToString() ?? string.Empty;
    }

    private static T? FindVisualParent<T>(DependencyObject source)
        where T : DependencyObject
    {
        DependencyObject? current = source;
        while (current is not null)
        {
            if (current is T target)
                return target;

            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private static System.Windows.Controls.DataGridTextColumn CreateTextColumn(string header, string bindingPath, double width)
    {
        return new System.Windows.Controls.DataGridTextColumn
        {
            Header = header,
            Binding = new System.Windows.Data.Binding(bindingPath),
            Width = new System.Windows.Controls.DataGridLength(width)
        };
    }

    private void UpdateSummaryForSelectedTab()
    {
        if (ResultsTabControl.SelectedItem is not TabItem selectedTab
            || selectedTab.DataContext is not IReadOnlyList<ChineseTextRecord> records)
        {
            SummaryTextBlock.Text = allRecords.Count == 0 ? "还没有扫描结果" : $"总计 {allRecords.Count} 条";
            return;
        }

        string tabName = selectedTab.Tag as string ?? AllTabName;
        int deduplicatedCount = records.Select(record => record.Text).Distinct().Count();
        string deduplicateStatus = DeduplicateCheckBox.IsChecked == true ? "；当前为去重显示" : $"，去重后 {deduplicatedCount} 条";
        SummaryTextBlock.Text = $"{tabName}：{records.Count} 条{deduplicateStatus}；全部 {allRecords.Count} 条";
    }

    private void SetBusy(bool isBusy)
    {
        ScanButton.IsEnabled = !isBusy;
        ExportButton.IsEnabled = !isBusy && allRecords.Count > 0;
        IncludeThirdPartyCheckBox.IsEnabled = !isBusy;
        ExcludedFilesListBox.IsEnabled = !isBusy;
        ExcludedFoldersListBox.IsEnabled = !isBusy;
        AddExcludedFilesButton.IsEnabled = !isBusy;
        RemoveExcludedFilesButton.IsEnabled = !isBusy;
        AddExcludedFoldersButton.IsEnabled = !isBusy;
        RemoveExcludedFoldersButton.IsEnabled = !isBusy;
        if (isBusy)
        {
            ScanProgressBar.Value = 0;
            ScanProgressBar.IsIndeterminate = true;
            ProgressPercentTextBlock.Text = "准备中";
            ProgressFileTextBlock.Text = "文件 0/0";
            ProgressRecordTextBlock.Text = "结果 0";
            StatusTextBlock.Text = "正在统计需要扫描的文件...";
            CurrentFileTextBlock.Text = string.Empty;
        }
        else
        {
            ScanProgressBar.IsIndeterminate = false;
        }
    }

    private void UpdateScanProgress(ScanProgress report)
    {
        ScanProgressBar.IsIndeterminate = false;

        double percent = report.TotalFiles > 0
            ? Math.Clamp(report.ProcessedFiles * 100.0 / report.TotalFiles, 0, 100)
            : 0;

        ScanProgressBar.Value = percent;
        ProgressPercentTextBlock.Text = $"{percent:0.0}%";
        ProgressFileTextBlock.Text = $"文件 {report.ProcessedFiles}/{report.TotalFiles}";
        ProgressRecordTextBlock.Text = $"结果 {report.Records}";
        StatusTextBlock.Text = "正在扫描项目中文文本...";
        CurrentFileTextBlock.Text = string.IsNullOrWhiteSpace(report.CurrentFile)
            ? "准备扫描文件..."
            : report.CurrentFile;
    }

    private sealed record ResultTabData(string Name, IReadOnlyList<ChineseTextRecord> Records);
}
