import Foundation

// MARK: - UploadTaskDelegate

/// NSObject that bridges URLSession delegate callbacks to Swift concurrency.
///
/// Two paths:
/// 1. **Live path** – a continuation is registered before the task starts; the delegate
///    resumes it when the task completes. This is the normal case when the extension is alive.
/// 2. **Orphan path** – the extension is killed while a task is in flight. nsurlsessiond
///    continues the upload and wakes the extension when done. At that point no continuation
///    is registered, so `onOrphan` fires instead, allowing the caller to persist the result.
final class UploadTaskDelegate: NSObject, @unchecked Sendable {
    typealias Handler = (Result<(Data, HTTPURLResponse), Error>) -> Void

    private let lock = NSLock()
    private var handlers: [Int: Handler] = [:]
    private var accumulator: [Int: Data] = [:]
    private var httpResponses: [Int: HTTPURLResponse] = [:]

    var onOrphan: (@Sendable (_ taskId: Int, _ data: Data, _ response: HTTPURLResponse) -> Void)?
    var onFinishedEvents: (@Sendable () -> Void)?

    func register(taskId: Int, handler: @escaping Handler) {
        lock.withLock { handlers[taskId] = handler }
    }
}

extension UploadTaskDelegate: URLSessionDataDelegate {
    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask,
                    didReceive response: URLResponse,
                    completionHandler: @escaping (URLSession.ResponseDisposition) -> Void) {
        if let http = response as? HTTPURLResponse {
            lock.withLock { httpResponses[dataTask.taskIdentifier] = http }
        }
        completionHandler(.allow)
    }

    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive data: Data) {
        let id = dataTask.taskIdentifier
        lock.withLock {
            if accumulator[id] == nil { accumulator[id] = data }
            else { accumulator[id]!.append(data) }
        }
    }
}

extension UploadTaskDelegate: URLSessionTaskDelegate {
    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        let id = task.taskIdentifier
        let data = lock.withLock { accumulator.removeValue(forKey: id) ?? Data() }
        let response = lock.withLock { httpResponses.removeValue(forKey: id) }
        let handler = lock.withLock { handlers.removeValue(forKey: id) }

        if let handler {
            if let error { handler(.failure(error)) }
            else if let response { handler(.success((data, response))) }
            else { handler(.failure(URLError(.badServerResponse))) }
        } else if let response, error == nil {
            // Orphaned task: extension was killed while this was in flight.
            onOrphan?(id, data, response)
        }
    }
}

extension UploadTaskDelegate: URLSessionDelegate {
    func urlSessionDidFinishEvents(forBackgroundURLSession session: URLSession) {
        onFinishedEvents?()
    }
}

// MARK: - BackgroundUploader

/// Wraps a background URLSession for chunk uploads that survive extension process kills.
///
/// Background sessions run upload tasks in `nsurlsessiond`. When the extension process
/// is terminated by fileproviderd, in-flight tasks continue uploading. When nsurlsessiond
/// finishes a task, it wakes the extension and delivers the result via the URLSession delegate.
///
/// **Reconnection:** Creating two `BackgroundUploader` instances with the same `identifier`
/// connects them to the same underlying session. On extension restart, create with the same
/// identifier to receive events from tasks started in a previous process.
///
/// **Orphan handling:** If the extension was killed after a task started but before the
/// `upload(request:fromFile:)` call could register its continuation, `onOrphan` fires with
/// the completed result. Wire this to `NodeDatabase.completeUploadChunkByTaskId` to persist
/// the chunk blobId so the next `createItem`/`modifyItem` retry can skip re-uploading it.
public actor BackgroundUploader {
    public let identifier: String
    private let urlSession: URLSession
    private let taskDelegate: UploadTaskDelegate

    public init(
        identifier: String,
        onOrphan: (@Sendable (_ taskId: Int, _ data: Data, _ response: HTTPURLResponse) -> Void)? = nil,
        onFinishedEvents: (@Sendable () -> Void)? = nil
    ) {
        self.identifier = identifier
        let d = UploadTaskDelegate()
        d.onOrphan = onOrphan
        d.onFinishedEvents = onFinishedEvents
        let config = URLSessionConfiguration.background(withIdentifier: identifier)
        config.sessionSendsLaunchEvents = true
        config.isDiscretionary = false
        self.urlSession = URLSession(configuration: config, delegate: d, delegateQueue: nil)
        self.taskDelegate = d
    }

    /// Upload a file as an HTTP request body.
    ///
    /// The task runs in `nsurlsessiond` and survives extension process termination.
    ///
    /// - Parameter request: Must include the `Authorization` header; background sessions cannot
    ///   refresh tokens mid-flight so the token must be injected before calling this method.
    /// - Parameter taskIdSink: Called synchronously before the async suspension with the task
    ///   identifier. Use to persist the task ID to DB so orphaned tasks can be matched on reconnect.
    public func upload(
        request: URLRequest,
        fromFile fileURL: URL,
        taskIdSink: (@Sendable (Int) -> Void)? = nil
    ) async throws -> (Data, HTTPURLResponse) {
        let task = urlSession.uploadTask(with: request, fromFile: fileURL)
        return try await withCheckedThrowingContinuation { continuation in
            taskDelegate.register(taskId: task.taskIdentifier) { result in
                switch result {
                case .success(let v): continuation.resume(returning: v)
                case .failure(let e): continuation.resume(throwing: e)
                }
            }
            taskIdSink?(task.taskIdentifier)
            task.resume()
        }
    }

    /// Cancel the upload task with the given task identifier.
    public func cancel(taskId: Int) {
        urlSession.getAllTasks { tasks in
            tasks.first { $0.taskIdentifier == taskId }?.cancel()
        }
    }
}
