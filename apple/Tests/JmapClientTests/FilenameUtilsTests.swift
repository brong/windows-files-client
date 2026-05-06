import Testing
@testable import JmapClient

// MARK: - caseCollisionSuffixes

@Test func testNoCollisions() {
    let overrides = FilenameUtils.caseCollisionSuffixes(for: ["foo.txt", "bar.txt", "baz.txt"])
    #expect(overrides == [nil, nil, nil])
}

@Test func testSingleCollisionKeepsFirstChangesSecond() {
    // Uppercase < lowercase in Unicode: "FOO.txt" < "foo.txt" — FOO wins, foo gets (2)
    let names = ["FOO.txt", "foo.txt"]
    let overrides = FilenameUtils.caseCollisionSuffixes(for: names)
    #expect(overrides[0] == nil)            // "FOO.txt" is lex-first → unchanged
    #expect(overrides[1] == "foo (2).txt")  // "foo.txt" gets suffix
}

@Test func testSuffixInsertedBeforeExtension() {
    // "REPORT.docx" < "Report.docx" (E=69 < e=101) — REPORT wins
    let names = ["Report.docx", "REPORT.docx"]
    let overrides = FilenameUtils.caseCollisionSuffixes(for: names)
    #expect(overrides[1] == nil)              // "REPORT.docx" is lex-first → unchanged
    #expect(overrides[0] == "Report (2).docx")
}

@Test func testExtensionlessNameGetsSuffixAtEnd() {
    let names = ["Makefile", "makefile"]
    let overrides = FilenameUtils.caseCollisionSuffixes(for: names)
    // "Makefile" < "makefile"
    #expect(overrides[0] == nil)
    #expect(overrides[1] == "makefile (2)")
}

@Test func testDotfileGetsTrailingSuffix() {
    // Names that start with "." — dot is at index 0, treated as extensionless
    let names = [".gitignore", ".GITIGNORE"]
    let overrides = FilenameUtils.caseCollisionSuffixes(for: names)
    #expect(overrides.filter { $0 == nil }.count == 1)
    let suffixed = overrides.compactMap { $0 }
    #expect(suffixed.count == 1)
    #expect(suffixed[0].hasSuffix("(2)"))
}

@Test func testThreeWayCollision() {
    // names[0]="a.txt", names[1]="A.txt", names[2]="a.TXT"
    // Lex order: "A.txt"(A=65) < "a.TXT"(a=97,T=84) < "a.txt"(a=97,t=116)
    // → sorted indices = [1, 2, 0]: "A.txt" wins (nil), "a.TXT"→(2), "a.txt"→(3)
    let names = ["a.txt", "A.txt", "a.TXT"]
    let overrides = FilenameUtils.caseCollisionSuffixes(for: names)
    #expect(overrides[1] == nil)            // "A.txt" is lex-first → unchanged
    #expect(overrides[2] == "a (2).TXT")    // second lex → (2)
    #expect(overrides[0] == "a (3).txt")    // third lex → (3)
}

@Test func testNoSiblingsMeansNoOverrides() {
    let overrides = FilenameUtils.caseCollisionSuffixes(for: [])
    #expect(overrides.isEmpty)
}

@Test func testSingleItemMeansNoOverride() {
    let overrides = FilenameUtils.caseCollisionSuffixes(for: ["lonely.txt"])
    #expect(overrides == [nil])
}

// MARK: - NFC normalization

/// "é" as a single precomposed codepoint (U+00E9)
private let eAcuteNFC = "\u{00E9}"
/// "é" as base "e" + combining acute accent (U+0301)
private let eAcuteNFD = "e\u{0301}"

@Test func testNfcNormalizesDecomposedString() {
    // NFD input should become NFC output
    let result = FilenameUtils.nfc(eAcuteNFD)
    #expect(result == eAcuteNFC)
    // The result must be a single scalar (precomposed)
    #expect(result.unicodeScalars.count == 1)
}

@Test func testNfcLeavesAlreadyComposedStringAlone() {
    let result = FilenameUtils.nfc(eAcuteNFC)
    #expect(result == eAcuteNFC)
    #expect(result.unicodeScalars.count == 1)
}

@Test func testSanitizeNormalizesToNFC() {
    // NFD name "café" should come out NFC after sanitize
    let nfdName = "caf\(eAcuteNFD).txt"
    let result = FilenameUtils.sanitize(nfdName)
    #expect(result == "caf\(eAcuteNFC).txt")
    #expect(result.unicodeScalars.count == 8)  // c-a-f-é-.-t-x-t
}

@Test func testDesanitizeNormalizesToNFC() {
    // APFS returns filenames in NFD; desanitize must produce NFC for the server
    let nfdName = "caf\(eAcuteNFD).txt"
    let result = FilenameUtils.desanitize(nfdName)
    #expect(result == "caf\(eAcuteNFC).txt")
    #expect(result.unicodeScalars.count == 8)
}

@Test func testDesanitizeReversesSubstitutions() {
    // Round-trip: sanitize → desanitize should recover the original name
    let original = "foo/bar:baz.txt"
    let roundTripped = FilenameUtils.desanitize(FilenameUtils.sanitize(original))
    #expect(roundTripped == original)
}

@Test func testNFDAndNFCNamesTreatedAsCollision() {
    // If the server somehow has both NFC and NFD forms of the same name,
    // the collision-suffix logic must treat them as collisions.
    let nfcName = "caf\(eAcuteNFC).txt"
    let nfdName = "caf\(eAcuteNFD).txt"
    let overrides = FilenameUtils.caseCollisionSuffixes(for: [nfcName, nfdName])
    #expect(overrides.filter { $0 == nil }.count == 1)
    #expect(overrides.compactMap { $0 }.count == 1)
}

// MARK: - sanitize (character substitution)

@Test func testSanitizeSlash() {
    let result = FilenameUtils.sanitize("foo/bar")
    #expect(!result.contains("/"))
    #expect(result.contains("\u{2215}"))
}

@Test func testSanitizeColon() {
    let result = FilenameUtils.sanitize("foo:bar")
    #expect(!result.contains(":"))
    #expect(result.contains("\u{A789}"))
}

@Test func testSanitizeCleanNameUnchanged() {
    let name = "hello world.txt"
    #expect(FilenameUtils.sanitize(name) == name)
}
