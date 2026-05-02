import Foundation

/// Orchestrates JMAP FileNode sync for one account.
///
/// Encapsulates the protocol layer (home/trash discovery, incremental changes)
/// and keeps the database state token in sync. Lives in the JmapClient library
/// so it can be unit-tested without a FileProvider extension running.
public actor SyncEngine {
    private let client: JmapClient
    private let database: NodeDatabase
    public let accountId: String

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
            return (homeId, await database.trashNodeId)
        }
        let home = try await client.findHomeNode(accountId: accountId)
        await database.setHomeNodeId(home.id)
        await database.upsertFromServer(home)
        let trash = try await client.findTrashNode(accountId: accountId)
        await database.setTrashNodeId(trash?.id)
        if let trash { await database.upsertFromServer(trash) }
        try? await database.save()
        return (home.id, trash?.id)
    }

    // MARK: - Incremental Changes

    /// Fetch incremental changes since the last known state token.
    ///
    /// Returns `([], [])` immediately when no state token is set — this happens
    /// before the first full enumeration completes and is not an error.
    ///
    /// Throws `JmapError.cannotCalculateChanges` when the server cannot diff
    /// from the stored token (it has been too long since the last sync). The
    /// caller should clear the state token and trigger a fresh enumeration.
    public func fetchChanges() async throws -> (updated: [FileNode], deleted: [String]) {
        guard let stateToken = await database.stateToken, !stateToken.isEmpty else {
            return ([], [])
        }
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
        await database.setStateToken(changes.newState)
        try? await database.save()
        return (allUpdated, changes.destroyed)
    }
}
