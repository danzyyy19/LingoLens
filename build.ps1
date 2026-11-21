$ErrorActionPreference = "Stop"

$Root    = $PSScriptRoot
$DistDir = Join-Path $Root "dist"
$Assets  = Join-Path $Root "assets"
New-Item -ItemType Directory -Force -Path $DistDir | Out-Null
New-Item -ItemType Directory -Force -Path $Assets  | Out-Null

# Stop running instances if any
try {
    Get-Process -Name "LingoLens", "LingoLens-Portable", "Setup-LingoLens", "TranslatorLive", "TranslatorLive-Portable", "Setup-TranslatorLive" -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue
} catch {}

$FwDir = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319"
$WinMd = "C:\Windows\System32\WinMetadata"
$Csc   = Join-Path $FwDir "csc.exe"
if (!(Test-Path $Csc)) { throw "C# Compiler tidak ditemukan di: $Csc" }

# 1. Generate high-resolution custom multi-size icon
$IconPath = Join-Path $Assets "app.ico"
$IconPrev = Join-Path $Assets "icon_preview.png"
& (Join-Path $Root "generate_icon.ps1") -OutIco $IconPath -OutPreviewPng $IconPrev

# 2. Build Portable & Core App Binary (LingoLens.exe + LingoLens-Portable.exe)
$OutExe      = Join-Path $DistDir "LingoLens.exe"
$PortableExe = Join-Path $DistDir "LingoLens-Portable.exe"

$SrcFiles = @(
    (Join-Path $Root "src\Native.cs"),
    (Join-Path $Root "src\AppConfig.cs"),
    (Join-Path $Root "src\Theme.cs"),
    (Join-Path $Root "src\TranslationEngine.cs"),
    (Join-Path $Root "src\UpdateChecker.cs"),
    (Join-Path $Root "src\OverlayManager.cs"),
    (Join-Path $Root "src\MainWindow.cs"),
    (Join-Path $Root "src\Program.cs")
)

Write-Host "Building LingoLens.exe..."
& $Csc /nologo /target:winexe /platform:x64 /optimize+ `
    "/out:$OutExe" `
    "/win32icon:$IconPath" `
    "/res:$IconPrev,LingoLensIcon.png" `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
    "/r:$FwDir\System.Runtime.dll" `
    "/r:$WinMd\Windows.Foundation.winmd" `
    "/r:$WinMd\Windows.Media.winmd" `
    "/r:$WinMd\Windows.Graphics.winmd" `
    "/r:$WinMd\Windows.Globalization.winmd" `
    "/r:$WinMd\Windows.Storage.winmd" `
    "/r:$WinMd\Windows.Security.winmd" `
    @SrcFiles
if ($LASTEXITCODE -ne 0) { throw "Build LingoLens.exe gagal ($LASTEXITCODE)" }

Copy-Item $OutExe $PortableExe -Force

# 3. Build Windows Installer (Setup-LingoLens.exe) with embedded LingoLens.exe
$SetupExe = Join-Path $DistDir "Setup-LingoLens.exe"
Write-Host "Building Setup-LingoLens.exe..."
& $Csc /nologo /target:winexe /platform:x64 /optimize+ `
    "/out:$SetupExe" `
    "/win32icon:$IconPath" `
    "/res:$OutExe,LingoLens.exe" `
    /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
    (Join-Path $Root "installer\InstallerMain.cs")
if ($LASTEXITCODE -ne 0) { throw "Build Setup-LingoLens.exe gagal ($LASTEXITCODE)" }

Write-Host "Build Selesai! Portable & Installer siap digunakan:"
Get-Item $PortableExe, $SetupExe, $OutExe | Select-Object Name, Length, FullName
