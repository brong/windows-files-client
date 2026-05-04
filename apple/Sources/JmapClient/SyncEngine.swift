import Foundation
#if canImport(os)
import os
#endif

/// Orchestrates JMAP FileNode sync for one account.
///
/// Encapsulates the protocol layer (home/trash discovery, incremental changes)
/// and keeps the database state token in sync. Lives in the JmapClient library
/// so it can be unit-tested without a FileProvider extension running.
public actor SyncEngine {
    private let client: JmapClient
    private let database: NodeDatabase
    public let accountId: String

    #if canImport(os)
    private let logger = Logger(subsystem: "com.fastmail.files", category: "SyncEngine")
    #endif

    public init(client: JmapClient, database: NodeDatabase, accountId: String) {
        self.client = client
        self.database = database
        self.accountId = accountId
    }

    // MARK: - Special Node IDs

    public var homeNodeId: String? {
        get async { await database.homeNodeId }
    }

    public var trashNodeId: String? {
        get async { await database.trashNodeId }
    }

    /// Ensure home and trash node IDs are known, fetching from server if needed.
    /// Returns immediately if the home ID is already in the database.
    public func resolveSpecialNodes() async throws -> (homeId: String, trashId: String?) {
        if let homeId = await database.homeNodeId {
            #if canImport(os)
            logger.info("[\(self.accountId, privacy: .public)] resolveSpecialNodes: cached homeId=\(homeId, privacy: .public)")
            #endif
            return (homeId, await database.trashNodeId)
        }
        #if canImport(os)
        logger.info("[\(self.accountId, privacy: .public)] resolveSpecialNodes: fetching from server")
        #endif
        let home = try await client.findHomeNode(accountId: accountId)
        await database.setHomeNodeId(home.id)
        await database.upsertFromServer(home)
        let trash = try await client.findTrashNode(accountId: accountId)
        await database.setTrashNodeId(trash?.id)
        if let trash { await database.upsertFromServer(trash) }
        try? await database.save()
        #if canImport(os)
        logger.info("[\(self.accountId, privacy: .public)] resolveSpecialNodes: homeId=\(home.id, privacy: .public) trashId=\(trash?.id ?? "none", privacy: .public)")
        #endif
        return (home.id, trash?.id)
    }

    // MARK: - Incremental Changes

    /// Fetch incremental changes since the last known state token.
    ///
    /// Applies all node writes to the database **without** advancing the state token.
    /// The caller is responsible for writing `newState` to the database *after*
    /// successfully signaling the changes to the FileProvider system.  This ordering
    /// guarantees that any kill between DB write and system signal leaves the old
    /// token intact, so the next restart re-applies the same changes idempotently.
    ///
    /// Returns `([], [], "")` immediately when no state token is set — this happens
    /// before the first full enumeration completes and is not an error.
    ///
    /// Throws `JmapError.cannotCalculateChanges` when the server cannot diff
    /// from the stored token (too old).  The caller should clear the state token
    /// and trigger a fresh enumeration.
    public func fetchChanges() async throws -> (updated: [FileNode], deleted: [String], newState: String) {
        guard let stateToken = await database.stateToken, !stateToken.isEmpty else {
            #if canImport(os)
            logger.info("[\(self.accountId, privacy: .public)] fetchChanges: no state token yet — skipping")
            #endif
            return ([], [], "")
        }
        #if canImport(os)
        logger.info("[\(self.accountId, privacy: .public)] fetchChanges: sinceState=\(stateToken, privacy: .public)")
        #endif
        let (changes, created, updated) = try await client.getChanges(
            accountId: accountId,
            sinceState: stateToken
        )
        let allUpdated = created + updated
        for node in allUpdated {
            await database.upsertFromServer(node)
        }
        if !changes.destroyed.isEmpty {
            await database.remove(nodeIds: changes.destroyed)
        }
        // State token is NOT written here. The caller writes it after finishEnumeratingChanges
        // so that a kill at any point leaves the DB in a re-applicable state.
        #if canImport(os)
        logger.info("[\(self.accountId, privacy: .public)] fetchChanges: done — \(created.count) created, \(updated.count) updated, \(changes.destroyed.count) deleted, newState=\(changes.newState, privacy: .public)")
        #endif
        return (allUpdated, changes.destroyed, changes.newState)
    }
}
