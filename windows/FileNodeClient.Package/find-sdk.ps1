# Find the latest Windows SDK x64 tools directory containing makeappx.exe
$root = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
if (Test-Path $root) {
    Get-ChildItem $root -Directory -Filter '10.*' |
        Sort-Object Name -Descending |
        ForEach-Object {
            $x = Join-Path $_.FullName 'x64\makeappx.exe'
            if (Test-Path $x) { Split-Path $x; break }
        }
}
