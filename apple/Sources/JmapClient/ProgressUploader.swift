import Foundation

/// Delegate-based file upload with byte-level progress reporting.
/// URLSession.upload(for:fromFile:) doesn't report progress — this does.
final class ProgressUploader: NSObject, URLSessionTaskDelegate, URLSessionDataDelegate, @unchecked Sendable {
    private let onBytesSent: @Sendable (Int64, Int64) -> Void
    private var responseData = Data()
    private var httpResponse: HTTPURLResponse?
    private var continuation: CheckedContinuation<(Data, HTTPURLResponse), Error>?

    private init(onBytesSent: @escaping @Sendable (Int64, Int64) -> Void) {
        self.onBytesSent = onBytesSent
    }

    /// Upload a file with byte-level progress reporting.
    static func upload(
        request: URLRequest,
        fromFile fileURL: URL,
        session: URLSession,
        onBytesSent: @escaping @Sendable (Int64, Int64) -> Void
    ) async throws -> (Data, HTTPURLResponse) {
        let delegate = ProgressUploader(onBytesSent: onBytesSent)

        return try await withCheckedThrowingContinuation { continuation in
            delegate.continuation = continuation

            // Create a session with our delegate for this specific upload
            let config = session.configuration.copy() as! URLSessionConfiguration
            let delegateSession = URLSession(configuration: config, delegate: delegate, delegateQueue: nil)
            let task = delegateSession.uploadTask(with: request, fromFile: fileURL)
            task.resume()
        }
    }

    // MARK: - URLSessionTaskDelegate

    func urlSession(_ session: URLSession, task: URLSessionTask,
                    didSendBodyData bytesSent: Int64,
                    totalBytesSent: Int64,
                    totalBytesExpectedToSend: Int64) {
        onBytesSent(totalBytesSent, totalBytesExpectedToSend)
    }

    func urlSession(_ session: URLSession, task: URLSessionTask,
                    didCompleteWithError error: Error?) {
        if let error = error {
            continuation?.resume(throwing: error)
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
