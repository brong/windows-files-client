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

/// The FileNode (or StorageNode) state *value* for `accountId` in this push, or
/// nil if the push carries no file state for that account. The server's initial
/// `connect` event always includes the current value, so callers must compare it
/// against the last-seen value — reacting to its mere presence causes a poll on
/// every reconnect even when nothing changed.
public func sseFileNodeState(_ stateChange: SSEStateChange, accountId: String) -> String? {
    guard let accountChanges = stateChange.changed[accountId] else { return nil }
    return accountChanges["FileNode"] ?? accountChanges["StorageNode"]
}

/// Whether a push carrying `newState` should trigger a sync, given the
/// `lastSeen` state we already acted on. Only a genuine change warrants a poll.
public func pushShouldSignal(newState: String?, lastSeen: String?) -> Bool {
    guard let newState else { return false }
    return newState != lastSeen
}

/// Given a session-level push and the last-seen FileNode state per account,
/// return the accounts whose file state actually changed (so each can be
/// signalled) plus the updated last-seen map. A login's push covers all its
/// accounts in one event, so one push owner can fan out to every changed account.
public func changedFileNodeAccounts(
    in stateChange: SSEStateChange, lastSeen: [String: String]
) -> (changed: [String], updated: [String: String]) {
    var updated = lastSeen
    var changed: [String] = []
    for accountId in stateChange.changed.keys {
        guard let newState = sseFileNodeState(stateChange, accountId: accountId) else { continue }
        if newState != lastSeen[accountId] {
            changed.append(accountId)
            updated[accountId] = newState
        }
    }
    return (changed.sorted(), updated)
}

// MARK: - Idle timeout

/// Thrown when an SSE stream goes silent (no lines, including keepalive pings)
/// for longer than the allowed idle interval — i.e. a half-open connection.
public struct SSEIdleTimeoutError: Error {}

/// Tracks the time of the most recent activity, for the idle watchdog.
/// Uses the monotonic clock so wall-clock changes can't disturb the timeout.
private actor IdleTicker {
    private var lastNanos: UInt64 = DispatchTime.now().uptimeNanoseconds
    func tick() { lastNanos = DispatchTime.now().uptimeNanoseconds }
    func idle(forSeconds seconds: TimeInterval) -> Bool {
        let elapsed = Double(DispatchTime.now().uptimeNanoseconds &- lastNanos) / 1_000_000_000
        return elapsed >= seconds
    }
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
    private let loginId: String
    private let bandwidthPolicy: BandwidthPolicy?
    /// Cross-process lease electing one SSE-push owner per login. A login's push
    /// endpoint is session-level and covers all its accounts in one event, so only
    /// one of its per-account extension processes should hold the connection.
    /// nil → always connect (no leasing — e.g. tests or single-process use).
    private let pushLease: PushLease?
    /// How long a non-owner waits before retrying the lease, so it can take over
    /// if the owner process dies (the kernel drops the flock on exit).
    private static let leaseRetryInterval: TimeInterval = 20
    private var task: Task<Void, Never>?
    private var backoffSeconds: Double = 1.0
    private static let maxBackoff: Double = 60.0
    /// A connection must stay open at least this long to count as a real session;
    /// shorter ones are "flaps" and keep backing off rather than resetting to 1s.
    private static let stableConnectionThreshold: TimeInterval = 10.0

    /// The backoff to wait before the next reconnect, given the current backoff
    /// and how long the just-ended connection stayed up. A stable connection
    /// (≥ `stableThreshold`) resets to `floor`; a flap grows exponentially toward
    /// `ceiling`. This prevents a connection that opens and closes within ~1s
    /// from producing a 1 Hz reconnect storm.
    static func nextBackoff(
        current: Double, upSeconds: Double,
        stableThreshold: Double, floor: Double, ceiling: Double
    ) -> Double {
        if upSeconds >= stableThreshold { return floor }
        return min(current * 2, ceiling)
    }

    /// SSE parser state, reset on each (re)connect.
    private var sseParser = SSEParser()
    /// Last FileNode/StorageNode state value acted on, per account, persisted
    /// ACROSS reconnects so the initial `connect` event of each new connection
    /// doesn't re-poll unchanged state. Login-scoped: the push owner tracks every
    /// account the session reports, and fans out a signal to each changed one.
    private var lastFileNodeStates: [String: String] = [:]
    /// Reconnect if no SSE line (including the 60s keepalive ping) arrives for
    /// this long — 2.5x the ping interval allows for jitter without false trips.
    private static let sseIdleTimeout: TimeInterval = 150

    #if canImport(os)
    private let logger = Logger(subsystem: "com.fastmail.files", category: "PushWatcher")
    #endif

    public init(
        sessionManager: SessionManager,
        tokenProvider: TokenProvider,
        accountId: String,
        loginId: String,
        pushLease: PushLease? = nil,
        bandwidthPolicy: BandwidthPolicy? = nil
    ) {
        self.sessionManager = sessionManager
        self.tokenProvider = tokenProvider
        self.accountId = accountId
        self.loginId = loginId
        self.pushLease = pushLease
        self.bandwidthPolicy = bandwidthPolicy
    }

    /// Set the delegate. Must be called before `start()`.
    public func setDelegate(_ delegate: (any PushWatcherDelegate)?) {
        self.delegate = delegate
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

    /// Consume an SSE line stream, invoking `onLine` for each line. Returns
    /// normally when the stream ends. Throws `SSEIdleTimeoutError` if no line
    /// (including keepalive pings) arrives for `idleTimeout` — catching a
    /// half-open connection that would otherwise stall sync silently.
    static func consumeWithIdleTimeout<S: AsyncSequence & Sendable>(
        _ lines: S,
        idleTimeout: TimeInterval,
        onLine: @Sendable @escaping (String) async -> Void
    ) async throws where S.Element == String {
        let ticker = IdleTicker()
        try await withThrowingTaskGroup(of: Void.self) { group in
            // Reader: consume the stream; completes when it ends normally.
            group.addTask {
                for try await line in lines {
                    try Task.checkCancellation()
                    await ticker.tick()
                    await onLine(line)
                }
            }
            // Watchdog: throw if no line has arrived within the idle window.
            group.addTask {
                let napNanos = UInt64(idleTimeout * 1_000_000_000)
                while true {
                    try await Task.sleep(nanoseconds: napNanos)
                    if await ticker.idle(forSeconds: idleTimeout) {
                        throw SSEIdleTimeoutError()
                    }
                }
            }
            // First task to finish decides the outcome: the reader completing
            // (stream ended) returns; the watchdog throwing (idle) rethrows.
            defer { group.cancelAll() }
            try await group.next()
        }
    }

    private func connectionLoop() async {
        while !Task.isCancelled {
            // Bandwidth gate: skip SSE entirely on constrained/offline connections.
            // The sync engine's polling loop covers change detection in the meantime,
            // and we re-check every maxBackoff seconds in case conditions improve.
            if await bandwidthPolicy?.skipSse == true {
                #if canImport(os)
                logger.info("[\(self.accountId, privacy: .public)] SSE skipped (constrained/offline) — relying on polling")
                #endif
                do {
                    try await Task.sleep(nanoseconds: UInt64(Self.maxBackoff * 1_000_000_000))
                } catch {
                    return
                }
                continue
            }

            // Push-owner lease: only one process per login holds the (session-level)
            // SSE connection. A non-owner stands down and retries periodically so it
            // can take over if the owner process dies. Its domains still sync via the
            // owner's fan-out signals and the system's own enumeration cadence.
            if let lease = pushLease, !lease.tryAcquire() {
                #if canImport(os)
                logger.info("[\(self.accountId, privacy: .public)] another process owns the push for this login — standing down")
                #endif
                do {
                    try await Task.sleep(nanoseconds: UInt64(Self.leaseRetryInterval * 1_000_000_000))
                } catch {
                    return
                }
                continue
            }

            let startNanos = DispatchTime.now().uptimeNanoseconds
            do {
                try await connect()
                // connect() returned normally — server closed the stream.
                // Signal the delegate to catch up on any missed changes.
                await delegate?.pushWatcherDidReconnect(self)
            } catch is CancellationError {
                return
            } catch is SSEIdleTimeoutError {
                // Half-open connection: no pings for sseIdleTimeout. Catch up via
                // an enumeration (in case we missed changes while silently stalled),
                // then fall through to reconnect.
                #if canImport(os)
                logger.warning("[\(self.accountId, privacy: .public)] SSE idle timeout — reconnecting and catching up")
                #endif
                await delegate?.pushWatcherDidReconnect(self)
            } catch {
                #if canImport(os)
                logger.warning("[\(self.accountId, privacy: .public)] SSE connection error: \(error.localizedDescription, privacy: .public). Retrying in \(self.backoffSeconds, privacy: .public)s")
                #endif
            }

            // Backoff. A connection that stayed up only briefly is a "flap" and
            // keeps backing off; a stable one resets to ~1s — this stops a
            // connection that opens and closes within ~1s from reconnecting at
            // 1 Hz. The larger of the computed backoff and the bandwidth-policy
            // floor (30 s on expensive/cellular) is used.
            let upSeconds = Double(DispatchTime.now().uptimeNanoseconds &- startNanos) / 1_000_000_000
            backoffSeconds = Self.nextBackoff(
                current: backoffSeconds, upSeconds: upSeconds,
                stableThreshold: Self.stableConnectionThreshold, floor: 1.0, ceiling: Self.maxBackoff)
            let minDelay = await bandwidthPolicy?.minSseReconnectDelay ?? 0.0
            let sleepSeconds = max(backoffSeconds, minDelay)
            do {
                try await Task.sleep(nanoseconds: UInt64(sleepSeconds * 1_000_000_000))
            } catch {
                return
            }
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

        TrafficLog.shared.log("→ SSE [\(accountId)] \(url.absoluteString)")

        let (bytes, response) = try await URLSession.shared.bytes(for: request)
        guard let httpResponse = response as? HTTPURLResponse,
              httpResponse.statusCode == 200
        else {
            let status = (response as? HTTPURLResponse)?.statusCode ?? 0
            let ct = (response as? HTTPURLResponse)?.value(forHTTPHeaderField: "Content-Type") ?? "?"
            TrafficLog.shared.log("← \(status) SSE [\(accountId)] Content-Type: \(ct)")
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

        let ct = httpResponse.value(forHTTPHeaderField: "Content-Type") ?? "?"
        let cl = httpResponse.value(forHTTPHeaderField: "Content-Length") ?? "chunked"
        TrafficLog.shared.log("← 200 SSE [\(accountId)] Content-Type: \(ct) Content-Length: \(cl)")

        // Note: backoff is NOT reset here. connectionLoop resets it only after a
        // connection that stayed up past stableConnectionThreshold — resetting on
        // every 200 is what let a flapping connection reconnect at 1 Hz.
        #if canImport(os)
        logger.info("[\(self.accountId, privacy: .public)] SSE connected")
        #endif

        // Parse the SSE stream with an idle-timeout watchdog: if no line
        // (including the server's keepalive pings) arrives for sseIdleTimeout,
        // treat the connection as half-open and throw, so connectionLoop
        // reconnects and catches up rather than stalling sync silently.
        sseParser.reset()
        try await Self.consumeWithIdleTimeout(
            bytes.lines, idleTimeout: Self.sseIdleTimeout
        ) { [weak self] line in
            await self?.handleSSELine(line)
        }
        // If bytes.lines throws (network drop, server close) or the idle timeout
        // fires, connectionLoop catches it and retries with backoff. The next
        // connect() sends the cached token; if it was expired the server returns
        // 401 on the initial response, which the handler above invalidates. We do
        // NOT invalidate proactively here — a genuine network drop must not cycle
        // the token.

        TrafficLog.shared.log("← SSE closed [\(accountId)]")
    }

    private func handleSSELine(_ line: String) async {
        if let event = sseParser.feedLine(line) {
            TrafficLog.shared.log("← SSE event [\(accountId)] type=\(event.type.isEmpty ? "(state)" : event.type) data=\(event.data.prefix(200))")
            await handleEvent(event)
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

        // Act only on *changed* state values, and fan out to every changed
        // account in the login (one session push covers them all). The server's
        // initial `connect` event — re-sent on every reconnect — always carries
        // the current state, so reacting to mere presence would re-poll on every
        // reconnect (the reconnect-storm bug).
        let (changed, updated) = changedFileNodeAccounts(in: stateChange, lastSeen: lastFileNodeStates)
        guard !changed.isEmpty else { return }
        lastFileNodeStates = updated
        for changedAccount in changed {
            #if canImport(os)
            logger.debug("FileNode state changed for account \(changedAccount, privacy: .public) → \(updated[changedAccount] ?? "nil", privacy: .public)")
            #endif
            await delegate?.pushWatcher(self, didReceiveChangeForAccount: changedAccount)
        }
    }
}

// MARK: - Delegate

/// Delegate protocol for PushWatcher events.
/// The FileProvider extension implements this to signal the working set enumerator.
public protocol PushWatcherDelegate: AnyObject, Sendable {
    /// Called when a FileNode state change is received via SSE for `accountId`
    /// (which may be a sibling account in the same login, since the push owner
    /// fans out to every changed account). The delegate signals that account's
    /// domain enumerator.
    func pushWatcher(_ watcher: PushWatcher, didReceiveChangeForAccount accountId: String) async

    /// Called when the SSE connection is re-established after a disconnect.
    /// The delegate should signal the working set enumerator(s) to catch up on missed changes.
    func pushWatcherDidReconnect(_ watcher: PushWatcher) async
}

