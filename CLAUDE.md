## Project: Fastmail Files Client

Multi-platform cloud filesystem client for the JMAP FileNode API (draft-ietf-jmap-filenode).

### Repository Structure

```
files-client/
├── DESIGN.md              # Cross-platform design document (architecture, protocol, pitfalls)
├── CLAUDE.md              # This file
├── windows/               # Windows client (C#/.NET, cfapi cloud files)
│   ├── CLAUDE.md          # Windows-specific build instructions
│   ├── ROADMAP.md         # Windows cfapi feature roadmap
│   ├── FileNodeClient.sln # .NET solution (7 projects)
│   └── ...
├── apple/                 # macOS & iOS client (Swift, FileProvider)
│   ├── DESIGN.md          # Apple-specific design document
│   └── ...
└── linux/                 # Linux FUSE client (Python, pyfuse3)
    └── ...
```

### Design Document

Read `DESIGN.md` before working on any platform. It contains:
- JMAP FileNode protocol details (session, auth, methods, blob operations, SSE push)
- Sync engine architecture (populate, incremental sync, outbox, concurrency)
- Echo suppression strategy (mtime-based, never key-presence-only)
- Timestamp preservation (always pass local created/modified to server)
- 21 documented pitfalls from the Windows implementation
- Platform integration comparison table

### Shared Protocol Concepts

All platforms implement the same JMAP protocol. Key invariants:
- **blobId is mutable (v10)**: Update file content via `FileNode/set update { blobId }` — node ID stays the same. Use `onExists: "replace"` only for new file creation with possible name collisions
- **Timestamps are client-managed**: Server does NOT auto-update `modified`/`accessed`. Always set `modified` on content and metadata changes. Set to `null` for server to use current time
- **Direct HTTP Write**: If `webWriteUrlTemplate` available, PUT to replace content directly (< 16 MB)
- **Echo suppression**: Compare mtime, never suppress on key presence alone
- **State tokens**: Use `FileNode/changes` for incremental sync, handle `cannotCalculateChanges`
- **Capability checks**: Verify server capabilities before using optional features (Blob, BlobExt, Quota)

### Platform Clients

#### Windows (`windows/`)
- **Language**: C# / .NET 9
- **File system**: Windows Cloud Files API (cfapi) — native placeholder/hydration support
- **Build**: `dotnet.exe build windows/FileNodeClient.sln` (from WSL2)
- **Status**: Feature-complete (sync, thumbnails, URI source, recycle bin, progressive hydration)
- See `windows/CLAUDE.md` for detailed build/test instructions

#### Apple (`apple/`)
- **Language**: Swift 6
- **File system**: Apple FileProvider framework (`NSFileProviderReplicatedExtension`) — native placeholder/hydration support
- **Platforms**: macOS 12+ (Finder integration) and iOS 16+ (Files app integration)
- **Build**: Xcode (open `apple/FastmailFiles/FastmailFiles.xcodeproj`)
- **Status**: Design phase
- See `apple/DESIGN.md` for architecture and design decisions

#### Linux (`linux/`)
- **Language**: Python 3
- **File system**: FUSE3 via pyfuse3 — userspace filesystem
- **Dependencies**: `pyfuse3`, `aiohttp` (install via venv)
- **Status**: In development
- Target: Mount JMAP FileNode tree as a local filesystem with on-demand hydration

### Development Notes

- The Windows client was built first; lessons are captured in `DESIGN.md`
- Each platform directory has its own `.gitignore` and build instructions
- The JMAP protocol layer should be reimplemented per-platform (not shared across languages)
- Focus on getting the sync engine right first, then add shell integration (thumbnails, etc.)
