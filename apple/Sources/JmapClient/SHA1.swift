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

/// Compute SHA1 digest of a file by streaming it in 64 KB chunks.
/// Avoids loading the entire file into memory.
public func sha1OfFile(_ url: URL) throws -> String {
    let fileHandle = try FileHandle(forReadingFrom: url)
    defer { try? fileHandle.close() }
    var ctx = SHA1Context()
    while true {
        let chunk = fileHandle.readData(ofLength: 65536)
        if chunk.isEmpty { break }
        ctx.update(chunk)
    }
    return ctx.finalize()
}
