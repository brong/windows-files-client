import Foundation
import Testing
@testable import JmapClient

@Test func testBasicEvent() {
    var parser = SSEParser()
    #expect(parser.feedLine("event: state") == nil)
    #expect(parser.feedLine("data: hello") == nil)
    let event = parser.feedLine("")
    #expect(event != nil)
    #expect(event?.type == "state")
    #expect(event?.data == "hello")
}

@Test func testMultilineData() {
    var parser = SSEParser()
    _ = parser.feedLine("event: state")
    _ = parser.feedLine("data: line1")
    _ = parser.feedLine("data: line2")
    let event = parser.feedLine("")
    #expect(event?.data == "line1\nline2")
}

@Test func testNoEventType() {
    var parser = SSEParser()
    _ = parser.feedLine("data: payload")
    let event = parser.feedLine("")
    #expect(event?.type == "")
    #expect(event?.data == "payload")
}

@Test func testEmptyLineWithNoData() {
    var parser = SSEParser()
    // Empty line with no preceding data should not emit an event
    let event = parser.feedLine("")
    #expect(event == nil)
}

@Test func testCommentAndIdLinesIgnored() {
    var parser = SSEParser()
    _ = parser.feedLine(": this is a comment")
    _ = parser.feedLine("id: 123")
    _ = parser.feedLine("retry: 5000")
    _ = parser.feedLine("data: actual")
    let event = parser.feedLine("")
    #expect(event?.data == "actual")
}

@Test func testMultipleEvents() {
    var parser = SSEParser()
    _ = parser.feedLine("event: ping")
    _ = parser.feedLine("data: {}")
    let first = parser.feedLine("")
    #expect(first?.type == "ping")

    _ = parser.feedLine("event: state")
    _ = parser.feedLine("data: {\"changed\":{}}")
    let second = parser.feedLine("")
    #expect(second?.type == "state")
    #expect(second?.data == "{\"changed\":{}}")
}

@Test func testReset() {
    var parser = SSEParser()
    _ = parser.feedLine("event: state")
    _ = parser.feedLine("data: partial")
    parser.reset()
    // After reset, feeding empty line should not produce event from old data
    let event = parser.feedLine("")
    #expect(event == nil)
}

@Test func testWhitespaceAfterColon() {
    var parser = SSEParser()
    _ = parser.feedLine("event:   state  ")
    _ = parser.feedLine("data:   hello  ")
    let event = parser.feedLine("")
    #expect(event?.type == "state")
    #expect(event?.data == "hello")
}

// MARK: - SSEStateChange tests

@Test func testStateChangeHasFileNode() {
    let change = SSEStateChange(changed: [
        "acc1": ["FileNode": "newstate123"]
    ])
    #expect(sseStateChangeHasFileNode(change, accountId: "acc1") == true)
    #expect(sseStateChangeHasFileNode(change, accountId: "acc2") == false)
}

@Test func testStateChangeHasStorageNode() {
    let change = SSEStateChange(changed: [
        "acc1": ["StorageNode": "newstate123"]
    ])
    #expect(sseStateChangeHasFileNode(change, accountId: "acc1") == true)
}

@Test func testStateChangeNoFileNode() {
    let change = SSEStateChange(changed: [
        "acc1": ["Mailbox": "newstate123"]
    ])
    #expect(sseStateChangeHasFileNode(change, accountId: "acc1") == false)
}

@Test func testStateChangeDecoding() throws {
    let json = """
    {"changed":{"u123":{"FileNode":"state456","Mailbox":"state789"}}}
    """
    let data = json.data(using: .utf8)!
    let change = try JSONDecoder().decode(SSEStateChange.self, from: data)
    #expect(change.changed["u123"]?["FileNode"] == "state456")
    #expect(sseStateChangeHasFileNode(change, accountId: "u123") == true)
}

// MARK: - State-change comparison (push reconnect-storm fix)

@Test func testFileNodeStateExtraction() {
    let change = SSEStateChange(changed: [
        "acc1": ["FileNode": "67", "StorageNode": "67"],
        "acc2": ["StorageNode": "964"],
        "acc3": ["Mailbox": "J1"],
    ])
    #expect(sseFileNodeState(change, accountId: "acc1") == "67")
    #expect(sseFileNodeState(change, accountId: "acc2") == "964")  // StorageNode fallback
    #expect(sseFileNodeState(change, accountId: "acc3") == nil)    // no file state
    #expect(sseFileNodeState(change, accountId: "nope") == nil)
}

@Test func testPushSignalsOnlyOnChangedState() {
    #expect(pushShouldSignal(newState: nil, lastSeen: nil) == false)   // nothing to act on
    #expect(pushShouldSignal(newState: "67", lastSeen: nil) == true)   // first sighting
    #expect(pushShouldSignal(newState: "67", lastSeen: "67") == false) // the bug: unchanged → no poll
    #expect(pushShouldSignal(newState: "68", lastSeen: "67") == true)  // real change
}

@Test func testChangedFileNodeAccountsFansOutPerAccount() {
    // One session push covers every account in the login.
    let change = SSEStateChange(changed: [
        "acc1": ["FileNode": "67"],     // unchanged vs lastSeen
        "acc2": ["FileNode": "100"],    // changed (was 99)
        "acc3": ["StorageNode": "5"],   // new account (not in lastSeen)
        "acc4": ["Mailbox": "x"],       // no file state → ignored
    ])
    let (changed, updated) = changedFileNodeAccounts(
        in: change, lastSeen: ["acc1": "67", "acc2": "99"])

    #expect(changed == ["acc2", "acc3"])   // sorted, only genuinely-changed file accounts
    #expect(updated["acc2"] == "100")
    #expect(updated["acc3"] == "5")
    #expect(updated["acc1"] == "67")       // carried forward unchanged
}
