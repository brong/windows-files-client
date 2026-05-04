import Foundation

/// Delegate-based file upload with byte-level progress reporting and stall detection.
/// URLSession.upload(for:fromFile:) doesn't report progress — this does.
///
/// Stall detection: a concurrent Task wakes every 10 s and checks whether
/// didSendBodyData has fired since the last check. If the upload hasn't made
/// progress for `stallTimeout` seconds the URLSessionTask is cancelled and the
/// caller receives URLError(.timedOut).
final class ProgressUploader: NSObject, URLSessionTaskDelegate, URLSessionDataDelegate, @unchecked Sendable {
    private let onBytesSent: @Sendable (Int64, Int64) -> Void
    private let stallTimeout: TimeInterval
    private var responseData = Data()
    private var httpResponse: HTTPURLResponse?
    private var continuation: CheckedContinuation<(Data, HTTPURLResponse), Error>?
    private weak var currentTask: URLSessionTask?
    private var stallMonitorTask: Task<Void, Never>?
    private var stalledByMonitor = false

    // Protected by lock
    private let lock = NSLock()
    private var lastProgressDate = Date()

    private init(onBytesSent: @escaping @Sendable (Int64, Int64) -> Void,
                 stallTimeout: TimeInterval) {
        self.onBytesSent = onBytesSent
        self.stallTimeout = stallTimeout
    }

    /// Upload a file with byte-level progress reporting and stall detection.
    /// - Parameters:
    ///   - stallTimeout: Seconds of zero progress before the upload is cancelled
    ///                   and URLError(.timedOut) is thrown. Default 120 s.
    ///   - onBytesSent: Progress callback (totalBytesSent, totalBytesExpected).
    static func upload(
        request: URLRequest,
        fromFile fileURL: URL,
        session: URLSession,
        stallTimeout: TimeInterval = 120,
        onBytesSent: @escaping @Sendable (Int64, Int64) -> Void = { _, _ in }
    ) async throws -> (Data, HTTPURLResponse) {
        let delegate = ProgressUploader(onBytesSent: onBytesSent, stallTimeout: stallTimeout)

        return try await withCheckedThrowingContinuation { continuation in
            delegate.continuation = continuation

            let config = session.configuration.copy() as! URLSessionConfiguration
            // Disable URLSession's own request timeout — stall detection handles this.
            config.timeoutIntervalForRequest = 604800  // 1 week (effectively disabled)
            let delegateSession = URLSession(configuration: config, delegate: delegate, delegateQueue: nil)
            let task = delegateSession.uploadTask(with: request, fromFile: fileURL)
            delegate.currentTask = task
            task.resume()
            delegate.startStallMonitor()
        }
    }

    // MARK: - Stall monitor

    private func startStallMonitor() {
        let timeout = stallTimeout
        stallMonitorTask = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(nanoseconds: 10_000_000_000)  // 10 s
                guard let self else { return }
                let (elapsed, task): (TimeInterval, URLSessionTask?) = {
                    self.lock.lock()
                    defer { self.lock.unlock() }
                    return (Date().timeIntervalSince(self.lastProgressDate), self.currentTask)
                }()
                if elapsed > timeout {
                    self.stalledByMonitor = true
                    task?.cancel()
                    return
                }
            }
        }
    }

    // MARK: - URLSessionTaskDelegate

    func urlSession(_ session: URLSession, task: URLSessionTask,
                    didSendBodyData bytesSent: Int64,
                    totalBytesSent: Int64,
                    totalBytesExpectedToSend: Int64) {
        lock.lock()
        lastProgressDate = Date()
        lock.unlock()
        onBytesSent(totalBytesSent, totalBytesExpectedToSend)
    }

    func urlSession(_ session: URLSession, task: URLSessionTask,
                    didCompleteWithError error: Error?) {
        stallMonitorTask?.cancel()
        if let error {
            // If the stall monitor fired the cancel, report it as a timeout rather
            // than a generic cancellation so callers can distinguish the two.
            if stalledByMonitor {
                continuation?.resume(throwing: URLError(.timedOut))
            } else {
                continuation?.resume(throwing: error)
            }
        } else if let response = httpResponse {
            continuation?.resume(returning: (responseData, response))
        } else {
            continuation?.resume(throwing: JmapError.invalidResponse)
        }
        continuation = nil
        session.invalidateAndCancel()
    }

    // MARK: - URLSessionDataDelegate

    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask,
                    didReceive response: URLResponse,
                    completionHandler: @escaping (URLSession.ResponseDisposition) -> Void) {
        httpResponse = response as? HTTPURLResponse
        completionHandler(.allow)
    }

    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask,
                    didReceive data: Data) {
        responseData.append(data)
    }
}
