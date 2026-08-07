import AppKit
import CoreImage
import ImageIO
import Metal
import QuartzCore
import WebRTC

/// Eigener Video-Renderer auf Basis von `CAMetalLayer`.
///
/// **Warum nicht `RTCMTLNSVideoView`?** Die Klasse ist Teil des WebRTC-ObjC-SDK,
/// aber im macOS-Slice der verbreiteten xcframework-Distributionen nicht
/// enthalten — der Linker meldet
/// `Undefined symbols: _OBJC_CLASS_$_RTCMTLNSVideoView`, während jede andere
/// WebRTC-Klasse sauber auflöst. Auf eine Klasse zu bauen, deren Vorhandensein
/// von der Build-Konfiguration des Frameworks abhängt, ist ein Wackelkontakt.
/// Dieser Renderer implementiert stattdessen `RTCVideoRenderer` selbst und
/// benutzt nur Apple-Systemframeworks.
///
/// **Der Rendering-Pfad bleibt derselbe:**
/// `SRTP → WebRTC-Jitterbuffer → VideoToolbox (HW-Decode) → CVPixelBuffer → Metal`
///
/// Der Hardware-Decoder liefert `RTCCVPixelBuffer`; dessen `CVPixelBuffer` geht
/// ohne CPU-Kopie als `CIImage` in den Metal-Kontext. Der I420-Pfad darunter ist
/// der Rückfall für Software-Decoding und kopiert einmal — er ist selten und
/// darf langsamer sein.
///
/// **Erweiterungspunkt:** Wer Farbmanagement oder Skalierungsfilter braucht,
/// setzt hier an — `CIContext` nimmt `.workingColorSpace` und CIFilter-Ketten
/// entgegen, ohne dass der Rest der Klasse sich ändert.
final class MetalVideoView: NSView, RTCVideoRenderer {

    /// Wird gerufen, sobald sich die Auflösung des eingehenden Streams ändert.
    /// Immer auf dem Main-Thread. Die Geometrie interessiert `InputCapture`:
    /// ohne Videogröße keine normalisierten Mauskoordinaten.
    var onVideoSizeChanged: ((CGSize) -> Void)?

    private let metalLayer = CAMetalLayer()
    private let device: MTLDevice?
    private let commandQueue: MTLCommandQueue?
    private let ciContext: CIContext?

    /// Rendern läuft nicht auf dem Main-Thread. `renderFrame` kommt vom
    /// Decoder-Thread; würde jedes Bild auf den Main-Thread springen, konkurriert
    /// das Video mit der UI und die Latenz steigt genau dann, wenn der Nutzer
    /// interagiert. Die serielle Queue serialisiert die Metal-Zugriffe.
    private let renderQueue = DispatchQueue(label: "de.appcontrol.viewer.render",
                                            qos: .userInteractive)

    /// Von `layout()` (Main-Thread) geschrieben, von `renderFrame` (Render-Queue)
    /// gelesen. `atomic` gibt es in Swift nicht — die Lock ist billig genug,
    /// sie wird pro Bild einmal genommen.
    private let geometryLock = NSLock()
    private var drawableSize: CGSize = .zero

    private var lastReportedSize: CGSize = .zero

    /// Wiederverwendeter Puffer für den I420-Rückfall. Ihn pro Bild neu
    /// anzulegen wäre der teuerste Teil eines ohnehin teuren Pfads.
    private var fallbackPixelBuffer: CVPixelBuffer?
    private var fallbackPixelBufferSize: CGSize = .zero

    override init(frame frameRect: NSRect) {
        device = MTLCreateSystemDefaultDevice()
        commandQueue = device?.makeCommandQueue()
        if let queue = commandQueue {
            // Der CIContext teilt sich die Command-Queue mit dem Renderer,
            // damit Filter und Präsentation im selben Command-Buffer landen.
            ciContext = CIContext(mtlCommandQueue: queue,
                                  options: [.cacheIntermediates: false,
                                            .name: "AppControlVideo"])
        } else {
            ciContext = nil
        }

        super.init(frame: frameRect)

        // Der Layer wird ueber makeBackingLayer() gestellt, nicht per Zuweisung
        // an .layer: Bei der Zuweisung ersetzt AppKit den Layer beim naechsten
        // Wechsel des Bildschirms oder der Backing-Skalierung wieder durch einen
        // eigenen. makeBackingLayer() ist der dokumentierte Weg.
        wantsLayer = true
        metalLayer.device = device
        metalLayer.pixelFormat = .bgra8Unorm
        // CIContext rendert in die Drawable-Textur; das geht nur, wenn die
        // Textur nicht ausschließlich als Framebuffer deklariert ist.
        metalLayer.framebufferOnly = false
        metalLayer.isOpaque = true
        metalLayer.backgroundColor = NSColor.black.cgColor
        // Kein implizites Animieren der Layer-Größe: Ein Video, dessen Layer
        // über 0,25 s in die neue Größe "gleitet", zeigt in dieser Zeit
        // verzerrte Bilder.
        metalLayer.actions = ["bounds": NSNull(), "position": NSNull()]

        if device == nil {
            // Kein Metal-Gerät: theoretisch möglich in VMs ohne GPU-Passthrough.
            // Das Fenster bleibt schwarz statt abzustürzen; die Steuerung
            // funktioniert weiter, und der Nutzer sieht in der Statuszeile,
            // dass die Verbindung steht.
            NSLog("AppControl: Kein Metal-Gerät verfügbar — Videoausgabe deaktiviert.")
        }
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) wird nicht verwendet") }

    override func makeBackingLayer() -> CALayer { metalLayer }

    override func viewDidChangeBackingProperties() {
        super.viewDidChangeBackingProperties()
        updateDrawableSize()
    }

    override func layout() {
        super.layout()
        updateDrawableSize()
    }

    private func updateDrawableSize() {
        let scale = window?.backingScaleFactor ?? 1.0
        metalLayer.contentsScale = scale
        let size = CGSize(width: bounds.width * scale, height: bounds.height * scale)
        metalLayer.drawableSize = size

        geometryLock.lock()
        drawableSize = size
        geometryLock.unlock()
    }

    // MARK: - RTCVideoRenderer

    /// WebRTC meldet hierüber die Auflösung des Streams — nicht die der View.
    func setSize(_ size: CGSize) {
        DispatchQueue.main.async { [weak self] in
            guard let self, size != self.lastReportedSize else { return }
            self.lastReportedSize = size
            self.onVideoSizeChanged?(size)
        }
    }

    func renderFrame(_ frame: RTCVideoFrame?) {
        guard let frame,
              let ciContext,
              let commandQueue else { return }

        renderQueue.async { [weak self] in
            guard let self else { return }

            geometryLock.lock()
            let target = drawableSize
            geometryLock.unlock()

            guard target.width >= 1, target.height >= 1 else { return }

            guard var image = self.makeImage(from: frame) else { return }
            image = image.oriented(Self.orientation(for: frame.rotation))

            // Seitenverhältnisgetreu einpassen (aspect fit). Der schwarze Rand
            // entsteht dadurch, dass wir nur in das eingepasste Rechteck
            // zeichnen und den Rest der Textur löschen.
            let source = image.extent
            guard source.width > 0, source.height > 0 else { return }
            let scale = min(target.width / source.width, target.height / source.height)
            let scaled = image
                .transformed(by: CGAffineTransform(scaleX: scale, y: scale))
            let offsetX = (target.width - source.width * scale) / 2
            let offsetY = (target.height - source.height * scale) / 2
            let positioned = scaled.transformed(
                by: CGAffineTransform(translationX: offsetX - scaled.extent.origin.x,
                                      y: offsetY - scaled.extent.origin.y))

            guard let drawable = self.metalLayer.nextDrawable(),
                  let buffer = commandQueue.makeCommandBuffer() else { return }

            // Der Hintergrund muss explizit gelöscht werden: Drawables werden
            // wiederverwendet, und ohne Löschen bleiben bei einem Wechsel des
            // Seitenverhältnisses Reste des vorigen Bildes in den Balken stehen.
            let background = CIImage(color: .black).cropped(
                to: CGRect(origin: .zero, size: target))
            let composited = positioned.composited(over: background)

            ciContext.render(composited,
                             to: drawable.texture,
                             commandBuffer: buffer,
                             bounds: CGRect(origin: .zero, size: target),
                             colorSpace: CGColorSpaceCreateDeviceRGB())

            buffer.present(drawable)
            buffer.commit()
        }
    }

    // MARK: - Pufferkonvertierung

    private func makeImage(from frame: RTCVideoFrame) -> CIImage? {
        // Regelfall: VideoToolbox hat in Hardware dekodiert, der Puffer liegt
        // bereits als CVPixelBuffer im GPU-tauglichen Speicher.
        if let native = frame.buffer as? RTCCVPixelBuffer {
            return CIImage(cvPixelBuffer: native.pixelBuffer)
        }

        // Rückfall: Software-Decoder liefert I420. Einmal kopieren, NV12 daraus
        // bauen, dann denselben Weg weiter.
        let i420 = frame.buffer.toI420()
        return copyToPixelBuffer(i420).map { CIImage(cvPixelBuffer: $0) }
    }

    /// I420 (drei Ebenen) → NV12 (zwei Ebenen, UV verschränkt).
    ///
    /// Core Image kennt kein planares I420, wohl aber NV12. Die Verschränkung
    /// ist der einzige Grund, warum hier überhaupt Arbeit anfällt.
    private func copyToPixelBuffer(_ buffer: RTCI420BufferProtocol) -> CVPixelBuffer? {
        let width = Int(buffer.width)
        let height = Int(buffer.height)
        guard width > 0, height > 0 else { return nil }

        let size = CGSize(width: width, height: height)
        if fallbackPixelBufferSize != size { fallbackPixelBuffer = nil }

        if fallbackPixelBuffer == nil {
            var created: CVPixelBuffer?
            let attributes: [CFString: Any] = [
                kCVPixelBufferIOSurfacePropertiesKey: [:] as CFDictionary,
                kCVPixelBufferMetalCompatibilityKey: true,
            ]
            let status = CVPixelBufferCreate(
                kCFAllocatorDefault, width, height,
                kCVPixelFormatType_420YpCbCr8BiPlanarFullRange,
                attributes as CFDictionary, &created)
            guard status == kCVReturnSuccess, let created else { return nil }
            fallbackPixelBuffer = created
            fallbackPixelBufferSize = size
        }

        guard let pixelBuffer = fallbackPixelBuffer else { return nil }
        CVPixelBufferLockBaseAddress(pixelBuffer, [])
        defer { CVPixelBufferUnlockBaseAddress(pixelBuffer, []) }

        // Y-Ebene: zeilenweise kopieren, weil die Zeilenlängen (strides) von
        // Quelle und Ziel unterschiedlich sein dürfen.
        if let dstY = CVPixelBufferGetBaseAddressOfPlane(pixelBuffer, 0) {
            let dstStride = CVPixelBufferGetBytesPerRowOfPlane(pixelBuffer, 0)
            let srcStride = Int(buffer.strideY)
            let src = buffer.dataY
            for row in 0..<height {
                memcpy(dstY.advanced(by: row * dstStride),
                       src.advanced(by: row * srcStride),
                       min(dstStride, srcStride))
            }
        }

        // UV-Ebene: U und V liegen in der Quelle getrennt, im Ziel abwechselnd.
        if let dstUV = CVPixelBufferGetBaseAddressOfPlane(pixelBuffer, 1) {
            let dstStride = CVPixelBufferGetBytesPerRowOfPlane(pixelBuffer, 1)
            let chromaWidth = Int(buffer.chromaWidth)
            let chromaHeight = Int(buffer.chromaHeight)
            let strideU = Int(buffer.strideU)
            let strideV = Int(buffer.strideV)
            let srcU = buffer.dataU
            let srcV = buffer.dataV
            let dst = dstUV.assumingMemoryBound(to: UInt8.self)

            for row in 0..<chromaHeight {
                let rowBase = row * dstStride
                for column in 0..<chromaWidth {
                    dst[rowBase + column * 2]     = srcU[row * strideU + column]
                    dst[rowBase + column * 2 + 1] = srcV[row * strideV + column]
                }
            }
        }

        return pixelBuffer
    }

    private static func orientation(for rotation: RTCVideoRotation) -> CGImagePropertyOrientation {
        switch rotation {
        case ._0:   return .up
        case ._90:  return .right
        case ._180: return .down
        case ._270: return .left
        @unknown default: return .up
        }
    }
}
