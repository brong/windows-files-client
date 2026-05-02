import Foundation
import Testing
@testable import JmapClient

// MARK: - Symbol names

@Test func testIdleSymbol() {
    #expect(MenuBarState.idle.symbolName == "checkmark.icloud")
}

@Test func testSyncingSymbol() {
    #expect(MenuBarState.syncing(count: 2).symbolName == "arrow.triangle.2.circlepath.icloud")
}

@Test func testPendingSymbol() {
    #expect(MenuBarState.pending(count: 3).symbolName == "icloud.and.arrow.up")
}

@Test func testErrorSymbol() {
    #expect(MenuBarState.error(message: "Auth failed").symbolName == "exclamationmark.icloud")
}

@Test func testOfflineSymbol() {
    #expect(MenuBarState.offline.symbolName == "icloud.slash")
}

// MARK: - Status text

@Test func testIdleStatusText() {
    #expect(MenuBarState.idle.statusText == "All files up to date")
}

@Test func testSyncingStatusTextSingular() {
    #expect(MenuBarState.syncing(count: 1).statusText == "Syncing 1 file…")
}

@Test func testSyncingStatusTextPlural() {
    #expect(MenuBarState.syncing(count: 3).statusText == "Syncing 3 files…")
}

@Test func testPendingStatusTextSingular() {
    #expect(MenuBarState.pending(count: 1).statusText == "1 change not yet uploaded")
}

@Test func testPendingStatusTextPlural() {
    #expect(MenuBarState.pending(count: 5).statusText == "5 changes not yet uploaded")
}

@Test func testOfflineStatusText() {
    #expect(MenuBarState.offline.statusText == "Offline")
}

@Test func testErrorStatusText() {
    #expect(MenuBarState.error(message: "Auth failed").statusText == "Auth failed")
}

// MARK: - derive() priority

private func makeStatus(_ accountId: String, state: ExtensionStatus.State, error: String? = nil) -> ExtensionStatus {
    var s = ExtensionStatus(accountId: accountId, state: state, nodeCount: 0, error: error)
    return s
}

@Test func testDeriveOfflineWhenNotOnline() {
    let state = MenuBarState.derive(
        pendingCount: 0, activeCount: 0,
        extensionStatuses: [makeStatus("a1", state: .idle)],
        isOnline: false
    )
    #expect(state == .offline)
}

@Test func testDeriveErrorTakesPriority() {
    let state = MenuBarState.derive(
        pendingCount: 2, activeCount: 1,
        extensionStatuses: [makeStatus("a1", state: .error, error: "Auth failed")],
        isOnline: true
    )
    if case .error(let msg) = state {
        #expect(msg == "Auth failed")
    } else {
        Issue.record("Expected .error, got \(state)")
    }
}

@Test func testDerivePendingBeforeSyncing() {
    let state = MenuBarState.derive(
        pendingCount: 3, activeCount: 2,
        extensionStatuses: [makeStatus("a1", state: .syncing)],
        isOnline: true
    )
    if case .pending(let count) = state {
        #expect(count == 3)
    } else {
        Issue.record("Expected .pending, got \(state)")
    }
}

@Test func testDeriveSyncingFromActiveCount() {
    let state = MenuBarState.derive(
        pendingCount: 0, activeCount: 4,
        extensionStatuses: [makeStatus("a1", state: .idle)],
        isOnline: true
    )
    if case .syncing(let count) = state {
        #expect(count == 4)
    } else {
        Issue.record("Expected .syncing, got \(state)")
    }
}

@Test func testDeriveSyncingFromExtensionStatus() {
    let state = MenuBarState.derive(
        pendingCount: 0, activeCount: 0,
        extensionStatuses: [makeStatus("a1", state: .syncing)],
        isOnline: true
    )
    #expect(state == .syncing(count: 1))
}

@Test func testDeriveIdleWhenAllQuiet() {
    let state = MenuBarState.derive(
        pendingCount: 0, activeCount: 0,
        extensionStatuses: [makeStatus("a1", state: .idle)],
        isOnline: true
    )
    #expect(state == .idle)
}

@Test func testDeriveIdleWithNoAccounts() {
    let state = MenuBarState.derive(
        pendingCount: 0, activeCount: 0,
        extensionStatuses: [],
        isOnline: true
    )
    #expect(state == .idle)
}

@Test func testDeriveErrorOverridesOffline() {
    // An auth error while offline must surface as .error, not .offline — it's actionable
    let state = MenuBarState.derive(
        pendingCount: 0, activeCount: 0,
        extensionStatuses: [makeStatus("a1", state: .error, error: "Auth failed")],
        isOnline: false
    )
    if case .error(let msg) = state {
        #expect(msg == "Auth failed")
    } else {
        Issue.record("Expected .error to override offline, got \(state)")
    }
}

// MARK: - Paused state

@Test func testPausedCellularSymbol() {
    #expect(MenuBarState.paused(reason: .cellular).symbolName == "pause.icloud")
}

@Test func testPausedCellularStatusText() {
    #expect(MenuBarState.paused(reason: .cellular).statusText == "On cellular — background sync paused")
}

@Test func testPausedLowDataStatusText() {
    #expect(MenuBarState.paused(reason: .lowDataMode).statusText == "Low Data Mode — sync paused")
}

@Test func testPausedUserRequestedStatusText() {
    #expect(MenuBarState.paused(reason: .userRequested).statusText == "Sync paused")
}
