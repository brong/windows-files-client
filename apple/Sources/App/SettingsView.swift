import SwiftUI
import JmapClient
#if canImport(AppKit)
import AppKit
#endif

struct SettingsView: View {
    @ObservedObject var appState: AppState

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            Text("Accounts")
                .font(.title2)
                .bold()

            if appState.accounts.isEmpty {
                Text("No accounts configured. Add an account to start syncing files.")
                    .foregroundColor(.secondary)
                    .frame(maxWidth: .infinity, alignment: .center)
                    .padding()
            } else {
                List {
                    ForEach(appState.accounts) { account in
                        HStack {
                            VStack(alignment: .leading) {
                                Text(account.displayName)
                                    .font(.body)
                                Text(account.accountId)
                                    .font(.caption)
                                    .foregroundColor(.secondary)
                            }
                            Spacer()
                            Button("Remove") {
                                Task {
                                    try? await appState.removeAccount(account.accountId)
                                }
                            }
                            .foregroundColor(.red)
                        }
                    }
                }
            }

            Divider()

            Button("Add Account...") {
                appState.showingAddAccount = true
            }
            .sheet(isPresented: $appState.showingAddAccount) {
                AddAccountView(appState: appState)
            }
        }
        .padding()
        .frame(minWidth: 450, minHeight: 300)
    }
}

struct AddAccountView: View {
    @ObservedObject var appState: AppState
    @State private var showAdvanced = false
    @State private var sessionURL = "https://api.fastmail.com/jmap/session"
    @State private var token = ""
    @State private var isLoading = false
    @State private var statusMessage: String?
    @State private var errorMessage: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            Text("Add Account")
                .font(.title2)
                .bold()

            // Primary: OAuth
            VStack(spacing: 12) {
                Button(action: { Task { await oauthLogin() } }) {
                    HStack {
                        if isLoading && !showAdvanced {
                            ProgressView()
                                .controlSize(.small)
                                .padding(.trailing, 4)
                        }
                        Image(systemName: "person.badge.key")
                        Text("Sign in with Fastmail")
                    }
                    .frame(maxWidth: .infinity)
                    .padding(.vertical, 8)
                }
                .buttonStyle(.borderedProminent)
                .disabled(isLoading)

                if let status = statusMessage {
                    Text(status)
                        .font(.caption)
                        .foregroundColor(.secondary)
                }
            }

            // Advanced: manual token
            DisclosureGroup("Advanced: Use App Password", isExpanded: $showAdvanced) {
                VStack(alignment: .leading, spacing: 8) {
                    TextField("Session URL", text: $sessionURL)
                        .textFieldStyle(.roundedBorder)

                    SecureField("API Token", text: $token)
                        .textFieldStyle(.roundedBorder)

                    Button("Add with Token") {
                        Task { await manualLogin() }
                    }
                    .disabled(token.isEmpty || isLoading)
                }
                .padding(.top, 4)
            }

            if let error = errorMessage {
                Text(error)
                    .foregroundColor(.red)
                    .font(.caption)
            }

            HStack {
                Spacer()
                Button("Cancel") {
                    appState.showingAddAccount = false
                }
                .keyboardShortcut(.cancelAction)
            }
        }
        .padding()
        .frame(width: 420)
    }

    private func oauthLogin() async {
        isLoading = true
        errorMessage = nil
        statusMessage = "Discovering OAuth endpoints..."

        do {
            let (sessionUrl, metadata) = try await oauthDiscover()

            // Pick a port for callback
            let port = UInt16.random(in: 49152...65000)
            let redirectURI = "http://127.0.0.1:\(port)/callback"

            guard let regEndpoint = metadata.registrationEndpoint else {
                throw JmapError.serverError("oauth", "Server does not support dynamic client registration")
            }

            statusMessage = "Registering client..."
            let registration = try await oauthRegisterClient(
                registrationEndpoint: regEndpoint, redirectURI: redirectURI)

            let pkce = PKCEChallenge()
            let state = UUID().uuidString

            guard let authURL = oauthAuthorizationURL(
                authorizationEndpoint: metadata.authorizationEndpoint,
                clientId: registration.clientId,
                redirectURI: redirectURI,
                codeChallenge: pkce.codeChallenge,
                state: state
            ) else {
                throw JmapError.serverError("oauth", "Failed to build authorization URL")
            }

            statusMessage = "Waiting for browser authentication..."

            // Start callback server and open browser
            let code = try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<String, Error>) in
                startOAuthCallbackServer(port: port, expectedState: state, continuation: continuation)

                #if canImport(AppKit)
                NSWorkspace.shared.open(authURL)
                #endif
            }

            statusMessage = "Exchanging code for tokens..."
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

            statusMessage = "Connecting to account..."
            try await appState.addAccountWithOAuth(
                sessionURL: sessionUrl, credential: credential)

            appState.showingAddAccount = false
        } catch {
            errorMessage = String(describing: error)
            statusMessage = nil
        }
        isLoading = false
    }

    private func manualLogin() async {
        isLoading = true
        errorMessage = nil
        do {
            try await appState.addAccount(sessionURL: sessionURL, token: token)
            appState.showingAddAccount = false
        } catch {
            errorMessage = error.localizedDescription
        }
        isLoading = false
    }
}

/// Minimal HTTP server for OAuth callback (same approach as CLI).
private func startOAuthCallbackServer(
    port: UInt16, expectedState: String,
    continuation: CheckedContinuation<String, Error>
) {
    Thread.detachNewThread {
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
            continuation.resume(throwing: JmapError.serverError("oauth", "Failed to bind to port \(port)"))
            return
        }

        listen(serverSocket, 1)

        var timeout = timeval(tv_sec: 300, tv_usec: 0)
        setsockopt(serverSocket, SOL_SOCKET, SO_RCVTIMEO, &timeout, socklen_t(MemoryLayout<timeval>.size))

        let clientSocket = accept(serverSocket, nil, nil)
        guard clientSocket >= 0 else {
            close(serverSocket)
            continuation.resume(throwing: JmapError.serverError("oauth", "Timeout waiting for callback"))
            return
        }

        var buffer = [UInt8](repeating: 0, count: 4096)
        let bytesRead = recv(clientSocket, &buffer, buffer.count, 0)
        let requestStr = bytesRead > 0 ? String(bytes: buffer[..<bytesRead], encoding: .utf8) ?? "" : ""

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

        let responseBody: String
        if code != nil && receivedState == expectedState {
            responseBody = """
            <html><body style="font-family:system-ui;text-align:center;padding:60px">
            <h1>Authentication successful!</h1>
            <p>You can close this tab and return to the app.</p>
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

        guard let code = code else {
            continuation.resume(throwing: JmapError.serverError("oauth", "No authorization code received"))
            return
        }
        guard receivedState == expectedState else {
            continuation.resume(throwing: JmapError.serverError("oauth", "State mismatch"))
            return
        }

        continuation.resume(returning: code)
    }
}

// Shared content view for iOS
struct ContentView: View {
    @ObservedObject var appState: AppState

    var body: some View {
        NavigationView {
            SettingsView(appState: appState)
                .navigationTitle("Fastmail Files")
        }
    }
}
