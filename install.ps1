# For developers: builds DialedKeyboard from source and installs it to Program Files with uiAccess,
# so it can type into Windows Security password prompts and admin windows (Windows blocks keystrokes
# from normal apps there). Run it again after changing the code to update the installed copy.
# Everyone else should use DialedKeyboard-Setup.exe from the GitHub Releases page instead.
#
# What it changes on this PC (the elevated half, after one UAC prompt):
#   - copies the build to C:\Program Files\DialedKeyboard
#   - installer\trust-and-sign.ps1 signs DialedKeyboard.exe with a certificate made on this PC,
#     trusts it machine-wide and deletes its private key
# Then it points the Start menu shortcut at the installed copy and starts it.
param([switch]$Elevated, [string]$Source)

$ErrorActionPreference = 'Stop'
$AppName = 'DialedKeyboard'
$InstallDir = Join-Path $env:ProgramFiles $AppName

if ($Elevated) {
    try {
        # Only the installed copy: a dev build running from the repo is left alone.
        $full = $InstallDir.TrimEnd('\') + '\'
        Get-Process $AppName -ErrorAction SilentlyContinue |
            Where-Object { $_.Path -and $_.Path.StartsWith($full, [StringComparison]::OrdinalIgnoreCase) } |
            Stop-Process -Force
        Start-Sleep -Milliseconds 500
        New-Item -ItemType Directory -Force $InstallDir | Out-Null
        Copy-Item (Join-Path $Source '*') $InstallDir -Recurse -Force

        & (Join-Path $PSScriptRoot 'installer\trust-and-sign.ps1') -InstallDir $InstallDir
        if ($LASTEXITCODE) { throw "Signing failed, see $InstallDir\setup-error.log" }
        exit 0
    } catch {
        Write-Host ($_ | Out-String) -ForegroundColor Red
        Read-Host 'Install failed. Press Enter to close'
        exit 1
    }
}

$publish = Join-Path $PSScriptRoot 'publish'
dotnet publish (Join-Path $PSScriptRoot "$AppName\$AppName.csproj") -c Release -r win-x64 `
    --self-contained false -p:PublishSingleFile=true -o $publish
if ($LASTEXITCODE) { throw 'dotnet publish failed' }

$elevatedArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-Elevated', '-Source', "`"$publish`"")
$p = Start-Process powershell.exe -Verb RunAs -Wait -PassThru -ArgumentList $elevatedArgs
if ($p.ExitCode) { throw "Install step failed (exit $($p.ExitCode))" }

$exe = Join-Path $InstallDir "$AppName.exe"
$shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut(
    (Join-Path ([Environment]::GetFolderPath('Programs')) "$AppName.lnk"))
$shortcut.TargetPath = $exe
$shortcut.WorkingDirectory = $InstallDir
$shortcut.Save()

# Start-Process uses ShellExecute, which a uiAccess exe needs (plain CreateProcess is refused).
Start-Process $exe
Write-Host "Installed to $InstallDir"
