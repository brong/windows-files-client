## Active work: simplification → reliability port (READ FIRST)

**Start at `SIMPLIFICATION.md`.** Phases 1–3 there (JMAP account scoping,
single process, LoginManager) are written but were never compiled — your first
job is to build, smoke-test and commit them. Phase 4 (collapsing `SyncEngine`'s
four sync paths into one) is the vehicle for the reliability port: don't port a
fix into four places.

The cfapi feature set is done (see `ROADMAP.md`). After simplification, the
effort is **reliability hardening** — making the client detect every sync
failure / drift and recover understandably. `RELIABILITY.md` is the
cross-platform tracker, with a "For the Windows port — start here" section
mapping each gap to the Apple reference commit/file and the Windows target file.

The **Apple client (`../apple/`) is the reference implementation**: every gap is
already fixed and unit-tested there. For each item, read the Apple commit + code
and the Apple tests (which encode the expected behavior), then port to the C#
files cited in `RELIABILITY.md`. The cross-platform lessons are also distilled as
`../DESIGN.md` pitfalls #34–39. Several gaps were *missing* on Windows
specifically (e.g. download digest verified-but-ignored, silent dirty-vs-server
conflict drop, state-token crash window) — don't assume the happy path is enough.

## Building

Development runs in WSL2 — use `dotnet.exe` (Windows binary) for all build/run commands.

### Debug build (all projects)

```
dotnet.exe build FileNodeClient.sln
```

The solution contains 5 projects: Logging (cross-platform, Log static class), Jmap (cross-platform, JMAP protocol), Windows (cfapi sync engine), App (single tray process: login/sync supervision + UI + COM hosts), Package (MSIX). The native ThumbnailExtension DLL is built separately with MinGW (not in the .sln).

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

This publishes App + ThumbnailExtension, creates a self-signed dev cert, and produces `FileNodeClient.Package\bin\Release\FileNodeClient.msix`.

**Dev inner loop (no .msix rebuild needed):**
```powershell
dotnet publish FileNodeClient.App -c Release -r win-x64 --self-contained -o FileNodeClient.Package\publish
# Register from publish dir — SurrogateServer DLL paths resolve relative to AppxManifest.xml
copy FileNodeClient.Package\AppxManifest.xml FileNodeClient.Package\publish\
xcopy /s /y FileNodeClient.Package\Assets FileNodeClient.Package\publish\Assets\
Add-AppxPackage -Register FileNodeClient.Package\publish\AppxManifest.xml
```

This publishes into a flat layout and registers the package identity from the publish directory. The manifest must be alongside the published files so SurrogateServer `Path` attributes resolve correctly (dllhost.exe loads comhost.dll relative to the package root).

