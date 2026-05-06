import Foundation
import FileProvider
import JmapClient

/// Manages NSFileProviderManager and file-cleanup operations.
final class DomainRegistrar: @unchecked Sendable {
    private let appGroupId: String
    private let defaults: UserDefaults?

    init(appGroupId: String, defaults: UserDefaults?) {
        self.appGroupId = appGroupId
        self.defaults = defaults
    }

    // MARK: - Register

    func register(accountId: String, displayName: String,
                  loginId: String, sessionURL: String,
                  authType: AuthType) async throws {
        // Store mapping so extension can find the login credential
        defaults?.set(loginId, forKey: "loginForAccount-\(accountId)")
        defaults?.set(sessionURL, forKey: "sessionURL-\(accountId)")
        defaults?.set(authType.rawValue, forKey: "authType-\(accountId)")

        let domainName = displayName.isEmpty ? accountId : "\(displayName) Files"
        do {
            let domain = NSFileProviderDomain(
                identifier: NSFileProviderDomainIdentifier(rawValue: accountId),
                displayName: domainName
            )
            try await NSFileProviderManager.add(domain)

            if let containerURL = FileManager.default.containerURL(
                forSecurityApplicationGroupIdentifier: appGroupId) {
                // Clear the state token so enumerateWorkingSet is forced on next wake.
                let db = NodeDatabase(containerURL: containerURL, accountId: accountId)
                await db.setStateToken("")
                await db.setEnumerationFailureCount(0)

                // Pre-write a syncing status so the UI can't show "Up to date" before
                // enumerateWorkingSet has actually run.
                let writer = ExtensionStatusWriter(containerURL: containerURL, accountId: accountId)
                writer.setSyncing()
            }

            // Signal the system to drive enumerateItems on the working set.
            NSFileProviderManager(for: domain)?.signalEnumerator(for: .workingSet) { _ in }
        } catch {
            // Clean up mapping on failure
            defaults?.removeObject(forKey: "loginForAccount-\(accountId)")
            defaults?.removeObject(forKey: "sessionURL-\(accountId)")
            defaults?.removeObject(forKey: "authType-\(accountId)")
            throw error
        }
    }

    // MARK: - Remove

    func remove(accountId: String) async {
        do {
            let domain = NSFileProviderDomain(
                identifier: NSFileProviderDomainIdentifier(rawValue: accountId), displayName: "")
            try await NSFileProviderManager.remove(domain)
        } catch {
            print("FileProvider domain removal failed: \(error.localizedDescription)")
        }

        if let containerURL = FileManager.default.containerURL(
            forSecurityApplicationGroupIdentifier: appGroupId) {
            let nodeCacheDir = containerURL
                .appendingPathComponent("NodeCache", isDirectory: true)
                .appendingPathComponent(accountId, isDirectory: true)
            try? FileManager.default.removeItem(at: nodeCacheDir)
            let blobDir = containerURL.appendingPathComponent("blobs-\(accountId)")
            try? FileManager.default.removeItem(at: blobDir)
            let sessionCache = containerURL.appendingPathComponent("session-\(accountId).json")
            try? FileManager.default.removeItem(at: sessionCache)
            let statusFile = containerURL.appendingPathComponent("status-\(accountId).json")
            try? FileManager.default.removeItem(at: statusFile)
        }

        defaults?.removeObject(forKey: "loginForAccount-\(accountId)")
        defaults?.removeObject(forKey: "sessionURL-\(accountId)")
        defaults?.removeObject(forKey: "authType-\(accountId)")
        RoleCache.clear(accountId: accountId, defaults: defaults)
    }

    // MARK: - Purge Files

    /// Delete all local files for an account without touching the FileProvider domain.
    /// Used for accounts that were never synced (no registered domain).
    func purgeFiles(accountId: String) {
        guard let containerURL = FileManager.default.containerURL(
            forSecurityApplicationGroupIdentifier: appGroupId) else { return }
        let nodeCacheDir = containerURL
            .appendingPathComponent("NodeCache", isDirectory: true)
            .appendingPathComponent(accountId, isDirectory: true)
        try? FileManager.default.removeItem(at: nodeCacheDir)
        let blobDir = containerURL.appendingPathComponent("blobs-\(accountId)")
        try? FileManager.default.removeItem(at: blobDir)
        let sessionCache = containerURL.appendingPathComponent("session-\(accountId).json")
        try? FileManager.default.removeItem(at: sessionCache)
        let statusFile = containerURL.appendingPathComponent("status-\(accountId).json")
        try? FileManager.default.removeItem(at: statusFile)
        defaults?.removeObject(forKey: "loginForAccount-\(accountId)")
        defaults?.removeObject(forKey: "sessionURL-\(accountId)")
        defaults?.removeObject(forKey: "authType-\(accountId)")
        RoleCache.clear(accountId: accountId, defaults: defaults)
    }

    // MARK: - Evict / Signal

    func evict(accountId: String) {
        let domain = NSFileProviderDomain(
            identifier: NSFileProviderDomainIdentifier(rawValue: accountId), displayName: "")
        NSFileProviderManager(for: domain)?.evictItem(identifier: .rootContainer) { _ in }
    }

    func signal(accountId: String) {
        let domain = NSFileProviderDomain(
            identifier: NSFileProviderDomainIdentifier(rawValue: accountId), displayName: "")
        NSFileProviderManager(for: domain)?.signalEnumerator(for: .workingSet) { _ in }
    }

    // MARK: - Domain Listing

    func listDomains() async -> [NSFileProviderDomain] {
        await withUnsafeContinuation { continuation in
            NSFileProviderManager.getDomainsWithCompletionHandler { domains, _ in
                nonisolated(unsafe) let result = domains
                continuation.resume(returning: result)
            }
        }
    }

    func cleanOrphaned(knownIds: Set<String>) async {
        let domains = await listDomains()
        for domain in domains {
            if !knownIds.contains(domain.identifier.rawValue) {
                try? await NSFileProviderManager.remove(domain)
            }
        }
    }
}
