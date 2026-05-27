$ErrorActionPreference = "Stop"

$projectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $projectDir

dotnet publish ProjectChineseTextCollector.csproj `
    /p:PublishProfile=win-x64-self-contained

Write-Host "Self-contained build output:"
Write-Host (Join-Path $projectDir "publish\win-x64-self-contained")
