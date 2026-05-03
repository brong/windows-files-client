import SwiftUI
import FileProvider
import JmapClient
#if canImport(AppKit)
import AppKit
#endif

private enum ConfirmAction: Identifiable {
    case removeLogin(String)
    case removeAccount(String, String)
    case cleanAccount(String, String)

    var id: String {
        switch self {
        case .removeLogin(let id): return "removeLogin:\(id)"
        case .removeAccount(let l, let a): return "removeAccount:\(l):\(a)"
        case .cleanAccount(let l, let a): return "cleanAccount:\(l):\(a)"
        }
    }

    var title: String {
        switch self {
        case .removeLogin: return "Remove Login?"
        case .removeAccount: return "Remove Account?"
        case .cleanAccount: return "Reset Cache?"
        }
    }

    var message: String {
        switch self {
        case .removeLogin: return "This will sign you out and remove all accounts for this login. No server data will be deleted."
        case .removeAccount: return "This account will be removed from sync. No server data will be deleted. You can re-add it from the login later."
        case .cleanAccount: return "This will delete the local metadata cache and re-download it from the server. No server data will be deleted."
        }
    }

    var buttonLabel: String {
        switch self {
        case .removeLogin: return "Remove Login"
        case .removeAccount: return "Remove Account"
        case .cleanAccount: return "Reset Cache"
        }
    }
}

struct SettingsView: View {
    @ObservedObject var appState: AppState
    @State private var orphanedDomains: [NSFileProviderDomain] = []
    @State private var confirmAction: ConfirmAction?

    var body: some View {
        VStack(spacing: 0) {
            // Scrollable content
            ScrollView {
                VStack(alignment: .leading, spacing: 12) {
                    Text("Fastmail Files")
                        .font(.title2)
                        .bold()

                    if appState.logins.isEmpty && orphanedDomains.isEmpty {
                        Text("No accounts configured. Add a login to start syncing files.")
                            .foregroundColor(.secondary)
                            .frame(maxWidth: .infinity, alignment: .center)
                            .padding()
                    } else {
                        ForEach(appState.logins) { login in
                            loginHeader(login: login)
                            ForEach(login.accounts) { acct in
                                accountRow(login: login, account: acct)
                                    .padding(.leading, 24)
                            }
                            Divider()
                        }

                        if !orphanedDomains.isEmpty {
                            Text("Orphaned (no longer in config)")
                                .font(.caption)
                                .foregroundColor(.secondary)
                                .textCase(.uppercase)
                            ForEach(orphanedDomains, id: \.identifier.rawValue) { domain in
                                HStack {
                                    VStack(alignment: .leading) {
                                        Text(domain.displayName)
                                            .foregroundColor(.orange)
                                        Text(domain.identifier.rawValue)
                                            .font(.caption)
                                            .foregroundColor(.secondary)
                                    }
                                    Spacer()
                                    Button("Remove") {
                                        Task {
                                            try? await NSFileProviderManager.remove(domain)
                                            await refreshOrphanedDomains()
                                        }
                                    }
                                    .foregroundColor(.red)
                                }
                                .padding(.leading, 24)
                            }
                        }
                    }

                    // Activity section
                    if !appState.logins.isEmpty {
                        Divider()
                        ActivityView(appState: appState)
                    }
                }
                .padding()
                .frame(maxWidth: .infinity, alignment: .leading)
            }

            // Fixed bottom bar
            Divider()
            HStack {
                Button("Add Login...") {
                    appState.showingAddAccount = true
                }
                .sheet(isPresented: $appState.showingAddAccount) {
                    AddAccountView(appState: appState)
                }

                Spacer()

                if !appState.logins.isEmpty || !orphanedDomains.isEmpty {
                    Button("Remove All") {
                        Task {
                            await appState.removeAll()
                            await refreshOrphanedDomains()
                        }
                    }
                    .foregroundColor(.red)
                }
            }
            .padding()
        }
        .frame(minWidth: 500, minHeight: 450)
        .task {
            await refreshOrphanedDomains()
            appState.reloadExtensionStatuses()
        }
        .alert(item: $confirmAction) { action in
            Alert(
                title: Text(action.title),
                message: Text(action.message),
                primaryButton: .destructive(Text(action.buttonLabel)) {
                    Task {
                        switch action {
                        case .removeLogin(let loginId):
                            await appState.removeLogin(loginId)
                        case .removeAccount(let loginId, let accountId):
                            await appState.removeAccount(loginId: loginId, accountId: accountId)
                        case .cleanAccount(let loginId, let accountId):
                            await appState.cleanAccount(loginId: loginId, accountId: accountId)
                        }
                    }
                },
                secondaryButton: .cancel()
            )
        }
    }

    // MARK: - Login Header

    private func loginHeader(login: LoginInfo) -> some View {
        HStack {
            Image(systemName: connectionIcon(login.connectionStatus))
                .foregroundColor(connectionColor(login.connectionStatus))
            VStack(alignment: .leading) {
                Text(login.displayLabel)
                    .font(.headline)
                if login.connectionStatus == .authFailed {
                    Text("Authentication failed — click Reauthenticate")
                        .font(.caption)
                        .foregroundColor(.red)
                } else if login.connectionStatus == .networkError {
                    Text("Cannot reach server")
                        .font(.caption)
                        .foregroundColor(.orange)
                } else if login.connectionStatus == .connecting {
                    Text("Connecting...")
                        .font(.caption)
                        .foregroundColor(.secondary)
                }
            }
            Spacer()
            Button("Reauthenticate") {
                Task { await reauthenticate(login: login) }
            }
            .font(.caption)
            Button("Remove Login") {
                confirmAction = .removeLogin(login.loginId)
            }
            .font(.caption)
            .foregroundColor(.red)
        }
    }

    private func connectionIcon(_ status: ConnectionStatus) -> String {
        switch status {
        case .connected: return "person.circle.fill"
        case .connecting: return "person.circle"
        case .authFailed: return "exclamationmark.triangle.fill"
        case .networkError: return "wifi.slash"
        case .unknown: return "person.circle"
        }
    }

    private func connectionColor(_ status: ConnectionStatus) -> Color {
        switch status {
        case .connected: return .green
        case .connecting: return .gray
        case .authFailed: return .red
        case .networkError: return .orange
        case .unknown: return .gray
        }
    }

    @State private var isReauthenticating = false

    private func reauthenticate(login: LoginInfo) async {
        guard login.authType == .oauth, !isReauthenticating else { return }
        isReauthenticating = true
        defer { isReauthenticating = false }

        do {
            let (sessionUrl, metadata) = try await oauthDiscover()

            let port = UInt16.random(in: 49152...65000)
            let redirectURI = "http://127.0.0.1:\(port)/callback"

            guard let regEndpoint = metadata.registrationEndpoint else { return }
            let registration = try await oauthRegisterClient(
                registrationEndpoint: regEndpoint, redirectURI: redirectURI)

            let pkce = PKCEChallenge()
            let state = UUID().uuidString

            guard let authURL = oauthAuthorizationURL(
                authorizationEndpoint: metadata.authorizationEndpoint,
                clientId: registration.clientId,
                redirectURI: redirectURI,
                codeChallenge: pkce.codeChallenge,
                state: state,
                resource: sessionUrl
            ) else { return }

            let code = try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<String, Error>) in
                startOAuthCallbackServer(port: port, expectedState: state, continuation: continuation)
                #if canImport(AppKit)
                NSWorkspace.shared.open(authURL)
                #endif
            }

            #if canImport(AppKit)
            NSApp.activate(ignoringOtherApps: true)
            #endif

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

            // Update credential for all accounts in this login
            await appState.updateLoginCredential(loginId: login.loginId, credential: credential)
        } catch {
            // Could show error in UI
        }
    }

    // MARK: - Account Row

    private func accountRow(login: LoginInfo, account: AccountInfo) -> some View {
        let status = appState.liveStatus(for: account.accountId)
        return HStack {
            Image(systemName: account.isSynced ? "checkmark.circle.fill" : "circle")
                .foregroundColor(account.isSynced ? statusColor(status) : .gray)

            VStack(alignment: .leading, spacing: 2) {
                Text(account.displayName.isEmpty ? account.accountId : account.displayName)
                Text(account.isSynced ? statusText(status) : "Not synced")
                    .font(.caption)
                    .foregroundColor(.secondary)
                if let quota = appState.quotaInfo[account.accountId] {
                    quotaBar(quota)
                }
            }

            Spacer()

            if account.isSynced {
                Button("Open") {
                    openInFinder(accountId: account.accountId)
                }
                .font(.caption)

                Button("Sync") {
                    appState.syncNow(account.accountId)
                }
                .font(.caption)

                Button("Clean") {
                    confirmAction = .cleanAccount(login.loginId, account.accountId)
                }
                .font(.caption)
                .foregroundColor(.orange)

                Button("Remove") {
                    confirmAction = .removeAccount(login.loginId, account.accountId)
                }
                .font(.caption)
                .foregroundColor(.red)
            } else {
                Button("Enable") {
                    Task { await appState.enableAccount(loginId: login.loginId, accountId: account.accountId) }
                }
                .font(.caption)
                .foregroundColor(.blue)
            }
        }
        .onAppear {
            if account.isSynced { appState.refreshQuota(for: account.accountId) }
        }
    }

    // MARK: - Helpers

    #if os(macOS)
    private func openInFinder(accountId: String) {
        let domain = NSFileProviderDomain(
            identifier: NSFileProviderDomainIdentifier(rawValue: accountId),
            displayName: "")
        if let manager = NSFileProviderManager(for: domain) {
            manager.getUserVisibleURL(for: .rootContainer) { url, error in
                if let url = url {
                    DispatchQueue.main.async {
                        NSWorkspace.shared.activateFileViewerSelecting([url])
                    }
                }
            }
        }
    }
    #endif

    @ViewBuilder
    private func quotaBar(_ quota: QuotaInfo) -> some View {
        let usedStr = ByteCountFormatter.string(fromByteCount: quota.used, countStyle: .file)
        if let limit = quota.limit, let fraction = quota.usedFraction {
            let limitStr = ByteCountFormatter.string(fromByteCount: limit, countStyle: .file)
            VStack(alignment: .leading, spacing: 1) {
                ProgressView(value: min(fraction, 1.0))
                    .progressViewStyle(.linear)
                    .tint(fraction > 0.9 ? .red : fraction > 0.75 ? .orange : .accentColor)
                Text("\(usedStr) of \(limitStr)")
                    .font(.caption2)
                    .foregroundColor(fraction > 0.9 ? .red : .secondary)
            }
        } else {
            Text("\(usedStr) used")
                .font(.caption2)
                .foregroundColor(.secondary)
        }
    }

    private func statusColor(_ status: SyncStatus) -> Color {
        switch status {
        case .idle: return .green
        case .syncing: return .blue
        case .error: return .red
        case .offline: return .gray
        case .paused: return .orange
        case .notSynced: return .gray
        }
    }

    private func statusText(_ status: SyncStatus) -> String {
        switch status {
        case .idle: return "Up to date"
        case .syncing: return "Syncing..."
        case .error: return "Error"
        case .offline: return "Offline"
        case .paused: return "Paused"
        case .notSynced: return "Not synced"
        }
    }

    private func refreshOrphanedDomains() async {
        let domains = await appState.listDomains()
        let knownIds = Set(appState.logins.flatMap { $0.accounts.map { $0.accountId } })
        orphanedDomains = domains.filter { !knownIds.contains($0.identifier.rawValue) }
    }
}

// MARK: - Add Account Flow

/// Discovered account info for the picker.
struct DiscoveredAccount: Identifiable {
    let accountId: String
    let name: String
    let isPrimary: Bool
    var enabled: Bool

    var id: String { accountId }
}

struct AddAccountView: View {
    @ObservedObject var appState: AppState
    @State private var email = ""
    @State private var showAdvanced = false
    @State private var sessionURL = "https://api.fastmail.com/jmap/session"
    @State private var token = ""
    @State private var isLoading = false
    @State private var statusMessage: String?
    @State private var errorMessage: String?

    // OAuth fallback state (shown after PACC discovery fails)
    @State private var showOAuthFallback = false
    @State private var showCustomOAuth = false
    @State private var customIssuer = "https://api.fastmail.com"
    @State private var customSessionURL = "https://api.fastmail.com/jmap/session"

    // Account picker state
    @State private var discoveredAccounts: [DiscoveredAccount] = []
    @State private var pendingCredential: OAuthCredential?
    @State private var pendingSessionURL: String?
    @State private var pendingLoginId: String?
    @State private var showAccountPicker = false

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            if showAccountPicker {
                accountPickerView
            } else {
                loginView
            }
        }
        .padding()
        .frame(width: 420)
    }

    // MARK: - Login View

    private var loginView: some View {
        VStack(alignment: .leading, spacing: 16) {
            Text("Add Login")
                .font(.title2)
                .bold()

            VStack(alignment: .leading, spacing: 8) {
                TextField("Email address", text: $email)
                    .textFieldStyle(.roundedBorder)
                    .modifier(EmailContentTypeModifier())
                    #if os(macOS)
                    .onSubmit { Task { await oauthLogin() } }
                    #endif

                Button(action: { Task { await oauthLogin() } }) {
                    HStack {
                        if isLoading && !showAdvanced {
                            ProgressView()
                                .controlSize(.small)
                                .padding(.trailing, 4)
                        }
                        Image(systemName: "person.badge.key")
                        Text("Continue with OAuth")
                    }
                    .frame(maxWidth: .infinity)
                    .padding(.vertical, 8)
                }
                .buttonStyle(.borderedProminent)
                .disabled(isLoading || email.isEmpty)

                if let status = statusMessage {
                    Text(status)
                        .font(.caption)
                        .foregroundColor(.secondary)
                }
            }

            if let error = errorMessage {
                Text(error)
                    .foregroundColor(.red)
                    .font(.caption)
            }

            if showOAuthFallback {
                Divider()
                VStack(alignment: .leading, spacing: 8) {
                    Text("Sign in with a known server instead:")
                        .font(.caption)
                        .foregroundColor(.secondary)
                    Button(action: { Task { await oauthLoginWithFastmail() } }) {
                        HStack {
                            if isLoading && !showAdvanced && !showCustomOAuth {
                                ProgressView().controlSize(.small).padding(.trailing, 4)
                            }
                            Image(systemName: "person.badge.key")
                            Text("Use Fastmail OAuth")
                        }
                        .frame(maxWidth: .infinity)
                        .padding(.vertical, 6)
                    }
                    .buttonStyle(.bordered)
                    .disabled(isLoading)

                    DisclosureGroup("Custom OAuth server", isExpanded: $showCustomOAuth) {
                        VStack(alignment: .leading, spacing: 6) {
                            TextField("OAuth issuer URL", text: $customIssuer)
                                .textFieldStyle(.roundedBorder)
                            TextField("JMAP session URL", text: $customSessionURL)
                                .textFieldStyle(.roundedBorder)
                            Button(action: { Task { await oauthLoginWithCustomIssuer() } }) {
                                HStack {
                                    if isLoading && showCustomOAuth {
                                        ProgressView().controlSize(.small).padding(.trailing, 4)
                                    }
                                    Text("Continue")
                                }
                                .frame(maxWidth: .infinity)
                                .padding(.vertical, 4)
                            }
                            .buttonStyle(.bordered)
                            .disabled(isLoading || customIssuer.isEmpty || customSessionURL.isEmpty)
                        }
                        .padding(.top, 4)
                    }
                }
            }

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

            HStack {
                Spacer()
                Button("Cancel") {
                    appState.showingAddAccount = false
                }
                .keyboardShortcut(.cancelAction)
            }
        }
    }

    // MARK: - Account Picker View

    private var accountPickerView: some View {
        VStack(alignment: .leading, spacing: 16) {
            Text("Select Accounts")
                .font(.title2)
                .bold()

            Text("Choose which accounts to sync:")
                .foregroundColor(.secondary)

            List {
                ForEach($discoveredAccounts) { $account in
                    Toggle(isOn: $account.enabled) {
                        HStack {
                            Text(account.name.isEmpty ? account.accountId : account.name)
                            if account.isPrimary {
                                Text("(primary)")
                                    .font(.caption)
                                    .foregroundColor(.secondary)
                            }
                        }
                    }
                }
            }
            .frame(minHeight: 100)

            if let error = errorMessage {
                Text(error)
                    .foregroundColor(.red)
                    .font(.caption)
            }

            HStack {
                Button("Back") {
                    showAccountPicker = false
                    errorMessage = nil
                }
                Spacer()
                Button("Cancel") {
                    appState.showingAddAccount = false
                }
                Button("Add Selected") {
                    Task { await addSelectedAccounts() }
                }
                .buttonStyle(.borderedProminent)
                .disabled(isLoading || !discoveredAccounts.contains(where: { $0.enabled }))
            }
        }
    }

    // MARK: - OAuth Login

    private func oauthLogin() async {
        isLoading = true
        errorMessage = nil
        showOAuthFallback = false
        statusMessage = "Discovering OAuth endpoints…"

        do {
            let (sessionUrl, metadata) = try await PaccDiscovery.discover(email: email)
            try await performOAuth(sessionUrl: sessionUrl, metadata: metadata)
        } catch {
            let urlError = error as? URLError
            switch urlError?.code {
            case .serverCertificateUntrusted, .serverCertificateHasUnknownRoot,
                 .serverCertificateNotYetValid, .serverCertificateHasBadDate,
                 .secureConnectionFailed:
                let domain = domainFrom(email: email) ?? "your domain"
                errorMessage = "TLS error reaching ua-auto-config.\(domain) — the server's certificate doesn't cover this subdomain."
                showOAuthFallback = true
            case .cannotFindHost, .cannotConnectToHost, .dnsLookupFailed:
                let domain = domainFrom(email: email) ?? email
                errorMessage = "No discovery server found for \(domain)."
                showOAuthFallback = true
            case .notConnectedToInternet, .networkConnectionLost:
                errorMessage = "No internet connection."
            default:
                errorMessage = error.localizedDescription
                // Also offer fallback for other PACC errors (e.g. missing JMAP config)
                if error is PaccError { showOAuthFallback = true }
            }
            statusMessage = nil
        }
        isLoading = false
    }

    private func oauthLoginWithFastmail() async {
        isLoading = true
        errorMessage = nil
        statusMessage = "Connecting to Fastmail…"
        do {
            let (sessionUrl, metadata) = try await PaccDiscovery.discover(email: "discover@fastmail.com")
            try await performOAuth(sessionUrl: sessionUrl, metadata: metadata)
        } catch {
            errorMessage = error.localizedDescription
            statusMessage = nil
        }
        isLoading = false
    }

    private func oauthLoginWithCustomIssuer() async {
        isLoading = true
        errorMessage = nil
        statusMessage = "Fetching OAuth metadata…"
        do {
            let (sessionUrl, metadata) = try await PaccDiscovery.discoverFromIssuer(
                issuer: customIssuer, sessionUrl: customSessionURL)
            try await performOAuth(sessionUrl: sessionUrl, metadata: metadata)
        } catch {
            errorMessage = error.localizedDescription
            statusMessage = nil
        }
        isLoading = false
    }

    private func performOAuth(sessionUrl: String, metadata: OAuthServerMetadata) async throws {
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
            state: state,
            resource: sessionUrl
        ) else {
            throw JmapError.serverError("oauth", "Failed to build authorization URL")
        }

        statusMessage = "Waiting for browser authentication..."

        let code = try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<String, Error>) in
            startOAuthCallbackServer(port: port, expectedState: state, continuation: continuation)
            #if canImport(AppKit)
            NSWorkspace.shared.open(authURL)
            #endif
        }

        #if canImport(AppKit)
        NSApp.activate(ignoringOtherApps: true)
        #endif

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

        statusMessage = "Discovering accounts..."
        let tokenProvider = OAuthTokenProvider(credential: credential)
        let sessionManager = SessionManager(
            sessionURL: URL(string: sessionUrl)!, tokenProvider: tokenProvider)
        let session = try await sessionManager.session()

        let fileNodeAccounts = session.fileNodeAccounts()
        if fileNodeAccounts.isEmpty {
            throw JmapError.noAccountId
        }

        let loginId = fileNodeAccounts.first(where: { $0.isPrimary })?.name
            ?? fileNodeAccounts.first?.name ?? "unknown"

        discoveredAccounts = fileNodeAccounts.map { acct in
            DiscoveredAccount(accountId: acct.accountId, name: acct.name,
                              isPrimary: acct.isPrimary, enabled: true)
        }
        pendingCredential = credential
        pendingSessionURL = sessionUrl
        pendingLoginId = loginId
        showAccountPicker = true
        statusMessage = nil
    }

    private func domainFrom(email: String) -> String? {
        email.split(separator: "@").last.map(String.init)
    }

    // MARK: - Manual Login

    private func manualLogin() async {
        isLoading = true
        errorMessage = nil
        do {
            let tokenProvider = StaticTokenProvider(token: token)
            let sessionManager = SessionManager(
                sessionURL: URL(string: sessionURL)!, tokenProvider: tokenProvider)
            let session = try await sessionManager.session()

            let fileNodeAccounts = session.fileNodeAccounts()
            if fileNodeAccounts.isEmpty {
                throw JmapError.noAccountId
            }

            let loginId = fileNodeAccounts.first(where: { $0.isPrimary })?.name
                ?? fileNodeAccounts.first?.name ?? "unknown"

            discoveredAccounts = fileNodeAccounts.map { acct in
                DiscoveredAccount(accountId: acct.accountId, name: acct.name,
                                  isPrimary: acct.isPrimary, enabled: true)
            }
            pendingCredential = nil
            pendingSessionURL = sessionURL
            pendingLoginId = loginId
            showAccountPicker = true
        } catch {
            errorMessage = String(describing: error)
        }
        isLoading = false
    }

    // MARK: - Add Selected Accounts

    private func addSelectedAccounts() async {
        isLoading = true
        errorMessage = nil

        guard let sessionUrl = pendingSessionURL, let loginId = pendingLoginId else { return }
        let selectedIds = Set(discoveredAccounts.filter { $0.enabled }.map { $0.accountId })
        let allAccounts = discoveredAccounts.map { ($0.accountId, $0.name, $0.isPrimary) }

        do {
            if let credential = pendingCredential {
                try await appState.addLogin(
                    loginId: loginId, sessionURL: sessionUrl, credential: credential,
                    discoveredAccounts: allAccounts, selectedAccountIds: selectedIds)
            } else {
                try await appState.addLoginWithToken(
                    loginId: loginId, sessionURL: sessionUrl, token: token,
                    discoveredAccounts: allAccounts, selectedAccountIds: selectedIds)
            }
            appState.showingAddAccount = false
        } catch {
            errorMessage = String(describing: error)
        }
        isLoading = false
    }
}

// MARK: - OAuth Callback Server

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

// MARK: - Activity View

struct ActivityView: View {
    @ObservedObject var appState: AppState

    var body: some View {
        let hints = appState.activeOperationHints
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Text("Activity")
                    .font(.headline)
                Spacer()
                if !hints.isEmpty {
                    Text("\(hints.count) in flight")
                        .font(.caption)
                        .foregroundColor(.blue)
                }
            }

            if hints.isEmpty {
                Text("No active operations")
                    .font(.caption)
                    .foregroundColor(.secondary)
                    .frame(maxWidth: .infinity, alignment: .center)
                    .padding(.vertical, 4)
            } else {
                ScrollView {
                    VStack(alignment: .leading, spacing: 4) {
                        ForEach(hints.prefix(10)) { hint in
                            hintRow(hint)
                        }
                        if hints.count > 10 {
                            Text("+ \(hints.count - 10) more…")
                                .font(.caption)
                                .foregroundColor(.secondary)
                        }
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)
                }
                .frame(height: 150)
            }
        }
    }

    private func hintRow(_ hint: ExtensionStatus.OperationHint) -> some View {
        HStack(spacing: 8) {
            Image(systemName: iconForVerb(hint.actionVerb))
                .foregroundColor(.blue)
                .frame(width: 16)
            VStack(alignment: .leading, spacing: 2) {
                Text(hint.fileName)
                    .font(.caption)
                    .lineLimit(1)
                    .truncationMode(.middle)
                Text(hint.actionVerb)
                    .font(.caption2)
                    .foregroundColor(.secondary)
            }
        }
        .id(hint.id)
    }

    private func iconForVerb(_ verb: String) -> String {
        switch verb {
        case "Uploading":   return "arrow.up.circle"
        case "Downloading": return "arrow.down.circle"
        case "Deleting":    return "trash"
        default:            return "arrow.triangle.2.circlepath"
        }
    }
}

// Shared content view for iOS
// textContentType(.emailAddress) requires macOS 14+; no-op on 13.
private struct EmailContentTypeModifier: ViewModifier {
    func body(content: Content) -> some View {
        if #available(macOS 14.0, *) {
            content.textContentType(.emailAddress)
        } else {
            content
        }
    }
}

struct ContentView: View {
    @ObservedObject var appState: AppState

    var body: some View {
        NavigationView {
            SettingsView(appState: appState)
                .navigationTitle("Fastmail Files")
        }
    }
}
