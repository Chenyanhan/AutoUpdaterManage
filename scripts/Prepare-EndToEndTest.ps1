param(
    [string]$LabRoot = "D:\UpdaterLab"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$appDirectory = Join-Path $LabRoot "App"
$newVersionDirectory = Join-Path $LabRoot "NewVersion"
$serverDirectory = Join-Path $LabRoot "Server"
$updaterDirectory = Join-Path $appDirectory "AutoUpdater"

foreach ($directory in @($appDirectory, $newVersionDirectory, $serverDirectory, $updaterDirectory)) {
    New-Item $directory -ItemType Directory -Force | Out-Null
}

dotnet publish (Join-Path $repoRoot "AutoUpdater.Client.TestHost\AutoUpdater.Client.TestHost.csproj") `
    -c Release -r win-x64 --self-contained false -o $appDirectory
if ($LASTEXITCODE -ne 0) { throw "Failed to publish test host." }

dotnet publish (Join-Path $repoRoot "AutoUpdater.Client.TestHost\AutoUpdater.Client.TestHost.csproj") `
    -c Release -r win-x64 --self-contained false -o $newVersionDirectory
if ($LASTEXITCODE -ne 0) { throw "Failed to publish new test host." }

dotnet publish (Join-Path $repoRoot "AutoUpdater.Updater\AutoUpdater.Updater.csproj") `
    -c Release -r win-x64 --self-contained true -o $updaterDirectory
if ($LASTEXITCODE -ne 0) { throw "Failed to publish updater." }

Set-Content (Join-Path $appDirectory "version.txt") "1.0.0.0" -Encoding UTF8
Set-Content (Join-Path $newVersionDirectory "version.txt") "2.0.0.0" -Encoding UTF8
Set-Content (Join-Path $newVersionDirectory "new-file.txt") "Created by update 2.0.0.0" -Encoding UTF8

$packagePath = Join-Path $serverDirectory "application-2.0.0.zip"
if (Test-Path $packagePath) {
    Remove-Item -LiteralPath $packagePath -Force
}
Compress-Archive -Path (Join-Path $newVersionDirectory "*") -DestinationPath $packagePath
$hash = (Get-FileHash $packagePath -Algorithm SHA256).Hash

$manifest = [ordered]@{
    version = "2.0.0.0"
    package = "application-2.0.0.zip"
    sha256 = $hash
    releaseNotes = "End-to-end test update"
    mandatory = $false
}
$manifest | ConvertTo-Json | Set-Content `
    (Join-Path $serverDirectory "manifest.json") -Encoding UTF8

Write-Host ""
Write-Host "End-to-end test environment is ready." -ForegroundColor Green
Write-Host "Old version:" $appDirectory
Write-Host "Manifest:" (Join-Path $serverDirectory "manifest.json")
Write-Host "Updater:" (Join-Path $updaterDirectory "AutoUpdater.Updater.exe")
Write-Host ""
Write-Host "Start the test host:"
Write-Host "& `"$appDirectory\AutoUpdater.Client.TestHost.exe`""
