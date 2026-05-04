import Foundation
import OSLog

/// Shared debug traffic log written to the app group container.
/// All JmapClient instances log here by default — configure once at app/extension startup.
public final class TrafficLog: Sendable {
    public static let shared = TrafficLog()

    private let logger = Logger(subsystem: "com.fastmail.files", category: "JMAP")
    private let lock = NSLock()
    nonisolated(unsafe) private var fileURL: URL?

    private init() {}

    /// Call once at process startup with the shared container URL.
    /// Appends a session-start marker so logs survive across extension restarts.
    public func configure(containerURL: URL) {
        let url = containerURL.appendingPathComponent("jmap-traffic.log")
        lock.lock()
        fileURL = url
        lock.unlock()

        let df = DateFormatter()
        df.dateFormat = "yyyy-MM-dd HH:mm:ss"
        let separator = "\n=== SESSION START \(df.string(from: Date())) ===\n"
        lock.lock()
        if let handle = try? FileHandle(forWritingTo: url) {
            handle.seekToEndOfFile()
            handle.write(Data(separator.utf8))
            handle.closeFile()
        } else {
            try? separator.write(to: url, atomically: false, encoding: .utf8)
        }
        lock.unlock()

        // Cap the log at ~500 KB to avoid unbounded growth.
        trimIfNeeded(url: url, maxBytes: 500_000)
    }

    public func log(_ message: String) {
        logger.info("\(message, privacy: .public)")
        lock.lock()
        let url = fileURL
        lock.unlock()
        guard let url else { return }
        let line = message + "\n---\n"
        lock.lock()
        if let handle = try? FileHandle(forWritingTo: url) {
            handle.seekToEndOfFile()
            handle.write(Data(line.utf8))
            handle.closeFile()
        } else {
            try? line.write(to: url, atomically: false, encoding: .utf8)
        }
        lock.unlock()
    }

    /// Format a JMAP body for logging, extracting method calls/responses.
    public static func formatBody(_ data: Data) -> String {
        guard let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            return String(data: data, encoding: .utf8) ?? "(\(data.count) bytes)"
        }

        let opts: JSONSerialization.WritingOptions = [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]

        let key = obj["methodCalls"] != nil ? "methodCalls" : (obj["methodResponses"] != nil ? "methodResponses" : nil)
        if let key, let methods = obj[key] as? [[Any]] {
            var lines: [String] = []
            for method in methods {
                guard method.count >= 3,
                      let name = method[0] as? String,
                      let callId = method[2] as? String else { continue }
                if let argsData = try? JSONSerialization.data(withJSONObject: method[1], options: opts),
                   var argsStr = String(data: argsData, encoding: .utf8) {
                    argsStr = argsStr.replacingOccurrences(of: "\\[\\s*\\]", with: "[]", options: .regularExpression)
                    argsStr = argsStr.replacingOccurrences(of: "\\{\\s*\\}", with: "{}", options: .regularExpression)
                    lines.append("  \(name) #\(callId)\n    \(argsStr.replacingOccurrences(of: "\n", with: "\n    "))")
                } else {
                    lines.append("  \(name) #\(callId)")
                }
            }
            return lines.joined(separator: "\n")
        }

        if let data = try? JSONSerialization.data(withJSONObject: obj, options: opts),
           let str = String(data: data, encoding: .utf8) {
            let maxLen = 1000
            return str.count > maxLen ? String(str.prefix(maxLen)) + "..." : str
        }
        return "(\(data.count) bytes)"
    }

    private func trimIfNeeded(url: URL, maxBytes: Int) {
        guard let attrs = try? FileManager.default.attributesOfItem(atPath: url.path),
              let size = attrs[.size] as? Int,
              size > maxBytes,
              let data = try? Data(contentsOf: url) else { return }
        // Keep the last half of the file so context is preserved.
        let trimmed = data.suffix(maxBytes / 2)
        try? trimmed.write(to: url, options: .atomic)
    }
}
