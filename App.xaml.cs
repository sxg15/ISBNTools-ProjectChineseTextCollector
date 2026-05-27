using System.IO;
using System.Windows;
using ProjectChineseTextCollector.Services;

namespace ProjectChineseTextCollector;

public partial class App : System.Windows.Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (TryBuildCommandLineRun(e.Args, out CommandLineRun? run))
        {
            try
            {
                CommandLineRun commandLineRun = run!;
                UnityChineseTextScanner scanner = new();
                ExcelExporter exporter = new();
                IReadOnlyList<Models.ChineseTextRecord> records = await scanner.ScanAsync(
                    commandLineRun.ProjectPath,
                    commandLineRun.IncludeLikelyThirdParty,
                    commandLineRun.ExcludedFiles,
                    commandLineRun.ExcludedFolders,
                    null,
                    CancellationToken.None);

                exporter.Export(commandLineRun.OutputPath, records);
                Shutdown(0);
            }
            catch (Exception ex)
            {
                TryWriteCommandLineError(run?.OutputPath, ex);
                Shutdown(1);
            }

            return;
        }

        MainWindow window = new();
        window.Show();
    }

    private static bool TryBuildCommandLineRun(string[] args, out CommandLineRun? run)
    {
        run = null;
        if (args.Length == 0)
            return false;

        string? projectPath = null;
        string? outputPath = null;
        bool includeThirdParty = false;
        List<string> excludedFiles = [];
        List<string> excludedFolders = [];

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg.Equals("--scan", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    projectPath = args[++i];
            }
            else if (arg.Equals("--project", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                projectPath = args[++i];
            }
            else if (arg.Equals("--output", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                outputPath = args[++i];
            }
            else if (arg.Equals("--include-third-party", StringComparison.OrdinalIgnoreCase))
            {
                includeThirdParty = true;
            }
            else if (arg.Equals("--exclude-file", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                excludedFiles.Add(args[++i]);
            }
            else if ((arg.Equals("--exclude-folder", StringComparison.OrdinalIgnoreCase)
                      || arg.Equals("--exclude-dir", StringComparison.OrdinalIgnoreCase))
                     && i + 1 < args.Length)
            {
                excludedFolders.Add(args[++i]);
            }
        }

        if (string.IsNullOrWhiteSpace(projectPath))
            return false;

        if (string.IsNullOrWhiteSpace(outputPath))
        {
            string exportDirectory = Path.Combine(AppContext.BaseDirectory, "Exports");
            outputPath = Path.Combine(exportDirectory, $"ChineseTextReport_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx");
        }

        run = new CommandLineRun(projectPath, outputPath, includeThirdParty, excludedFiles, excludedFolders);
        return true;
    }

    private static void TryWriteCommandLineError(string? outputPath, Exception exception)
    {
        try
        {
            string logPath = string.IsNullOrWhiteSpace(outputPath)
                ? Path.Combine(AppContext.BaseDirectory, "scan-error.log")
                : outputPath + ".error.txt";

            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            File.WriteAllText(logPath, exception.ToString());
            Console.Error.WriteLine(exception);
        }
        catch
        {
            // Last-resort diagnostic helper only.
        }
    }

    private sealed record CommandLineRun(
        string ProjectPath,
        string OutputPath,
        bool IncludeLikelyThirdParty,
        IReadOnlyCollection<string> ExcludedFiles,
        IReadOnlyCollection<string> ExcludedFolders);
}
