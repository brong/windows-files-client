## Building

Development runs in WSL2 — use `dotnet.exe` (Windows binary) for all build/run commands.

### Debug build (all projects)

```
dotnet.exe build FileNodeClient.sln
```

The solution contains 8 projects: Jmap (platform-agnostic), Windows (cfapi), App (tray UI), Service (background sync), Ipc (shared IPC), ThumbnailExtension (comhost.dll for IThumbnailProvider), Installer (WiX MSI, legacy), Package (MSIX).

### Dev testing

```
dotnet.exe run --project FileNodeClient.App -- --debug --token <token> --session-url <url> --clean
```

### MSIX Package (primary installer)

The MSIX package provides package identity required for cloud files extensions (Explorer columns, thumbnails, context menus).

**Build from Windows (or WSL2 via `powershell.exe`):**
```
FileNodeClient.Package\build.cmd
```

This publishes App + Service + ThumbnailExtension, creates a self-signed dev cert, and produces `FileNodeClient.Package\bin\Release\FileNodeClient.msix`.

**Dev inner loop (no .msix rebuild needed):**
```powershell
# Publish all into shared dir so ServiceLauncher finds Service.exe
dotnet publish FileNodeClient.Service -c Release -r win-x64 --self-contained -o FileNodeClient.Package\publish
dotnet publish FileNodeClient.App -c Release -r win-x64 --self-contained -o FileNodeClient.Package\publish
# COM hosting requires framework-dependent (no --self-contained)
dotnet publish FileNodeClient.ThumbnailExtension -c Release -r win-x64 -o FileNodeClient.Package\publish
Add-AppxPackage -Register FileNodeClient.Package\AppxManifest.xml
```

This publishes into a flat layout and registers the package identity from loose files for fast iteration. Both executables must be in the same directory for stop/start/restart to work (ServiceLauncher finds Service.exe via AppContext.BaseDirectory). The ThumbnailExtension comhost.dll must also be in this directory for dllhost.exe to load it.

### MSI Installer (legacy, kept for now)

- **Bump `FileNodeClient.Installer/Version.txt` before each release build.** MajorUpgrade uses this version to detect and replace older installs.
- Build: `FileNodeClient.Installer\build.cmd`
- WiX requires native Windows paths — the build script auto-detects UNC (WSL2) and rsyncs to a temp dir.
