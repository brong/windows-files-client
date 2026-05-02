import Foundation
import Network

/// Monitors network path quality and gates background transfers.
///
/// Priority: user-initiated transfers (interactive file open) always proceed.
/// Background hydration and large uploads are deferred on expensive or constrained paths.
public actor BandwidthPolicy {
    public enum Tier: Equatable {
        case unrestricted   // WiFi or ethernet
        case expensive      // Cellular / hotspot
        case constrained    // Low Data Mode
        case offline
    }

    public private(set) var tier: Tier = .unrestricted

    private let monitor: NWPathMonitor
    private let monitorQueue = DispatchQueue(label: "com.fastmail.files.bandwidth", qos: .utility)

    public init() {
        monitor = NWPathMonitor()
    }

    public func start() {
        monitor.pathUpdateHandler = { [weak self] path in
            Task { await self?.update(path: path) }
        }
        monitor.start(queue: monitorQueue)
    }

    public func stop() {
        monitor.cancel()
    }

    private func update(path: NWPath) {
        if path.status != .satisfied {
            tier = .offline
        } else if path.isConstrained {
            tier = .constrained
        } else if path.isExpensive {
            tier = .expensive
        } else {
            tier = .unrestricted
        }
    }

    /// Interactive file-open downloads always proceed regardless of connection type.
    public var allowsInteractiveDownload: Bool { tier != .offline }

    /// Background hydration (system pre-fetching) is allowed only on unrestricted connections.
    public var allowsBackgroundDownload: Bool { tier == .unrestricted }

    /// Uploads always allowed for small files; deferred on constrained; size-gated on expensive.
    public func allowsUpload(bytes: Int) -> Bool {
        switch tier {
        case .unrestricted:
            return true
        case .expensive:
            return bytes <= 1_000_000  // 1 MB — allow saves and small files on cellular
        case .constrained, .offline:
            return false
        }
    }

    /// Override tier for testing without starting NWPathMonitor.
    func setTierForTesting(_ t: Tier) {
        tier = t
    }
}
