using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
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
            ItemsSource = records
        };

        grid.Columns.Add(CreateTextColumn("#", nameof(ChineseTextRecord.Id), 56));
        grid.Columns.Add(CreateTextColumn("中文内容", nameof(ChineseTextRecord.Text), 2, true));
        grid.Columns.Add(CreateTextColumn("来源类型", nameof(ChineseTextRecord.SourceType), 130));
        grid.Columns.Add(CreateTextColumn("分类", nameof(ChineseTextRecord.Category), 150));
        grid.Columns.Add(CreateTextColumn("可信度", nameof(ChineseTextRecord.Confidence), 80));
        grid.Columns.Add(CreateTextColumn("是否乱码", nameof(ChineseTextRecord.GarbledStatus), 86));
        grid.Columns.Add(CreateTextColumn("乱码判断", nameof(ChineseTextRecord.GarbledReason), 180));
        grid.Columns.Add(CreateTextColumn("文件路径", nameof(ChineseTextRecord.RelativePath), 2, true));
        grid.Columns.Add(CreateTextColumn("行", nameof(ChineseTextRecord.LineNumber), 70));
        grid.Columns.Add(CreateTextColumn("字段", nameof(ChineseTextRecord.FieldName), 130));
        grid.Columns.Add(CreateTextColumn("对象/资源名", nameof(ChineseTextRecord.ObjectPath), 150));
        grid.Columns.Add(CreateTextColumn("备注", nameof(ChineseTextRecord.Notes), 220));

        return grid;
    }

    private static System.Windows.Controls.DataGridTextColumn CreateTextColumn(string header, string bindingPath, double width, bool star = false)
    {
        return new System.Windows.Controls.DataGridTextColumn
        {
            Header = header,
            Binding = new System.Windows.Data.Binding(bindingPath),
            Width = star
                ? new System.Windows.Controls.DataGridLength(width, System.Windows.Controls.DataGridLengthUnitType.Star)
                : new System.Windows.Controls.DataGridLength(width)
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
        SummaryTextBlock.Text = $"{tabName}：{records.Count} 条，去重后 {deduplicatedCount} 条；全部 {allRecords.Count} 条";
    }

    private void SetBusy(bool isBusy)
    {
        ScanButton.IsEnabled = !isBusy;
        ExportButton.IsEnabled = !isBusy && allRecords.Count > 0;
        IncludeThirdPartyCheckBox.IsEnabled = !isBusy;
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
