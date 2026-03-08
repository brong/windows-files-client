#!/bin/bash
# Start the FileNodeClient service
# Usage: ./dev-start.sh [--clean]
#   --clean: unregister sync root and delete local files before syncing

set -e

PUBLISHDIR="C:\\Users\\brong\\AppData\\Local\\Temp\\files-client-build\\FileNodeClient.Package\\publish"
TOKEN="fmu1-358041ae-f84302c93d1827aaa1a82d620f30d9b5-0-98e8f87d385828490554c671484088e1"
SESSION_URL="https://api.fastmaildev.com/jmap/session"

EXTRA_ARGS=""
if [ "$1" = "--clean" ]; then
    EXTRA_ARGS=",'--clean'"
fi

powershell.exe -Command "Start-Process -FilePath '$PUBLISHDIR\\FileNodeClient.Service.exe' -ArgumentList '--debug','--token','$TOKEN','--session-url','$SESSION_URL'$EXTRA_ARGS"

echo "Service started, waiting for sync..."
sleep 12

echo "=== Status ==="
powershell.exe -NoProfile -Command '
$proc = Get-Process FileNodeClient.Service -ErrorAction SilentlyContinue
if ($proc) { Write-Host "PID: $($proc.Id), Path: $($proc.Path)" } else { Write-Host "NOT RUNNING"; exit 1 }

$root = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager"
$entry = Get-ChildItem -LiteralPath $root | Where-Object { $_.PSChildName -match "FileNodeClient" }
if ($entry) {
    $subs = Get-ChildItem -LiteralPath $entry.PSPath -ErrorAction SilentlyContinue | ForEach-Object { $_.PSChildName }
    Write-Host "HKLM subkeys: $($subs -join ", ")"

    # Set AUMID to link sync root to MSIX package (required for cloud files handlers)
    $pkg = Get-AppxPackage -Name "Fastmail.FileNodeClient" -ErrorAction SilentlyContinue
    if ($pkg) {
        $fn = $pkg.PackageFamilyName
        $aumid = "${fn}!Service"
        # Use .NET registry API — PowerShell Set-ItemProperty escapes ! with backslash
        $regKey = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey(
            "SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager\" + $entry.PSChildName, $true)
        if ($regKey) {
            $regKey.SetValue("AUMID", $aumid, [Microsoft.Win32.RegistryValueKind]::String)
            $regKey.Close()
            Write-Host "AUMID: $aumid"
        }
    }
} else { Write-Host "No sync root registered!" }

Write-Host "COM thumb: $(Test-Path "HKCU:\Software\Classes\CLSID\{B8C4F3E2-1A5D-4B9C-C6F7-2E4D8A9B3F1C}")"
Write-Host "COM uri: $(Test-Path "HKCU:\Software\Classes\CLSID\{A7B3E2D1-9F4C-4A8B-B5E6-1D3C7F8A2E9B}")"

if (Test-Path "C:\Users\brong\AppData\Local\FileNodeClient\debug.log") {
    Write-Host ""
    Get-Content "C:\Users\brong\AppData\Local\FileNodeClient\debug.log" -Tail 5
}
'
