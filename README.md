# Fastmail Files Client

A proof-of-concept Windows filesystem adaptor that exposes [Fastmail](https://www.fastmail.com/) Files as a native cloud storage provider using the Windows Cloud Files API (cfapi). Files appear in Explorer with cloud status icons (blue cloud / green checkmark) and a dedicated nav pane entry, just like OneDrive or Dropbox.

## Features

- **Native Explorer integration** — sync root appears in the Explorer nav pane with the Fastmail favicon
- **Cloud status icons** — placeholder files show a blue cloud overlay; hydrated files show a green checkmark
- **On-demand hydration** — files are downloaded from Fastmail only when opened (Full hydration policy)
- **Upload support** — files copied or modified in the sync root are automatically uploaded to Fastmail
- **Change polling** — server-side changes are detected and reflected locally every 30 seconds
- **Stub mode** — `--stub` flag for testing cfapi integration without a Fastmail account

## Architecture

The solution is split into three projects:

```
FilesClient.sln
├── FilesClient.Jmap/          # Platform-agnostic JMAP client
│   ├── IJmapClient.cs         # Client interface (real + stub)
│   ├── JmapClient.cs          # Fastmail StorageNode API implementation
│   ├── JmapSession.cs         # JMAP session/capability discovery
│   ├── StubJmapClient.cs      # In-memory stub for testing
│   ├── Auth/
│   │   ├── TokenAuth.cs       # Bearer token auth handler
│   │   └── DebugLoggingHandler.cs  # Optional HTTP traffic logging
│   └── Models/
│       ├── StorageNode.cs     # File/folder model
│       ├── JmapRequest.cs     # JMAP request envelope
│       └── JmapResponse.cs    # JMAP response models (get, set, changes, upload)
│
├── FilesClient.Windows/       # Windows Cloud Files API integration
│   ├── SyncEngine.cs          # Main orchestrator (populate, poll, upload)
│   ├── SyncRoot.cs            # WinRT sync root registration + cfapi connection
│   ├── SyncCallbacks.cs       # FETCH_DATA callback for on-demand hydration
│   ├── PlaceholderManager.cs  # Bulk placeholder creation via CfCreatePlaceholders
│   ├── FileChangeWatcher.cs   # FileSystemWatcher with debouncing for uploads
│   └── NavPaneIntegration.cs  # Cleanup for stale registry entries
│
└── FilesClient.App/           # Console application entry point
    └── Program.cs             # CLI argument parsing, JMAP connection, sync loop
```

## How it works

1. **Registration** — the app registers a sync root with `StorageProviderSyncRootManager.Register()`, which gives Explorer the cloud provider metadata (name, icon, hydration policy). The sync root ID follows the required `Provider!WindowsSID!AccountId` format.

2. **Connection** — `CfConnectSyncRoot` establishes the callback channel for hydration requests.

3. **Population** — the full directory tree is fetched from the Fastmail StorageNode API, then placeholders are created in one pass using `CfCreatePlaceholders`. Each placeholder stores the StorageNode ID as its file identity blob.

4. **Hydration** — when a user opens a placeholder file, Windows sends a `FETCH_DATA` callback. The callback extracts the StorageNode ID, downloads the blob via JMAP, and transfers the data back through `CfExecute`.

5. **Upload** — a `FileSystemWatcher` monitors the sync root for new or modified files (skipping placeholders). Changed files are uploaded via JMAP blob upload, then either a new StorageNode is created or an existing one is updated. The file is then converted to a cloud placeholder with `CfConvertToPlaceholder`.

6. **Change polling** — every 30 seconds, `StorageNode/changes` is called to detect server-side creates, updates, and deletes.

## Prerequisites

- Windows 10/11 with NTFS (cfapi requires NTFS)
- .NET 9 SDK with the `net9.0-windows10.0.22621` targeting pack
- A Fastmail account with an [app password](https://www.fastmail.com/help/clients/apppassword.html) (scope: `JMAP Files`)

## Building

From WSL2 or a Windows terminal:

```
dotnet build
```

## Usage

```
FilesClient.App --token <fastmail-app-password>
```

### Options

| Flag | Description |
|------|-------------|
| `--token <token>` | Fastmail app password / bearer token |
| `--session-url <url>` | JMAP session URL (default: `https://api.fastmail.com/jmap/session`) |
| `--sync-root <path>` | Local sync folder path (default: `~/<username> Files`) |
| `--debug` | Log all JMAP HTTP request/response traffic to stderr |
| `--stub` | Use an in-memory stub client for cfapi testing without a server |

### Example

```
# Real Fastmail account
FilesClient.App --token fmu1-xxxxxxxxxxxxxxxx

# Stub mode for testing cfapi integration
FilesClient.App --stub
```

The sync root folder appears in your user profile directory (e.g. `C:\Users\you\you@fastmail.com Files`) and in the Explorer navigation pane.

Press Ctrl+C to shut down cleanly. The sync root is unregistered on exit.

## Current API

This client currently uses the Fastmail-specific **StorageNode** API. It will be updated to use the standard **JMAP FileNode** API ([draft-ietf-jmap-filenode](https://datatracker.ietf.org/doc/draft-ietf-jmap-filenode/)) once that specification is finalised.

## Limitations

- **Proof of concept** — not intended for production use
- **No delete sync** — server-side deletes are logged but not yet applied locally
- **No rename/move sync** — renames and moves are not yet detected or synced
- **No conflict resolution** — concurrent edits are not handled
- **Depth limit** — initial population traverses 3 levels deep
- **Unpackaged app** — runs as a console app without MSIX packaging
