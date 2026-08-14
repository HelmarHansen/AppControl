using AppControl.Host.Interop;
using Microsoft.Extensions.Logging;

namespace AppControl.Host.Capture;

/// <summary>
/// Meldet, welche Zeigerform gerade unter der Maus des Hosts liegt.
///
/// WOZU DAS GUT IST: Der Zeiger des Hosts steckt bereits im Videobild
/// (<c>IsCursorCaptureEnabled</c> in <see cref="CaptureEngine"/>). Der Viewer hat
/// aber zusaetzlich seinen eigenen, lokalen Zeiger - und der bleibt ein Pfeil,
/// auch wenn im Bild darunter ein Textcursor steht. Zwei Zeiger, die
/// unterschiedlich aussehen, wirken wie ein Fehler. Mit dieser Meldung passt der
/// Viewer seinen lokalen Zeiger an.
///
/// ABGEFRAGT STATT ABONNIERT: Windows bietet keine Benachrichtigung ueber
/// Zeigerwechsel ausserhalb des eigenen Fensters. Zehnmal pro Sekunde
/// nachzusehen kostet praktisch nichts (ein Aufruf von GetCursorInfo) und ist
/// schnell genug, dass ein Wechsel nicht auffaellt - der Zeiger wechselt beim
/// Ueberfahren einer Fensterkante, nicht sechzigmal pro Sekunde.
///
/// Gemeldet wird nur bei AENDERUNG. Bei jedem Tick zu senden hiesse zehn
/// Nachrichten pro Sekunde ueber denselben Kanal, ueber den auch Pause und Stopp
/// laufen - fuer eine Information, die sich minutenlang nicht aendert.
/// </summary>
public sealed class CursorMonitor : IDisposable
{
    /// <summary>
    /// Zehn Abfragen je Sekunde. Schneller brauchte es einen Grund; langsamer
    /// wuerde das Nachziehen des Zeigers sichtbar hinterherhinken.
    /// </summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    private readonly ILogger<CursorMonitor> _log;
    private readonly object _gate = new();

    private Timer? _timer;
    private string _lastShape = "";
    private bool _lastVisible = true;
    private bool _hasReported;

    /// <summary>Form und Sichtbarkeit. Wird nur bei Aenderung ausgeloest.</summary>
    public event Action<string, bool>? CursorChanged;

    public CursorMonitor(ILogger<CursorMonitor> log) => _log = log;

    public void Start()
    {
        lock (_gate)
        {
            if (_timer is not null) return;

            _hasReported = false;
            _timer = new Timer(_ => Poll(), null, TimeSpan.Zero, PollInterval);
            _log.LogDebug("Zeigerbeobachtung gestartet");
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }

    public void Dispose() => Stop();

    private void Poll()
    {
        try
        {
            var info = new NativeMethods.CURSORINFO
            {
                cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.CURSORINFO>(),
            };

            if (!NativeMethods.GetCursorInfo(ref info)) return;

            var visible = (info.flags & NativeMethods.CURSOR_SHOWING) != 0;
            var shape = MapShape(info.hCursor);

            if (_hasReported && shape == _lastShape && visible == _lastVisible) return;

            _hasReported = true;
            _lastShape = shape;
            _lastVisible = visible;
            CursorChanged?.Invoke(shape, visible);
        }
        catch (Exception ex)
        {
            // Ein Fehler beim Zeiger darf die Sitzung nicht beruehren - es ist
            // ein Komfort-Feature, und der Stream laeuft unabhaengig davon weiter.
            _log.LogDebug(ex, "Zeigerabfrage fehlgeschlagen");
        }
    }

    /// <summary>
    /// Ordnet ein Cursor-Handle einem der sechs Protokollwerte zu.
    ///
    /// Die Liste ist in protocol/schemas/control-messages.schema.json
    /// abschliessend. Was nicht hineinpasst, wird zu "arrow" - das betrifft vor
    /// allem die diagonalen Groessenaenderungszeiger (IDC_SIZENWSE, IDC_SIZENESW)
    /// und alle anwendungseigenen Zeiger. Bewusst so: Den Protokollwert-Vorrat zu
    /// erweitern hiesse Schema, Dokumentation und Swift-Seite mitzuziehen, und
    /// der Gewinn waere ein etwas passenderer Pfeil an einer Fensterecke.
    /// </summary>
    private static string MapShape(nint hCursor)
    {
        if (hCursor == nint.Zero) return "arrow";

        if (hCursor == SystemCursor(NativeMethods.IDC_IBEAM)) return "ibeam";
        if (hCursor == SystemCursor(NativeMethods.IDC_HAND)) return "hand";
        if (hCursor == SystemCursor(NativeMethods.IDC_SIZENS)) return "resize-ns";
        if (hCursor == SystemCursor(NativeMethods.IDC_SIZEWE)) return "resize-ew";
        if (hCursor == SystemCursor(NativeMethods.IDC_WAIT)) return "wait";
        if (hCursor == SystemCursor(NativeMethods.IDC_APPSTARTING)) return "wait";

        return "arrow";
    }

    /// <summary>
    /// Handles der Systemzeiger, einmal geholt und behalten. Sie sind fuer die
    /// Lebensdauer des Prozesses gueltig und duerfen nicht freigegeben werden -
    /// LoadCursor gibt bei Systemzeigern ein gemeinsam genutztes Handle zurueck.
    /// </summary>
    private static readonly Dictionary<int, nint> SystemCursors = [];

    private static nint SystemCursor(int id)
    {
        lock (SystemCursors)
        {
            if (SystemCursors.TryGetValue(id, out var cached)) return cached;

            var handle = NativeMethods.LoadCursorW(nint.Zero, id);
            SystemCursors[id] = handle;
            return handle;
        }
    }
}
