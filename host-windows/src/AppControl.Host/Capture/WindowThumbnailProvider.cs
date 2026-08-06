using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;

namespace AppControl.Host.Capture;

/// <summary>
/// Liefert Live-Vorschaubilder fuer den App-Picker.
///
/// WARUM LIVE UND NICHT ICONS: Der Host soll sehen, was tatsaechlich uebertragen
/// wuerde, bevor er zustimmt - inklusive dessen, was gerade im Fenster steht. Ein
/// Icon oder ein Standbild von vor fuenf Minuten wuerde genau das verfehlen.
/// Siehe docs/03-consent-and-transparency.md §3.3.
///
/// Fuer jedes sichtbare Fenster laeuft eine eigene, kurzlebige WGC-Session mit
/// niedriger Rate (~2 fps). Das ist bewusst sparsam: Zwanzig gleichzeitige
/// 60-fps-Sessions wuerden die GPU belasten, ohne dass es dem Zweck dient.
/// </summary>
public sealed class WindowThumbnailProvider : IDisposable
{
    private readonly ILogger<WindowThumbnailProvider> _log;
    private readonly Dictionary<nint, ThumbnailSession> _sessions = new();
    private readonly object _gate = new();

    public WindowThumbnailProvider(ILogger<WindowThumbnailProvider> log) => _log = log;

    private sealed record ThumbnailSession(nint Hwnd, WriteableBitmap Bitmap, IDisposable Capture);

    /// <summary>
    /// Startet eine Vorschau und gibt das Bitmap zurueck, das sich selbst
    /// aktualisiert. Direkt an ein WPF-Image bindbar.
    /// </summary>
    public WriteableBitmap? StartPreview(nint hwnd, int maxWidth = 320, int maxHeight = 200)
    {
        lock (_gate)
        {
            if (_sessions.TryGetValue(hwnd, out var existing)) return existing.Bitmap;
        }

        // PLATZHALTER FUER ERWEITERUNG
        // ────────────────────────────
        // Die vollstaendige Implementierung braucht denselben Weg wie CaptureEngine,
        // nur mit gedrosselter Rate und Skalierung:
        //
        //   1. GraphicsCaptureInterop.CreateForWindow(hwnd)
        //   2. Direct3D11CaptureFramePool.CreateFreeThreaded(...) mit kleiner Zielgroesse
        //   3. Im FrameArrived: nur jedes N-te Frame verarbeiten (Zielrate ~2 fps)
        //   4. IDirect3DSurface -> ID3D11Texture2D (via IDirect3DDxgiInterfaceAccess)
        //   5. In eine CPU-lesbare Staging-Textur kopieren (D3D11_USAGE_STAGING)
        //   6. Map() und die Bytes in WriteableBitmap.BackBuffer schreiben
        //
        // Schritt 5-6 sind der einzige Ort im ganzen Host, an dem Pixel bewusst
        // auf die CPU heruntergeladen werden. Fuer 320x200 bei 2 fps ist das
        // vernachlaessigbar; fuer den eigentlichen Stream waere es untragbar -
        // deshalb bleibt der Hauptpfad in CaptureEngine strikt GPU-seitig.
        //
        // Zwischenloesung bis dahin: DwmpQueryWindowThumbnailSourceSize +
        // DwmRegisterThumbnail (die API, die auch die Taskleisten-Vorschau nutzt).
        // Weniger Kontrolle ueber die Rate, dafuer wenige Zeilen Code.

        _log.LogDebug("Vorschau fuer HWND {Hwnd:X} angefordert (max {W}x{H})", hwnd, maxWidth, maxHeight);
        return null;
    }

    public void StopPreview(nint hwnd)
    {
        lock (_gate)
        {
            if (!_sessions.Remove(hwnd, out var session)) return;
            session.Capture.Dispose();
        }
    }

    /// <summary>Beendet alle Vorschauen - beim Schliessen des Pickers aufzurufen.</summary>
    public void StopAll()
    {
        lock (_gate)
        {
            foreach (var session in _sessions.Values) session.Capture.Dispose();
            _sessions.Clear();
        }
    }

    public void Dispose() => StopAll();
}
