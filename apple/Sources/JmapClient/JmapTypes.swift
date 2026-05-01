import Foundation

// MARK: - Capability URIs

public enum JmapCapability {
    public static let core = "urn:ietf:params:jmap:core"
    // Legacy `urn:ietf:params:jmap:blob` capability is deprecated and must
    // not be sent in `using` arrays. Its URI is referenced only to read
    // advertised limits (maxDataSources, maxSizeBlobSet) from session metadata.
    public static let blob = "urn:ietf:params:jmap:blob"
    public static let blob2 = "https://www.fastmail.com/dev/blob2"
    public static let quota = "urn:ietf:params:jmap:quota"

    // FileNode capability — check both dev and IETF URIs
    public static let fileNodeDev = "https://www.fastmail.com/dev/filenode"
    public static let fileNodeIETF = "urn:ietf:params:jmap:filenode"

    public static let allFileNodeURIs = [fileNodeDev, fileNodeIETF]
}

// MARK: - Session

/// JMAP session response (RFC 8620 §2).
public struct JmapSession: Codable, Sendable {
    public let apiUrl: String
    public let downloadUrl: String
    public let uploadUrl: String
    public let eventSourceUrl: String
    public let capabilities: [String: AnyCodable]
    public let accounts: [String: JmapAccount]
    public let primaryAccounts: [String: String]

    /// Find the primary accountId for the FileNode capability.
    public func fileNodeAccountId() -> String? {
        for uri in JmapCapability.allFileNodeURIs {
            if let accountId = primaryAccounts[uri] {
                return accountId
            }
        }
        return nil
    }

    /// Resolve the download URL template for a specific blob.
    public func downloadURL(accountId: String, blobId: String, name: String, type: String?) -> URL? {
        var urlString = downloadUrl
            .replacingOccurrences(of: "{accountId}", with: accountId)
            .replacingOccurrences(of: "{blobId}", with: blobId)
            .replacingOccurrences(of: "{name}", with: name.addingPercentEncoding(withAllowedCharacters: .urlPathAllowed) ?? name)
        if let type = type {
            urlString = urlString.replacingOccurrences(of: "{type}", with: type)
        }
        return URL(string: urlString)
    }

    /// Resolve the upload URL for an account.
    public func uploadURL(accountId: String) -> URL? {
        let urlString = uploadUrl
            .replacingOccurrences(of: "{accountId}", with: accountId)
        return URL(string: urlString)
    }

    /// Check whether the server advertises a capability.
    public func hasCapability(_ uri: String) -> Bool {
        capabilities[uri] != nil
    }

    /// Check whether an account has a capability.
    public func accountHasCapability(_ accountId: String, _ uri: String) -> Bool {
        guard let account = accounts[accountId] else { return false }
        return account.accountCapabilities[uri] != nil
    }

    /// Check if any FileNode capability is present.
    public var hasFileNode: Bool {
        JmapCapability.allFileNodeURIs.contains(where: hasCapability)
    }

    /// Check if blob2 capability is present for an account.
    /// Required for Blob/set (chunked combine), Blob/convert (thumbnails), and
    /// the `chunks` property on Blob/get.
    public func hasBlob2(accountId: String) -> Bool {
        accountHasCapability(accountId, JmapCapability.blob2)
    }

    /// Server-suggested chunk size from the blob2 capability, if advertised.
    public func blob2ChunkSize(accountId: String) -> Int? {
        accounts[accountId]?.accountCapabilities[JmapCapability.blob2]?.dictValue?["chunkSize"]?.intValue
    }

    /// Maximum total upload size from the legacy blob capability metadata.
    /// Files exceeding this limit cannot be uploaded via Blob/set.
    public func maxSizeBlobSet(accountId: String) -> Int? {
        accounts[accountId]?.accountCapabilities[JmapCapability.blob]?.dictValue?["maxSizeBlobSet"]?.intValue
    }

    /// Get the FileNode capability URI that the server actually uses.
    public var fileNodeCapabilityURI: String? {
        JmapCapability.allFileNodeURIs.first(where: hasCapability)
    }

    /// Find all accounts that have the FileNode capability.
    /// Returns (accountId, displayName, isPrimary) for each.
    public func fileNodeAccounts() -> [(accountId: String, name: String, isPrimary: Bool)] {
        let primaryId = fileNodeAccountId()
        var result: [(String, String, Bool)] = []
        for (accountId, account) in accounts {
            let hasFileNode = JmapCapability.allFileNodeURIs.contains { uri in
                account.accountCapabilities[uri] != nil
            }
            if hasFileNode {
                result.append((accountId, account.name, accountId == primaryId))
            }
        }
        // Sort: primary first, then alphabetical
        result.sort { a, b in
            if a.2 != b.2 { return a.2 }
            return a.1 < b.1
        }
        return result
    }

    /// Get the webWriteUrlTemplate for direct HTTP writes, if available.
    public func webWriteUrlTemplate(accountId: String) -> String? {
        guard let account = accounts[accountId] else { return nil }
        for uri in JmapCapability.allFileNodeURIs {
            if let cap = account.accountCapabilities[uri],
               let dict = cap.dictValue,
               let template = dict["webWriteUrlTemplate"]?.stringValue {
                return template
            }
        }
        return nil
    }

    /// Check if server supports direct HTTP write for an account.
    public func hasDirectWrite(accountId: String) -> Bool {
        webWriteUrlTemplate(accountId: accountId) != nil
    }
}

public struct JmapAccount: Codable, Sendable {
    public let name: String
    public let isPersonal: Bool
    public let accountCapabilities: [String: AnyCodable]
}

// MARK: - FileNode

/// A FileNode as returned by FileNode/get.
public struct FileNode: Codable, Sendable {
    public let id: String
    public let parentId: String?
    public let blobId: String?
    public let name: String?     // Optional: set responses omit fields that match the request
    public let type: String?
    public let size: Int?
    public let created: Date?
    public let modified: Date?
    public let accessed: Date?
    public let role: String?
    public let executable: Bool?
    public let isSubscribed: Bool?
    public let myRights: FileNodeRights?
    public let shareWith: [String: FileNodeRights]?

    public init(id: String, parentId: String?, blobId: String?,
                name: String?, type: String?, size: Int?,
                created: Date?, modified: Date?, accessed: Date?,
                role: String?, executable: Bool?, isSubscribed: Bool?,
                myRights: FileNodeRights?, shareWith: [String: FileNodeRights]?) {
        self.id = id
        self.parentId = parentId
        self.blobId = blobId
        self.name = name
        self.type = type
        self.size = size
        self.created = created
        self.modified = modified
        self.accessed = accessed
        self.role = role
        self.executable = executable
        self.isSubscribed = isSubscribed
        self.myRights = myRights
        self.shareWith = shareWith
    }

    public var isFolder: Bool { blobId == nil }

    /// Check for well-known roles.
    public var isHome: Bool { role == "home" }
    public var isTrash: Bool { role == "trash" }
    public var isRoot: Bool { role == "root" }
}

public struct FileNodeRights: Codable, Sendable {
    public let mayRead: Bool
    public let mayWrite: Bool
    public let mayShare: Bool

    public init(mayRead: Bool = true, mayWrite: Bool = true, mayShare: Bool = false) {
        self.mayRead = mayRead
        self.mayWrite = mayWrite
        self.mayShare = mayShare
    }
}

// MARK: - JMAP Method Responses

/// Generic JMAP method response envelope.
public struct JmapResponse: Codable, Sendable {
    public let methodResponses: [[AnyCodable]]
    public let sessionState: String?
}

/// FileNode/get response.
public struct FileNodeGetResponse: Codable, Sendable {
    public let accountId: String
    public let state: String
    public let list: [FileNode]
    public let notFound: [String]?
}

/// FileNode/query response.
public struct FileNodeQueryResponse: Codable, Sendable {
    public let accountId: String
    public let queryState: String
    public let ids: [String]
    public let total: Int?
    public let position: Int?
}

/// FileNode/changes response.
public struct FileNodeChangesResponse: Codable, Sendable {
    public let accountId: String
    public let oldState: String
    public let newState: String
    public let created: [String]
    public let updated: [String]
    public let destroyed: [String]
    public let hasMoreChanges: Bool?
}

/// FileNode/set response.
public struct FileNodeSetResponse: Codable, Sendable {
    public let accountId: String
    public let oldState: String?
    public let newState: String?
    public let created: [String: FileNode]?
    public let updated: [String: AnyCodable?]?
    public let destroyed: [String]?
    public let notCreated: [String: JmapSetError]?
    public let notUpdated: [String: JmapSetError]?
    public let notDestroyed: [String: JmapSetError]?
}

public struct JmapSetError: Codable, Sendable {
    public let type: String
    public let description: String?
}

/// Blob upload response.
public struct BlobUploadResponse: Codable, Sendable {
    public let blobId: String
    public let size: Int
    public let type: String
}

// MARK: - AnyCodable

/// Type-erased Codable wrapper for heterogeneous JMAP structures.
public struct AnyCodable: Codable, Sendable, ExpressibleByStringLiteral,
    ExpressibleByIntegerLiteral, ExpressibleByBooleanLiteral,
    ExpressibleByFloatLiteral, ExpressibleByNilLiteral,
    ExpressibleByArrayLiteral, ExpressibleByDictionaryLiteral
{
    public let value: any Sendable

    public init(_ value: some Sendable) {
        self.value = value
    }

    // MARK: Literal conformances

    public init(stringLiteral value: String) { self.value = value }
    public init(integerLiteral value: Int) { self.value = value }
    public init(booleanLiteral value: Bool) { self.value = value }
    public init(floatLiteral value: Double) { self.value = value }
    public init(nilLiteral: ()) { self.value = NSNull() }
    public init(arrayLiteral elements: AnyCodable...) { self.value = elements }
    public init(dictionaryLiteral elements: (String, AnyCodable)...) {
        self.value = Dictionary(uniqueKeysWithValues: elements)
    }

    // MARK: Typed accessors

    public var stringValue: String? { value as? String }
    public var intValue: Int? { value as? Int }
    public var boolValue: Bool? { value as? Bool }
    public var doubleValue: Double? { value as? Double }
    public var arrayValue: [AnyCodable]? { value as? [AnyCodable] }
    public var dictValue: [String: AnyCodable]? { value as? [String: AnyCodable] }

    // MARK: Codable

    public init(from decoder: Decoder) throws {
        let container = try decoder.singleValueContainer()
        if container.decodeNil() {
            value = NSNull()
        } else if let bool = try? container.decode(Bool.self) {
            value = bool
        } else if let int = try? container.decode(Int.self) {
            value = int
        } else if let double = try? container.decode(Double.self) {
            value = double
        } else if let string = try? container.decode(String.self) {
            value = string
        } else if let array = try? container.decode([AnyCodable].self) {
            value = array
        } else if let dict = try? container.decode([String: AnyCodable].self) {
            value = dict
        } else {
            throw DecodingError.dataCorruptedError(
                in: container, debugDescription: "Unsupported AnyCodable value")
        }
    }

    public func encode(to encoder: Encoder) throws {
        var container = encoder.singleValueContainer()
        switch value {
        case let v as Bool: try container.encode(v)
        case let v as Int: try container.encode(v)
        case let v as Double: try container.encode(v)
        case let v as String: try container.encode(v)
        case let v as [AnyCodable]: try container.encode(v)
        case let v as [String: AnyCodable]: try container.encode(v)
        case is NSNull: try container.encodeNil()
        default:
            try container.encodeNil()
        }
    }
}
