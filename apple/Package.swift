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
        .library(name: "FuseMount", targets: ["FuseMount"]),
        .executable(name: "fastmail-files", targets: ["FuseCLI"]),
    ],
    targets: [
        // JMAP protocol library (cross-platform)
        .target(
            name: "JmapClient",
            path: "Sources/JmapClient"
        ),
        .testTarget(
            name: "JmapClientTests",
            dependencies: ["JmapClient"],
            path: "Tests/JmapClientTests"
        ),

        // C module wrapper for libfuse-t (macOS only)
        .target(
            name: "CFuseT",
            path: "Sources/CFuseT",
            publicHeadersPath: "include",
            cSettings: [
                .define("FUSE_USE_VERSION", to: "26"),
                .define("_FILE_OFFSET_BITS", to: "64"),
                .unsafeFlags(["-I/usr/local/include"]),
            ],
            linkerSettings: [
                .unsafeFlags(["-L/usr/local/lib", "-lfuse-t",
                              "-Xlinker", "-rpath", "-Xlinker", "/usr/local/lib"]),
            ]
        ),

        // Swift FUSE filesystem backed by JmapClient
        .target(
            name: "FuseMount",
            dependencies: ["JmapClient", "CFuseT"],
            path: "Sources/FuseMount",
            swiftSettings: [
                .define("FUSE_ENABLED"),
            ]
        ),
        .testTarget(
            name: "FuseMountTests",
            dependencies: ["FuseMount", "JmapClient"],
            path: "Tests/FuseMountTests"
        ),

        // CLI tool to mount the filesystem
        .executableTarget(
            name: "FuseCLI",
            dependencies: ["FuseMount", "JmapClient"],
            path: "Sources/FuseCLI"
        ),
    ]
)
