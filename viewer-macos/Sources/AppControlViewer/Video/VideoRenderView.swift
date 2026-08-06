import AppKit
import SwiftUI
import WebRTC

/// SwiftUI-Hülle um `RTCMTLNSVideoView`.
///
/// **Der Rendering-Pfad:**
/// `SRTP → WebRTC-Jitterbuffer → VideoToolbox (HW-Decode) → CVPixelBuffer → Metal`
///
/// `RTCMTLNSVideoView` rendert `CVPixelBuffer` direkt über Metal, ohne Umweg über
/// CPU-Speicher oder Core Animation. Auf Apple Silicon ist das der kürzeste
/// existierende Weg von Netzwerkpaket zu Pixel.
///
/// **Warum nicht selbst dekodieren?** Man *könnte* `VTDecompressionSession` direkt
/// ansteuern und in einen eigenen `CAMetalLayer` rendern. Das ergäbe Sinn, wenn
/// man den Jitter-Buffer selbst bauen wollte — solange WebRTC den Transport macht,
/// wäre es doppelte Arbeit mit identischem Ergebnis. Siehe docs/02-tech-stack.md §2.4.
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

    /// Container, der die Video-View einbettet und ihre Geometrie meldet.
    ///
    /// Die Zwischenschicht ist nötig, weil `RTCMTLNSVideoView` das Video
    /// seitenverhältnisgetreu einpasst: Bei einem 16:9-Stream in einem
    /// 4:3-Fenster entstehen oben und unten schwarze Balken. Klicks in diese
    /// Balken dürfen **nicht** als Eingaben zählen — sonst würde ein Klick auf
    /// schwarze Fläche irgendwo auf dem Host landen.
    final class ContainerView: NSView, RTCVideoViewDelegate {
        private let videoView = RTCMTLNSVideoView(frame: .zero)
        private var videoSize: CGSize = .zero
        private var currentTrack: RTCVideoTrack?

        var onFrameChanged: ((CGRect) -> Void)?

        override init(frame frameRect: NSRect) {
            super.init(frame: frameRect)
            wantsLayer = true
            layer?.backgroundColor = NSColor.black.cgColor

            videoView.delegate = self
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

        // MARK: RTCVideoViewDelegate

        func videoView(_ videoView: RTCVideoRenderer, didChangeVideoSize size: CGSize) {
            videoSize = size
            DispatchQueue.main.async { [weak self] in self?.reportVideoFrame() }
        }

        override func layout() {
            super.layout()
            reportVideoFrame()
        }

        /// Berechnet das tatsächliche Video-Rechteck innerhalb der View
        /// (`aspect fit`) und meldet es in **Fensterkoordinaten**.
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
