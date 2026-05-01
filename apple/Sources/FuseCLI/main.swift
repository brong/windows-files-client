import Foundation
import JmapClient
import FuseMount
#if canImport(AppKit)
import AppKit
#endif

// MARK: - CLI Entry Point

func printUsage() {
    let name = CommandLine.arguments.first ?? "fastmail-files"
    print("""
    Usage: \(name) [options] <mountpoint>

    Mount your Fastmail Files as a local filesystem via FUSE.

    Authentication (choose one):
      --login                Sign in via OAuth (opens browser)
      --token <token>        Use a static bearer token (app password)
      --token-file <path>    Read saved OAuth credentials from file

    If none specified, looks for saved credentials, then falls back to --login.

    Options:
      --session-url <url>    JMAP session URL (default: auto-discovered)
      --cache-dir <path>     Blob cache directory (default: ~/Library/Caches/com.fastmail.files)
      --allow-other          Allow other users to access the mount
      --sync-interval <s>    Seconds between sync polls (default: 30)
      --clean                Wipe cached data and do a full sync from server
      --debug                Log all JMAP request/response traffic to stderr

    Data is stored in:
      ~/Library/Application Support/com.fastmail.files/  (credentials, node cache)
      ~/Library/Caches/com.fastmail.files/               (downloaded blobs)

    To unmount: umount <mountpoint>
    """)
}

// MARK: - OAuth CLI Flow

/// Run the full OAuth PKCE flow: discover, register, open browser, wait for callback.
func performOAuthLogin() async throws -> (sessionUrl: String, credential: OAuthCredential) {
    print("Discovering OAuth endpoints...")
    let (sessionUrl, metadata) = try await oauthDiscover()
    print("Session URL: \(sessionUrl)")

    // Pick a random port for the callback server
    let port = UInt16.random(in: 49152...65000)
    let redirectURI = "http://127.0.0.1:\(port)/callback"

    // Dynamic client registration
    guard let regEndpoint = metadata.registrationEndpoint else {
        throw JmapError.serverError("oauth", "Server does not support dynamic client registration")
    }
    print("Registering client...")
    let registration = try await oauthRegisterClient(
        registrationEndpoint: regEndpoint, redirectURI: redirectURI)
    print("Client ID: \(registration.clientId)")

    // Generate PKCE
    let pkce = PKCEChallenge()
    let state = UUID().uuidString

    // Build authorization URL
    guard let authURL = oauthAuthorizationURL(
        authorizationEndpoint: metadata.authorizationEndpoint,
        clientId: registration.clientId,
        redirectURI: redirectURI,
        codeChallenge: pkce.codeChallenge,
        state: state,
        resource: sessionUrl
    ) else {
        throw JmapError.serverError("oauth", "Failed to build authorization URL")
    }

    // Start local HTTP server to receive the callback
    print("Opening browser for authentication...")
    print("(If browser doesn't open, visit: \(authURL.absoluteString))")

    let code = try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<String, Error>) in
        startCallbackServer(port: port, expectedState: state, continuation: continuation)

        // Open browser
        #if canImport(AppKit)
        NSWorkspace.shared.open(authURL)
        #else
        // Fallback: use `open` command
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/open")
        process.arguments = [authURL.absoluteString]
        try? process.run()
        #endif
    }

    // Exchange code for tokens
    print("Exchanging authorization code for tokens...")
    let tokenResponse = try await oauthExchangeCode(
        tokenEndpoint: metadata.tokenEndpoint,
        clientId: registration.clientId,
        code: code,
        redirectURI: redirectURI,
        codeVerifier: pkce.codeVerifier
    )

    let credential = OAuthCredential(
        sessionUrl: sessionUrl,
        accessToken: tokenResponse.accessToken,
        refreshToken: tokenResponse.refreshToken ?? "",
        tokenEndpoint: metadata.tokenEndpoint,
        clientId: registration.clientId,
        expiresAt: Date().addingTimeInterval(TimeInterval(tokenResponse.expiresIn))
    )

    return (sessionUrl, credential)
}

/// Start a minimal HTTP server on localhost to receive the OAuth callback.
func startCallbackServer(port: UInt16, expectedState: String,
                         continuation: CheckedContinuation<String, Error>) {
    // Use a background thread for the blocking socket server
    Thread.detachNewThread {
        do {
            let serverSocket = socket(AF_INET, SOCK_STREAM, 0)
            guard serverSocket >= 0 else {
                continuation.resume(throwing: JmapError.serverError("oauth", "Failed to create socket"))
                return
            }

            var reuse: Int32 = 1
            setsockopt(serverSocket, SOL_SOCKET, SO_REUSEADDR, &reuse, socklen_t(MemoryLayout<Int32>.size))

            var addr = sockaddr_in()
            addr.sin_family = sa_family_t(AF_INET)
            addr.sin_port = port.bigEndian
            addr.sin_addr.s_addr = inet_addr("127.0.0.1")

            let bindResult = withUnsafePointer(to: &addr) { addrPtr in
                addrPtr.withMemoryRebound(to: sockaddr.self, capacity: 1) { sockaddrPtr in
                    bind(serverSocket, sockaddrPtr, socklen_t(MemoryLayout<sockaddr_in>.size))
                }
            }
            guard bindResult == 0 else {
                close(serverSocket)
                continuation.resume(throwing: JmapError.serverError("oauth",
                    "Failed to bind to port \(port)"))
                return
            }

            listen(serverSocket, 1)

            // Set a 5-minute timeout
            var timeout = timeval(tv_sec: 300, tv_usec: 0)
            setsockopt(serverSocket, SOL_SOCKET, SO_RCVTIMEO, &timeout, socklen_t(MemoryLayout<timeval>.size))

            let clientSocket = accept(serverSocket, nil, nil)
            guard clientSocket >= 0 else {
                close(serverSocket)
                continuation.resume(throwing: JmapError.serverError("oauth", "Timeout waiting for callback"))
                return
            }

            // Read the HTTP request
            var buffer = [UInt8](repeating: 0, count: 4096)
            let bytesRead = recv(clientSocket, &buffer, buffer.count, 0)
            let requestStr = bytesRead > 0 ? String(bytes: buffer[..<bytesRead], encoding: .utf8) ?? "" : ""

            // Parse the GET request for the code and state
            var code: String?
            var receivedState: String?

            if let firstLine = requestStr.split(separator: "\r\n").first {
                let parts = firstLine.split(separator: " ")
                if parts.count >= 2, let urlStr = URL(string: "http://localhost\(parts[1])") {
                    let components = URLComponents(url: urlStr, resolvingAgainstBaseURL: false)
                    code = components?.queryItems?.first(where: { $0.name == "code" })?.value
                    receivedState = components?.queryItems?.first(where: { $0.name == "state" })?.value
                }
            }

            // Send response to browser
            let responseBody: String
            if code != nil && receivedState == expectedState {
                responseBody = """
                <html><body style="font-family:system-ui;text-align:center;padding:60px">
                <h1>Authentication successful!</h1>
                <p>You can close this tab and return to the terminal.</p>
                </body></html>
                """
            } else {
                responseBody = """
                <html><body style="font-family:system-ui;text-align:center;padding:60px">
                <h1>Authentication failed</h1>
                <p>Please try again.</p>
                </body></html>
                """
            }

            let httpResponse = "HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nConnection: close\r\n\r\n\(responseBody)"
            _ = httpResponse.withCString { send(clientSocket, $0, httpResponse.utf8.count, 0) }

            close(clientSocket)
            close(serverSocket)

            // Resume the continuation
            guard let code = code else {
                continuation.resume(throwing: JmapError.serverError("oauth", "No authorization code received"))
                return
            }
            guard receivedState == expectedState else {
                continuation.resume(throwing: JmapError.serverError("oauth", "State mismatch (possible CSRF)"))
                return
            }

            continuation.resume(returning: code)
        }
    }
}

// MARK: - Debug Logging URLProtocol

/// URLProtocol that logs JMAP request/response bodies to stderr, then passes through.
final class DebugLoggingProtocol: URLProtocol, @unchecked Sendable {
    private var dataTask: URLSessionDataTask?
    private static let innerSession: URLSession = {
        let config = URLSessionConfiguration.default
        return URLSession(configuration: config)
    }()

    // Pre-send body cache: stash request bodies before URLSession converts them to streams.
    // Key is a unique request ID set via URLProtocol.setProperty.
    private static let bodyLock = NSLock()
    nonisolated(unsafe) private static var bodyCache: [String: Data] = [:]

    /// Stash a request body before URLSession touches it.
    static func stashBody(_ data: Data, for url: URL) {
        bodyLock.lock()
        bodyCache[url.absoluteString] = data
        bodyLock.unlock()
    }

    /// Retrieve and remove stashed body for a request.
    static func popBody(for request: URLRequest) -> Data? {
        guard let key = request.url?.absoluteString else { return nil }
        bodyLock.lock()
        let data = bodyCache.removeValue(forKey: key)
        bodyLock.unlock()
        return data
    }

    override class func canInit(with request: URLRequest) -> Bool {
        // Only intercept if we haven't already tagged this request
        return URLProtocol.property(forKey: "DebugLogged", in: request) == nil
    }

    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }

    override func startLoading() {
        // Tag the request so we don't intercept it again
        let mutableRequest = (request as NSURLRequest).mutableCopy() as! NSMutableURLRequest
        URLProtocol.setProperty(true, forKey: "DebugLogged", in: mutableRequest)

        // Log request
        let method = request.httpMethod ?? "GET"
        let url = request.url?.absoluteString ?? "?"
        FileHandle.standardError.write(Data("\n→ \(method) \(url)\n".utf8))
        // Log request body from the pre-send cache (stream reading is destructive)
        if let bodyData = DebugLoggingProtocol.popBody(for: request) {
            logJmapBody(bodyData, prefix: "  Send")
        }

        let startTime = Date()
        dataTask = Self.innerSession.dataTask(with: mutableRequest as URLRequest) { [weak self] data, response, error in
            guard let self = self else { return }
            let elapsed = String(format: "%.0fms", Date().timeIntervalSince(startTime) * 1000)

            if let httpResp = response as? HTTPURLResponse {
                FileHandle.standardError.write(Data("← \(httpResp.statusCode) \(url) (\(elapsed))\n".utf8))
            }
            if let data = data, !data.isEmpty {
                logJmapBody(data, prefix: "  Recv")
            }

            if let error = error {
                self.client?.urlProtocol(self, didFailWithError: error)
                return
            }
            if let response = response {
                self.client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
            }
            if let data = data {
                self.client?.urlProtocol(self, didLoad: data)
            }
            self.client?.urlProtocolDidFinishLoading(self)
        }
        dataTask?.resume()
    }

    override func stopLoading() {
        dataTask?.cancel()
    }
}

/// Pretty-print a JSON value, collapsing empty arrays/objects to one line.
private func compactPrettyJSON(_ obj: Any) -> String? {
    var opts: JSONSerialization.WritingOptions = [.prettyPrinted, .sortedKeys]
    if #available(macOS 13, *) {
        opts.insert(.withoutEscapingSlashes)
    }
    guard let data = try? JSONSerialization.data(withJSONObject: obj, options: opts),
          var str = String(data: data, encoding: .utf8) else { return nil }
    // Collapse empty arrays/objects: "[\n\n  ]" → "[]", "{\n\n  }" → "{}"
    str = str.replacingOccurrences(of: "\\[\\s*\\]", with: "[]", options: .regularExpression)
    str = str.replacingOccurrences(of: "\\{\\s*\\}", with: "{}", options: .regularExpression)
    return str
}

/// Format a JMAP method call/response: "MethodName #callId { args }" with indented args.
private func formatMethod(_ name: String, callId: String, args: Any, indent: String) -> String {
    var result = "\(name) #\(callId)"
    if let argsStr = compactPrettyJSON(args) {
        let indented = argsStr.split(separator: "\n", omittingEmptySubsequences: false)
            .joined(separator: "\n\(indent)")
        result += "\n\(indent)\(indented)"
    }
    return result
}

/// Log a JMAP request or response body, showing method calls/responses together.
private func logJmapBody(_ data: Data, prefix: String) {
    guard let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else {
        if let str = String(data: data, encoding: .utf8), !str.isEmpty {
            FileHandle.standardError.write(Data("\(prefix): \(str)\n".utf8))
        }
        return
    }

    let indent = String(repeating: " ", count: prefix.count + 2)

    // Request: methodCalls
    if let calls = obj["methodCalls"] as? [[Any]] {
        var lines: [String] = []
        for call in calls {
            guard call.count >= 3,
                  let name = call[0] as? String,
                  let callId = call[2] as? String else { continue }
            lines.append(formatMethod(name, callId: callId, args: call[1], indent: indent))
        }
        FileHandle.standardError.write(Data("\(prefix): \(lines.joined(separator: "\n\(prefix): "))\n".utf8))
        return
    }

    // Response: methodResponses
    if let responses = obj["methodResponses"] as? [[Any]] {
        var lines: [String] = []
        for resp in responses {
            guard resp.count >= 3,
                  let name = resp[0] as? String,
                  let callId = resp[2] as? String else { continue }
            lines.append(formatMethod(name, callId: callId, args: resp[1], indent: indent))
        }
        FileHandle.standardError.write(Data("\(prefix): \(lines.joined(separator: "\n\(prefix): "))\n".utf8))
        return
    }

    // Non-JMAP JSON (e.g. session) — compact summary
    if let pretty = compactPrettyJSON(obj as Any) {
        let maxLen = 2000
        let output = pretty.count > maxLen ? String(pretty.prefix(maxLen)) + "... (\(pretty.count) bytes)" : pretty
        FileHandle.standardError.write(Data("\(prefix): \(output)\n".utf8))
    }
}

// MARK: - macOS standard directories

let appSupportDir: URL = {
    let dir = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first!
        .appendingPathComponent("com.fastmail.files")
    try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
    return dir
}()

let blobCacheDir: URL = {
    let dir = FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask).first!
        .appendingPathComponent("com.fastmail.files/blobs")
    try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
    return dir
}()

// MARK: - Credential persistence

let defaultAuthFile = appSupportDir.appendingPathComponent("auth.json")

func saveCredential(_ credential: OAuthCredential, to url: URL? = nil) {
    let fileURL = url ?? defaultAuthFile
    let encoder = JSONEncoder()
    encoder.dateEncodingStrategy = .iso8601
    encoder.outputFormatting = .prettyPrinted
    guard let data = try? encoder.encode(credential) else { return }
    // Set restrictive permissions
    FileManager.default.createFile(atPath: fileURL.path, contents: data,
                                   attributes: [.posixPermissions: 0o600])
}

func loadCredential(from url: URL? = nil) -> OAuthCredential? {
    let fileURL = url ?? defaultAuthFile
    // Try primary location
    if let data = try? Data(contentsOf: fileURL) {
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        if let cred = try? decoder.decode(OAuthCredential.self, from: data) {
            return cred
        }
    }
    // Fall back to legacy location if no explicit path given
    if url == nil {
        let legacyFile = FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent(".fastmail-files-auth.json")
        if let data = try? Data(contentsOf: legacyFile) {
            let decoder = JSONDecoder()
            decoder.dateDecodingStrategy = .iso8601
            if let cred = try? decoder.decode(OAuthCredential.self, from: data) {
                // Migrate to new location
                saveCredential(cred)
                try? FileManager.default.removeItem(at: legacyFile)
                return cred
            }
        }
    }
    return nil
}

// MARK: - SSE + Poll Sync Loop

/// Combined sync loop: tries to maintain an SSE connection for instant push notifications.
/// If SSE fails, polls every `pollInterval` seconds while trying to re-establish SSE.
func syncLoop(fs: FileNodeFuseFS, sessionManager: SessionManager,
              tokenProvider: TokenProvider, accountId: String,
              pollInterval: TimeInterval) async {
    while !Task.isCancelled {
        // Try to connect SSE — this blocks while the connection is alive
        do {
            try await runSSEConnection(
                fs: fs, sessionManager: sessionManager,
                tokenProvider: tokenProvider, accountId: accountId)
            // SSE returned normally (server closed connection) — sync and reconnect
            print("[sync] SSE connection closed by server, syncing and reconnecting...")
            await doSync(fs: fs)
        } catch is CancellationError {
            return
        } catch {
            // SSE failed — poll once then try again
            print("[sync] SSE connection failed (\(error)), falling back to poll...")
            await doSync(fs: fs)
        }

        // Wait before retrying SSE
        do {
            try await Task.sleep(nanoseconds: UInt64(pollInterval * 1_000_000_000))
        } catch {
            return // Cancelled
        }
    }
}

/// Connect to SSE and process events. Blocks until the connection drops.
/// Calls fs.sync() immediately when a FileNode state change is received.
func runSSEConnection(fs: FileNodeFuseFS, sessionManager: SessionManager,
                      tokenProvider: TokenProvider, accountId: String) async throws {
    let session = try await sessionManager.session()

    var urlString = session.eventSourceUrl
    let separator = urlString.contains("?") ? "&" : "?"
    urlString += "\(separator)types=FileNode&closeafter=no&ping=60"

    guard let url = URL(string: urlString) else {
        throw JmapError.invalidResponse
    }

    var request = URLRequest(url: url)
    request.setValue("text/event-stream", forHTTPHeaderField: "Accept")
    request.timeoutInterval = 0

    let token = try await tokenProvider.currentToken()
    request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")

    let (bytes, response) = try await URLSession.shared.bytes(for: request)
    guard let httpResponse = response as? HTTPURLResponse,
          httpResponse.statusCode == 200 else {
        if let httpResponse = response as? HTTPURLResponse, httpResponse.statusCode == 401 {
            await sessionManager.invalidate()
            throw JmapError.unauthorized
        }
        throw JmapError.invalidResponse
    }

    print("[sync] SSE connected — listening for changes")
    var parser = SSEParser()

    for try await line in bytes.lines {
        if Task.isCancelled { return }

        guard let event = parser.feedLine(line) else { continue }
        guard event.type == "state" else { continue }

        guard let jsonData = event.data.data(using: .utf8),
              let stateChange = try? JSONDecoder().decode(SSEStateChange.self, from: jsonData)
        else { continue }

        if sseStateChangeHasFileNode(stateChange, accountId: accountId) {
            print("[sync] SSE push received — syncing")
            await doSync(fs: fs)
        }
    }
}

/// Run a sync, swallowing errors.
func doSync(fs: FileNodeFuseFS) async {
    do {
        try await fs.sync()
    } catch {
        print("[sync] Sync error: \(error)")
    }
}

// MARK: - Main

func main() async {
    let args = CommandLine.arguments

    var mountPoint: String?
    var cacheDir: String?
    var allowOther = false
    var syncInterval: TimeInterval = 30
    var staticToken: String?
    var sessionURLOverride: String?
    var tokenFile: String?
    var doLogin = false
    var debugMode = false
    var cleanStart = false

    var i = 1
    while i < args.count {
        switch args[i] {
        case "--help", "-h":
            printUsage()
            exit(0)
        case "--login":
            doLogin = true
        case "--token":
            i += 1
            guard i < args.count else { print("Error: --token requires a value"); exit(1) }
            staticToken = args[i]
        case "--token-file":
            i += 1
            guard i < args.count else { print("Error: --token-file requires a path"); exit(1) }
            tokenFile = args[i]
        case "--session-url":
            i += 1
            guard i < args.count else { print("Error: --session-url requires a URL"); exit(1) }
            sessionURLOverride = args[i]
        case "--cache-dir":
            i += 1
            guard i < args.count else { print("Error: --cache-dir requires a path"); exit(1) }
            cacheDir = args[i]
        case "--allow-other":
            allowOther = true
        case "--sync-interval":
            i += 1
            guard i < args.count, let interval = TimeInterval(args[i]) else {
                print("Error: --sync-interval requires a number"); exit(1)
            }
            syncInterval = interval
        case "--debug":
            debugMode = true
        case "--clean":
            cleanStart = true
        default:
            if args[i].hasPrefix("-") {
                print("Unknown option: \(args[i])"); exit(1)
            }
            mountPoint = args[i]
        }
        i += 1
    }

    guard let mountPoint = mountPoint else {
        printUsage()
        exit(1)
    }

    // Verify mountpoint exists
    var isDir: ObjCBool = false
    guard FileManager.default.fileExists(atPath: mountPoint, isDirectory: &isDir), isDir.boolValue else {
        print("Error: Mount point does not exist or is not a directory: \(mountPoint)")
        exit(1)
    }

    // Set up cache directory (override or macOS standard)
    let blobCacheDirURL: URL
    if let dir = cacheDir {
        blobCacheDirURL = URL(fileURLWithPath: dir)
    } else {
        blobCacheDirURL = blobCacheDir
    }

    // Resolve authentication
    let tokenProvider: TokenProvider
    let sessionURL: URL

    do {
        if let staticToken = staticToken {
            // Static bearer token
            guard let urlStr = sessionURLOverride,
                  let url = URL(string: urlStr) else {
                print("Error: --token requires --session-url")
                exit(1)
            }
            sessionURL = url
            tokenProvider = StaticTokenProvider(token: staticToken)

        } else if let tokenFilePath = tokenFile, let cred = loadCredential(from: URL(fileURLWithPath: tokenFilePath)) {
            // Load from specified file
            guard let url = URL(string: cred.sessionUrl), !cred.sessionUrl.isEmpty else {
                print("Error: Saved credentials have invalid session URL. Re-run with --login.")
                exit(1)
            }
            sessionURL = url
            tokenProvider = OAuthTokenProvider(credential: cred) { updated in
                saveCredential(updated, to: URL(fileURLWithPath: tokenFilePath))
            }

        } else if !doLogin, let cred = loadCredential() {
            // Load saved credentials
            guard let url = URL(string: cred.sessionUrl), !cred.sessionUrl.isEmpty else {
                print("Error: Saved credentials have invalid session URL. Re-run with --login.")
                exit(1)
            }
            print("Using saved credentials from \(defaultAuthFile.path)")
            sessionURL = url
            tokenProvider = OAuthTokenProvider(credential: cred) { updated in
                saveCredential(updated)
            }

        } else {
            // OAuth login
            let (discoveredURL, credential) = try await performOAuthLogin()
            sessionURL = URL(string: sessionURLOverride ?? discoveredURL)!
            saveCredential(credential)
            print("Credentials saved to \(defaultAuthFile.path)")
            tokenProvider = OAuthTokenProvider(credential: credential) { updated in
                saveCredential(updated)
            }
        }
    } catch {
        print("Authentication error: \(error)")
        exit(1)
    }

    let debugProtocols: [AnyClass]? = debugMode ? [DebugLoggingProtocol.self] : nil
    let sessionManager = SessionManager(sessionURL: sessionURL, tokenProvider: tokenProvider,
                                         protocolClasses: debugProtocols)
    let bodyStasher: (@Sendable (URL, Data) -> Void)? = debugMode
        ? { @Sendable url, body in DebugLoggingProtocol.stashBody(body, for: url) }
        : nil
    let jmapClient = JmapClient(sessionManager: sessionManager, tokenProvider: tokenProvider,
                                 protocolClasses: debugProtocols,
                                 requestWillSend: bodyStasher)
    if debugMode {
        FileHandle.standardError.write(Data("[debug] JMAP traffic logging enabled\n".utf8))
    }

    // Discover account and mount
    print("Connecting to JMAP server...")
    do {
        let session = try await sessionManager.session()
        guard let accountId = session.fileNodeAccountId() else {
            print("Error: Server does not support FileNode")
            exit(1)
        }

        let fs = FileNodeFuseFS(
            jmapClient: jmapClient, sessionManager: sessionManager,
            accountId: accountId, stateDir: appSupportDir,
            blobCacheDir: blobCacheDirURL)

        print("Account: \(session.accounts[accountId]?.name ?? accountId)")
        if cleanStart {
            print("Clean start: clearing node cache and blob cache...")
            // Delete node cache
            let cacheFile = appSupportDir.appendingPathComponent("nodes-\(accountId).json")
            try? FileManager.default.removeItem(at: cacheFile)
            // Delete blob cache contents
            if let files = try? FileManager.default.contentsOfDirectory(
                at: blobCacheDirURL, includingPropertiesForKeys: nil) {
                for file in files {
                    try? FileManager.default.removeItem(at: file)
                }
            }
        }
        let startupMsg = try await fs.startUp()
        print(startupMsg)

        // Start background sync with SSE push, falling back to polling
        let syncTask = Task {
            await syncLoop(fs: fs, sessionManager: sessionManager,
                           tokenProvider: tokenProvider, accountId: accountId,
                           pollInterval: syncInterval)
        }

        signal(SIGINT) { _ in
            print("\nUnmounting...")
            exit(0)
        }

        print("Mounting at \(mountPoint)")
        print("Press Ctrl+C or run 'umount \(mountPoint)' to unmount")

        let result = runFuseMount(fs: fs, mountPoint: mountPoint,
                                   foreground: true, allowOther: allowOther)
        syncTask.cancel()

        if result != 0 {
            print("FUSE exited with code \(result)")
            exit(result)
        }
    } catch {
        print("Error: \(error)")
        exit(1)
    }
}

await main()
