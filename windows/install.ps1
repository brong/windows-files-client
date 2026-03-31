# Fastmail Files Installer — trust cert, install app, launch.

$ErrorActionPreference = 'Stop'
$msixPath = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'FileNodeClient.msix'
$packageName = 'Fastmail.FileNodeClient'

if (-not (Test-Path $msixPath)) {
    [System.Windows.Forms.MessageBox]::Show(
        "FileNodeClient.msix not found.`nMake sure it's in the same folder as this installer.",
        "Fastmail Files", 'OK', 'Error') | Out-Null
    exit 1
}

Add-Type -AssemblyName System.Windows.Forms

# Trust the signing certificate (needs elevation once)
$sig = Get-AuthenticodeSignature $msixPath
$cert = $sig.SignerCertificate
if ($cert) {
    $found = Get-ChildItem Cert:\LocalMachine\TrustedPeople -ErrorAction SilentlyContinue |
        Where-Object { $_.Thumbprint -eq $cert.Thumbprint }
    if (-not $found) {
        $tempCer = Join-Path $env:TEMP 'FastmailFiles-dev.cer'
        [System.IO.File]::WriteAllBytes($tempCer, $cert.Export('Cert'))
        $p = Start-Process certutil.exe -ArgumentList "-addstore TrustedPeople `"$tempCer`"" -Verb RunAs -Wait -PassThru
        Remove-Item $tempCer -ErrorAction SilentlyContinue
        if ($p.ExitCode -ne 0) {
            [System.Windows.Forms.MessageBox]::Show(
                "Could not trust the developer certificate.`nThe certificate must be trusted before the app can be installed.",
                "Fastmail Files", 'OK', 'Warning') | Out-Null
            exit 1
        }
    }
}

# Remove old version quietly
Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue | Remove-AppxPackage -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1

# Install
try {
    Add-AppxPackage -Path $msixPath
} catch {
    [System.Windows.Forms.MessageBox]::Show(
        "Installation failed:`n$($_.Exception.Message)",
        "Fastmail Files", 'OK', 'Error') | Out-Null
    exit 1
}

$installed = Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue
if ($installed) {
    # Launch immediately
    Start-Process "shell:AppsFolder\$($installed.PackageFamilyName)!App"
} else {
    [System.Windows.Forms.MessageBox]::Show(
        "Something went wrong — the app doesn't appear to be installed.",
        "Fastmail Files", 'OK', 'Error') | Out-Null
}
