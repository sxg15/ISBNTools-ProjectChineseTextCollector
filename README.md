# Project Chinese Text Collector

Windows desktop tool for collecting Chinese text from a Unity project.

## Current Features

- Select a Unity project folder from the UI.
- Scan common Unity text sources under `Assets`.
- Classify records into localization tables, fixed UI text, resource config text, script candidates, serialized text, and items that need review.
- Show scan results in separate tabs by source type.
- Mark records with `是否乱码` and provide a dedicated garbled-text tab.
- Preview scan results in a table.
- Export an Excel workbook with a deduplicated summary and source-specific sheets.
- Run a non-UI smoke scan from the command line.

## Run

```powershell
dotnet run -c Release
```

## Command-Line Scan

```powershell
dotnet run -c Release -- --scan "D:\Unity Data\AbyaPB" --output "D:\ISBN Tools\ProjectChineseTextCollector\Exports\AbyaPB_Texts.xlsx"
```

Add `--include-third-party` if third-party and example folders should be included.

## Build

```powershell
dotnet build -c Release
```

The built executable is:

```text
D:\ISBN Tools\ProjectChineseTextCollector\bin\Release\net10.0-windows\ProjectChineseTextCollector.exe
```

## Self-Contained Publish

Use this when the output should run on a Windows machine without installing .NET.

```powershell
powershell -ExecutionPolicy Bypass -File .\publish-self-contained.ps1
```

The self-contained executable is:

```text
D:\ISBN Tools\ProjectChineseTextCollector\publish\win-x64-self-contained\ProjectChineseTextCollector.exe
```
