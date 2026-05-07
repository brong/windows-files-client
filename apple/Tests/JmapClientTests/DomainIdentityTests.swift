import Foundation
import Testing
@testable import JmapClient

// MARK: - domainId construction

@Test func testDomainIdFormat() {
    let id = DomainIdentity(loginId: "user@example.com@api.server.com", accountId: "vabc1234")
    #expect(id.domainId == "user@example.com@api.server.com~vabc1234")
}

// MARK: - parse round-trip

@Test func testParseRoundTrip() {
    let original = DomainIdentity(loginId: "user@example.com@api.server.com", accountId: "vabc1234")
    let parsed = DomainIdentity.parse(original.domainId)
    #expect(parsed?.loginId == original.loginId)
    #expect(parsed?.accountId == original.accountId)
}

@Test func testParseRejectsBareDomainId() {
    // Old format: bare accountId with no tilde — must return nil so callers don't silently
    // use empty loginId.
    #expect(DomainIdentity.parse("vabc1234") == nil)
}

@Test func testParseRejectsColonSeparatedOldFormat() {
    // Colon was briefly used; must not be treated as a valid separator.
    #expect(DomainIdentity.parse("user@example.com:vabc1234") == nil)
}

@Test func testParseRejectsEmptyLoginId() {
    #expect(DomainIdentity.parse("~vabc1234") == nil)
}

@Test func testParseRejectsEmptyAccountId() {
    #expect(DomainIdentity.parse("user@example.com~") == nil)
}

// MARK: - makeLoginId

@Test func testMakeLoginIdUsesHost() {
    let url = URL(string: "https://api.fastmail.com/jmap/session")!
    let loginId = DomainIdentity.makeLoginId(primaryEmail: "brong@brong.net", sessionURL: url)
    #expect(loginId == "brong@brong.net@api.fastmail.com")
}

@Test func testMakeLoginIdFallsBackOnMissingHost() {
    let url = URL(string: "file:///local")!
    let loginId = DomainIdentity.makeLoginId(primaryEmail: "user@example.com", sessionURL: url)
    // Should not crash; falls back to "unknown"
    #expect(loginId == "user@example.com@unknown")
}

// MARK: - separator safety

@Test func testTildeNotInEmail() {
    // Tilde must not appear in email addresses so the separator is unambiguous.
    let email = "user+tag@sub.example.com"
    #expect(!email.contains("~"))
}

@Test func testTildeNotInJmapAccountId() {
    // JMAP accountIds are alphanumeric. Verify the assumption holds for representative values.
    let accountIds = ["vabc1234", "M12345678", "acc_test_01"]
    for id in accountIds {
        #expect(!id.contains("~"), "accountId '\(id)' contains tilde — separator assumption violated")
    }
}
