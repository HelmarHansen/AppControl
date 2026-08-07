// swift-tools-version: 5.9
import PackageDescription

let package = Package(
    name: "AppControlViewer",
    platforms: [
        // macOS 13 Ventura: Untergrenze für die verwendeten SwiftUI-APIs
        // (Window-Szenen, .onKeyPress-Alternativen) und für swift-crypto 3.
        .macOS(.v13)
    ],
    products: [
        .executable(name: "AppControlViewer", targets: ["AppControlViewer"])
    ],
    dependencies: [
        // WebRTC: SwiftPM-Distribution des offiziellen Google-WebRTC.xcframework.
        // Bringt RTCPeerConnection, RTCDataChannel und RTCMTLNSVideoView mit —
        // Letzteres rendert CVPixelBuffer direkt über Metal, ohne Umweg über den
        // CPU-Speicher. Siehe docs/02-tech-stack.md §2.4.
        .package(url: "https://github.com/stasel/WebRTC.git", from: "125.0.0"),

        // Apples Portierung der BoringSSL-Primitiven mit CryptoKit-API.
        // Gegenstück zu NSec/libsodium auf der Host-Seite — beide implementieren
        // exakt dieselben Standards, verifiziert über tools/crypto-vectors.
        .package(url: "https://github.com/apple/swift-crypto.git", from: "3.0.0"),
    ],
    targets: [
        .executableTarget(
            name: "AppControlViewer",
            dependencies: [
                .product(name: "WebRTC", package: "WebRTC"),
                .product(name: "Crypto", package: "swift-crypto"),
            ],
            path: "Sources/AppControlViewer",
            // Bewusst KEINE resources: Das Info.plist liegt unter Packaging/ und
            // gehoert zum Xcode-App-Projekt, nicht ins SwiftPM-Ressourcenbuendel.
            // SwiftPM lehnt ein Info.plist als Top-Level-Ressource ausdruecklich
            // ab - es wuerde mit dem Bundle-eigenen kollidieren.

            // ── Explizites Framework-Linking ─────────────────────────────────
            //
            // WebRTC.xcframework traegt Auto-Link-Direktiven, die auf macOS
            // unvollstaendig aufgehen: Der Linker meldet
            //   "Could not find or use auto-linked framework 'CoreAudioTypes'"
            // und bricht das Auto-Linking danach ab.
            //
            // Die Frameworks hier explizit zu nennen macht das Linken unabhaengig
            // vom Auto-Linking. Metal und CoreImage braucht
            // Video/MetalVideoRenderer.swift, VideoToolbox den Hardware-Decoder,
            // die Audio-Frameworks zieht WebRTC selbst nach - auch wenn
            // AppControl kein Audio uebertraegt.
            //
            // Was das NICHT reparieren kann: eine Klasse, die im macOS-Slice des
            // Frameworks gar nicht enthalten ist. Genau das war bei
            // RTCMTLNSVideoView der Fall - deshalb rendert AppControl selbst.
            linkerSettings: [
                .linkedFramework("Metal"),
                .linkedFramework("MetalKit"),
                .linkedFramework("CoreImage"),
                .linkedFramework("CoreMedia"),
                .linkedFramework("CoreVideo"),
                .linkedFramework("CoreGraphics"),
                .linkedFramework("VideoToolbox"),
                .linkedFramework("AVFoundation"),
                .linkedFramework("AudioToolbox"),
                .linkedFramework("CoreAudio"),
                .linkedFramework("AppKit"),
            ]
        ),
        .testTarget(
            name: "AppControlViewerTests",
            dependencies: [
                "AppControlViewer",
                .product(name: "Crypto", package: "swift-crypto"),
            ],
            path: "Tests/AppControlViewerTests",
            resources: [
                // Die gemeinsamen Testvektoren. Der Symlink im Tests-Ordner zeigt
                // auf tools/crypto-vectors/vectors.json, damit es nur eine Quelle gibt.
                .copy("vectors.json")
            ]
        ),
    ]
)
