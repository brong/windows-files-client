// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "FastmailFiles",
    platforms: [
        .macOS(.v12),
        .iOS(.v16),
    ],
    products: [
        .library(name: "JmapClient", targets: ["JmapClient"]),
    ],
    targets: [
        .target(
            name: "JmapClient",
            path: "Sources/JmapClient"
        ),
        .testTarget(
            name: "JmapClientTests",
            dependencies: ["JmapClient"],
            path: "Tests/JmapClientTests"
        ),
    ]
)
