import Foundation
import Testing
@testable import JmapClient

@Test func pushLeaseIsExclusiveAcrossHolders() {
    let path = NSTemporaryDirectory() + "pushlease-\(UUID().uuidString).lock"
    let a = PushLease(path: path)
    let b = PushLease(path: path)
    defer { a.release(); b.release(); try? FileManager.default.removeItem(atPath: path) }

    #expect(a.tryAcquire() == true)    // first holder wins
    #expect(b.tryAcquire() == false)   // second is locked out while a holds it
    a.release()
    #expect(b.tryAcquire() == true)    // released → now available
}

@Test func pushLeaseReacquireByHolderIsIdempotent() {
    let path = NSTemporaryDirectory() + "pushlease-\(UUID().uuidString).lock"
    let a = PushLease(path: path)
    defer { a.release(); try? FileManager.default.removeItem(atPath: path) }

    #expect(a.tryAcquire() == true)
    #expect(a.tryAcquire() == true)    // already held — still true, no double-lock
}
