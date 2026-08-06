using System.Windows;
using AppControl.Host.Core;
using AppControl.Host.Interop;

namespace AppControl.Host.Input;

public enum GateRejection
{
    None = 0,
    NotSharing,           // Gate 1
    ControlNotGranted,    // Gate 2
    WindowNotForeground,  // Gate 3
    TargetRectInvalid,    // Gate 4
    KeyBlocked,           // Gate 5
    RateLimited,          // optional, siehe TODO unten
}

public readonly record struct GateResult(GateRejection Rejection, Point? ScreenPoint)
{
    public bool Allowed => Rejection == GateRejection.None;

    public static GateResult Reject(GateRejection r) => new(r, null);
    public static GateResult Allow(Point p) => new(GateRejection.None, p);
    public static readonly GateResult AllowNoPoint = new(GateRejection.None, null);
}

/// <summary>
/// Die fuenf Gates aus docs/03-consent-and-transparency.md §3.7.
///
/// ENTWURFSPRINZIP: Deny-by-default. Jede Pruefung muss aktiv bestanden werden;
/// jeder unerwartete Zustand fuehrt zur Ablehnung. Der Viewer kann keines dieser
/// Gates beeinflussen - er kann nur Events schicken.
///
/// Diese Klasse haengt bewusst nur an <see cref="SessionStateMachine"/> und an
/// Win32-Abfragen, nicht an der Netzwerk- oder UI-Schicht. Dadurch ist sie in
/// tests/InputGuardTests.cs vollstaendig testbar.
/// </summary>
public sealed class InputGuard
{
    private readonly SessionStateMachine _state;
    private readonly Config.HostSettings _settings;

    public InputGuard(SessionStateMachine state, Config.HostSettings settings)
    {
        _state = state;
        _settings = settings;
    }

    /// <summary>Zaehler pro Ablehnungsgrund - fuer Overlay-Anzeige und Audit.</summary>
    private readonly Dictionary<GateRejection, long> _rejections = new();
    public IReadOnlyDictionary<GateRejection, long> Rejections => _rejections;

    /// <summary>
    /// Prueft ein Zeiger-Event und liefert bei Erfolg den umgerechneten
    /// Bildschirmpunkt in physischen Pixeln.
    /// </summary>
    public GateResult CheckPointer(float normalizedX, float normalizedY)
    {
        var snapshot = _state.Current;

        // ── Gate 1: Sitzungszustand ──────────────────────────────────────────
        if (snapshot.State != SessionState.Sharing)
            return Count(GateRejection.NotSharing);

        // ── Gate 2: Steuerungsfreigabe ───────────────────────────────────────
        if (!snapshot.ControlGranted)
            return Count(GateRejection.ControlNotGranted);

        var scope = snapshot.Scope;
        if (scope is null)
            return Count(GateRejection.NotSharing);

        // ── Gate 3: Fenster im Vordergrund (nur bei App-Freigabe) ────────────
        //
        // Das wichtigste Gate. Ohne es waere App-Freigabe eine Illusion: Der Viewer
        // saehe nur ein Fenster, koennte aber mit Koordinaten ausserhalb davon in
        // jede andere Anwendung klicken. Mit dem Gate laufen injizierte Events ins
        // Leere, sobald der Host selbst die Anwendung wechselt - etwa um ein
        // Passwort einzugeben.
        if (scope is ShareScope.SingleWindow window)
        {
            if (NativeMethods.GetForegroundWindow() != window.Hwnd)
                return Count(GateRejection.WindowNotForeground);
        }

        // ── Gate 4: Geometrische Begrenzung ──────────────────────────────────
        var rect = scope.GetTargetRect();
        if (rect.Width <= 0 || rect.Height <= 0)
            return Count(GateRejection.TargetRectInvalid);

        // Geklemmt statt abgelehnt: Wer die Maus ueber den Rand zieht, erwartet
        // ein Verhalten am Rand, keine ignorierten Events.
        //
        // ACHTUNG NaN: Math.Clamp(NaN, 0, 1) gibt NaN zurueck - beide Vergleiche
        // sind bei NaN false, also faellt der Wert unveraendert durch. Ein
        // boesartiger Viewer kann NaN in einem float-Feld schicken, und NaN wuerde
        // sich bis in die SendInput-Koordinatenrechnung fortpflanzen. Deshalb
        // wird zuerst auf Endlichkeit geprueft.
        var clampedX = Sanitize(normalizedX);
        var clampedY = Sanitize(normalizedY);

        var screenPoint = new Point(
            rect.X + clampedX * rect.Width,
            rect.Y + clampedY * rect.Height);

        return GateResult.Allow(screenPoint);
    }

    /// <summary>Prueft ein Tastatur-Event. Gates 1, 2, 3 und 5.</summary>
    public GateResult CheckKey(ushort hidUsage, KeyModifiers modifiers)
    {
        var snapshot = _state.Current;

        if (snapshot.State != SessionState.Sharing)
            return Count(GateRejection.NotSharing);
        if (!snapshot.ControlGranted)
            return Count(GateRejection.ControlNotGranted);

        var scope = snapshot.Scope;
        if (scope is null)
            return Count(GateRejection.NotSharing);

        if (scope is ShareScope.SingleWindow window &&
            NativeMethods.GetForegroundWindow() != window.Hwnd)
            return Count(GateRejection.WindowNotForeground);

        // ── Gate 5: Tasten-Blocklist ─────────────────────────────────────────
        var enforceBlocklist = scope is ShareScope.SingleWindow || _settings.BlockSystemKeysOnFullScreen;
        if (enforceBlocklist && IsBlocked(hidUsage, modifiers))
            return Count(GateRejection.KeyBlocked);

        return GateResult.AllowNoPoint;
    }

    /// <summary>
    /// Tasten und Kombinationen, die den freigegebenen Bereich verlassen wuerden
    /// oder dem Host vorbehalten sind.
    /// </summary>
    public static bool IsBlocked(ushort hidUsage, KeyModifiers modifiers)
    {
        // Windows-/Meta-Taste oeffnet das Startmenue - verlaesst jeden Scope.
        if (hidUsage is HidKeyMap.LeftGui or HidKeyMap.RightGui) return true;
        if (modifiers.HasFlag(KeyModifiers.Meta)) return true;

        // Fensterwechsel: Alt+Tab, Alt+Esc, Ctrl+Esc
        if (modifiers.HasFlag(KeyModifiers.Alt) && hidUsage is HidKeyMap.Tab or HidKeyMap.Escape) return true;
        if (modifiers.HasFlag(KeyModifiers.Control) && hidUsage is HidKeyMap.Escape) return true;

        // Ctrl+Alt+Entf ist ohnehin unmoeglich (Secure Desktop), wird aber
        // zusaetzlich abgelehnt, damit der Zaehler es sichtbar macht.
        if (modifiers.HasFlag(KeyModifiers.Control) && modifiers.HasFlag(KeyModifiers.Alt)
            && hidUsage == HidKeyMap.Delete) return true;

        // Der Notfall-Hotkey des Hosts bleibt dem Host vorbehalten. Ohne diese
        // Zeile koennte der Viewer den Kill-Switch selbst ausloesen - harmlos -
        // aber auch gezielt vor einer Aktion feuern, um Verwirrung zu stiften.
        if (modifiers.HasFlag(KeyModifiers.Control) && modifiers.HasFlag(KeyModifiers.Alt)
            && modifiers.HasFlag(KeyModifiers.Shift) && hidUsage == HidKeyMap.X) return true;

        return false;
    }

    /// <summary>
    /// Normalisierte Koordinate auf [0,1] bringen. NaN und Unendlich werden auf
    /// die Mitte abgebildet statt weitergereicht - siehe Kommentar in CheckPointer.
    /// </summary>
    private static float Sanitize(float value)
        => float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : 0.5f;

    private GateResult Count(GateRejection reason)
    {
        lock (_rejections)
        {
            _rejections[reason] = _rejections.GetValueOrDefault(reason) + 1;
        }
        _state.CountRejected();
        return GateResult.Reject(reason);
    }

    // TODO(Erweiterung): Token-Bucket-Ratenbegrenzung.
    //
    // Ein boesartiger Viewer kann den Host mit Events fluten. Die Gates halten sie
    // zwar im Scope, aber die CPU-Last entsteht trotzdem. Ein Bucket mit z. B.
    // 500 Events/s Dauerrate und 2000 Burst wuerde das abfangen, ohne legitime
    // schnelle Mausbewegungen (typisch 60-125 Hz) zu stoeren.
    //
    // Bewusst als sichtbares TODO statt als stille Luecke - siehe
    // docs/05-security.md §5.9 Punkt 6.
}
