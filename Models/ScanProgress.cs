namespace ProjectChineseTextCollector.Models;

public sealed class ScanProgress
{
    public int ProcessedFiles { get; init; }
    public int TotalFiles { get; init; }
    public int Records { get; init; }
    public string CurrentFile { get; init; } = string.Empty;
}
