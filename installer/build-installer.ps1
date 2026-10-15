# Builds installer\Output\DialedKeyboard-Setup.exe: a self-contained Release build (no .NET install
# needed on the user's PC) wrapped in an Inno Setup installer. Needs the .NET 8 SDK and Inno Setup 6
# (winget install JRSoftware.InnoSetup).
$ErrorActionPreference = 'Stop'
$Root = Split-Path $PSScriptRoot -Parent
$Project = Join-Path $Root 'DialedKeyboard\DialedKeyboard.csproj'
$AppDir = Join-Path $PSScriptRoot 'build\app'

$Version = ([xml](Get-Content $Project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $Version) { throw "No <Version> in $Project" }

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup' }

if (Test-Path $AppDir) { Remove-Item $AppDir -Recurse -Force }
dotnet publish $Project -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:DebugType=none -o $AppDir
if ($LASTEXITCODE) { throw 'dotnet publish failed' }

& $iscc "/DAppVersion=$Version" (Join-Path $PSScriptRoot 'DialedKeyboard.iss')
if ($LASTEXITCODE) { throw 'Inno Setup failed' }
Write-Host "Built $(Join-Path $PSScriptRoot 'Output\DialedKeyboard-Setup.exe') (version $Version)"
