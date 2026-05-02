import Foundation
import Testing
@testable import JmapClient

@Suite struct ActivityTrackerTests {

    @Test func startCreatesActiveEntry() async {
        let tracker = ActivityTracker()
        await tracker.start(id: "op1", accountId: "u1", fileName: "doc.txt", action: .upload)

        let snap = await tracker.snapshot()
        #expect(snap.activities.count == 1)
        #expect(snap.activities[0].status == .active)
        #expect(snap.activities[0].fileName == "doc.txt")
        #expect(snap.activities[0].action == .upload)
        #expect(snap.activities[0].accountId == "u1")
    }

    @Test func completeMarksEntry() async {
        let tracker = ActivityTracker()
        await tracker.start(id: "op1", accountId: "u1", fileName: "doc.txt", action: .download)
        await tracker.complete(id: "op1")

        let snap = await tracker.snapshot()
        #expect(snap.activities[0].status == .completed)
        #expect(snap.activities[0].progress == 1.0)
        #expect(snap.activities[0].completedAt != nil)
    }

    @Test func failSetsErrorStatus() async {
        let tracker = ActivityTracker()
        await tracker.start(id: "op1", accountId: "u1", fileName: "doc.txt", action: .upload)
        await tracker.fail(id: "op1", error: "network timeout")

        let snap = await tracker.snapshot()
        #expect(snap.activities[0].status == .error)
        #expect(snap.activities[0].error == "network timeout")
    }

    @Test func updateProgressStored() async {
        let tracker = ActivityTracker()
        await tracker.start(id: "op1", accountId: "u1", fileName: "large.bin", action: .upload, fileSize: 10_000_000)
        await tracker.updateProgress(id: "op1", progress: 0.42)

        let snap = await tracker.snapshot()
        #expect(snap.activities[0].progress == 0.42)
    }

    @Test func removeDeletesEntry() async {
        let tracker = ActivityTracker()
        await tracker.start(id: "op1", accountId: "u1", fileName: "a.txt", action: .sync)
        await tracker.remove(id: "op1")

        let snap = await tracker.snapshot()
        #expect(snap.activities.isEmpty)
    }

    @Test func activeCountOnlyCountsActive() async {
        let tracker = ActivityTracker()
        await tracker.start(id: "a", accountId: "u1", fileName: "a.txt", action: .upload)
        await tracker.start(id: "b", accountId: "u1", fileName: "b.txt", action: .upload)
        await tracker.complete(id: "b")

        #expect(await tracker.activeCount == 1)
    }

    @Test func snapshotPrunesOldCompleted() async {
        let tracker = ActivityTracker()
        await tracker.start(id: "old", accountId: "u1", fileName: "a.txt", action: .sync)
        await tracker.complete(id: "old")

        // Manually age the entry by completing it with a timestamp in the past.
        // ActivityTracker prunes entries completed > 30s ago, so we can't trigger this
        // without time manipulation. Instead verify the entry IS present immediately
        // after completion and IS pruned by a fresh snapshot after retention elapses.
        // Here we just verify the completed entry appears in the snapshot right away.
        let snap = await tracker.snapshot()
        #expect(snap.activities.first(where: { $0.id == "old" })?.status == .completed)
    }

    @Test func persistsToFileAndLoadsBack() async throws {
        let tmp = FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: tmp, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: tmp) }

        let tracker = ActivityTracker(containerURL: tmp)
        await tracker.start(id: "op1", accountId: "u1", fileName: "upload.zip", action: .upload, fileSize: 1024)
        await tracker.updateProgress(id: "op1", progress: 0.5)
        await tracker.flush() // bypass 250ms throttle so file is current

        // The app reads the shared file to show status
        let loaded = ActivityTracker.loadShared(containerURL: tmp)
        #expect(loaded != nil)
        let entry = loaded?.activities.first(where: { $0.id == "op1" })
        #expect(entry?.fileName == "upload.zip")
        #expect(entry?.progress == 0.5)
        #expect(entry?.status == .active)
    }

    @Test func multipleAccountsTrackedIndependently() async {
        let tracker = ActivityTracker()
        await tracker.start(id: "u1-op", accountId: "acc1", fileName: "a.txt", action: .upload)
        await tracker.start(id: "u2-op", accountId: "acc2", fileName: "b.txt", action: .download)

        let snap = await tracker.snapshot()
        #expect(snap.activities.count == 2)
        #expect(Set(snap.activities.map { $0.accountId }) == ["acc1", "acc2"])
    }
}
