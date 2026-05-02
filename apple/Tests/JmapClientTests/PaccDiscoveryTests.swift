import Foundation
import Testing
@testable import JmapClient

// MARK: - URL construction

@Test func testDiscoveryURLFromEmail() throws {
    let url = try PaccDiscovery.discoveryURL(for: "user@fastmail.com")
    #expect(url.absoluteString == "https://ua-auto-config.fastmail.com/.well-known/user-agent-configuration.json")
}

@Test func testDiscoveryURLFromSubdomainEmail() throws {
    let url = try PaccDiscovery.discoveryURL(for: "user@example.org")
    #expect(url.absoluteString == "https://ua-auto-config.example.org/.well-known/user-agent-configuration.json")
}

@Test func testDiscoveryURLPreservesLongerDomain() throws {
    let url = try PaccDiscovery.discoveryURL(for: "alice@mail.company.co.uk")
    #expect(url.absoluteString == "https://ua-auto-config.mail.company.co.uk/.well-known/user-agent-configuration.json")
}

// MARK: - Invalid email validation

@Test func testInvalidEmailNoAt() throws {
    #expect(throws: PaccError.invalidEmail) {
        _ = try PaccDiscovery.discoveryURL(for: "notanemail")
    }
}

@Test func testInvalidEmailEmptyDomain() throws {
    #expect(throws: PaccError.invalidEmail) {
        _ = try PaccDiscovery.discoveryURL(for: "user@")
    }
}

@Test func testEmptyEmail() throws {
    #expect(throws: PaccError.invalidEmail) {
        _ = try PaccDiscovery.discoveryURL(for: "")
    }
}

// MARK: - PaccError descriptions

@Test func testInvalidEmailDescription() {
    let err = PaccError.invalidEmail
    #expect(err.errorDescription?.contains("Invalid email") == true)
}

@Test func testNoJmapProtocolDescription() {
    let err = PaccError.noJmapProtocol
    #expect(err.errorDescription?.contains("JMAP") == true)
}

@Test func testDiscoveryFailedDescription() {
    let err = PaccError.discoveryFailed(404)
    #expect(err.errorDescription?.contains("404") == true)
}

@Test func testIssuerMismatchDescription() {
    let err = PaccError.issuerMismatch(expected: "https://a.com", got: "https://b.com")
    #expect(err.errorDescription?.contains("a.com") == true)
    #expect(err.errorDescription?.contains("b.com") == true)
}
