import SwiftUI

struct SettingsView: View {
    @ObservedObject var appState: AppState
    @State private var showingAddAccount = false

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
                showingAddAccount = true
            }
            .sheet(isPresented: $showingAddAccount) {
                AddAccountView(appState: appState, isPresented: $showingAddAccount)
            }
        }
        .padding()
        .frame(minWidth: 450, minHeight: 300)
    }
}

struct AddAccountView: View {
    @ObservedObject var appState: AppState
    @Binding var isPresented: Bool
    @State private var sessionURL = "https://api.fastmail.com/jmap/session"
    @State private var token = ""
    @State private var isLoading = false
    @State private var errorMessage: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Add Account")
                .font(.title2)
                .bold()

            TextField("Session URL", text: $sessionURL)
                .textFieldStyle(.roundedBorder)

            SecureField("API Token", text: $token)
                .textFieldStyle(.roundedBorder)

            if let error = errorMessage {
                Text(error)
                    .foregroundColor(.red)
                    .font(.caption)
            }

            HStack {
                Spacer()
                Button("Cancel") {
                    isPresented = false
                }
                .keyboardShortcut(.cancelAction)

                Button("Add") {
                    Task {
                        await addAccount()
                    }
                }
                .keyboardShortcut(.defaultAction)
                .disabled(token.isEmpty || isLoading)
            }
        }
        .padding()
        .frame(width: 400)
    }

    private func addAccount() async {
        isLoading = true
        errorMessage = nil
        do {
            try await appState.addAccount(sessionURL: sessionURL, token: token)
            isPresented = false
        } catch {
            errorMessage = error.localizedDescription
        }
        isLoading = false
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
