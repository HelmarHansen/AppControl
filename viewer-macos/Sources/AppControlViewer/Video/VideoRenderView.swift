import AppKit
import SwiftUI
import WebRTC

/// SwiftUI-Hülle um `MetalVideoView`.
///
/// **Der Rendering-Pfad:**
/// `SRTP → WebRTC-Jitterbuffer → VideoToolbox (HW-Decode) → CVPixelBuffer → Metal`
///
/// Der Renderer bekommt den `CVPixelBuffer` des Hardware-Decoders und zeichnet
/// ihn über Metal, ohne Umweg über CPU-Speicher. Auf Apple Silicon ist das der
/// kürzeste existierende Weg von Netzwerkpaket zu Pixel.
///
/// **Warum nicht selbst dekodieren?** Man *könnte* `VTDecompressionSession` direkt
/// ansteuern. Das ergäbe Sinn, wenn man den Jitter-Buffer selbst bauen wollte —
/// solange WebRTC den Transport macht, wäre es doppelte Arbeit mit identischem
/// Ergebnis. Siehe docs/02-tech-stack.md §2.4.
struct VideoRenderView: NSViewRepresentable {
    let track: RTCVideoTrack?

    /// Meldet den tatsächlichen Bereich, in dem das Video dargestellt wird —
    /// in Fensterkoordinaten. `InputCapture` braucht das, um Mauspositionen zu
    /// normalisieren.
    let onVideoFrameChanged: (CGRect) -> Void

    func makeNSView(context: Context) -> ContainerView {
        let container = ContainerView()
        container.onFrameChanged = onVideoFrameChanged
        return container
    }

    func updateNSView(_ view: ContainerView, context: Context) {
        view.attach(track: track)
    }

    static func dismantleNSView(_ view: ContainerView, coordinator: ()) {
        // Ohne dieses Abmelden hält der Track eine Referenz auf die View und
        // der Decoder rendert weiter in ein Fenster, das es nicht mehr gibt.
        view.attach(track: nil)
    }

    /// Container, der die Video-View einbettet und ihre Geometrie meldet.
    ///
    /// Die Zwischenschicht ist nötig, weil der Renderer das Video
    /// seitenverhältnisgetreu einpasst: Bei einem 16:9-Stream in einem
    /// 4:3-Fenster entstehen oben und unten schwarze Balken. Klicks in diese
    /// Balken dürfen **nicht** als Eingaben zählen — sonst würde ein Klick auf
    /// schwarze Fläche irgendwo auf dem Host landen.
    final class ContainerView: NSView {
        private let videoView = MetalVideoView(frame: .zero)
        private var videoSize: CGSize = .zero
        private var currentTrack: RTCVideoTrack?

        var onFrameChanged: ((CGRect) -> Void)?

        override init(frame frameRect: NSRect) {
            super.init(frame: frameRect)
            wantsLayer = true
            layer?.backgroundColor = NSColor.black.cgColor

            videoView.onVideoSizeChanged = { [weak self] size in
                guard let self else { return }
                self.videoSize = size
                self.reportVideoFrame()
            }

            videoView.translatesAutoresizingMaskIntoConstraints = false
            addSubview(videoView)
            NSLayoutConstraint.activate([
                videoView.leadingAnchor.constraint(equalTo: leadingAnchor),
                videoView.trailingAnchor.constraint(equalTo: trailingAnchor),
                videoView.topAnchor.constraint(equalTo: topAnchor),
                videoView.bottomAnchor.constraint(equalTo: bottomAnchor),
            ])
        }

        @available(*, unavailable)
        required init?(coder: NSCoder) { fatalError("init(coder:) wird nicht verwendet") }

        func attach(track: RTCVideoTrack?) {
            guard track !== currentTrack else { return }
            currentTrack?.remove(videoView)
            currentTrack = track
            track?.add(videoView)
        }

        override func layout() {
            super.layout()
            reportVideoFrame()
        }

        /// Berechnet das tatsächliche Video-Rechteck innerhalb der View
        /// (`aspect fit`) und meldet es in **Fensterkoordinaten**.
        ///
        /// Die Rechnung muss exakt der Einpassung in `MetalVideoView.renderFrame`
        /// entsprechen — sie ist dieselbe Formel. Weicht sie ab, zielt die Maus
        /// systematisch daneben. tools/consistency/check.py prüft, dass beide
        /// Stellen `min(...)` auf demselben Verhältnis bilden.
        private func reportVideoFrame() {
            guard videoSize.width > 0, videoSize.height > 0,
                  bounds.width > 0, bounds.height > 0 else {
                onFrameChanged?(.zero)
                return
            }

            let viewAspect = bounds.width / bounds.height
            let videoAspect = videoSize.width / videoSize.height

            let fitted: CGRect
            if videoAspect > viewAspect {
                // Video ist breiter → Balken oben und unten
                let height = bounds.width / videoAspect
                fitted = CGRect(x: 0, y: (bounds.height - height) / 2,
                                width: bounds.width, height: height)
            } else {
                // Video ist höher → Balken links und rechts
                let width = bounds.height * videoAspect
                fitted = CGRect(x: (bounds.width - width) / 2, y: 0,
                                width: width, height: bounds.height)
            }

            onFrameChanged?(convert(fitted, to: nil))
        }
    }
}
