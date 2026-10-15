# Builds DialedKeyboard and installs it to Program Files with uiAccess, so it can type into
# Windows Security password prompts and admin windows (Windows blocks keystrokes from normal
# apps there). Run it again after changing the code to update the installed copy.
#
# What it changes on this PC (the elevated half, after one UAC prompt):
#   - creates a local code-signing certificate and trusts it machine-wide (Root + TrustedPublisher)
#   - copies the build to C:\Program Files\DialedKeyboard and signs DialedKeyboard.exe
# Then it points the Start menu shortcut at the installed copy and starts it.
param([switch]$Elevated, [string]$Source)

$ErrorActionPreference = 'Stop'
$AppName = 'DialedKeyboard'
$InstallDir = Join-Path $env:ProgramFiles $AppName
$CertSubject = 'CN=DialedKeyboard Local Code Signing'

if ($Elevated) {
    try {
        $cert = Get-ChildItem Cert:\LocalMachine\My -CodeSigningCert |
            Where-Object Subject -eq $CertSubject | Sort-Object NotAfter -Descending | Select-Object -First 1
        if (-not $cert) {
            $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $CertSubject `
                -CertStoreLocation Cert:\LocalMachine\My -KeyExportPolicy NonExportable `
                -NotAfter (Get-Date).AddYears(10)
        }
        # uiAccess only starts for an exe signed by a certificate the machine trusts.
        $public = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($cert.RawData)
        foreach ($name in 'Root', 'TrustedPublisher') {
            $store = [System.Security.Cryptography.X509Certificates.X509Store]::new($name, 'LocalMachine')
            $store.Open('ReadWrite'); $store.Add($public); $store.Close()
        }

        Get-Process $AppName -ErrorAction SilentlyContinue | Stop-Process -Force
        Start-Sleep -Milliseconds 500
        New-Item -ItemType Directory -Force $InstallDir | Out-Null
        Copy-Item (Join-Path $Source '*') $InstallDir -Recurse -Force

        $sig = Set-AuthenticodeSignature (Join-Path $InstallDir "$AppName.exe") $cert -HashAlgorithm SHA256
        if ($sig.Status -ne 'Valid') { throw "Signing failed: $($sig.StatusMessage)" }
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
