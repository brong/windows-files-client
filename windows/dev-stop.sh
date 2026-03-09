#!/bin/bash
# Stop the FileNodeClient service
powershell.exe -NoProfile -Command '
Stop-Process -Name "FileNodeClient*" -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1
$remaining = Get-Process -Name "FileNodeClient*" -ErrorAction SilentlyContinue
if ($remaining) { Write-Host "WARNING: still running: $($remaining.Name)" } else { Write-Host "All processes stopped" }
'
