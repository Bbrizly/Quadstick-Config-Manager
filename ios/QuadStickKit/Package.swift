// swift-tools-version: 5.9
import PackageDescription

let package = Package(
    name: "QuadStickKit",
    platforms: [.iOS(.v17), .macOS(.v14)],
    products: [.library(name: "QuadStickKit", targets: ["QuadStickKit"])],
    targets: [
        .target(name: "QuadStickKit"),
        .testTarget(name: "QuadStickKitTests", dependencies: ["QuadStickKit"]),
    ]
)
