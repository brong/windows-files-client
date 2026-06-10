import Foundation
import Testing
@testable import JmapClient

// MARK: - SSE idle-timeout (reliability D3)

/// A stream that yields one line then stays silent forever must be detected as
/// a half-open connection and rejected with SSEIdleTimeoutError.
@Test func sseIdleTimeoutFiresOnSilentStream() async {
    let (stream, continuation) = AsyncStream.makeStream(of: String.self)
    continuation.yield(":keepalive")   // one line, then silence (never finishes)

    do {
        try await PushWatcher.consumeWithIdleTimeout(
            stream, idleTimeout: 0.05
        ) { _ in }
        Issue.record("expected SSEIdleTimeoutError on a silent stream")
    } catch is SSEIdleTimeoutError {
        // expected
    } catch {
        Issue.record("expected SSEIdleTimeoutError, got \(error)")
    }
}

/// A stream that delivers lines and then ends normally must complete without a
/// timeout, delivering every line to onLine.
@Test func sseIdleTimeoutDoesNotFireWhenStreamCompletes() async throws {
    let (stream, continuation) = AsyncStream.makeStream(of: String.self)
    continuation.yield("a")
    continuation.yield("b")
    continuation.yield("c")
    continuation.finish()

    let collected = Collected()
    try await PushWatcher.consumeWithIdleTimeout(
        stream, idleTimeout: 0.2
    ) { line in await collected.append(line) }

    #expect(await collected.lines == ["a", "b", "c"])
}

private actor Collected {
    var lines: [String] = []
    func append(_ s: String) { lines.append(s) }
}
