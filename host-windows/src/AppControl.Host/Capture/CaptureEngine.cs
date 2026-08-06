using System.Runtime.InteropServices;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using AppControl.Host.Core;
using Microsoft.Extensions.Logging;

namespace AppControl.Host.Capture;

/// <summary>Ein erfasstes Frame. Die Oberflaeche bleibt auf der GPU.</summary>
public sealed record CapturedFrame(IDirect3DSurface Surface, SizeInt32 Size, TimeSpan SystemRelativeTime);

/// <summary>
/// Bildschirm-/Fenstererfassung ueber Windows.Graphics.Capture.
///
/// ZWEI ENTWURFSENTSCHEIDUNGEN, die den Unterschied machen:
///
/// 1. DAS ZUSTANDS-GATE SITZT VOR DEM ENCODER, NICHT VOR DEM NETZWERK.
///    Ist der Host pausiert, wird das Frame verworfen, BEVOR CPU/GPU-Zeit in die
///    Kompression fliesst. Ein pausierter Host kostet praktisch nichts - und,
///    wichtiger: Es gibt keinen Puffer, in dem noch alte Frames liegen koennten,
///    die nach dem Pausieren doch noch rausgehen.
///
/// 2. BACKPRESSURE STATT WARTESCHLANGE.
///    Maximal ein Frame ist gleichzeitig "in Bearbeitung". Ist der Verbraucher
///    noch beschaeftigt, wenn das naechste Frame kommt, wird das NEUE Frame
///    verworfen - nicht eingereiht. Eine Warteschlange wuerde bei Ueberlast
///    wachsen und die Latenz mit jeder Sekunde verschlechtern; Verwerfen haelt sie
///    konstant. Das ist der Unterschied zwischen "ruckelt bei Last" und "ist nach
///    zwei Minuten unbenutzbar".
///
/// Siehe docs/01-architecture.md §1.5.
/// </summary>
public sealed class CaptureEngine : IDisposable
{
    private readonly SessionStateMachine _state;
    private readonly ILogger<CaptureEngine> _log;
    private readonly IDirect3DDevice _device;

    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private SizeInt32 _lastSize;

    /// <summary>1 = ein Frame ist beim Verbraucher, 0 = frei. Siehe Backpressure oben.</summary>
    private int _frameInFlight;

    private long _framesDelivered;
    private long _framesDroppedByGate;
    private long _framesDroppedByBackpressure;

    /// <summary>
    /// Wird pro freigegebenem Frame ausgeloest. Der Handler MUSS
    /// <see cref="ReleaseFrame"/> aufrufen, wenn er fertig ist - sonst haelt die
    /// Backpressure die Pipeline dauerhaft an.
    /// </summary>
    public event Action<CapturedFrame>? FrameReady;

    /// <summary>Die erfasste Quelle ist verschwunden (Fenster geschlossen).</summary>
    public event Action? SourceClosed;

    public long FramesDelivered => Interlocked.Read(ref _framesDelivered);
    public long FramesDroppedByGate => Interlocked.Read(ref _framesDroppedByGate);
    public long FramesDroppedByBackpressure => Interlocked.Read(ref _framesDroppedByBackpressure);

    public CaptureEngine(SessionStateMachine state, IDirect3DDevice device, ILogger<CaptureEngine> log)
    {
        _state = state;
        _device = device;
        _log = log;
    }

    public bool IsRunning => _session is not null;

    /// <summary>Startet die Erfassung fuer den angegebenen Scope.</summary>
    public bool Start(ShareScope scope)
    {
        Stop();

        _item = scope switch
        {
            ShareScope.SingleWindow w => GraphicsCaptureInterop.CreateForWindow(w.Hwnd),
            ShareScope.FullScreen f => GraphicsCaptureInterop.CreateForMonitor(f.MonitorHandle),
            _ => null,
        };

        if (_item is null)
        {
            _log.LogError("Capture-Item fuer '{Scope}' konnte nicht erstellt werden", scope.DisplayName);
            return false;
        }

        _lastSize = _item.Size;

        // FreeThreaded: Frames kommen auf einem Threadpool-Thread statt auf dem
        // UI-Thread. Sonst wuerde jede UI-Verzoegerung (ein geoeffnetes Menue, ein
        // Layout-Durchlauf) direkt die Framerate einbrechen lassen.
        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _device,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            numberOfBuffers: 2,          // 2 statt mehr: kuerzere Pipeline = weniger Latenz
            size: _lastSize);

        _framePool.FrameArrived += OnFrameArrived;

        _session = _framePool.CreateCaptureSession(_item);

        // ── TRANSPARENZ: Der gelbe Systemrahmen bleibt AN ────────────────────
        //
        // Ab Windows 11 liesse sich `_session.IsBorderRequired = false` setzen, um
        // den gelben Rahmen zu entfernen, den Windows um jedes erfasste Fenster
        // zeichnet. Das ist hier BEWUSST NICHT implementiert.
        //
        // Der Rahmen ist ein vom Betriebssystem garantierter Transparenzhinweis -
        // staerker als alles, was wir selbst bauen koennen, weil unser eigenes
        // Overlay theoretisch manipulierbar waere, dieser Rahmen aber nicht.
        // Die dafuer noetige Capability `graphicsCaptureWithoutBorder` wird von
        // AppControl nicht angefordert.
        //
        // Siehe docs/03-consent-and-transparency.md §3.4 und §3.9.

        // Der Mauszeiger des HOSTS wird mit uebertragen, damit der Viewer sieht,
        // wo der Host gerade zeigt. Konfigurierbar - siehe Config/HostSettings.cs.
        _session.IsCursorCaptureEnabled = true;

        _item.Closed += (_, _) =>
        {
            _log.LogInformation("Erfasste Quelle wurde geschlossen");
            SourceClosed?.Invoke();
        };

        _session.StartCapture();
        _log.LogInformation("Erfassung gestartet: {Scope} ({W}x{H})",
            scope.DisplayName, _lastSize.Width, _lastSize.Height);

        return true;
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        // TryGetNextFrame ist Pflicht, auch wenn wir das Frame verwerfen: Wird es
        // nicht abgeholt, laeuft der Pool voll und die Erfassung stockt.
        using var frame = sender.TryGetNextFrame();
        if (frame is null) return;

        // ── Groessenaenderung ────────────────────────────────────────────────
        if (frame.ContentSize.Width != _lastSize.Width || frame.ContentSize.Height != _lastSize.Height)
        {
            _lastSize = frame.ContentSize;
            _log.LogDebug("Inhaltsgroesse geaendert: {W}x{H}", _lastSize.Width, _lastSize.Height);
            // Recreate leert den Pool - dieses Frame gehoert noch zum alten Format.
            sender.Recreate(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _lastSize);
            return;
        }

        // ── GATE: Darf ueberhaupt gestreamt werden? ──────────────────────────
        //
        // Kein eigenes Flag in dieser Klasse - die Zustandsmaschine ist die
        // einzige Wahrheitsquelle. Damit ist strukturell ausgeschlossen, dass
        // gestreamt wird, waehrend das Overlay etwas anderes anzeigt.
        if (!_state.Current.IsStreaming)
        {
            Interlocked.Increment(ref _framesDroppedByGate);
            return;
        }

        // ── BACKPRESSURE ─────────────────────────────────────────────────────
        if (Interlocked.CompareExchange(ref _frameInFlight, 1, 0) != 0)
        {
            Interlocked.Increment(ref _framesDroppedByBackpressure);
            return;
        }

        try
        {
            Interlocked.Increment(ref _framesDelivered);
            FrameReady?.Invoke(new CapturedFrame(frame.Surface, frame.ContentSize, frame.SystemRelativeTime));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Fehler beim Verarbeiten eines Frames");
            ReleaseFrame();
        }
    }

    /// <summary>
    /// Vom Verbraucher aufzurufen, sobald das Frame kodiert ist. Ohne diesen Aufruf
    /// bleibt die Pipeline stehen - das ist Absicht: ein haengender Encoder soll
    /// die Latenz nicht durch eine wachsende Warteschlange verschleiern.
    /// </summary>
    public void ReleaseFrame() => Interlocked.Exchange(ref _frameInFlight, 0);

    public void Stop()
    {
        if (_session is null) return;

        _log.LogInformation(
            "Erfassung gestoppt. Frames: {Delivered} geliefert, {Gate} durch Gate verworfen, " +
            "{Backpressure} durch Backpressure verworfen",
            FramesDelivered, FramesDroppedByGate, FramesDroppedByBackpressure);

        if (_framePool is not null) _framePool.FrameArrived -= OnFrameArrived;

        _session?.Dispose();
        _framePool?.Dispose();
        _session = null;
        _framePool = null;
        _item = null;
        Interlocked.Exchange(ref _frameInFlight, 0);
    }

    public void Dispose() => Stop();

    // TODO(Erweiterung): Adaptive Framerate.
    // Aktuell liefert WGC Frames im Takt des Compositors (bis zu 60/s bei
    // Aenderungen). Bei statischem Inhalt entstehen kaum Frames, was gut ist.
    // Sinnvoll waere zusaetzlich eine Obergrenze, die sich an der von WebRTC
    // gemeldeten Bandbreite orientiert - dann wuerden Frames schon hier
    // verworfen statt erst im Encoder.
}
