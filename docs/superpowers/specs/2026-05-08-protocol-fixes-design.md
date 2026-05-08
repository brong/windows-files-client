# Protocol Fixes — Design Spec

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix three protocol-correctness gaps in the Apple client: remove the deprecated legacy blob capability, make filename handling robust against user-introduced corruption, and eliminate ghost files after a full re-enumeration.

**Architecture:** Three independent commits touching `JmapTypes`, `JmapClient`, `FilenameUtils`, `FileProviderExtension`, `FileProviderEnumerator`, and `NodeDatabase`. No new source files. Schema change to `nodes` and `sync_state` tables (no migration — accounts will be removed and re-added).

**Tech Stack:** Swift 6, GRDB (SQLite), NSFileProviderReplicatedExtension

---

## Fix 1 — Remove legacy blob capability

### What and why

`JmapCapability.blob = "urn:ietf:params:jmap:blob"` is the deprecated capability. `CLAUDE.md` forbids sending it in `using` arrays. The only surviving use is in `uploadBlobChunked`, which reads `maxDataSources` from the legacy capability's metadata instead of from `blob2`. This must be moved to `blob2`.

### Files

- Modify: `apple/Sources/JmapClient/JmapTypes.swift`
- Modify: `apple/Sources/JmapClient/JmapClient.swift`

### Changes

**`JmapTypes.swift`** — delete the `blob` constant from `JmapCapability`:
```swift
// DELETE this line:
public static let blob = "urn:ietf:params:jmap:blob"
```

**`JmapClient.swift`** — in `uploadBlobChunked`, replace the legacy lookup:
```swift
// OLD:
let blobCap = session.accounts[accountId]?.accountCapabilities[JmapCapability.blob]
let maxDataSources = blobCap?.dictValue?["maxDataSources"]?.intValue ?? 100

// NEW:
let maxDataSources = session.accounts[accountId]?
    .accountCapabilities[JmapCapability.blob2]?.dictValue?["maxDataSources"]?.intValue ?? 100
```

Remove the now-stale comment ("maxDataSources comes from the legacy blob capability — readable without sending that URI").

### Verification

`grep -r "JmapCapability.blob[^2]" apple/Sources/` returns no results after the change.

---

## Fix 2 — Filename robustness

### What and why

`FilenameUtils.sanitize` replaces `/` with U+2215 (DIVISION SLASH) and `:` with U+A789 (MODIFIER LETTER COLON) for safe display in macOS Finder. `FilenameUtils.desanitize` reverses this before sending names to the JMAP server.

The gap: if a user deliberately creates or renames a file with a literal U+2215 or U+A789 in the name, `desanitize` maps those characters to `/` and `:` respectively, silently corrupting the server name. We cannot distinguish "this character came from our own sanitize" from "the user typed it."

The fix: detect these characters at the point of use (before sending to the server) and reject the operation with a user-facing rename prompt rather than silently corrupting.

### Files

- Modify: `apple/Sources/JmapClient/FilenameUtils.swift`
- Modify: `apple/Sources/FileProvider/FileProviderExtension.swift`
- Modify: `apple/Tests/JmapClientTests/FilenameUtilsTests.swift`

### Changes

**`FilenameUtils.swift`** — add detection function:
```swift
/// Returns true if the name contains U+2215 (DIVISION SLASH) or U+A789
/// (MODIFIER LETTER COLON) — the substitute characters used by `sanitize`.
/// A macOS filename containing these cannot be safely round-tripped through
/// `desanitize` because we cannot tell whether they were introduced by
/// `sanitize` (mapping server `/` or `:`) or typed literally by the user.
public static func containsSubstituteChars(_ name: String) -> Bool {
    name.unicodeScalars.contains { $0.value == 0x2215 || $0.value == 0xA789 }
}
```

**`FileProviderExtension.swift`** — add guard before every `desanitizeFilename` call in `createItem` and `modifyItem`. The guard sits between receiving the filename from the FileProvider system and using it in a JMAP call:
```swift
let rawName = itemTemplate.filename  // (or item.filename for modifyItem)
guard !FilenameUtils.containsSubstituteChars(rawName) else {
    completionHandler(nil, [], false, NSFileProviderError(.filenameCollision))
    return
}
let fileName = desanitizeFilename(rawName)
```

`NSFileProviderError(.filenameCollision)` causes the system to present the user with a rename dialog — the correct UX for "this name can't be used."

**`FilenameUtilsTests.swift`** — add tests:

```swift
// Round-trip: server name with / and : survives sanitize→desanitize
@Test func testRoundTripSlashAndColon() {
    let serverName = "report: 2026/05/08.txt"
    XCTAssertEqual(FilenameUtils.desanitize(FilenameUtils.sanitize(serverName)),
                   FilenameUtils.nfc(serverName))
}

// Round-trip: NFC/NFD normalization
@Test func testRoundTripNFCNormalization() {
    let nfd = "cafe\u{0301}.txt"  // NFD: e + combining acute
    let nfc = "caf\u{00E9}.txt"   // NFC: é
    XCTAssertEqual(FilenameUtils.sanitize(nfd), FilenameUtils.sanitize(nfc))
    XCTAssertEqual(FilenameUtils.desanitize(nfd), FilenameUtils.desanitize(nfc))
}

// Substitute char detection
@Test func testDetectsSubstituteChars() {
    XCTAssertTrue(FilenameUtils.containsSubstituteChars("foo\u{2215}bar"))   // DIVISION SLASH
    XCTAssertTrue(FilenameUtils.containsSubstituteChars("foo\u{A789}bar"))   // MODIFIER LETTER COLON
    XCTAssertFalse(FilenameUtils.containsSubstituteChars("normal-name.txt"))
    XCTAssertFalse(FilenameUtils.containsSubstituteChars("report: 2026.txt")) // real colon — no substitute
}

// sanitize does not double-encode: a server name already containing U+2215 is unchanged
@Test func testSanitizeDoesNotDoubleEncode() {
    let nameWithSubstitute = "foo\u{2215}bar.txt"
    XCTAssertEqual(FilenameUtils.sanitize(nameWithSubstitute), nameWithSubstitute)
}
```

---

## Fix 3 — Echo suppression via BFS generation counter

### What and why

`enumerateWorkingSet` performs a full BFS of the server's file tree after a `cannotCalculateChanges` error (state token too old). The BFS upserts all returned nodes but does not remove DB nodes that the server no longer has. The result: files deleted on the server persist in the local DB and appear as ghost entries in Finder until the next incremental sync happens to enumerate them as deleted.

The fix: stamp every node upserted during BFS with a monotonically-increasing generation number. After BFS completes, delete any node for this account whose generation is behind the current one — those nodes were not returned by the server and no longer exist.

### Files

- Modify: `apple/Sources/JmapClient/NodeDatabase.swift`
- Modify: `apple/Sources/FileProvider/FileProviderEnumerator.swift`
- Modify: `apple/Tests/JmapClientTests/NodeDatabaseTests.swift`

### Schema changes

No migration. User will remove and re-add accounts to get fresh databases.

The `nodes` table is per-account (one SQLite file per account, named `nodes-{domainId}.sqlite`), so there is no `accountId` column — all rows in the file belong to that account.

`sync_state` is a key-value table `(key TEXT PRIMARY KEY, value TEXT)`.

In `NodeDatabase`, add to the `v1_initial` migration's `CREATE TABLE` statements:

```swift
// nodes table — add generation stamp column:
t.column("bfsGeneration", .integer).notNull().defaults(to: 0)

// sync_state already exists as key-value; bfsGeneration is stored as a row:
// key = "bfsGeneration", value = "5"  (no schema change needed for sync_state)
```

### New NodeDatabase methods

```swift
/// Increments the BFS generation counter (stored in sync_state key-value table)
/// and returns the new value. Called once at the start of enumerateWorkingSet.
func incrementBfsGeneration() async -> Int

/// Upserts a server node into the DB. When bfsGeneration is non-nil,
/// also stamps the node row with that generation.
/// Non-BFS callers (incremental sync) pass nil — their nodes keep whatever
/// generation they were last stamped with. pruneStaleNodes is only called
/// after a complete BFS, so incremental-sync nodes are not at risk of
/// spurious pruning during normal operation.
func upsertFromServer(_ node: FileNode, bfsGeneration: Int? = nil) async

/// Deletes all nodes whose bfsGeneration does not match `generation`.
/// The DB file is already scoped to one account — no accountId filter needed.
/// Called once after a complete BFS to remove nodes the server no longer has.
func pruneStaleNodes(generation: Int) async
```

Implementation sketches:

```swift
func incrementBfsGeneration() async -> Int {
    // sync_state is a key-value table: key="bfsGeneration", value=N
    return (try? pool.write { db -> Int in
        try db.execute(sql: """
            INSERT INTO sync_state (key, value) VALUES ('bfsGeneration', '1')
            ON CONFLICT(key) DO UPDATE SET value = CAST(CAST(value AS INTEGER) + 1 AS TEXT)
            """)
        return try Int.fetchOne(db,
            sql: "SELECT CAST(value AS INTEGER) FROM sync_state WHERE key = 'bfsGeneration'") ?? 1
    }) ?? 1
}

func pruneStaleNodes(generation: Int) async {
    // nodes table has no accountId column — the DB file is already scoped to one account
    try? pool.write { db in
        try db.execute(
            sql: "DELETE FROM nodes WHERE bfsGeneration != ?",
            arguments: [generation])
    }
}
```

For `upsertFromServer`, the existing upsert SQL gets an additional `bfsGeneration = ?` field in the ON CONFLICT DO UPDATE clause when the parameter is non-nil. When nil, the column is omitted from the SET clause (generation stays at its current/default value).

### FileProviderEnumerator changes

In `enumerateWorkingSet`, three additions:

```swift
// 1. Before BFS loop — get the new generation for this pass
let bfsGen = await database.incrementBfsGeneration()

// 2. Inside BFS loop — stamp each upserted node (replaces bare upsertFromServer calls)
for node in allNodes {
    await database.upsertFromServer(node, bfsGeneration: bfsGen)
}

// 3. After upserts, before setStateToken — prune stale nodes
await database.pruneStaleNodes(generation: bfsGen)
await database.setStateToken(finalState)
```

The prune runs after all upserts so partially-completed BFS passes (extension killed mid-way) leave the DB in a safe state: generation incremented but prune not run = some stale nodes remain. On the next restart the token is empty → another full BFS → generation increments again → prune catches everything.

### Test cases

Use `FileNode` directly in tests (not a fictional `toFileNode()` helper — `upsertFromServer` takes `FileNode`). A minimal `FileNode` for test purposes only needs `id`, `parentId`, `name`, and `isFolder`.

```swift
// Stale node is pruned after BFS
@Test func testPruneRemovesStaleNode() async throws {
    // Plant a node with generation 0 (simulating a prior sync result)
    let staleNode = FileNode(id: "stale1", parentId: "root", name: "old.txt", ...)
    await db.upsertFromServer(staleNode)   // generation = 0 (default)

    // BFS runs and only sees a different node
    let gen = await db.incrementBfsGeneration()
    let freshNode = FileNode(id: "fresh1", parentId: "root", name: "new.txt", ...)
    await db.upsertFromServer(freshNode, bfsGeneration: gen)
    await db.pruneStaleNodes(generation: gen)

    #expect(db.entry(for: "stale1") == nil)
    #expect(db.entry(for: "fresh1") != nil)
}

// Node returned by BFS is not pruned
@Test func testBfsNodeSurvivesPrune() async throws {
    let gen = await db.incrementBfsGeneration()
    let node = FileNode(id: "n1", parentId: "root", name: "keep.txt", ...)
    await db.upsertFromServer(node, bfsGeneration: gen)
    await db.pruneStaleNodes(generation: gen)
    #expect(db.entry(for: "n1") != nil)
}

// A node not seen by the next BFS is pruned, even if incremental sync updated it
@Test func testNodeNotInBfsIsPruned() async throws {
    // First BFS stamps node with gen 1
    let gen1 = await db.incrementBfsGeneration()
    await db.upsertFromServer(FileNode(id: "n1", ...), bfsGeneration: gen1)

    // Incremental sync updates the node — no generation stamp
    await db.upsertFromServer(FileNode(id: "n1", ...), bfsGeneration: nil)

    // Second BFS does not include n1 (it was deleted on the server)
    let gen2 = await db.incrementBfsGeneration()
    await db.pruneStaleNodes(generation: gen2)
    #expect(db.entry(for: "n1") == nil)
}
```

The last test is the critical correctness case: a node deleted on the server will not appear in the next BFS, so it correctly gets pruned even if incremental sync had touched it in the interim.

---

## Implementation order

1. Fix 1 (legacy blob) — smallest change, good warm-up
2. Fix 2 (filename robustness) — self-contained, touches two files
3. Fix 3 (echo suppression) — schema + DB + enumerator, most involved

Each fix is one commit. Tests for each fix are committed with the fix.
