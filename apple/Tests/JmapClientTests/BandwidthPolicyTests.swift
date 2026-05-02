import Foundation
import Testing
@testable import JmapClient

// MARK: - Unrestricted (WiFi)

@Test func testUnrestrictedAllowsBackgroundDownload() async {
    let policy = BandwidthPolicy()
    await policy.setTierForTesting(.unrestricted)
    #expect(await policy.allowsBackgroundDownload)
}

@Test func testUnrestrictedAllowsInteractiveDownload() async {
    let policy = BandwidthPolicy()
    await policy.setTierForTesting(.unrestricted)
    #expect(await policy.allowsInteractiveDownload)
}

@Test func testUnrestrictedAllowsLargeUpload() async {
    let policy = BandwidthPolicy()
    await policy.setTierForTesting(.unrestricted)
    #expect(await policy.allowsUpload(bytes: 100_000_000))
}

// MARK: - Expensive (cellular / hotspot)

@Test func testExpensiveBlocksBackgroundDownload() async {
    let policy = BandwidthPolicy()
    await policy.setTierForTesting(.expensive)
    #expect(!(await policy.allowsBackgroundDownload))
}

@Test func testExpensiveAllowsInteractiveDownload() async {
    let policy = BandwidthPolicy()
    await policy.setTierForTesting(.expensive)
    #expect(await policy.allowsInteractiveDownload)
}

@Test func testExpensiveAllowsSmallUpload() async {
    let policy = BandwidthPolicy()
    await policy.setTierForTesting(.expensive)
    #expect(await policy.allowsUpload(bytes: 500_000))    // 500 KB — under 1 MB threshold
}

@Test func testExpensiveBlocksLargeUpload() async {
    let policy = BandwidthPolicy()
    await policy.setTierForTesting(.expensive)
    #expect(!(await policy.allowsUpload(bytes: 5_000_000)))  // 5 MB — over threshold
}

@Test func testExpensiveAllowsExactlyOneMillionBytes() async {
    let policy = BandwidthPolicy()
    await policy.setTierForTesting(.expensive)
    #expect(await policy.allowsUpload(bytes: 1_000_000))   // exactly at limit
}

// MARK: - Constrained (Low Data Mode)

@Test func testConstrainedBlocksBackgroundDownload() async {
    let policy = BandwidthPolicy()
    await policy.setTierForTesting(.constrained)
    #expect(!(await policy.allowsBackgroundDownload))
}

@Test func testConstrainedAllowsInteractiveDownload() async {
    let policy = BandwidthPolicy()
    await policy.setTierForTesting(.constrained)
    #expect(await policy.allowsInteractiveDownload)
}

@Test func testConstrainedBlocksAllUploads() async {
    let policy = BandwidthPolicy()
    await policy.setTierForTesting(.constrained)
    #expect(!(await policy.allowsUpload(bytes: 1)))  // even 1 byte blocked
}

// MARK: - Offline

@Test func testOfflineBlocksEverything() async {
    let policy = BandwidthPolicy()
    await policy.setTierForTesting(.offline)
    #expect(!(await policy.allowsInteractiveDownload))
    #expect(!(await policy.allowsBackgroundDownload))
    #expect(!(await policy.allowsUpload(bytes: 1)))
}
