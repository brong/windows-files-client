import Foundation

/// Errors from the JMAP client layer.
public enum JmapError: Error, Sendable {
    case invalidResponse
    case unauthorized
    case httpError(Int, String?)
    case missingCapability(String)
    case serverError(String, String?)  // type, description
    case notFound(String)              // itemId
    case cannotCalculateChanges
    case forbidden(String?)
    case rateLimited
    case payloadTooLarge
    case keychainError(OSStatus)
    case noAccountId
    case uploadFailed(String)
    case blobTooLarge(Int, Int)        // fileSize, maxSize

    public var isRetriable: Bool {
        switch self {
        case .httpError(let code, _):
            return code >= 500 || code == 429
        case .rateLimited, .invalidResponse:
            return true
        case .unauthorized, .forbidden, .payloadTooLarge, .blobTooLarge,
             .missingCapability, .noAccountId, .keychainError:
            return false
        case .serverError(let type, _):
            return type != "forbidden"
        case .notFound, .cannotCalculateChanges, .uploadFailed:
            return false
        }
    }
}

extension JmapError: LocalizedError {
    public var errorDescription: String? {
        switch self {
        case .invalidResponse:
            return "Invalid server response"
        case .unauthorized:
            return "Authentication required"
        case .httpError(let code, let body):
            return "HTTP \(code)\(body.map { ": \($0)" } ?? "")"
        case .missingCapability(let cap):
            return "Server does not support \(cap)"
        case .serverError(let type, let desc):
            return "Server error: \(type)\(desc.map { " — \($0)" } ?? "")"
        case .notFound(let id):
            return "Item not found: \(id)"
        case .cannotCalculateChanges:
            return "State token expired, full sync required"
        case .forbidden(let desc):
            return "Forbidden\(desc.map { ": \($0)" } ?? "")"
        case .rateLimited:
            return "Rate limited by server"
        case .payloadTooLarge:
            return "File too large for server"
        case .keychainError(let status):
            return "Keychain error: \(status)"
        case .noAccountId:
            return "No FileNode account found"
        case .uploadFailed(let reason):
            return "Upload failed: \(reason)"
        case .blobTooLarge(let size, let max):
            return "File size \(size) exceeds server maximum \(max)"
        }
    }
}
