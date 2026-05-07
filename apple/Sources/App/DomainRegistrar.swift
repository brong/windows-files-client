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
        let domainId = DomainIdentity(loginId: loginId, accountId: accountId).domainId
        // Store config so the extension can bootstrap from UserDefaults.
        // loginId is encoded in the domain identifier, so no separate loginForAccount key needed.
        defaults?.set(sessionURL, forKey: "sessionURL-\(domainId)")
        defaults?.set(authType.rawValue, forKey: "authType-\(domainId)")

        let domainName = displayName.isEmpty ? accountId : "\(displayName) Files"
        do {
            let domain = NSFileProviderDomain(
                identifier: NSFileProviderDomainIdentifier(rawValue: domainId),
                displayName: domainName
            )
            try await NSFileProviderManager.add(domain)

            if let containerURL = FileManager.default.containerURL(
                forSecurityApplicationGroupIdentifier: appGroupId) {
                // Clear the state token so enumerateWorkingSet is forced on next wake.
                let db = NodeDatabase(containerURL: containerURL, accountId: domainId)
                await db.setStateToken("")
                await db.setEnumerationFailureCount(0)

                // Pre-write a syncing status so the UI can't show "Up to date" before
                // enumerateWorkingSet has actually run.
                let writer = ExtensionStatusWriter(containerURL: containerURL, domainId: domainId)
                writer.setSyncing()
            }

            // Signal the system to drive enumerateItems on the working set.
            NSFileProviderManager(for: domain)?.signalEnumerator(for: .workingSet) { _ in }
        } catch {
            // Clean up mapping on failure
            defaults?.removeObject(forKey: "sessionURL-\(domainId)")
            defaults?.removeObject(forKey: "authType-\(domainId)")
            throw error
        }
    }

    // MARK: - Remove

    func remove(domainId: String) async {
        do {
            let domain = NSFileProviderDomain(
                identifier: NSFileProviderDomainIdentifier(rawValue: domainId), displayName: "")
            try await NSFileProviderManager.remove(domain)
        } catch {
            print("FileProvider domain removal failed: \(error.localizedDescription)")
        }
        purgeFiles(domainId: domainId)
    }

    // MARK: - Purge Files

    /// Delete all local files for a domain without touching the FileProvider domain.
    /// Used for accounts that were never synced (no registered domain).
    func purgeFiles(domainId: String) {
        guard let containerURL = FileManager.default.containerURL(
            forSecurityApplicationGroupIdentifier: appGroupId) else { return }
        let nodeCacheDir = containerURL
            .appendingPathComponent("NodeCache", isDirectory: true)
            .appendingPathComponent(domainId, isDirectory: true)
        try? FileManager.default.removeItem(at: nodeCacheDir)
        let blobDir = containerURL.appendingPathComponent("blobs-\(domainId)")
        try? FileManager.default.removeItem(at: blobDir)
        let statusFile = containerURL.appendingPathComponent("status-\(domainId).json")
        try? FileManager.default.removeItem(at: statusFile)
        defaults?.removeObject(forKey: "sessionURL-\(domainId)")
        defaults?.removeObject(forKey: "authType-\(domainId)")
        RoleCache.clear(accountId: domainId, defaults: defaults)
    }

    // MARK: - Evict / Signal

    func evict(domainId: String) {
        let domain = NSFileProviderDomain(
            identifier: NSFileProviderDomainIdentifier(rawValue: domainId), displayName: "")
        NSFileProviderManager(for: domain)?.evictItem(identifier: .rootContainer) { _ in }
    }

    func signal(domainId: String) {
        let domain = NSFileProviderDomain(
            identifier: NSFileProviderDomainIdentifier(rawValue: domainId), displayName: "")
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
