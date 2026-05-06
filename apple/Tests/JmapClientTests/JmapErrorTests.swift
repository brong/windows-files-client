import Foundation
import FileProvider
import Testing
@testable import JmapClient

// MARK: - isRetriable

@Test func testRetriableErrors() {
    #expect(JmapError.rateLimited.isRetriable)
    #expect(JmapError.invalidResponse.isRetriable)
    #expect(JmapError.httpError(503, nil).isRetriable)
    #expect(JmapError.httpError(500, "Internal error").isRetriable)
    #expect(JmapError.httpError(429, nil).isRetriable)
    #expect(JmapError.serverError("internalError", nil).isRetriable)
}

@Test func testNonRetriableErrors() {
    #expect(!JmapError.unauthorized.isRetriable)
    #expect(!JmapError.forbidden(nil).isRetriable)
    #expect(!JmapError.payloadTooLarge.isRetriable)
    #expect(!JmapError.blobTooLarge(100, 50).isRetriable)
    #expect(!JmapError.missingCapability("urn:ietf:params:jmap:quota").isRetriable)
    #expect(!JmapError.noAccountId.isRetriable)
    #expect(!JmapError.keychainError(-25300).isRetriable)
    #expect(!JmapError.notFound("M1").isRetriable)
    #expect(!JmapError.alreadyExists.isRetriable)
    #expect(!JmapError.cannotCalculateChanges.isRetriable)
    #expect(!JmapError.uploadFailed("disk full").isRetriable)
    #expect(!JmapError.serverError("forbidden", nil).isRetriable)
}

@Test func testClientErrorCodesAreNotRetriable() {
    #expect(!JmapError.httpError(400, nil).isRetriable)
    #expect(!JmapError.httpError(404, nil).isRetriable)
}

// MARK: - fileProviderError

@Test func testUnauthorizedMapsToNotAuthenticated() {
    let err = JmapError.unauthorized.fileProviderError
    #expect(err.domain == NSFileProviderErrorDomain)
    #expect(err.code == NSFileProviderError.notAuthenticated.rawValue)
}

@Test func testForbiddenMapsToNotAuthenticated() {
    let err = JmapError.forbidden("not allowed").fileProviderError
    #expect(err.code == NSFileProviderError.notAuthenticated.rawValue)
}

@Test func testCannotCalculateChangesMapsToSyncAnchorExpired() {
    let err = JmapError.cannotCalculateChanges.fileProviderError
    #expect(err.code == NSFileProviderError.syncAnchorExpired.rawValue)
}

@Test func testNotFoundMapsToNoSuchItem() {
    let err = JmapError.notFound("M1").fileProviderError
    #expect(err.code == NSFileProviderError.noSuchItem.rawValue)
}

@Test func testServerErrorMapToServerUnreachable() {
    let err5xx = JmapError.httpError(503, nil).fileProviderError
    #expect(err5xx.code == NSFileProviderError.serverUnreachable.rawValue)
}

@Test func testRateLimitedMapsToServerUnreachable() {
    let err = JmapError.rateLimited.fileProviderError
    #expect(err.code == NSFileProviderError.serverUnreachable.rawValue)
}

@Test func testPayloadTooLargeMapsToInsufficientQuota() {
    let err = JmapError.payloadTooLarge.fileProviderError
    #expect(err.code == NSFileProviderError.insufficientQuota.rawValue)
}

@Test func testBlobTooLargeMapsToInsufficientQuota() {
    let err = JmapError.blobTooLarge(100, 50).fileProviderError
    #expect(err.code == NSFileProviderError.insufficientQuota.rawValue)
}

@Test func testMissingCapabilityMapsToNotAuthenticated() {
    let err = JmapError.missingCapability("urn:foo").fileProviderError
    #expect(err.code == NSFileProviderError.notAuthenticated.rawValue)
}

@Test func testAlreadyExistsMapsToFilenameCollision() {
    let err = JmapError.alreadyExists.fileProviderError
    #expect(err.domain == NSFileProviderErrorDomain)
    #expect(err.code == NSFileProviderError.filenameCollision.rawValue)
}

@Test func testUploadFailedMapsToCannotSynchronize() {
    let err = JmapError.uploadFailed("network").fileProviderError
    #expect(err.code == NSFileProviderError.cannotSynchronize.rawValue)
}
