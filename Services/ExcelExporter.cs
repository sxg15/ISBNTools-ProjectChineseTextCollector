using System.IO;
using ClosedXML.Excel;
using ProjectChineseTextCollector.Models;

namespace ProjectChineseTextCollector.Services;

public sealed class ExcelExporter
{
    private static readonly string[] AllColumns =
    [
        "序号",
        "中文内容",
        "来源类型",
        "分类",
        "文件路径",
        "行号",
        "字段",
        "对象/资源名",
        "Key",
        "可信度",
        "是否乱码",
        "乱码判断",
        "备注",
        "原始行"
    ];

    public void Export(string outputPath, IReadOnlyList<ChineseTextRecord> records)
    {
        if (records.Count == 0)
            throw new InvalidOperationException("没有可导出的扫描结果。");

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        using XLWorkbook workbook = new();
        AddSummarySheet(workbook, records);
        AddAllRecordsSheet(workbook, "全部中文文本", records);

        List<ChineseTextRecord> garbledRecords = records.Where(record => record.IsGarbled).ToList();
        if (garbledRecords.Count > 0)
            AddAllRecordsSheet(workbook, "疑似乱码文本", garbledRecords);

        foreach (IGrouping<string, ChineseTextRecord> group in records.GroupBy(record => record.SourceType).OrderBy(group => group.Key))
            AddAllRecordsSheet(workbook, ToSafeSheetName(group.Key), group.ToList());

        workbook.SaveAs(outputPath);
    }

    private static void AddSummarySheet(XLWorkbook workbook, IReadOnlyList<ChineseTextRecord> records)
    {
        IXLWorksheet sheet = workbook.Worksheets.Add("去重汇总");
        string[] columns =
        [
            "中文内容",
            "出现次数",
            "来源类型",
            "分类",
            "可信度",
            "是否乱码",
            "示例位置",
            "全部位置"
        ];

        for (int column = 0; column < columns.Length; column++)
            sheet.Cell(1, column + 1).Value = columns[column];

        int row = 2;
        foreach (IGrouping<string, ChineseTextRecord> group in records.GroupBy(record => record.Text).OrderBy(group => group.Key))
        {
            List<ChineseTextRecord> items = group.ToList();
            sheet.Cell(row, 1).Value = LimitCellText(group.Key);
            sheet.Cell(row, 2).Value = items.Count;
            sheet.Cell(row, 3).Value = LimitCellText(string.Join(" / ", items.Select(item => item.SourceType).Distinct().OrderBy(x => x)));
            sheet.Cell(row, 4).Value = LimitCellText(string.Join(" / ", items.Select(item => item.Category).Distinct().OrderBy(x => x)));
            sheet.Cell(row, 5).Value = LimitCellText(string.Join(" / ", items.Select(item => item.Confidence).Distinct().OrderBy(x => x)));
            sheet.Cell(row, 6).Value = items.Any(item => item.IsGarbled) ? "是" : "否";
            sheet.Cell(row, 7).Value = LimitCellText(items[0].Location);
            sheet.Cell(row, 8).Value = LimitCellText(string.Join(Environment.NewLine, items.Select(item => item.Location).Distinct().Take(1000)));
            row++;
        }

        FormatSheet(sheet, columns.Length);
    }

    private static void AddAllRecordsSheet(XLWorkbook workbook, string sheetName, IReadOnlyList<ChineseTextRecord> records)
    {
        IXLWorksheet sheet = workbook.Worksheets.Add(sheetName);

        for (int column = 0; column < AllColumns.Length; column++)
            sheet.Cell(1, column + 1).Value = AllColumns[column];

        int row = 2;
        foreach (ChineseTextRecord record in records.OrderBy(record => record.Id))
        {
            sheet.Cell(row, 1).Value = record.Id;
            sheet.Cell(row, 2).Value = LimitCellText(record.Text);
            sheet.Cell(row, 3).Value = LimitCellText(record.SourceType);
            sheet.Cell(row, 4).Value = LimitCellText(record.Category);
            sheet.Cell(row, 5).Value = LimitCellText(record.RelativePath);
            sheet.Cell(row, 6).Value = record.LineNumber > 0 ? record.LineNumber : string.Empty;
            sheet.Cell(row, 7).Value = LimitCellText(record.FieldName);
            sheet.Cell(row, 8).Value = LimitCellText(record.ObjectPath);
            sheet.Cell(row, 9).Value = LimitCellText(record.Key);
            sheet.Cell(row, 10).Value = LimitCellText(record.Confidence);
            sheet.Cell(row, 11).Value = record.GarbledStatus;
            sheet.Cell(row, 12).Value = LimitCellText(record.GarbledReason);
            sheet.Cell(row, 13).Value = LimitCellText(record.Notes);
            sheet.Cell(row, 14).Value = LimitCellText(record.RawLine);
            row++;
        }

        FormatSheet(sheet, AllColumns.Length);
    }

    private static void FormatSheet(IXLWorksheet sheet, int columnCount)
    {
        IXLRange header = sheet.Range(1, 1, 1, columnCount);
        header.Style.Font.Bold = true;
        header.Style.Fill.BackgroundColor = XLColor.FromHtml("#E7EEF8");
        header.SetAutoFilter();

        sheet.SheetView.FreezeRows(1);
        sheet.Columns().AdjustToContents(1, 80);
        sheet.Column(2).Width = Math.Min(Math.Max(sheet.Column(2).Width, 28), 80);
        sheet.Rows().Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
    }

    private static string ToSafeSheetName(string value)
    {
        string sheetName = string.IsNullOrWhiteSpace(value) ? "未分类" : value;
        foreach (char invalid in Path.GetInvalidFileNameChars().Concat(['[', ']', ':', '*', '?', '/', '\\']))
            sheetName = sheetName.Replace(invalid, '_');

        return sheetName.Length <= 31 ? sheetName : sheetName[..31];
    }

    private static string LimitCellText(string value)
    {
        const int maxExcelTextLength = 32767;
        const int safeLength = 32000;
        if (string.IsNullOrEmpty(value) || value.Length <= maxExcelTextLength)
            return value;

        return value[..safeLength] + Environment.NewLine + $"...[已截断，原长度 {value.Length}]";
    }
}
