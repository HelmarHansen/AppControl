import AppKit
import Foundation

/// Erfasst Maus und Tastatur und übersetzt sie ins Binärprotokoll.
///
/// **Zwei Ebenen, bewusst getrennt:**
///
/// | Ebene | API | Erfasst | Berechtigung |
/// |---|---|---|---|
/// | Standard | `NSEvent.addLocalMonitorForEvents` | alles, was an unser Fenster geht | keine |
/// | Erweitert | `CGEvent.tapCreate` | zusätzlich Cmd+Tab, Cmd+Q, F-Tasten | **Bedienungshilfen** |
///
/// Der lokale Monitor ist der Standardweg und braucht keinerlei Sonderrechte — er
/// reicht für alles, was während der Sitzung in unserem Fenster passiert. Der
/// `CGEventTap` ist **opt-in** und nur dafür da, dass Tastenkombinationen, die
/// macOS sonst selbst abfängt, an den Windows-Host durchgereicht werden können.
/// Die App funktioniert ohne ihn vollständig.
///
/// Siehe docs/02-tech-stack.md §2.4.
@MainActor
final class InputCapture {

    /// Wird für jedes erfasste Event aufgerufen. Der Empfänger entscheidet, ob
    /// und über welchen Kanal gesendet wird.
    var onInput: ((EncodedInput) -> Void)?

    /// Meldet, ob der systemweite Tap aktiv ist — die UI zeigt das an.
    private(set) var eventTapActive = false

    private let encoder = InputEncoder()
    private var localMonitor: Any?
    private var flagsMonitor: Any?
    private var eventTap: CFMachPort?
    private var runLoopSource: CFRunLoopSource?

    /// Der Bereich des Fensters, in dem das Video dargestellt wird. Nur Events
    /// innerhalb dieses Rechtecks werden zu normalisierten Koordinaten.
    var videoFrame: CGRect = .zero

    /// Nur senden, wenn der Host uns die Steuerung erteilt hat. Der Host prüft
    /// das ohnehin (Gate 2), aber gar nicht erst zu senden spart Bandbreite und
    /// macht die Absicht im Code sichtbar.
    var controlEnabled = false

    /// Zuletzt gesehener Modifier-Zustand, um Änderungen zu erkennen.
    private var lastFlags: NSEvent.ModifierFlags = []

    // ── Lebenszyklus ─────────────────────────────────────────────────────────

    func start() {
        startLocalMonitor()
    }

    func stop() {
        if let monitor = localMonitor { NSEvent.removeMonitor(monitor); localMonitor = nil }
        if let monitor = flagsMonitor { NSEvent.removeMonitor(monitor); flagsMonitor = nil }
        stopEventTap()
        releaseAll()
    }

    /// Alle gehaltenen Tasten auf dem Host freigeben.
    ///
    /// Beim Fokusverlust unseres Fensters aufzurufen. Ohne diesen Aufruf bliebe
    /// eine Taste hängen, wenn der Nutzer mitten im Tastendruck zu einer anderen
    /// App wechselt — ein Zustand, der auf dem Host nur durch manuelles Drücken
    /// oder einen Neustart aufzulösen wäre.
    func releaseAll() {
        onInput?(encoder.releaseAll())
    }

    // ── Ebene 1: lokaler Monitor ─────────────────────────────────────────────

    private func startLocalMonitor() {
        let mask: NSEvent.EventTypeMask = [
            .mouseMoved, .leftMouseDragged, .rightMouseDragged, .otherMouseDragged,
            .leftMouseDown, .leftMouseUp,
            .rightMouseDown, .rightMouseUp,
            .otherMouseDown, .otherMouseUp,
            .scrollWheel,
            .keyDown, .keyUp,
        ]

        localMonitor = NSEvent.addLocalMonitorForEvents(matching: mask) { [weak self] event in
            guard let self else { return event }
            // `nil` zurückgeben verschluckt das Event, damit es nicht zusätzlich
            // lokal wirkt (etwa den Systemton bei unbehandelten Tasten auslöst).
            return self.handle(event) ? nil : event
        }

        // Modifier kommen als eigener Eventtyp, nicht als keyDown/keyUp.
        flagsMonitor = NSEvent.addLocalMonitorForEvents(matching: [.flagsChanged]) { [weak self] event in
            self?.handleFlagsChanged(event)
            return event
        }
    }

    @discardableResult
    private func handle(_ event: NSEvent) -> Bool {
        guard controlEnabled else { return false }

        switch event.type {
        case .mouseMoved, .leftMouseDragged, .rightMouseDragged, .otherMouseDragged:
            guard let point = normalize(event.locationInWindow) else { return false }
            let ts = UInt32(truncatingIfNeeded: Int(event.timestamp * 1000))
            onInput?(encoder.mouseMove(x: point.x, y: point.y, timestampMs: ts))
            return true

        case .leftMouseDown, .leftMouseUp,
             .rightMouseDown, .rightMouseUp,
             .otherMouseDown, .otherMouseUp:
            guard let point = normalize(event.locationInWindow),
                  let button = Self.button(for: event) else { return false }
            let pressed = event.type == .leftMouseDown
                       || event.type == .rightMouseDown
                       || event.type == .otherMouseDown
            onInput?(encoder.mouseButton(
                x: point.x, y: point.y, button: button, pressed: pressed,
                clickCount: UInt16(max(1, min(event.clickCount, 3)))))
            return true

        case .scrollWheel:
            guard let point = normalize(event.locationInWindow) else { return false }
            // hasPreciseScrollingDeltas unterscheidet Trackpad (Pixel) von
            // Mausrad (Rastungen). Der Host rechnet entsprechend um.
            onInput?(encoder.mouseScroll(
                x: point.x, y: point.y,
                deltaX: Float(event.scrollingDeltaX),
                deltaY: Float(event.scrollingDeltaY),
                precise: event.hasPreciseScrollingDeltas))
            return true

        case .keyDown, .keyUp:
            return handleKey(event)

        default:
            return false
        }
    }

    private func handleKey(_ event: NSEvent) -> Bool {
        guard let usage = HidKeyMap.hidUsage(forKeyCode: event.keyCode) else {
            // Keine HID-Zuordnung: als Text senden, falls druckbar. Das fängt
            // Zeichen ab, die über Option-Kombinationen oder ein IME entstehen.
            if event.type == .keyDown,
               let characters = event.charactersIgnoringModifiers,
               !characters.isEmpty,
               characters.unicodeScalars.allSatisfy({ !CharacterSet.controlCharacters.contains($0) }),
               let encoded = encoder.text(characters) {
                onInput?(encoded)
                return true
            }
            return false
        }

        onInput?(encoder.key(
            hidUsage: usage,
            pressed: event.type == .keyDown,
            modifiers: Self.modifiers(from: event.modifierFlags),
            isRepeat: event.type == .keyDown && event.isARepeat))
        return true
    }

    /// Modifier-Tasten separat behandeln: macOS meldet sie nicht als keyDown/keyUp,
    /// sondern als Zustandsänderung. Ohne diese Behandlung käme auf dem Host nie
    /// ein „Shift gedrückt" an.
    private func handleFlagsChanged(_ event: NSEvent) {
        guard controlEnabled else { return }

        let current = event.modifierFlags
        let modifierKeys: [(NSEvent.ModifierFlags, UInt16)] = [
            (.shift,   0xE1),
            (.control, 0xE0),
            (.option,  0xE2),
            (.command, 0xE3),
            (.capsLock, 0x39),
        ]

        for (flag, usage) in modifierKeys {
            let wasDown = lastFlags.contains(flag)
            let isDown = current.contains(flag)
            guard wasDown != isDown else { continue }

            onInput?(encoder.key(
                hidUsage: usage, pressed: isDown,
                modifiers: Self.modifiers(from: current), isRepeat: false))
        }

        lastFlags = current
    }

    // ── Ebene 2: systemweiter CGEventTap (opt-in) ────────────────────────────

    /// Startet den systemweiten Tap. Gibt `false` zurück, wenn die
    /// Bedienungshilfen-Berechtigung fehlt.
    ///
    /// Der Tap fängt zusätzlich Tastenkombinationen ab, die macOS sonst selbst
    /// verarbeitet (Cmd+Tab, Cmd+Q, Cmd+Leertaste). Ohne ihn erreichen diese den
    /// Windows-Host nie — mit ihm wird macOS für die Dauer der Sitzung
    /// „durchsichtig".
    @discardableResult
    func startEventTap() -> Bool {
        guard eventTap == nil else { return true }
        guard Self.hasAccessibilityPermission() else { return false }

        let mask = (1 << CGEventType.keyDown.rawValue) | (1 << CGEventType.keyUp.rawValue)

        // `refcon` transportiert self als unretained Pointer in den C-Callback.
        // Unretained ist korrekt: Der Tap wird in stop() vor der Deallokation
        // abgebaut, und ein retain würde einen Zyklus erzeugen.
        let refcon = Unmanaged.passUnretained(self).toOpaque()

        guard let tap = CGEvent.tapCreate(
            tap: .cgSessionEventTap,
            place: .headInsertEventTap,
            options: .defaultTap,
            eventsOfInterest: CGEventMask(mask),
            callback: { _, type, event, refcon in
                guard let refcon else { return Unmanaged.passUnretained(event) }
                let capture = Unmanaged<InputCapture>.fromOpaque(refcon).takeUnretainedValue()
                return capture.handleTapEvent(type: type, event: event)
            },
            userInfo: refcon
        ) else { return false }

        eventTap = tap
        runLoopSource = CFMachPortCreateRunLoopSource(kCFAllocatorDefault, tap, 0)
        CFRunLoopAddSource(CFRunLoopGetCurrent(), runLoopSource, .commonModes)
        CGEvent.tapEnable(tap: tap, enable: true)
        eventTapActive = true
        return true
    }

    func stopEventTap() {
        if let tap = eventTap { CGEvent.tapEnable(tap: tap, enable: false) }
        if let source = runLoopSource {
            CFRunLoopRemoveSource(CFRunLoopGetCurrent(), source, .commonModes)
        }
        eventTap = nil
        runLoopSource = nil
        eventTapActive = false
    }

    /// Callback des CGEventTap. Läuft **nicht** auf dem Main-Actor.
    private nonisolated func handleTapEvent(
        type: CGEventType, event: CGEvent
    ) -> Unmanaged<CGEvent>? {
        // macOS deaktiviert den Tap, wenn er zu langsam antwortet. Diesen Fall
        // müssen wir abfangen und neu aktivieren — sonst hört die Erfassung
        // stillschweigend auf zu funktionieren.
        if type == .tapDisabledByTimeout || type == .tapDisabledByUserInput {
            Task { @MainActor [weak self] in
                guard let tap = self?.eventTap else { return }
                CGEvent.tapEnable(tap: tap, enable: true)
            }
            return Unmanaged.passUnretained(event)
        }

        let keyCode = UInt16(event.getIntegerValueField(.keyboardEventKeycode))
        let isRepeat = event.getIntegerValueField(.keyboardEventAutorepeat) != 0
        let flags = event.flags

        Task { @MainActor [weak self] in
            guard let self, self.controlEnabled,
                  let usage = HidKeyMap.hidUsage(forKeyCode: keyCode) else { return }

            self.onInput?(self.encoder.key(
                hidUsage: usage,
                pressed: type == .keyDown,
                modifiers: Self.modifiers(fromCGFlags: flags),
                isRepeat: isRepeat,
                fromEventTap: true))
        }

        // Event weiterreichen statt verschlucken: Cmd+Q soll den Viewer nach wie
        // vor beenden können. Wer das Gegenteil will (macOS vollständig
        // "durchsichtig" machen), gibt hier nil zurück — dann ist allerdings auch
        // der Notausstieg aus dem Viewer weg, weshalb es nicht der Standard ist.
        return Unmanaged.passUnretained(event)
    }

    static func hasAccessibilityPermission() -> Bool {
        AXIsProcessTrusted()
    }

    /// Öffnet die Systemeinstellungen an der richtigen Stelle.
    static func requestAccessibilityPermission() {
        let options = [kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String: true]
        _ = AXIsProcessTrustedWithOptions(options as CFDictionary)
    }

    // ── Hilfsfunktionen ──────────────────────────────────────────────────────

    /// Fensterkoordinate → normalisierte Koordinate (0.0–1.0) im Videobereich.
    ///
    /// Gibt `nil` zurück, wenn der Punkt außerhalb des Videos liegt — dann wird
    /// gar nicht erst gesendet.
    ///
    /// Der y-Flip ist nötig, weil macOS den Ursprung unten links hat, das
    /// Protokoll aber oben links (wie Windows und wie jedes Bildformat).
    private func normalize(_ locationInWindow: CGPoint) -> (x: Float, y: Float)? {
        guard videoFrame.width > 0, videoFrame.height > 0 else { return nil }

        let relativeX = (locationInWindow.x - videoFrame.minX) / videoFrame.width
        let relativeY = (locationInWindow.y - videoFrame.minY) / videoFrame.height

        guard (0...1).contains(relativeX), (0...1).contains(relativeY) else { return nil }

        return (Float(relativeX), Float(1.0 - relativeY))
    }

    private static func button(for event: NSEvent) -> RemoteMouseButton? {
        switch event.type {
        case .leftMouseDown, .leftMouseUp:   return .left
        case .rightMouseDown, .rightMouseUp: return .right
        case .otherMouseDown, .otherMouseUp:
            switch event.buttonNumber {
            case 2:  return .middle
            case 3:  return .back
            case 4:  return .forward
            default: return nil
            }
        default: return nil
        }
    }

    private static func modifiers(from flags: NSEvent.ModifierFlags) -> RemoteModifiers {
        var result: RemoteModifiers = []
        if flags.contains(.shift)   { result.insert(.shift) }
        if flags.contains(.control) { result.insert(.control) }
        if flags.contains(.option)  { result.insert(.alt) }
        if flags.contains(.command) { result.insert(.meta) }
        return result
    }

    private static func modifiers(fromCGFlags flags: CGEventFlags) -> RemoteModifiers {
        var result: RemoteModifiers = []
        if flags.contains(.maskShift)     { result.insert(.shift) }
        if flags.contains(.maskControl)   { result.insert(.control) }
        if flags.contains(.maskAlternate) { result.insert(.alt) }
        if flags.contains(.maskCommand)   { result.insert(.meta) }
        return result
    }
}
