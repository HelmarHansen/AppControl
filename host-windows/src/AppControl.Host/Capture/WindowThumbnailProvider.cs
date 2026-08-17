using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AppControl.Host.Interop;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AppControl.Host.Capture;

/// <summary>
/// Liefert Live-Vorschaubilder fuer den App-Picker.
///
/// WARUM LIVE UND NICHT ICONS: Der Host soll sehen, was tatsaechlich uebertragen
/// wuerde, bevor er zustimmt - inklusive dessen, was gerade im Fenster steht. Ein
/// Icon oder ein Standbild von vor fuenf Minuten wuerde genau das verfehlen.
/// Siehe docs/03-consent-and-transparency.md §3.3.
///
/// ── WARUM HIER NICHT WINDOWS.GRAPHICS.CAPTURE ────────────────────────────────
///
/// Der Stream laeuft ueber WGC und bleibt dabei vollstaendig auf der GPU. Fuer
/// die Vorschau waere derselbe Weg naheliegend - und waere falsch:
///
/// WGC laesst Windows einen gelben Rahmen um jedes erfasste Fenster zeichnen.
/// Das ist im Betrieb genau richtig und bleibt dort bewusst eingeschaltet. Im
/// Picker haetten wir aber ZWANZIG Sessions gleichzeitig offen, also zwanzig
/// gelb umrandete Fenster auf dem Bildschirm - bevor ueberhaupt jemand
/// zugestimmt hat. Das saehe aus wie ein Fehler und wuerde den Rahmen als
/// Signal entwerten: Wer ihn staendig sieht, hoert auf, ihn zu lesen.
///
/// Deshalb nimmt die Vorschau PrintWindow mit PW_RENDERFULLCONTENT - dieselbe
/// API, mit der Windows seine eigenen Taskleisten-Vorschauen zeichnet. Sie
/// bleibt lokal: Es entsteht keine Sitzung, nichts verlaesst den Rechner, und
/// der Einzige, der die Bilder sieht, ist der Host selbst.
///
/// GRENZE DIESES WEGES: Einzelne Fenster mit eigenem Renderpfad (manche Spiele,
/// einige Browser mit bestimmten GPU-Einstellungen) liefern trotz
/// PW_RENDERFULLCONTENT ein schwarzes Bild. Das wird erkannt und die Kachel
/// zeigt dann ihren Platzhalter, statt schwarz zu bleiben - eine leere Kachel
/// ist ehrlicher als eine, die faelschlich "dieses Fenster ist schwarz" sagt.
/// </summary>
public sealed class WindowThumbnailProvider : IDisposable
{
    private readonly ILogger<WindowThumbnailProvider> _log;
    private readonly Dictionary<object, ThumbnailSession> _sessions = [];

    /// <summary>
    /// Schuetzt die Sitzungsliste UND ihre GDI-Objekte. Beides zusammen, weil das
    /// Aufnehmen im Hintergrund laeuft: Wuerde StopAll waehrenddessen die Handles
    /// loeschen, zeichnete der Hintergrund-Thread in freigegebenen Speicher.
    /// </summary>
    private readonly object _gate = new();

    private readonly Dispatcher _dispatcher;

    /// <summary>1 = ein Durchlauf ist unterwegs. Verhindert, dass sich zwei
    /// Durchlaeufe ueberlappen und derselbe Puffer gleichzeitig beschrieben und
    /// gelesen wird.</summary>
    private int _refreshInFlight;

    public WindowThumbnailProvider(ILogger<WindowThumbnailProvider>? log = null)
    {
        _log = log ?? NullLogger<WindowThumbnailProvider>.Instance;

        // Der Picker erzeugt den Provider auf dem UI-Thread; genau dorthin
        // muessen die Bitmap-Schreibzugriffe zurueck.
        _dispatcher = Dispatcher.CurrentDispatcher;
    }

    /// <summary>
    /// Eine laufende Vorschau. Haelt ihre GDI-Objekte ueber die gesamte Lebensdauer,
    /// statt sie pro Bild neu anzulegen - bei zwanzig Kacheln und zwei Bildern je
    /// Sekunde waeren das sonst achtzig Allokationen pro Sekunde.
    /// </summary>
    private sealed class ThumbnailSession : IDisposable
    {
        public required WriteableBitmap Bitmap { get; init; }
        public required int Width { get; init; }
        public required int Height { get; init; }

        /// <summary>Fensterhandle, oder nint.Zero bei einer Monitor-Vorschau.</summary>
        public nint Hwnd { get; init; }

        /// <summary>Nur bei Monitor-Vorschauen gesetzt: der Ausschnitt des Bildschirms.</summary>
        public Int32Rect ScreenBounds { get; init; }

        public nint TargetDc;
        public nint TargetDib;
        public nint TargetOldObject;
        public nint TargetBits;

        public nint SourceDc;
        public nint SourceDib;
        public nint SourceOldObject;
        public int SourceWidth;
        public int SourceHeight;

        /// <summary>
        /// Ob je ein brauchbares Bild ankam. Steuert nur die Protokollierung -
        /// eine Warnung pro Fenster, nicht zwei pro Sekunde.
        /// </summary>
        public bool EverSucceeded;
        public bool BlackWarningLogged;

        public void Dispose()
        {
            ReleaseSource();

            if (TargetDc != nint.Zero)
            {
                if (TargetOldObject != nint.Zero) NativeMethods.SelectObject(TargetDc, TargetOldObject);
                NativeMethods.DeleteDC(TargetDc);
                TargetDc = nint.Zero;
            }
            if (TargetDib != nint.Zero)
            {
                NativeMethods.DeleteObject(TargetDib);
                TargetDib = nint.Zero;
            }
        }

        public void ReleaseSource()
        {
            if (SourceDc != nint.Zero)
            {
                if (SourceOldObject != nint.Zero) NativeMethods.SelectObject(SourceDc, SourceOldObject);
                NativeMethods.DeleteDC(SourceDc);
                SourceDc = nint.Zero;
                SourceOldObject = nint.Zero;
            }
            if (SourceDib != nint.Zero)
            {
                NativeMethods.DeleteObject(SourceDib);
                SourceDib = nint.Zero;
            }
            SourceWidth = 0;
            SourceHeight = 0;
        }
    }

    // ── Oeffentliche API ─────────────────────────────────────────────────────

    /// <summary>
    /// Startet die Vorschau eines Fensters und gibt das Bitmap zurueck, das sich
    /// bei jedem <see cref="Refresh"/> aktualisiert. Direkt an ein WPF-Image
    /// bindbar.
    /// </summary>
    /// <returns>
    /// <c>null</c>, wenn sich vom Fenster kein Bild holen laesst - dann soll die
    /// Kachel ihren Platzhalter behalten.
    /// </returns>
    public WriteableBitmap? StartPreview(nint hwnd, int maxWidth = 320, int maxHeight = 200)
    {
        if (hwnd == nint.Zero) return null;
        lock (_gate)
        {
            if (_sessions.TryGetValue(hwnd, out var existing)) return existing.Bitmap;
        }

        if (!NativeMethods.GetWindowRect(hwnd, out var rect)) return null;
        var size = FitInto(rect.Right - rect.Left, rect.Bottom - rect.Top, maxWidth, maxHeight);
        if (size is null) return null;

        var session = CreateSession(size.Value.Width, size.Value.Height, hwnd, default);
        if (session is null) return null;

        lock (_gate) _sessions[hwnd] = session;
        return FirstCapture(session, hwnd);
    }

    /// <summary>
    /// Startet die Vorschau eines ganzen Bildschirms. Der Ausschnitt kommt aus
    /// <see cref="WindowEnumerator"/> und ist in virtuellen Desktop-Koordinaten
    /// angegeben - genau der Bezugsrahmen, in dem auch der Bildschirm-DC liegt.
    /// </summary>
    public WriteableBitmap? StartMonitorPreview(Int32Rect bounds, int maxWidth = 320, int maxHeight = 200)
    {
        var key = (object)(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        lock (_gate)
        {
            if (_sessions.TryGetValue(key, out var existing)) return existing.Bitmap;
        }

        var size = FitInto(bounds.Width, bounds.Height, maxWidth, maxHeight);
        if (size is null) return null;

        var session = CreateSession(size.Value.Width, size.Value.Height, nint.Zero, bounds);
        if (session is null) return null;

        lock (_gate) _sessions[key] = session;
        return FirstCapture(session, key);
    }

    /// <summary>
    /// Stoesst eine Aktualisierung aller Vorschauen an. Kehrt sofort zurueck.
    ///
    /// WARUM NICHT EINFACH DURCHLAUFEN: PrintWindow kostet bei einem grossen
    /// Fenster leicht 10-20 ms. Bei zwanzig Kacheln waeren das ueber 300 ms - auf
    /// dem UI-Thread also ein sichtbares Stocken bei jedem Durchlauf, und zwar
    /// genau waehrend der Nutzer scrollt und auswaehlt.
    ///
    /// Deshalb: aufnehmen im Hintergrund, und nur das Kopieren in die
    /// WriteableBitmaps zurueck auf den UI-Thread. Ein neuer Durchlauf startet
    /// erst, wenn der vorige samt Schreiben fertig ist - sonst laege ein
    /// Hintergrund-Thread im selben Puffer, aus dem der UI-Thread gerade liest.
    /// </summary>
    public void Refresh()
    {
        if (Interlocked.CompareExchange(ref _refreshInFlight, 1, 0) != 0) return;

        Task.Run(() =>
        {
            List<ThumbnailSession> updated = [];
            try
            {
                lock (_gate)
                {
                    foreach (var session in _sessions.Values)
                    {
                        if (CaptureOnce(session)) updated.Add(session);
                    }
                }
            }
            catch (Exception ex)
            {
                // Eine fehlgeschlagene Vorschau darf den Picker nicht mitnehmen.
                // Der Nutzer sieht dann eine leere Kachel - unschoen, aber
                // deutlich besser als ein Absturz waehrend der Auswahl.
                _log.LogDebug(ex, "Vorschau-Durchlauf fehlgeschlagen.");
            }

            _dispatcher.InvokeAsync(() =>
            {
                try
                {
                    lock (_gate)
                    {
                        foreach (var session in updated)
                        {
                            if (_sessions.ContainsValue(session)) WriteToBitmap(session);
                        }
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref _refreshInFlight, 0);
                }
            });
        });
    }

    public void StopPreview(nint hwnd)
    {
        lock (_gate)
        {
            if (!_sessions.Remove(hwnd, out var session)) return;
            session.Dispose();
        }
    }

    /// <summary>Beendet alle Vorschauen - beim Schliessen des Pickers aufzurufen.</summary>
    public void StopAll()
    {
        lock (_gate)
        {
            foreach (var session in _sessions.Values) session.Dispose();
            _sessions.Clear();
        }
    }

    public void Dispose() => StopAll();

    // ── Intern ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Erste Aufnahme, synchron: Liefert sie kein Bild, hat die Kachel nichts zu
    /// zeigen und die Sitzung wird gleich wieder verworfen - sonst bliebe ein
    /// Fenster, das nie ein Bild liefert, fuer die Dauer des Pickers in der Liste
    /// und wuerde bei jedem Durchlauf erneut erfolglos abgefragt.
    /// </summary>
    private WriteableBitmap? FirstCapture(ThumbnailSession session, object key)
    {
        bool captured;
        lock (_gate) captured = CaptureOnce(session);

        if (!captured)
        {
            lock (_gate)
            {
                if (_sessions.Remove(key, out var removed)) removed.Dispose();
            }
            return null;
        }

        WriteToBitmap(session);
        return session.Bitmap;
    }

    /// <summary>Seitenverhaeltnis erhalten, nie vergroessern.</summary>
    private static (int Width, int Height)? FitInto(int sourceWidth, int sourceHeight, int maxWidth, int maxHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0) return null;

        var scale = Math.Min((double)maxWidth / sourceWidth, (double)maxHeight / sourceHeight);
        if (scale > 1.0) scale = 1.0;

        var width = Math.Max(1, (int)Math.Round(sourceWidth * scale));
        var height = Math.Max(1, (int)Math.Round(sourceHeight * scale));
        return (width, height);
    }

    private ThumbnailSession? CreateSession(int width, int height, nint hwnd, Int32Rect screenBounds)
    {
        var screenDc = NativeMethods.GetDC(nint.Zero);
        if (screenDc == nint.Zero) return null;

        try
        {
            var dc = NativeMethods.CreateCompatibleDC(screenDc);
            if (dc == nint.Zero) return null;

            var dib = CreateDib(dc, width, height, out var bits);
            if (dib == nint.Zero)
            {
                NativeMethods.DeleteDC(dc);
                return null;
            }

            var old = NativeMethods.SelectObject(dc, dib);
            NativeMethods.SetStretchBltMode(dc, NativeMethods.HALFTONE);

            return new ThumbnailSession
            {
                Bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null),
                Width = width,
                Height = height,
                Hwnd = hwnd,
                ScreenBounds = screenBounds,
                TargetDc = dc,
                TargetDib = dib,
                TargetOldObject = old,
                TargetBits = bits,
            };
        }
        finally
        {
            NativeMethods.ReleaseDC(nint.Zero, screenDc);
        }
    }

    private static nint CreateDib(nint dc, int width, int height, out nint bits)
    {
        var info = new NativeMethods.BITMAPINFO
        {
            bmiHeader = new NativeMethods.BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                biWidth = width,
                // Negativ: von oben nach unten. Sonst laege das Bild auf dem Kopf,
                // weil GDI-Bitmaps standardmaessig von unten nach oben zaehlen.
                biHeight = -height,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = NativeMethods.BI_RGB,
            },
        };

        return NativeMethods.CreateDIBSection(
            dc, ref info, NativeMethods.DIB_RGB_COLORS, out bits, nint.Zero, 0);
    }

    /// <summary>
    /// Nimmt ein Bild in den Zielpuffer der Sitzung auf. Laeuft im Hintergrund;
    /// das Uebertragen in die WriteableBitmap macht <see cref="WriteToBitmap"/>
    /// auf dem UI-Thread.
    /// </summary>
    private bool CaptureOnce(ThumbnailSession session)
    {
        var captured = session.Hwnd != nint.Zero
            ? CaptureWindow(session)
            : CaptureScreenRegion(session);

        if (!captured) return false;

        if (IsEffectivelyBlank(session))
        {
            if (!session.BlackWarningLogged && !session.EverSucceeded)
            {
                session.BlackWarningLogged = true;
                _log.LogDebug(
                    "Fenster {Hwnd:X} liefert kein Vorschaubild (eigener Renderpfad) - Kachel bleibt leer.",
                    session.Hwnd);
            }
            return false;
        }

        session.EverSucceeded = true;
        return true;
    }

    private static bool CaptureWindow(ThumbnailSession session)
    {
        if (!NativeMethods.GetWindowRect(session.Hwnd, out var rect)) return false;

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0) return false;

        if (!EnsureSource(session, width, height)) return false;

        // PrintWindow zeichnet in den Quell-DC in Originalgroesse; erst danach
        // wird verkleinert. Direkt skaliert zu zeichnen kann PrintWindow nicht.
        if (!NativeMethods.PrintWindow(session.Hwnd, session.SourceDc, NativeMethods.PW_RENDERFULLCONTENT))
            return false;

        return NativeMethods.StretchBlt(
            session.TargetDc, 0, 0, session.Width, session.Height,
            session.SourceDc, 0, 0, width, height, NativeMethods.SRCCOPY);
    }

    private static bool CaptureScreenRegion(ThumbnailSession session)
    {
        var bounds = session.ScreenBounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return false;

        var screenDc = NativeMethods.GetDC(nint.Zero);
        if (screenDc == nint.Zero) return false;

        try
        {
            return NativeMethods.StretchBlt(
                session.TargetDc, 0, 0, session.Width, session.Height,
                screenDc, bounds.X, bounds.Y, bounds.Width, bounds.Height,
                NativeMethods.SRCCOPY);
        }
        finally
        {
            NativeMethods.ReleaseDC(nint.Zero, screenDc);
        }
    }

    /// <summary>
    /// Legt den Quellpuffer an oder passt ihn an eine neue Fenstergroesse an.
    /// Groessenwechsel sind selten (Ziehen am Rand), deshalb lohnt das Neuanlegen
    /// gegenueber einem Puffer, der immer die groesste je gesehene Groesse haelt.
    /// </summary>
    private static bool EnsureSource(ThumbnailSession session, int width, int height)
    {
        if (session.SourceDc != nint.Zero && session.SourceWidth == width && session.SourceHeight == height)
            return true;

        session.ReleaseSource();

        var screenDc = NativeMethods.GetDC(nint.Zero);
        if (screenDc == nint.Zero) return false;

        try
        {
            var dc = NativeMethods.CreateCompatibleDC(screenDc);
            if (dc == nint.Zero) return false;

            var dib = CreateDib(dc, width, height, out _);
            if (dib == nint.Zero)
            {
                NativeMethods.DeleteDC(dc);
                return false;
            }

            session.SourceDc = dc;
            session.SourceDib = dib;
            session.SourceOldObject = NativeMethods.SelectObject(dc, dib);
            session.SourceWidth = width;
            session.SourceHeight = height;
            return true;
        }
        finally
        {
            NativeMethods.ReleaseDC(nint.Zero, screenDc);
        }
    }

    /// <summary>
    /// Erkennt das komplett schwarze Bild, das PrintWindow bei Fenstern mit
    /// eigenem Renderpfad liefert.
    ///
    /// Stichprobe statt vollstaendiger Pruefung: Bei zwanzig Kacheln und zwei
    /// Bildern je Sekunde waere das sonst Millionen Vergleiche pro Sekunde fuer
    /// eine Frage, die eine Stichprobe genauso gut beantwortet. Der Schritt ist
    /// bewusst ungerade, damit er nicht mit einem Pixelraster zusammenfaellt.
    ///
    /// Ein tatsaechlich schwarzes Fenster wird dabei als "kein Bild" gewertet.
    /// Das ist der bessere der beiden Irrtuemer: Die Kachel zeigt dann ihren
    /// Platzhalter statt einer schwarzen Flaeche, die aussieht wie ein Defekt.
    /// </summary>
    private static unsafe bool IsEffectivelyBlank(ThumbnailSession session)
    {
        if (session.TargetBits == nint.Zero) return true;

        var pixels = (uint*)session.TargetBits;
        var count = session.Width * session.Height;

        for (var i = 0; i < count; i += 97)
        {
            // Alphakanal ausblenden: PrintWindow liefert ihn je nach Fenster
            // als 0 oder als 255, ohne dass das etwas ueber den Inhalt sagt.
            if ((pixels[i] & 0x00FFFFFF) != 0) return false;
        }

        return true;
    }

    private static unsafe void WriteToBitmap(ThumbnailSession session)
    {
        var bitmap = session.Bitmap;
        bitmap.Lock();
        try
        {
            var source = (byte*)session.TargetBits;
            var destination = (byte*)bitmap.BackBuffer;
            var sourceStride = session.Width * 4;
            var destinationStride = bitmap.BackBufferStride;

            // Zeilenweise, weil WPF die Zeilenlaenge aufrunden darf. Bei 32 Bit
            // tut es das in der Praxis nie - sich darauf zu verlassen waere
            // trotzdem eine stille Annahme ueber fremden Code.
            for (var y = 0; y < session.Height; y++)
            {
                Buffer.MemoryCopy(
                    source + (long)y * sourceStride,
                    destination + (long)y * destinationStride,
                    destinationStride,
                    sourceStride);
            }

            bitmap.AddDirtyRect(new Int32Rect(0, 0, session.Width, session.Height));
        }
        finally
        {
            bitmap.Unlock();
        }
    }
}
