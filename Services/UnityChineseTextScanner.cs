using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using ProjectChineseTextCollector.Models;

namespace ProjectChineseTextCollector.Services;

public sealed class UnityChineseTextScanner
{
    private static readonly int MaxParallelFileScans = Math.Clamp(Environment.ProcessorCount, 2, 8);

    private static readonly Regex HanRegex = new("[\u3400-\u9fff\uf900-\ufaff]", RegexOptions.Compiled);
    private static readonly Regex MojibakeCjkRegex = new(
        @"[\u00C2-\u00F4][\u0080-\u00BF\u0100-\u017F\u2010-\u203F\u20AC]{2}",
        RegexOptions.Compiled);

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".asset",
        ".prefab",
        ".unity",
        ".cs",
        ".uxml",
        ".xml",
        ".json",
        ".txt",
        ".csv"
    };

    private static readonly string[] LikelyThirdPartyPathHints =
    [
        @"Assets\Plugins\",
        @"Assets\Packages\",
        @"Assets\com.unity.",
        @"Assets\Mirror\",
        @"Assets\Wwise\",
        @"Assets\XLua\",
        @"Assets\Chronos\",
        @"Assets\MarkdownParser\",
        @"\Examples\",
        @"\Example\"
    ];

    public Task<IReadOnlyList<ChineseTextRecord>> ScanAsync(
        string projectPath,
        bool includeLikelyThirdParty,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        return ScanAsync(projectPath, includeLikelyThirdParty, [], [], progress, cancellationToken);
    }

    public Task<IReadOnlyList<ChineseTextRecord>> ScanAsync(
        string projectPath,
        bool includeLikelyThirdParty,
        IReadOnlyCollection<string> excludedFiles,
        IReadOnlyCollection<string> excludedFolders,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        return Task.Run(() => Scan(projectPath, includeLikelyThirdParty, excludedFiles, excludedFolders, progress, cancellationToken), cancellationToken);
    }

    private static IReadOnlyList<ChineseTextRecord> Scan(
        string projectPath,
        bool includeLikelyThirdParty,
        IReadOnlyCollection<string> excludedFiles,
        IReadOnlyCollection<string> excludedFolders,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
            throw new InvalidOperationException("Project path is empty.");

        string normalizedProjectPath = Path.GetFullPath(projectPath);
        string assetsPath = Path.Combine(normalizedProjectPath, "Assets");
        if (!Directory.Exists(assetsPath))
            throw new InvalidOperationException("The selected folder is not a Unity project: Assets folder was not found.");

        ExclusionRules exclusionRules = ExclusionRules.Create(normalizedProjectPath, excludedFiles, excludedFolders);
        List<string> files = EnumerateFilesSafe(assetsPath)
            .Where(file => SupportedExtensions.Contains(Path.GetExtension(file)))
            .Where(file => includeLikelyThirdParty || !IsLikelyThirdParty(file, normalizedProjectPath))
            .Where(file => !exclusionRules.IsExcluded(file))
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
            .ToList();

        ChineseTextRecord[][] recordsByFile = new ChineseTextRecord[files.Count][];
        int processedFiles = 0;
        int recordsFound = 0;
        progress?.Report(new ScanProgress
        {
            ProcessedFiles = 0,
            TotalFiles = files.Count,
            Records = 0,
            CurrentFile = "文件列表已建立，准备开始扫描"
        });

        ParallelOptions options = new()
        {
            MaxDegreeOfParallelism = MaxParallelFileScans,
            CancellationToken = cancellationToken
        };

        Parallel.For(0, files.Count, options, index =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            string file = files[index];
            string relativePath = Path.GetRelativePath(normalizedProjectPath, file);
            List<ChineseTextRecord> fileRecords = new();
            try
            {
                ScanFile(file, relativePath, fileRecords);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
            {
                fileRecords.Add(new ChineseTextRecord
                {
                    SourceType = "扫描失败",
                    Category = "读取失败",
                    RelativePath = relativePath,
                    Confidence = "低",
                    Notes = ex.Message
                });
            }

            recordsByFile[index] = fileRecords.ToArray();

            int currentProcessedFiles = Interlocked.Increment(ref processedFiles);
            int currentRecordsFound = Interlocked.Add(ref recordsFound, fileRecords.Count);
            if (currentProcessedFiles % 10 == 0 || currentProcessedFiles == files.Count)
            {
                progress?.Report(new ScanProgress
                {
                    ProcessedFiles = currentProcessedFiles,
                    TotalFiles = files.Count,
                    Records = currentRecordsFound,
                    CurrentFile = relativePath
                });
            }
        });

        List<ChineseTextRecord> records = recordsByFile
            .Where(fileRecords => fileRecords is not null)
            .SelectMany(fileRecords => fileRecords)
            .ToList();

        for (int i = 0; i < records.Count; i++)
            records[i].Id = i + 1;

        return records;
    }

    private static void ScanFile(string file, string relativePath, List<ChineseTextRecord> records)
    {
        if (IsBinaryLike(file))
            return;

        string extension = Path.GetExtension(file);
        if (extension.Equals(".cs", StringComparison.OrdinalIgnoreCase))
        {
            ScanCSharpFile(file, relativePath, records);
        }
        else if (extension.Equals(".csv", StringComparison.OrdinalIgnoreCase))
        {
            ScanCsvFile(file, relativePath, records);
        }
        else
        {
            ScanStructuredTextFile(file, relativePath, records);
        }
    }

    private static void ScanCsvFile(string file, string relativePath, List<ChineseTextRecord> records)
    {
        int lineNumber = 0;
        int chineseColumnIndex = -1;
        string chineseColumnName = "Chinese (ZH)";
        bool headerProcessed = false;

        foreach (string line in ReadLines(file))
        {
            lineNumber++;
            List<string> cells = ParseCsvLine(line);
            if (cells.Count == 0)
                continue;

            if (!headerProcessed)
            {
                headerProcessed = true;
                chineseColumnIndex = FindChineseColumnIndex(cells);
                if (chineseColumnIndex >= 0)
                {
                    chineseColumnName = cells[chineseColumnIndex];
                    continue;
                }
            }

            if (chineseColumnIndex >= 0)
            {
                if (chineseColumnIndex >= cells.Count)
                    continue;

                AddCsvRecord(relativePath, lineNumber, chineseColumnName, cells[chineseColumnIndex], cells, records);
                continue;
            }

            for (int column = 0; column < cells.Count; column++)
                AddCsvRecord(relativePath, lineNumber, $"CSV column {column + 1}", cells[column], cells, records);
        }
    }

    private static void AddCsvRecord(
        string relativePath,
        int lineNumber,
        string fieldName,
        string value,
        IReadOnlyList<string> rowCells,
        List<ChineseTextRecord> records)
    {
        string text = NormalizeEscapedText(value);
        bool containsChinese = ContainsChinese(text);
        bool isGarbled = IsLikelyGarbled(text);
        if (!containsChinese && !isGarbled)
            return;

        if (IsUnityAssetPathValue(text))
            return;

        bool isLocalization = IsLocalizationPath(relativePath)
                              || IsChineseColumnHeader(fieldName);
        Classification classification = isLocalization
            ? new Classification("本地化表文本", "本地化中文", "确定", "来自 CSV 的 Chinese (ZH) 列")
            : new Classification("其他文本", "CSV 文本", "中", "CSV 单元格中出现中文");

        records.Add(new ChineseTextRecord
        {
            Text = text,
            SourceType = classification.SourceType,
            Category = classification.Category,
            RelativePath = relativePath,
            LineNumber = lineNumber,
            FieldName = fieldName,
            Key = rowCells.Count > 0 ? rowCells[0] : string.Empty,
            Confidence = classification.Confidence,
            IsGarbled = isGarbled,
            GarbledReason = isGarbled ? GetGarbledReason(text) : string.Empty,
            Notes = classification.Notes,
            RawLine = text
        });
    }

    private static void ScanStructuredTextFile(string file, string relativePath, List<ChineseTextRecord> records)
    {
        int lineNumber = 0;
        string lastObjectName = string.Empty;
        string lastKey = string.Empty;
        string lastKeyId = string.Empty;

        foreach (string line in ReadLines(file))
        {
            lineNumber++;
            ParsedLine parsed = ParseStructuredLine(line);

            if (parsed.FieldName.Equals("m_Name", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(parsed.Value))
                lastObjectName = parsed.Value;
            if (parsed.FieldName.Equals("m_Key", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(parsed.Value))
                lastKey = parsed.Value;
            if (parsed.FieldName.Equals("m_KeyId", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(parsed.Value))
                lastKeyId = parsed.Value;

            string text = parsed.Value;
            bool containsChinese = ContainsChinese(text);
            bool isGarbled = IsLikelyGarbled(text);
            if (!containsChinese && !isGarbled)
                continue;

            string extension = Path.GetExtension(file);
            if (ShouldSkipStructuredRecord(relativePath, extension, parsed.FieldName, text))
                continue;

            Classification classification = ClassifyStructured(relativePath, extension, parsed.FieldName, line);
            records.Add(new ChineseTextRecord
            {
                Text = text,
                SourceType = classification.SourceType,
                Category = classification.Category,
                RelativePath = relativePath,
                LineNumber = lineNumber,
                FieldName = parsed.FieldName,
                ObjectPath = lastObjectName,
                Key = IsLocalizationPath(relativePath) ? string.Empty : !string.IsNullOrWhiteSpace(lastKey) ? lastKey : lastKeyId,
                Confidence = classification.Confidence,
                IsGarbled = isGarbled,
                GarbledReason = isGarbled ? GetGarbledReason(text) : string.Empty,
                Notes = classification.Notes,
                RawLine = line.Trim()
            });
        }
    }

    private static void ScanCSharpFile(string file, string relativePath, List<ChineseTextRecord> records)
    {
        int lineNumber = 0;
        foreach (string line in ReadLines(file))
        {
            lineNumber++;
            List<string> literals = ExtractCSharpStringLiterals(line)
                .Select(NormalizeEscapedText)
                .Where(value => ContainsChinese(value) || IsLikelyGarbled(value))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (literals.Count == 0)
                continue;

            foreach (string literal in literals)
            {
                Classification classification = ClassifyCSharp(relativePath, line);
                bool isGarbled = IsLikelyGarbled(literal);
                records.Add(new ChineseTextRecord
                {
                    Text = literal,
                    SourceType = classification.SourceType,
                    Category = classification.Category,
                    RelativePath = relativePath,
                    LineNumber = lineNumber,
                    FieldName = "C# string",
                    Confidence = classification.Confidence,
                    IsGarbled = isGarbled,
                    GarbledReason = isGarbled ? GetGarbledReason(literal) : string.Empty,
                    Notes = classification.Notes,
                    RawLine = line.Trim()
                });
            }
        }
    }

    private static IEnumerable<string> ReadLines(string file)
    {
        using StreamReader reader = new(file, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        while (reader.ReadLine() is { } line)
            yield return line;
    }

    private static bool IsBinaryLike(string file)
    {
        const int sampleSize = 8192;
        byte[] buffer = new byte[sampleSize];
        int read;

        try
        {
            using FileStream stream = File.OpenRead(file);
            read = stream.Read(buffer, 0, buffer.Length);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        if (read == 0)
            return false;

        int zeroBytes = 0;
        int controlBytes = 0;
        for (int i = 0; i < read; i++)
        {
            byte value = buffer[i];
            if (value == 0)
                zeroBytes++;
            else if (value < 8 || (value > 13 && value < 32))
                controlBytes++;
        }

        return zeroBytes > 0 || controlBytes > read * 0.1;
    }

    private static IEnumerable<string> EnumerateFilesSafe(string root)
    {
        Stack<string> pending = new();
        pending.Push(root);

        while (pending.Count > 0)
        {
            string current = pending.Pop();
            IEnumerable<string> files;
            IEnumerable<string> directories;

            try
            {
                files = Directory.EnumerateFiles(current).ToList();
                directories = Directory.EnumerateDirectories(current).ToList();
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (IOException)
            {
                continue;
            }

            foreach (string file in files)
                yield return file;

            foreach (string directory in directories)
            {
                string name = Path.GetFileName(directory);
                if (name is "Library" or "Temp" or "obj" or "bin")
                    continue;
                pending.Push(directory);
            }
        }
    }

    private static bool IsLikelyThirdParty(string file, string projectPath)
    {
        string relative = Path.GetRelativePath(projectPath, file).Replace('/', '\\');
        return LikelyThirdPartyPathHints.Any(hint => relative.Contains(hint, StringComparison.OrdinalIgnoreCase))
               || relative.StartsWith(@"Assets\Editor\", StringComparison.OrdinalIgnoreCase);
    }

    private static ParsedLine ParseStructuredLine(string line)
    {
        string trimmed = line.Trim();
        if (trimmed.StartsWith("- ", StringComparison.Ordinal))
            trimmed = trimmed[2..].TrimStart();

        string fieldName = string.Empty;
        string value = trimmed;
        int colonIndex = trimmed.IndexOf(':');
        if (colonIndex >= 0 && colonIndex <= 80)
        {
            fieldName = trimmed[..colonIndex].Trim();
            value = trimmed[(colonIndex + 1)..].Trim();
        }

        value = StripWrappingQuotes(NormalizeEscapedText(value));
        return new ParsedLine(fieldName, value);
    }

    private static string StripWrappingQuotes(string value)
    {
        value = value.Trim();
        if (value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
            return value[1..^1];
        return value;
    }

    private static string NormalizeEscapedText(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        string result = Regex.Replace(value, @"\\u([0-9a-fA-F]{4})", match =>
        {
            int code = Convert.ToInt32(match.Groups[1].Value, 16);
            return char.ConvertFromUtf32(code);
        });

        result = result
            .Replace("\\r", "\r", StringComparison.Ordinal)
            .Replace("\\n", "\n", StringComparison.Ordinal)
            .Replace("\\t", "\t", StringComparison.Ordinal)
            .Replace("\\\"", "\"", StringComparison.Ordinal)
            .Replace("\\\\", "\\", StringComparison.Ordinal)
            .Replace("\"\"", "\"", StringComparison.Ordinal);

        return result.Trim();
    }

    private static List<string> ParseCsvLine(string line)
    {
        List<string> cells = new();
        StringBuilder builder = new();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char character = line[i];
            if (character == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    builder.Append('"');
                    i++;
                    continue;
                }

                inQuotes = !inQuotes;
                continue;
            }

            if (character == ',' && !inQuotes)
            {
                cells.Add(builder.ToString().Trim());
                builder.Clear();
                continue;
            }

            builder.Append(character);
        }

        cells.Add(builder.ToString().Trim());
        return cells;
    }

    private static int FindChineseColumnIndex(IReadOnlyList<string> headers)
    {
        for (int i = 0; i < headers.Count; i++)
        {
            if (IsChineseColumnHeader(headers[i]))
                return i;
        }

        return -1;
    }

    private static bool IsChineseColumnHeader(string header)
    {
        if (string.IsNullOrWhiteSpace(header))
            return false;

        string normalized = header
            .Trim()
            .ToLowerInvariant()
            .Replace("（", "(", StringComparison.Ordinal)
            .Replace("）", ")", StringComparison.Ordinal);

        string compact = Regex.Replace(normalized, @"[\s_\-()]+", string.Empty);
        return compact is "chinesezh"
            or "zh"
            or "zhcn"
            or "zhhans"
            or "chinese"
            or "simplifiedchinese"
            or "中文"
            or "简体中文";
    }

    private static bool ContainsChinese(string value)
    {
        return !string.IsNullOrWhiteSpace(value) && HanRegex.IsMatch(value);
    }

    private static bool IsLikelyGarbled(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        int replacementCount = value.Count(character => character == '\uFFFD');
        if (replacementCount >= 2 || (replacementCount == 1 && ContainsChinese(value)))
            return true;

        return MojibakeCjkRegex.IsMatch(value);
    }

    private static string GetGarbledReason(string value)
    {
        int replacementCount = value.Count(character => character == '\uFFFD');
        if (replacementCount >= 2 || (replacementCount == 1 && ContainsChinese(value)))
            return "包含替换字符";

        if (MojibakeCjkRegex.IsMatch(value))
            return "疑似 UTF-8 中文被错误解码";

        return string.Empty;
    }

    private static IEnumerable<string> ExtractCSharpStringLiterals(string line)
    {
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] != '"')
                continue;

            if (i > 0 && line[i - 1] == '\'')
                continue;

            int quoteCount = CountRepeatedQuotes(line, i);
            if (quoteCount >= 3)
            {
                int rawEnd = line.IndexOf(new string('"', quoteCount), i + quoteCount, StringComparison.Ordinal);
                if (rawEnd >= 0)
                {
                    yield return line[(i + quoteCount)..rawEnd];
                    i = rawEnd + quoteCount - 1;
                }
                continue;
            }

            bool verbatim = IsVerbatimStringStart(line, i);
            StringBuilder builder = new();
            int cursor = i + 1;
            while (cursor < line.Length)
            {
                char current = line[cursor];
                if (current == '"')
                {
                    if (verbatim && cursor + 1 < line.Length && line[cursor + 1] == '"')
                    {
                        builder.Append('"');
                        cursor += 2;
                        continue;
                    }

                    break;
                }

                if (!verbatim && current == '\\' && cursor + 1 < line.Length)
                {
                    builder.Append(current);
                    builder.Append(line[cursor + 1]);
                    cursor += 2;
                    continue;
                }

                builder.Append(current);
                cursor++;
            }

            yield return builder.ToString();
            i = Math.Max(i, cursor);
        }
    }

    private static int CountRepeatedQuotes(string value, int start)
    {
        int count = 0;
        for (int i = start; i < value.Length && value[i] == '"'; i++)
            count++;
        return count;
    }

    private static bool IsVerbatimStringStart(string line, int quoteIndex)
    {
        int index = quoteIndex - 1;
        while (index >= 0 && (line[index] == '$' || line[index] == '@'))
        {
            if (line[index] == '@')
                return true;
            index--;
        }

        return false;
    }

    private static Classification ClassifyStructured(string relativePath, string extension, string fieldName, string rawLine)
    {
        if (IsLocalizationPath(relativePath))
            return new Classification("本地化表文本", "本地化中文", "确定", "来自项目中文本地化表");

        if (extension.Equals(".prefab", StringComparison.OrdinalIgnoreCase) || extension.Equals(".unity", StringComparison.OrdinalIgnoreCase))
        {
            if (IsUiTextField(fieldName, rawLine))
                return new Classification("固定UI文本", "序列化 UI", "确定", "来自场景或预制体上的文本字段");

            return new Classification("序列化文本", "场景/预制体", "中", "场景或预制体内出现中文，需要确认是否会显示");
        }

        if (extension.Equals(".asset", StringComparison.OrdinalIgnoreCase))
        {
            if (fieldName.Equals("m_Localized", StringComparison.OrdinalIgnoreCase))
                return new Classification("本地化表文本", "本地化中文", "确定", "本地化表条目");

            return new Classification("资源配置文本", "ScriptableObject/资源", "较高", "资源文件里的中文字段，可能被界面读取");
        }

        return new Classification("其他文本", "文本资源", "中", "文本文件中出现中文");
    }

    private static bool IsUiTextField(string fieldName, string rawLine)
    {
        return fieldName.Equals("m_Text", StringComparison.OrdinalIgnoreCase)
               || fieldName.Equals("m_text", StringComparison.OrdinalIgnoreCase)
               || fieldName.Contains("Text", StringComparison.OrdinalIgnoreCase)
               || fieldName.Contains("Placeholder", StringComparison.OrdinalIgnoreCase)
               || rawLine.Contains("TMP", StringComparison.OrdinalIgnoreCase)
               || rawLine.Contains("Dropdown", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldSkipStructuredRecord(string relativePath, string extension, string fieldName, string text)
    {
        if ((extension.Equals(".prefab", StringComparison.OrdinalIgnoreCase) || extension.Equals(".unity", StringComparison.OrdinalIgnoreCase))
            && fieldName.Equals("m_Name", StringComparison.OrdinalIgnoreCase))
            return true;

        if (IsLocalizationPath(relativePath)
            && fieldName.Equals("m_Key", StringComparison.OrdinalIgnoreCase))
            return true;

        if (IsUnityAssetPathValue(text))
            return true;

        return false;
    }

    private static bool IsUnityAssetPathValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        string normalized = value.Trim().Replace('\\', '/');
        bool startsWithProjectPath =
            normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("ProjectSettings/", StringComparison.OrdinalIgnoreCase);

        return startsWithProjectPath && !string.IsNullOrEmpty(Path.GetExtension(normalized));
    }

    private static bool IsLocalizationPath(string relativePath)
    {
        return relativePath.StartsWith(@"Assets\Localization\", StringComparison.OrdinalIgnoreCase);
    }

    private static Classification ClassifyCSharp(string relativePath, string rawLine)
    {
        string trimmed = rawLine.TrimStart();
        bool likelyEditorOnly =
            relativePath.StartsWith(@"Assets\Editor\", StringComparison.OrdinalIgnoreCase)
            || relativePath.Contains(@"\Editor\", StringComparison.OrdinalIgnoreCase)
            || rawLine.Contains("LabelText(", StringComparison.Ordinal)
            || rawLine.Contains("Tooltip(", StringComparison.Ordinal)
            || rawLine.Contains("Header(", StringComparison.Ordinal)
            || rawLine.Contains("CreateAssetMenu(", StringComparison.Ordinal)
            || rawLine.Contains("MenuItem(", StringComparison.Ordinal);

        bool likelyDebugOnly =
            rawLine.Contains("Debug.Log", StringComparison.Ordinal)
            || rawLine.Contains("Console.Write", StringComparison.Ordinal)
            || trimmed.StartsWith("//", StringComparison.Ordinal)
            || trimmed.StartsWith("/*", StringComparison.Ordinal)
            || trimmed.StartsWith("*", StringComparison.Ordinal);

        if (likelyEditorOnly || likelyDebugOnly)
            return new Classification("疑似非游戏内文本", "编辑器/调试/注释", "低", "看起来不像正式游戏内显示文本");

        bool likelyUi =
            rawLine.Contains(".text", StringComparison.Ordinal)
            || rawLine.Contains(".SetText", StringComparison.Ordinal)
            || rawLine.Contains("SetText(", StringComparison.Ordinal)
            || rawLine.Contains("ButtonText", StringComparison.Ordinal)
            || rawLine.Contains("DisplayLoading", StringComparison.Ordinal)
            || rawLine.Contains("SetLoadingText", StringComparison.Ordinal)
            || rawLine.Contains("TempInfoBox", StringComparison.Ordinal)
            || rawLine.Contains("PendingButton", StringComparison.Ordinal)
            || rawLine.Contains("OptionData", StringComparison.Ordinal)
            || rawLine.Contains("SpawnSceneJumpText", StringComparison.Ordinal)
            || rawLine.Contains("InteractiveWorldUI", StringComparison.Ordinal)
            || rawLine.Contains("SuspendedInformationContent", StringComparison.Ordinal)
            || rawLine.Contains("RequsetLogTask", StringComparison.Ordinal)
            || rawLine.Contains("LocalizedString", StringComparison.Ordinal);

        if (likelyUi)
            return new Classification("脚本候选文本", "运行时 UI 候选", "中", "脚本中的中文字符串，周边代码像是会显示到界面");

        return new Classification("需要人工确认", "脚本中文", "中", "脚本中的中文字符串，暂时无法自动确认是否显示给玩家");
    }

    private readonly record struct ParsedLine(string FieldName, string Value);

    private readonly record struct Classification(string SourceType, string Category, string Confidence, string Notes);

    private sealed class ExclusionRules
    {
        private readonly string projectPath;
        private readonly HashSet<string> fileNames;
        private readonly HashSet<string> filePaths;
        private readonly HashSet<string> folderNames;
        private readonly HashSet<string> folderPaths;

        private ExclusionRules(
            string projectPath,
            HashSet<string> fileNames,
            HashSet<string> filePaths,
            HashSet<string> folderNames,
            HashSet<string> folderPaths)
        {
            this.projectPath = projectPath;
            this.fileNames = fileNames;
            this.filePaths = filePaths;
            this.folderNames = folderNames;
            this.folderPaths = folderPaths;
        }

        public static ExclusionRules Create(
            string projectPath,
            IReadOnlyCollection<string> excludedFiles,
            IReadOnlyCollection<string> excludedFolders)
        {
            HashSet<string> fileNames = new(StringComparer.OrdinalIgnoreCase);
            HashSet<string> filePaths = new(StringComparer.OrdinalIgnoreCase);
            HashSet<string> folderNames = new(StringComparer.OrdinalIgnoreCase);
            HashSet<string> folderPaths = new(StringComparer.OrdinalIgnoreCase);

            foreach (string item in excludedFiles.Select(value => NormalizeEntry(projectPath, value)).Where(value => !string.IsNullOrWhiteSpace(value)))
            {
                if (ContainsDirectorySeparator(item))
                    filePaths.Add(item);
                else
                    fileNames.Add(item);
            }

            foreach (string item in excludedFolders.Select(value => NormalizeEntry(projectPath, value)).Where(value => !string.IsNullOrWhiteSpace(value)))
            {
                if (ContainsDirectorySeparator(item))
                    folderPaths.Add(item);
                else
                    folderNames.Add(item);
            }

            return new ExclusionRules(projectPath, fileNames, filePaths, folderNames, folderPaths);
        }

        public bool IsExcluded(string file)
        {
            string relativePath = NormalizeRelativePath(Path.GetRelativePath(projectPath, file));
            if (fileNames.Contains(Path.GetFileName(file)))
                return true;

            if (filePaths.Any(path => IsSamePathOrSuffix(relativePath, path)))
                return true;

            string? directory = Path.GetDirectoryName(relativePath);
            string relativeDirectory = NormalizeRelativePath(directory ?? string.Empty);
            if (!string.IsNullOrEmpty(relativeDirectory))
            {
                if (relativeDirectory.Split('\\').Any(segment => folderNames.Contains(segment)))
                    return true;

                if (folderPaths.Any(path => IsSamePathOrChild(relativeDirectory, path) || IsSamePathOrSuffix(relativeDirectory, path)))
                    return true;
            }

            return false;
        }

        private static string NormalizeEntry(string projectPath, string value)
        {
            string item = value.Trim().Trim('"', '\'');
            if (string.IsNullOrWhiteSpace(item))
                return string.Empty;

            try
            {
                item = Path.TrimEndingDirectorySeparator(item);
                if (Path.IsPathRooted(item))
                    item = Path.GetRelativePath(projectPath, Path.GetFullPath(item));
            }
            catch (ArgumentException)
            {
                // Keep the original text and treat it as a name or relative path.
            }
            catch (NotSupportedException)
            {
                // Keep the original text and treat it as a name or relative path.
            }

            return NormalizeRelativePath(item);
        }

        private static string NormalizeRelativePath(string value)
        {
            string normalized = value.Trim().Replace('/', '\\');
            while (normalized.StartsWith(@".\", StringComparison.Ordinal))
                normalized = normalized[2..];

            return normalized.Trim('\\');
        }

        private static bool ContainsDirectorySeparator(string value)
        {
            return value.Contains('\\', StringComparison.Ordinal);
        }

        private static bool IsSamePathOrSuffix(string candidate, string rule)
        {
            return candidate.Equals(rule, StringComparison.OrdinalIgnoreCase)
                   || candidate.EndsWith("\\" + rule, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSamePathOrChild(string candidateDirectory, string rule)
        {
            return candidateDirectory.Equals(rule, StringComparison.OrdinalIgnoreCase)
                   || candidateDirectory.StartsWith(rule + "\\", StringComparison.OrdinalIgnoreCase);
        }
    }
}
