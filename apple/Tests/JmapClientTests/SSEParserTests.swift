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
