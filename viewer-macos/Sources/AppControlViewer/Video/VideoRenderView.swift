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

    /// Zeigerform, die der Host meldet. Einer der sechs Werte aus
    /// protocol/schemas/control-messages.schema.json.
    let cursorShape: String

    /// Ob der Host überhaupt einen Zeiger anzeigt.
    let cursorVisible: Bool

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
        view.applyRemoteCursor(shape: cursorShape, visible: cursorVisible)
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

        // MARK: Zeigerform

        private var remoteCursor: NSCursor = .arrow

        /// Übernimmt die vom Host gemeldete Zeigerform.
        ///
        /// **Warum überhaupt zwei Zeiger?** Der Zeiger des Hosts ist bereits Teil
        /// des Videobildes — der Host aktiviert `IsCursorCaptureEnabled`. Der
        /// Viewer hat zusätzlich seinen eigenen, lokalen Zeiger, und der reagiert
        /// ohne Netzverzögerung. Beide zu zeigen ist gewollt: Der lokale sagt
        /// „hier bin ich jetzt", der entfernte „hier ist der Host angekommen".
        /// Bei guter Verbindung liegen sie übereinander, bei schlechter sieht man
        /// die Latenz — was ehrlicher ist, als sie zu verstecken.
        ///
        /// Was nicht sein soll: dass die beiden UNTERSCHIEDLICH aussehen. Ein
        /// Pfeil über einem Textfeld, in dem das Bild einen Textcursor zeigt,
        /// wirkt wie ein Fehler. Genau das behebt diese Methode.
        func applyRemoteCursor(shape: String, visible: Bool) {
            let resolved = visible ? Self.cursor(for: shape) : Self.invisibleCursor
            guard resolved !== remoteCursor else { return }

            remoteCursor = resolved
            window?.invalidateCursorRects(for: self)
        }

        override func resetCursorRects() {
            addCursorRect(bounds, cursor: remoteCursor)
        }

        private static func cursor(for shape: String) -> NSCursor {
            switch shape {
            case "ibeam":     return .iBeam
            case "hand":      return .pointingHand
            case "resize-ns": return .resizeUpDown
            case "resize-ew": return .resizeLeftRight

            // macOS hat keinen öffentlichen „beschäftigt"-Zeiger: Der Regenbogen
            // gehört dem System und lässt sich nicht anfordern. Ein Pfeil ist die
            // ehrlichere Wahl als ein selbstgemalter Ersatz, der nach nichts
            // Bekanntem aussieht.
            case "wait":      return .arrow

            default:          return .arrow
            }
        }

        /// Ein vollständig transparenter Zeiger.
        ///
        /// `NSCursor.hide()` wäre der naheliegende Weg und wäre falsch: Es
        /// versteckt den Zeiger ANWENDUNGSWEIT und zählt Aufrufe mit — ein
        /// verpasstes `unhide()`, und der Nutzer sitzt ohne Mauszeiger da, auch
        /// in den Menüs. Ein leeres Bild als Zeigerform wirkt nur dort, wo es
        /// gesetzt ist, und verschwindet mit der Ansicht.
        private static let invisibleCursor: NSCursor = {
            let image = NSImage(size: NSSize(width: 1, height: 1))
            image.lockFocus()
            NSColor.clear.set()
            NSRect(x: 0, y: 0, width: 1, height: 1).fill()
            image.unlockFocus()
            return NSCursor(image: image, hotSpot: .zero)
        }()

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
