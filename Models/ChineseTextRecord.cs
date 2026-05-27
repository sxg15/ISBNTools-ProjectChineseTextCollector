namespace ProjectChineseTextCollector.Models;

public sealed class ChineseTextRecord
{
    public int Id { get; set; }
    public string Text { get; set; } = string.Empty;
    public string SourceType { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public int LineNumber { get; set; }
    public string FieldName { get; set; } = string.Empty;
    public string ObjectPath { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string Confidence { get; set; } = string.Empty;
    public bool IsGarbled { get; set; }
    public string GarbledReason { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public string RawLine { get; set; } = string.Empty;

    public string Location => LineNumber > 0 ? $"{RelativePath}:{LineNumber}" : RelativePath;
    public string GarbledStatus => IsGarbled ? "是" : "否";
}
