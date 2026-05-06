import Foundation

/// Persists special-role node IDs (home, trash, etc.) in UserDefaults.
///
/// Stored separately from NodeDatabase so it survives "Clean" operations.
/// Updated at account creation and whenever FileNode/changes returns a role node.
public enum RoleCache {
    private static func key(_ accountId: String) -> String { "rolecache-\(accountId)" }

    /// Read the full role map for an account.
    public static func load(accountId: String, defaults: UserDefaults?) -> [String: String] {
        guard let data = defaults?.data(forKey: key(accountId)),
              let roles = try? JSONDecoder().decode([String: String].self, from: data)
        else { return [:] }
        return roles
    }

    /// Persist the full role map for an account.
    public static func save(_ roles: [String: String], accountId: String, defaults: UserDefaults?) {
        guard let data = try? JSONEncoder().encode(roles) else { return }
        defaults?.set(data, forKey: key(accountId))
    }

    /// Return the nodeId for a specific role, or nil if not cached.
    public static func nodeId(for role: String, accountId: String, defaults: UserDefaults?) -> String? {
        load(accountId: accountId, defaults: defaults)[role]
    }

    /// Update a single role entry and persist.
    public static func setNodeId(_ nodeId: String, for role: String, accountId: String, defaults: UserDefaults?) {
        var roles = load(accountId: accountId, defaults: defaults)
        roles[role] = nodeId
        save(roles, accountId: accountId, defaults: defaults)
    }

    /// Update role entries from a batch of FileNodes (call during changes enumeration).
    public static func update(from nodes: [FileNode], accountId: String, defaults: UserDefaults?) {
        var roles = load(accountId: accountId, defaults: defaults)
        var changed = false
        for node in nodes {
            if let role = node.role {
                roles[role] = node.id
                changed = true
            }
        }
        if changed {
            save(roles, accountId: accountId, defaults: defaults)
        }
    }

    /// Remove all cached roles for an account (call when removing the account entirely).
    public static func clear(accountId: String, defaults: UserDefaults?) {
        defaults?.removeObject(forKey: key(accountId))
    }
}
