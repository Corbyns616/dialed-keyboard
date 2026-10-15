# Run elevated by the installer (and by install.ps1) after the files are copied, and by the
# uninstaller with -Uninstall.
#
# uiAccess lets the keyboard type into Windows Security password prompts and admin windows, but
# Windows only starts a uiAccess exe that is signed by a certificate this PC trusts. So on each
# install this script makes a fresh certificate on this PC, signs DialedKeyboard.exe with it,
# trusts it, then deletes its private key. Nothing else can ever be signed with that certificate,
# and no key is shipped with the download.
param(
    [Parameter(Mandatory)][string]$InstallDir,
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'
$CertSubject = 'CN=DialedKeyboard Local Code Signing'
$Exe = Join-Path $InstallDir 'DialedKeyboard.exe'

function Remove-Certificates {
    foreach ($store in 'My', 'Root', 'TrustedPublisher') {
        Get-ChildItem "Cert:\LocalMachine\$store" | Where-Object Subject -eq $CertSubject | ForEach-Object {
            if ($_.HasPrivateKey) { Remove-Item $_.PSPath -DeleteKey } else { Remove-Item $_.PSPath }
        }
    }
}

try {
    # Only the installed copy: a copy running from anywhere else (a dev build) is left alone.
    $full = [IO.Path]::GetFullPath($InstallDir).TrimEnd('\') + '\'
    Get-Process DialedKeyboard -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($full, [StringComparison]::OrdinalIgnoreCase) } |
        Stop-Process -Force

    Remove-Certificates
    if ($Uninstall) { exit 0 }

    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $CertSubject `
        -CertStoreLocation Cert:\LocalMachine\My -KeyExportPolicy NonExportable `
        -NotAfter (Get-Date).AddYears(50)
    $sig = Set-AuthenticodeSignature $Exe $cert -HashAlgorithm SHA256

    $public = [Security.Cryptography.X509Certificates.X509Certificate2]::new($cert.RawData)
    foreach ($name in 'Root', 'TrustedPublisher') {
        $store = [Security.Cryptography.X509Certificates.X509Store]::new($name, 'LocalMachine')
        $store.Open('ReadWrite'); $store.Add($public); $store.Close()
    }
    Remove-Item $cert.PSPath -DeleteKey

    if ($sig.Status -ne 'Valid' -and (Get-AuthenticodeSignature $Exe).Status -ne 'Valid') {
        throw "Signing failed: $($sig.StatusMessage)"
    }
    exit 0
} catch {
    $log = Join-Path $InstallDir 'setup-error.log'
    try { ($_ | Out-String) | Set-Content $log } catch {}
    Write-Host ($_ | Out-String) -ForegroundColor Red
    exit 1
}
