import Foundation
#if canImport(CryptoKit)
import CryptoKit
#endif

/// Compute SHA1 digest of data, returned as base64.
public func sha1Digest(_ data: Data) -> String {
    let digest = Insecure.SHA1.hash(data: data)
    return Data(digest).base64EncodedString()
}

/// Incremental SHA1 context for hashing large files chunk by chunk.
public struct SHA1Context {
    private var hasher = Insecure.SHA1()

    public init() {}

    /// Feed data into the hash.
    public mutating func update(_ data: Data) {
        hasher.update(data: data)
    }

    /// Finalize and return the base64-encoded digest.
    public mutating func finalize() -> String {
        let digest = hasher.finalize()
        return Data(digest).base64EncodedString()
    }
}
