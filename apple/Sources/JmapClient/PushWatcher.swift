import Foundation
#if canImport(os)
import os
#endif

// MARK: - SSE Parser

/// Parses Server-Sent Events from a stream of text lines.
///
/// SSE format: events are separated by blank lines. Each event has optional
/// `event:`, `data:`, `id:`, and `retry:` fields. Lines starting with `:` are comments.
public struct SSEParser {
    /// A parsed SSE event.
    public struct Event {
        public let type: String   // from "event:" field, empty string if absent
        public let data: String   // from "data:" field(s), joined by newlines
    }

    private var currentEvent = ""
    private var currentData = ""

    public init() {}

    /// Feed a single line from the SSE stream.
    /// Returns an `Event` when a blank line completes an event, otherwise nil.
    public mutating func feedLine(_ line: String) -> Event? {
        if line.isEmpty {
            // Empty line = end of event
            guard !currentData.isEmpty else { return nil }
            let event = Event(type: currentEvent, data: currentData)
            currentEvent = ""
            currentData = ""
            return event
        }

        if line.hasPrefix("event:") {
            currentEvent = String(line.dropFirst(6)).trimmingCharacters(in: .whitespaces)
        } else if line.hasPrefix("data:") {
            let data = String(line.dropFirst(5)).trimmingCharacters(in: .whitespaces)
            if currentData.isEmpty {
                currentData = data
            } else {
                currentData += "\n" + data
            }
        }
        // Ignore "id:", "retry:", and comment lines (starting with ":")
        return nil
    }

    /// Reset parser state (e.g. on reconnect).
    public mutating func reset() {
        currentEvent = ""
        currentData = ""
    }
}

// MARK: - SSE Types

/// Decoded JMAP StateChange event payload.
public struct SSEStateChange: Codable, Sendable {
    public let changed: [String: [String: String]]
}

/// Check if a state change contains FileNode changes for a given account.
public func sseStateChangeHasFileNode(_ stateChange: SSEStateChange, accountId: String) -> Bool {
    guard let accountChanges = stateChange.changed[accountId] else { return false }
    return accountChanges.keys.contains { $0 == "FileNode" || $0 == "StorageNode" }
}

// MARK: - PushWatcher

/// SSE (Server-Sent Events) push watcher for JMAP StateChange notifications.
///
/// Connects to the JMAP eventSourceUrl and notifies the delegate when
/// FileNode state changes are received. The delegate (typically the
/// FileProvider extension) signals the working set enumerator.
public actor PushWatcher {
    public weak var delegate: PushWatcherDelegate?

    private let sessionManager: SessionManager
    private let tokenProvider: TokenProvider
    private let accountId: String
    private var task: Task<Void, Never>?
    private var backoffSeconds: Double = 1.0
    private static let maxBackoff: Double = 60.0

    #if canImport(os)
    private let logger = Logger(subsystem: "com.fastmail.files", category: "PushWatcher")
    #endif

    public init(sessionManager: SessionManager, tokenProvider: TokenProvider, accountId: String) {
        self.sessionManager = sessionManager
        self.tokenProvider = tokenProvider
        self.accountId = accountId
    }

    /// Start the SSE connection. Reconnects automatically with backoff.
    public func start() {
        guard task == nil else { return }
        task = Task { await connectionLoop() }
    }

    /// Stop the SSE connection.
    public func stop() {
        task?.cancel()
        task = nil
        backoffSeconds = 1.0
    }

    private func connectionLoop() async {
        while !Task.isCancelled {
            do {
                try await connect()
                // If connect() returns normally, the connection was closed by the server.
                // Signal the delegate to poll for any missed changes.
                await delegate?.pushWatcherDidReconnect(self)
            } catch is CancellationError {
                return
            } catch {
                #if canImport(os)
                logger.warning("SSE connection error: \(error.localizedDescription). Retrying in \(self.backoffSeconds)s")
                #endif
            }

            // Exponential backoff
            do {
                try await Task.sleep(nanoseconds: UInt64(backoffSeconds * 1_000_000_000))
            } catch {
                return // Cancelled
            }
            backoffSeconds = min(backoffSeconds * 2, Self.maxBackoff)
        }
    }

    private func connect() async throws {
        let session = try await sessionManager.session()

        // Build SSE URL
        var urlString = session.eventSourceUrl
        let separator = urlString.contains("?") ? "&" : "?"
        urlString += "\(separator)types=FileNode&closeafter=no&ping=60"

        guard let url = URL(string: urlString) else {
            throw JmapError.invalidResponse
        }

        var request = URLRequest(url: url)
        request.setValue("text/event-stream", forHTTPHeaderField: "Accept")
        request.timeoutInterval = 0 // No timeout for SSE

        // Set auth header
        let token = try await tokenProvider.currentToken()
        request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")

        let (bytes, response) = try await URLSession.shared.bytes(for: request)
        guard let httpResponse = response as? HTTPURLResponse,
              httpResponse.statusCode == 200
        else {
            if let httpResponse = response as? HTTPURLResponse, httpResponse.statusCode == 401 {
                // Invalidate token and session so next retry refreshes OAuth
                if let oauthProvider = tokenProvider as? OAuthTokenProvider {
                    await oauthProvider.invalidateAccessToken()
                }
                await sessionManager.invalidate()
                throw JmapError.unauthorized
            }
            throw JmapError.invalidResponse
        }

        // Reset backoff on successful connection
        backoffSeconds = 1.0
        #if canImport(os)
        logger.info("SSE connected")
        #endif

        // Parse SSE stream using the extracted parser
        var parser = SSEParser()

        for try await line in bytes.lines {
            if Task.isCancelled { return }

            if let event = parser.feedLine(line) {
                await handleEvent(event)
            }
        }
    }

    private func handleEvent(_ event: SSEParser.Event) async {
        guard event.type == "state" else { return }

        guard let jsonData = event.data.data(using: .utf8),
              let stateChange = try? JSONDecoder().decode(SSEStateChange.self, from: jsonData)
        else {
            #if canImport(os)
            logger.warning("Failed to parse SSE state change: \(event.data)")
            #endif
            return
        }

        if sseStateChangeHasFileNode(stateChange, accountId: accountId) {
            #if canImport(os)
            logger.debug("FileNode state changed for account \(self.accountId)")
            #endif
            await delegate?.pushWatcherDidReceiveChange(self)
        }
    }
}

// MARK: - Delegate

/// Delegate protocol for PushWatcher events.
/// The FileProvider extension implements this to signal the working set enumerator.
public protocol PushWatcherDelegate: AnyObject, Sendable {
    /// Called when a FileNode state change is received via SSE.
    func pushWatcherDidReceiveChange(_ watcher: PushWatcher) async

    /// Called when the SSE connection is re-established after a disconnect.
    /// The delegate should signal the working set enumerator to catch up on missed changes.
    func pushWatcherDidReconnect(_ watcher: PushWatcher) async
}

