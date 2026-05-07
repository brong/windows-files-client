import Foundation

/// Single source of truth for account identity strings.
///
/// Two levels of identity:
///   loginId  = "\(primaryEmail)@\(serverHost)"   e.g. "user@example.com@api.server.com"
///   domainId = "\(loginId)~\(accountId)"         e.g. "user@example.com@api.server.com~vabc1234"
///
/// The tilde separator is safe in filenames (HFS+/APFS), NSFileProviderDomain
/// identifiers, UserDefaults keys, URLSession identifiers, and does not
/// appear in email addresses or JMAP accountIds.
///
/// All construction and parsing goes through this type. No inline string
/// interpolation for identity strings anywhere else in the codebase.
public struct DomainIdentity: Sendable {
    public let loginId: String
    public let accountId: String

    public init(loginId: String, accountId: String) {
        self.loginId = loginId
        self.accountId = accountId
    }

    /// The globally-unique domain identifier used as NSFileProviderDomain
    /// identifier, filesystem path prefix, and UserDefaults key prefix.
    public var domainId: String { "\(loginId)~\(accountId)" }

    /// Parse a domainId back to its parts. Returns nil if the string
    /// doesn't contain `~` (old format — bare accountId or colon-separated).
    public static func parse(_ domainId: String) -> DomainIdentity? {
        guard let idx = domainId.firstIndex(of: "~") else { return nil }
        let loginId = String(domainId[..<idx])
        let accountId = String(domainId[domainId.index(after: idx)...])
        guard !loginId.isEmpty, !accountId.isEmpty else { return nil }
        return DomainIdentity(loginId: loginId, accountId: accountId)
    }

    /// Compute the loginId for a server account.
    /// primaryEmail is the account's email address; sessionURL is the JMAP session endpoint.
    public static func makeLoginId(primaryEmail: String, sessionURL: URL) -> String {
        let host = sessionURL.host ?? "unknown"
        return "\(primaryEmail)@\(host)"
    }
}
