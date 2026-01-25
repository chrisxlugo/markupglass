param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug"
)

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$exePath = Join-Path $root "MarkupGlass\bin\$Configuration\net8.0-windows\MarkupGlass.exe"

if (-not (Test-Path $exePath)) {
    Write-Host "Build not found: $exePath"
    Write-Host "Build the project first, then re-run this script."
    exit 1
}

$shell = New-Object -ComObject WScript.Shell
$desktop = [Environment]::GetFolderPath("Desktop")
$startMenu = Join-Path ([Environment]::GetFolderPath("ApplicationData")) "Microsoft\Windows\Start Menu\Programs"

$desktopShortcut = $shell.CreateShortcut((Join-Path $desktop "MarkupGlass.lnk"))
$desktopShortcut.TargetPath = $exePath
$desktopShortcut.IconLocation = $exePath
$desktopShortcut.Description = "MarkupGlass"
$desktopShortcut.Save()

$startShortcut = $shell.CreateShortcut((Join-Path $startMenu "MarkupGlass.lnk"))
$startShortcut.TargetPath = $exePath
$startShortcut.IconLocation = $exePath
$startShortcut.Description = "MarkupGlass"
$startShortcut.Save()

Write-Host "Shortcuts created on Desktop and Start Menu."
