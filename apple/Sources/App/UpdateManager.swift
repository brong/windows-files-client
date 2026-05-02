// Automatic update support via Sparkle 2.
//
// To enable:
//   1. Xcode → File → Add Package → https://github.com/sparkle-project/Sparkle (≥ 2.0)
//      Add SPARKle to the app target only (not the FileProvider extension).
//   2. Add to Info.plist:
//        SUFeedURL = https://files.fastmail.com/update/macos/appcast.xml
//        SUPublicEDKey = <base64 EdDSA public key from generate_keys>
//   3. Generate signing keys:  .build/checkouts/Sparkle/.../bin/generate_keys
//      Put the private key in your CI keychain; ship only the public key in Info.plist.
//
// At runtime: if Sparkle is linked, UpdateManager starts the standard updater and
// exposes checkForUpdates() for the menu bar item.  If Sparkle is absent (e.g. in
// development builds without the package resolved), everything compiles and runs fine —
// the menu item just does nothing.

import Foundation

#if canImport(Sparkle)
import Sparkle

@MainActor
final class UpdateManager: ObservableObject {
    private let updaterController: SPUStandardUpdaterController

    init() {
        updaterController = SPUStandardUpdaterController(
            startingUpdater: true,
            updaterDelegate: nil,
            userDriverDelegate: nil
        )
    }

    func checkForUpdates() {
        updaterController.checkForUpdates(nil)
    }
}

#else

/// Stub used when Sparkle is not linked.
@MainActor
final class UpdateManager: ObservableObject {
    init() {}
    func checkForUpdates() {}
}

#endif
