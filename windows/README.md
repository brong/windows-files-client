# FileNodeClient — Windows

A Windows cloud storage client for [Fastmail](https://www.fastmail.com/) Files using the Windows Cloud Files API (cfapi). Files appear in Explorer with cloud status icons, on-demand hydration, and thumbnail previews — the same experience as OneDrive or Dropbox.

## Features

- **Native Explorer integration** — sync root in the nav pane with Fastmail branding and cloud status overlays
- **On-demand hydration** — files download only when opened; dehydrate to free space
- **Two-way sync** — local changes upload automatically; server changes arrive via SSE push
- **Thumbnails** — Explorer shows server-generated thumbnails for images and documents
- **Content URI source** — "View online" opens files in the Fastmail web UI
- **Recycle bin** — deleted files move to the server trash (FileNode `trash` role)
- **Progressive hydration** — large files stream via range requests
- **OAuth and app passwords** — authenticate with either method
- **Auto-start** — App and Service launch at login via MSIX startup tasks

## Architecture

The solution has 7 .NET projects plus a native C DLL:

```
FileNodeClient.sln
├── FileNodeClient.Logging/        # Cross-platform structured logging (Log static class)
├── FileNodeClient.Jmap/           # Cross-platform JMAP FileNode protocol client
├── FileNodeClient.Ipc/            # Named-pipe IPC messages between App and Service
├── FileNodeClient.Windows/        # cfapi sync engine (placeholders, hydration, callbacks)
├── FileNodeClient.App/            # System tray UI (login, status, account management)
├── FileNodeClient.Service/        # Background sync engine + thumbnail/URI COM servers
└── FileNodeClient.Package/        # MSIX packaging (manifest, assets, build.cmd)

FileNodeClient.ThumbnailExtension/ # Native C DLL — IThumbnailProvider via COM SurrogateServer
                                   # Built separately (not in the .sln)
```

**App** runs as a system tray application. It handles authentication and launches **Service** as a child process. Service runs the sync engine, responds to cfapi callbacks, and hosts COM servers for thumbnails and content URIs. The two communicate over named pipes (IPC).

## Prerequisites

- Windows 10 or 11 (NTFS required — cfapi does not work on ReFS or FAT)
- .NET 9 SDK with the `net9.0-windows10.0.22621` targeting pack
- **Native DLL compiler** — either MinGW (`x86_64-w64-mingw32-gcc`, recommended from WSL2) or MSVC (VS2022 C++ desktop workload)
- **MSIX tools** — `makeappx.exe` and `signtool.exe` from the Windows SDK (installed with VS2022, found under `Microsoft Visual Studio\Shared\NuGetPackages\microsoft.windows.sdk.buildtools\...`)

## Building

### Debug build (all .NET projects)

From WSL2:

```
dotnet.exe build windows/FileNodeClient.sln
```

Or from a Windows terminal:

```
dotnet build FileNodeClient.sln
```

### MSIX package

The MSIX package provides the package identity required for cloud files shell extensions (thumbnails, content URIs, context menus).

#### From a Windows terminal

```
FileNodeClient.Package\build.cmd
```

This publishes App + Service, compiles the native DLL with MSVC, creates a self-signed dev cert (first run only), and produces `FileNodeClient.Package\bin\Release\FileNodeClient.msix`.

> **Note:** `build.cmd` uses MSVC (`cl.exe` via `vcvarsall.bat`) for the native DLL. This can fail if the VS environment doesn't propagate correctly through nested `cmd /c` shells.

#### From WSL2 (recommended)

The WSL2 workflow is more reliable — it uses MinGW for the native DLL and avoids MSVC environment issues:

```bash
# 1. Copy source to a local Windows drive (UNC paths break MSIX tools)
BUILDDIR="/mnt/c/Users/$USER/AppData/Local/Temp/files-client-build"
rm -rf "$BUILDDIR"
rsync -a --exclude='.git' --exclude='.claude' --exclude='bin' --exclude='obj' \
    windows/ "$BUILDDIR/"

# 2. Publish .NET projects
cd "$BUILDDIR"
dotnet.exe publish FileNodeClient.Service/FileNodeClient.Service.csproj \
    -c Release -r win-x64 --self-contained -o FileNodeClient.Package/publish
dotnet.exe publish FileNodeClient.App/FileNodeClient.App.csproj \
    -c Release -r win-x64 --self-contained -o FileNodeClient.Package/publish

# 3. Build native thumbnail DLL with MinGW
x86_64-w64-mingw32-gcc -shared -O2 \
    -o FileNodeClient.Package/publish/FileNodeClient.ThumbnailExtension.dll \
    FileNodeClient.ThumbnailExtension/ThumbnailHandler.c \
    FileNodeClient.ThumbnailExtension/ThumbnailHandler.def \
    -lole32 -lwindowscodecs -lgdi32 -luser32 -luuid

# 4. Copy manifest and assets into publish dir
cp FileNodeClient.Package/AppxManifest.xml FileNodeClient.Package/publish/
cp -r FileNodeClient.Package/Assets FileNodeClient.Package/publish/

# 5. Generate mapping file for makeappx (flat layout)
{
  echo "[Files]"
  echo '"AppxManifest.xml" "AppxManifest.xml"'
  for asset in StoreLogo Square44x44Logo Square150x150Logo; do
    echo "\"Assets\\$asset.png\" \"Assets\\$asset.png\""
  done
  for f in FileNodeClient.Package/publish/*.exe \
           FileNodeClient.Package/publish/*.dll \
           FileNodeClient.Package/publish/*.json; do
    [ -f "$f" ] && echo "\"$(basename "$f")\" \"$(basename "$f")\""
  done
} > FileNodeClient.Package/mapping.txt

# 6. Pack and sign MSIX via PowerShell (makeappx needs Windows-native paths)
powershell.exe -NoProfile -Command '
  $sdkBin = (Get-ChildItem "C:\Program Files*\Microsoft Visual Studio\Shared\NuGetPackages\microsoft.windows.sdk.buildtools\*\bin\*\x64\makeappx.exe" -ErrorAction SilentlyContinue | Select-Object -Last 1).DirectoryName
  if (-not $sdkBin) { Write-Error "makeappx.exe not found"; exit 1 }
  $build = "C:\Users\brong\AppData\Local\Temp\files-client-build"
  $pub = "$build\FileNodeClient.Package\publish"
  $msix = "$build\FileNodeClient.Package\bin\Release\FileNodeClient.msix"
  $pfx = "$build\FileNodeClient.Package\DevCert.pfx"
  New-Item -ItemType Directory -Force -Path (Split-Path $msix) | Out-Null
  Push-Location $pub
  & "$sdkBin\makeappx.exe" pack /f "$build\FileNodeClient.Package\mapping.txt" /p $msix /o
  if (-not (Test-Path $pfx)) {
    $cert = New-SelfSignedCertificate -Type Custom -Subject "CN=Fastmail Pty Ltd" `
      -KeyUsage DigitalSignature -FriendlyName "FileNodeClient Dev" `
      -CertStoreLocation "Cert:\CurrentUser\My" `
      -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3","2.5.29.19={text}")
    Export-PfxCertificate -Cert $cert -FilePath $pfx `
      -Password (ConvertTo-SecureString -String "devpass" -Force -AsPlainText)
  }
  & "$sdkBin\signtool.exe" sign /fd SHA256 /a /f $pfx /p devpass $msix
  Pop-Location
'

# 7. Copy MSIX back to repo
cp "$BUILDDIR/FileNodeClient.Package/bin/Release/FileNodeClient.msix" \
    windows/FileNodeClient.Package/bin/Release/
```

The `dev-deploy.sh` script automates steps 1-4 plus loose-file registration (no .msix build) for the dev inner loop.

#### Key gotchas

- **UNC paths break MSIX tools**: `makeappx.exe` cannot read files from `\\wsl.localhost\...` paths. Always rsync to a local Windows drive first.
- **makeappx resolves relative paths from CWD**: The mapping file uses relative filenames, so `makeappx` must run with CWD set to the publish directory (use `Push-Location`).
- **makeappx/signtool not on PATH**: They live deep in the VS SDK build tools NuGet cache, not in `C:\Program Files (x86)\Windows Kits`. Find them with: `Get-ChildItem "C:\Program Files*\Microsoft Visual Studio\Shared\NuGetPackages\microsoft.windows.sdk.buildtools\*\bin\*\x64\makeappx.exe"`
- **MinGW vs MSVC for the native DLL**: Both work. MinGW (`x86_64-w64-mingw32-gcc`) is simpler from WSL2 since it doesn't require vcvarsall environment setup. MSVC (`cl.exe`) is used by `build.cmd` but the nested shell environment propagation is fragile.

### Dev inner loop

For rapid iteration without rebuilding the full MSIX, publish into the Package directory and register directly:

```powershell
# Publish App + Service into shared directory
dotnet publish FileNodeClient.Service -c Release -r win-x64 --self-contained -o FileNodeClient.Package\publish
dotnet publish FileNodeClient.App -c Release -r win-x64 --self-contained -o FileNodeClient.Package\publish

# Copy manifest and assets alongside published files
copy FileNodeClient.Package\AppxManifest.xml FileNodeClient.Package\publish\
xcopy /s /y FileNodeClient.Package\Assets FileNodeClient.Package\publish\Assets\

# Register package identity from the publish directory
Add-AppxPackage -Register FileNodeClient.Package\publish\AppxManifest.xml
```

Both executables must be in the same directory (ServiceLauncher finds Service.exe via `AppContext.BaseDirectory`). The manifest must be alongside them so COM SurrogateServer DLL paths resolve correctly.

## Usage

After installing the MSIX or registering via the dev inner loop, launch **FileNodeClient** from the Start menu. A system tray icon appears — click it to add your Fastmail account.

The sync root folder appears in your user profile (e.g. `C:\Users\you\you@fastmail.com Files`) and in the Explorer navigation pane.

### Dev testing

```
dotnet.exe run --project FileNodeClient.App -- --debug --token <app-password> --session-url <url>
```

| Flag | Description |
|------|-------------|
| `--token <token>` | Fastmail app password or bearer token |
| `--session-url <url>` | JMAP session URL (default: `https://api.fastmail.com/jmap/session`) |
| `--sync-root <path>` | Local sync folder path (default: `~/<email> Files`) |
| `--debug` | Log JMAP HTTP traffic to stderr |
| `--clean` | Remove existing sync root and re-sync from scratch |

## Protocol

This client implements the **JMAP FileNode** protocol ([draft-ietf-jmap-filenode](https://datatracker.ietf.org/doc/draft-ietf-jmap-filenode/)). See the top-level `DESIGN.md` for protocol details, sync architecture, and implementation pitfalls.
