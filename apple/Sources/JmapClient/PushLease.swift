import Foundation

// BSD flock(2): the lock is owned by the open file description and released
// automatically when the fd is closed or the process exits — exactly the
// semantics we want for electing a single SSE-push owner across the per-account
// extension processes of one login. Swift imports `flock` as the struct type
// (used with fcntl), so reach the syscall via @_silgen_name (same trick as OAuth).
@_silgen_name("flock")
private func _flock(_ fd: Int32, _ operation: Int32) -> Int32

/// A cross-process exclusive lease backed by `flock(2)` on a shared file.
///
/// Only one holder (across processes) can own the lease at a time. The lease is
/// released explicitly via `release()` or implicitly when the holding process
/// exits — so if the owner crashes, another process can take over. Used so that
/// only one of a login's per-account FileProvider extension instances opens the
/// (session-level) SSE push connection.
public final class PushLease: @unchecked Sendable {
    private let path: String
    private var fd: Int32 = -1
    private let lock = NSLock()

    public init(path: String) {
        self.path = path
    }

    /// Try to take the lease without blocking. Returns true if this instance now
    /// holds it (including if it already did); false if another holder owns it.
    public func tryAcquire() -> Bool {
        lock.lock()
        defer { lock.unlock() }
        if fd >= 0 { return true }  // already held by this instance
        let f = open(path, O_RDWR | O_CREAT, 0o600)
        guard f >= 0 else { return false }
        if _flock(f, LOCK_EX | LOCK_NB) == 0 {
            fd = f
            return true
        }
        close(f)
        return false
    }

    /// Release the lease if held.
    public func release() {
        lock.lock()
        defer { lock.unlock() }
        guard fd >= 0 else { return }
        _ = _flock(fd, LOCK_UN)
        close(fd)
        fd = -1
    }

    deinit { release() }
}
