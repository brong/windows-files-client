#!/bin/bash
# Full cleanup of FileNodeClient from Windows
# Stops processes, removes MSIX, registry, sync root, config, build dir

set -e

echo "=== Stopping processes ==="
powershell.exe -NoProfile -Command '
Stop-Process -Name "FileNodeClient*" -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2
$remaining = Get-Process -Name "FileNodeClient*" -ErrorAction SilentlyContinue
if ($remaining) { Write-Host "WARNING: still running: $($remaining.Name)" } else { Write-Host "All processes stopped" }
'

echo "=== Removing MSIX ==="
powershell.exe -NoProfile -Command '
$pkg = Get-AppxPackage -Name "Fastmail.FileNodeClient" -ErrorAction SilentlyContinue
if ($pkg) { Remove-AppxPackage -Package $pkg.PackageFullName; Write-Host "Removed" } else { Write-Host "None" }
'

echo "=== Cleaning registry ==="
powershell.exe -NoProfile -Command '
# COM handler CLSIDs
Remove-Item "HKCU:\Software\Classes\CLSID\{B8C4F3E2-1A5D-4B9C-C6F7-2E4D8A9B3F1C}" -Recurse -ErrorAction SilentlyContinue
Remove-Item "HKCU:\Software\Classes\CLSID\{A7B3E2D1-9F4C-4A8B-B5E6-1D3C7F8A2E9B}" -Recurse -ErrorAction SilentlyContinue

# Shell namespace CLSID created by StorageProviderSyncRootManager.Register()
# Find it dynamically by looking for TargetFolderPath matching our sync root
Get-ChildItem "HKCU:\Software\Classes\CLSID" -ErrorAction SilentlyContinue | ForEach-Object {
    $bag = Join-Path $_.PSPath "Instance\InitPropertyBag"
    if (Test-Path $bag) {
        $target = (Get-ItemProperty -LiteralPath $bag -ErrorAction SilentlyContinue).TargetFolderPath
        if ($target -match "fastmail" -or $target -match "FileNodeClient") {
            $clsid = $_.PSChildName
            Remove-Item -LiteralPath $_.PSPath -Recurse -Force
            Write-Host "Removed namespace CLSID: $clsid"
            # Also clean Desktop\NameSpace and HideDesktopIcons
            Remove-Item "HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Desktop\NameSpace\$clsid" -Recurse -Force -ErrorAction SilentlyContinue
            Get-ChildItem "HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons" -ErrorAction SilentlyContinue | ForEach-Object {
                Remove-ItemProperty -LiteralPath $_.PSPath -Name $clsid -ErrorAction SilentlyContinue
            }
        }
    }
}

# HKLM SyncRootManager
$root = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager"
Get-ChildItem -LiteralPath $root | Where-Object { $_.PSChildName -match "FileNodeClient" } | ForEach-Object { Remove-Item -LiteralPath $_.PSPath -Recurse -Force; Write-Host "Removed HKLM: $($_.PSChildName)" }
Write-Host "Registry clean"
'

echo "=== Removing folders ==="
powershell.exe -NoProfile -Command '
cmd /c "rmdir /s /q ""C:\Users\brong\brong@fastmaildev.com Files""" 2>$null
Remove-Item "C:\Users\brong\AppData\Local\FileNodeClient" -Recurse -Force -ErrorAction SilentlyContinue
exit 0
'
rm -rf /mnt/c/Users/brong/AppData/Local/Temp/files-client-build || true

echo "=== Verify ==="
powershell.exe -NoProfile -Command '
Write-Host "Processes: $(Get-Process FileNodeClient* -ErrorAction SilentlyContinue | Measure-Object | Select-Object -ExpandProperty Count)"
Write-Host "MSIX: $((Get-AppxPackage -Name "Fastmail.FileNodeClient" -ErrorAction SilentlyContinue) -ne $null)"
$root = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager"
Write-Host "HKLM: $((Get-ChildItem -LiteralPath $root | Where-Object { $_.PSChildName -match "FileNodeClient" } | Measure-Object).Count)"
Write-Host "COM thumb: $(Test-Path "HKCU:\Software\Classes\CLSID\{B8C4F3E2-1A5D-4B9C-C6F7-2E4D8A9B3F1C}")"
Write-Host "COM uri: $(Test-Path "HKCU:\Software\Classes\CLSID\{A7B3E2D1-9F4C-4A8B-B5E6-1D3C7F8A2E9B}")"
# Check for any remaining namespace CLSIDs
$nsCount = 0
Get-ChildItem "HKCU:\Software\Classes\CLSID" -ErrorAction SilentlyContinue | ForEach-Object {
    $bag = Join-Path $_.PSPath "Instance\InitPropertyBag"
    if (Test-Path $bag) {
        $target = (Get-ItemProperty -LiteralPath $bag -ErrorAction SilentlyContinue).TargetFolderPath
        if ($target -match "fastmail" -or $target -match "FileNodeClient") { $nsCount++; Write-Host "STALE namespace CLSID: $($_.PSChildName) -> $target" }
    }
}
Write-Host "Namespace CLSIDs: $nsCount"
Write-Host "Folder: $(Test-Path "C:\Users\brong\brong@fastmaildev.com Files")"
Write-Host "Config: $(Test-Path "C:\Users\brong\AppData\Local\FileNodeClient")"
Write-Host "Build: $(Test-Path "C:\Users\brong\AppData\Local\Temp\files-client-build")"
'
echo "=== Clean complete ==="
