using AppControl.Host.Audit;
using AppControl.Host.Config;
using AppControl.Host.Core;
using AppControl.Host.Input;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AppControl.Host.Tests;

/// <summary>
/// Prueft die Input-Gates aus docs/03-consent-and-transparency.md §3.7.
///
/// Diese Tests sind die wichtigsten im Projekt: Sie pruefen die Zusicherungen,
/// die AppControl dem Host gibt. Ein Fehler hier bedeutet, dass jemand Eingaben
/// ausloesen kann, obwohl er nicht darf.
///
/// Gate 3 (Fenster im Vordergrund) ist hier nicht abgedeckt, weil es einen echten
/// Fenster-Handle und GetForegroundWindow braucht - siehe den Hinweis am Ende.
/// </summary>
public sealed class InputGuardTests
{
    private sealed class NullAudit : IAuditLog
    {
        public void Write(AuditEventKind kind, string? detail = null) { }
        public IReadOnlyList<string> ReadRecent(int maxLines = 200) => [];
    }

    private static (SessionStateMachine State, InputGuard Guard) Build()
    {
        var state = new SessionStateMachine(new NullAudit(), NullLogger<SessionStateMachine>.Instance);
        return (state, new InputGuard(state, new HostSettings()));
    }

    private static ShareScope FullScreenScope() => new ShareScope.FullScreen(
        MonitorHandle: 1, MonitorName: "Test", Bounds: new System.Windows.Int32Rect(0, 0, 1920, 1080));

    private static void MakeSharing(SessionStateMachine state, bool control)
    {
        state.BeginPairing();
        state.PeerConnected(new PeerIdentity("Test", "ab cd", IsKnown: true, PreviousSessions: 1));
        state.ApplyConsent(new ConsentDecision(
            Granted: true, Scope: FullScreenScope(), ControlGranted: control,
            MaxDuration: TimeSpan.FromMinutes(30)));
    }

    // ── Gate 1: Sitzungszustand ──────────────────────────────────────────────

    [Fact]
    public void Gate1_im_Ruhezustand_wird_alles_abgelehnt()
    {
        var (_, guard) = Build();

        var result = guard.CheckPointer(0.5f, 0.5f);

        Assert.False(result.Allowed);
        Assert.Equal(GateRejection.NotSharing, result.Rejection);
    }

    [Fact]
    public void Gate1_im_pausierten_Zustand_wird_alles_abgelehnt()
    {
        var (state, guard) = Build();
        MakeSharing(state, control: true);
        state.Pause();

        Assert.Equal(GateRejection.NotSharing, guard.CheckPointer(0.5f, 0.5f).Rejection);
        Assert.Equal(GateRejection.NotSharing, guard.CheckKey(0x04, KeyModifiers.None).Rejection);
    }

    [Fact]
    public void Gate1_waehrend_des_Abbaus_wird_alles_abgelehnt()
    {
        // Ein Event, das waehrend des Verbindungsabbaus noch eintrifft, darf
        // nicht mehr durchkommen.
        var (state, guard) = Build();
        MakeSharing(state, control: true);
        state.Stop(StopReason.UserStopped);

        Assert.False(guard.CheckPointer(0.5f, 0.5f).Allowed);
    }

    // ── Gate 2: Steuerungsfreigabe ───────────────────────────────────────────

    [Fact]
    public void Gate2_Sharing_ohne_Steuerungsfreigabe_lehnt_ab()
    {
        // Der haeufigste Fall: "ich zeige dir was, fass nichts an".
        var (state, guard) = Build();
        MakeSharing(state, control: false);

        var result = guard.CheckPointer(0.5f, 0.5f);

        Assert.False(result.Allowed);
        Assert.Equal(GateRejection.ControlNotGranted, result.Rejection);
    }

    [Fact]
    public void Gate2_Pausieren_entzieht_die_Steuerung_hart()
    {
        var (state, guard) = Build();
        MakeSharing(state, control: true);

        state.Pause();

        Assert.False(state.Current.ControlGranted);
    }

    [Fact]
    public void Gate2_Fortsetzen_stellt_die_Steuerung_NICHT_wieder_her()
    {
        // Ein Klick auf "Fortsetzen" ist eine Zustimmung zum Zeigen, nicht zum
        // Steuern. Die Steuerung muss erneut erteilt werden.
        var (state, guard) = Build();
        MakeSharing(state, control: true);
        state.Pause();

        state.Resume();

        Assert.Equal(SessionState.Sharing, state.Current.State);
        Assert.False(state.Current.ControlGranted);
        Assert.Equal(GateRejection.ControlNotGranted, guard.CheckPointer(0.5f, 0.5f).Rejection);
    }

    // ── Gate 4: geometrische Begrenzung ──────────────────────────────────────

    [Fact]
    public void Gate4_normalisierte_Koordinaten_werden_korrekt_abgebildet()
    {
        var (state, guard) = Build();
        MakeSharing(state, control: true);

        var result = guard.CheckPointer(0.5f, 0.25f);

        Assert.True(result.Allowed);
        Assert.Equal(960.0, result.ScreenPoint!.Value.X, precision: 1);
        Assert.Equal(270.0, result.ScreenPoint!.Value.Y, precision: 1);
    }

    [Theory]
    [InlineData(-1.0f, 0.5f, 0.0, 540.0)]        // links ausserhalb
    [InlineData(2.0f, 0.5f, 1920.0, 540.0)]      // rechts ausserhalb
    [InlineData(0.5f, -5.0f, 960.0, 0.0)]        // oben ausserhalb
    [InlineData(0.5f, 99.0f, 960.0, 1080.0)]     // unten ausserhalb
    public void Gate4_Koordinaten_ausserhalb_werden_geklemmt_nicht_abgelehnt(
        float x, float y, double expectedX, double expectedY)
    {
        // Geklemmt statt abgelehnt: Wer die Maus ueber den Rand zieht, erwartet
        // ein Verhalten am Rand, keine ignorierten Events.
        var (state, guard) = Build();
        MakeSharing(state, control: true);

        var result = guard.CheckPointer(x, y);

        Assert.True(result.Allowed);
        Assert.Equal(expectedX, result.ScreenPoint!.Value.X, precision: 1);
        Assert.Equal(expectedY, result.ScreenPoint!.Value.Y, precision: 1);
    }

    [Fact]
    public void Gate4_NaN_wird_zuverlaessig_behandelt()
    {
        // Ein boesartiger Viewer kann NaN in einem float-Feld schicken.
        // Math.Clamp mit NaN gibt NaN zurueck - das darf nicht in SendInput landen.
        var (state, guard) = Build();
        MakeSharing(state, control: true);

        var result = guard.CheckPointer(float.NaN, float.NaN);

        Assert.True(result.Allowed);
        Assert.False(double.IsNaN(result.ScreenPoint!.Value.X),
            "NaN darf nicht bis zur Koordinatenberechnung durchkommen");
    }

    // ── Gate 5: Tasten-Blocklist ─────────────────────────────────────────────

    [Fact]
    public void Gate5_Windows_Taste_ist_blockiert()
    {
        Assert.True(InputGuard.IsBlocked(HidKeyMap.LeftGui, KeyModifiers.None));
        Assert.True(InputGuard.IsBlocked(HidKeyMap.RightGui, KeyModifiers.None));
        Assert.True(InputGuard.IsBlocked(0x04, KeyModifiers.Meta));
    }

    [Fact]
    public void Gate5_Fensterwechsel_Kombinationen_sind_blockiert()
    {
        Assert.True(InputGuard.IsBlocked(HidKeyMap.Tab, KeyModifiers.Alt));       // Alt+Tab
        Assert.True(InputGuard.IsBlocked(HidKeyMap.Escape, KeyModifiers.Alt));    // Alt+Esc
        Assert.True(InputGuard.IsBlocked(HidKeyMap.Escape, KeyModifiers.Control));// Ctrl+Esc
    }

    [Fact]
    public void Gate5_Notfall_Hotkey_bleibt_dem_Host_vorbehalten()
    {
        // Ohne diese Sperre koennte der Viewer den Kill-Switch selbst ausloesen.
        Assert.True(InputGuard.IsBlocked(
            HidKeyMap.X, KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift));
    }

    [Fact]
    public void Gate5_normale_Eingaben_bleiben_erlaubt()
    {
        Assert.False(InputGuard.IsBlocked(0x04, KeyModifiers.None));                  // A
        Assert.False(InputGuard.IsBlocked(0x06, KeyModifiers.Control));               // Strg+C
        Assert.False(InputGuard.IsBlocked(0x19, KeyModifiers.Control));               // Strg+V
        Assert.False(InputGuard.IsBlocked(0x2B, KeyModifiers.None));                  // Tab allein
        Assert.False(InputGuard.IsBlocked(0x04, KeyModifiers.Shift));                 // Umschalt+A
    }

    // ── Zaehler ──────────────────────────────────────────────────────────────

    [Fact]
    public void Abgelehnte_Events_werden_nach_Grund_gezaehlt()
    {
        // Ein Viewer, der 500 blockierte Win-Tastendruecke sendet, tut etwas,
        // das der Host sehen sollte.
        var (state, guard) = Build();
        MakeSharing(state, control: false);

        for (var i = 0; i < 5; i++) guard.CheckPointer(0.5f, 0.5f);

        Assert.Equal(5, guard.Rejections[GateRejection.ControlNotGranted]);
        Assert.Equal(5, state.Current.RejectedEventCount);
    }

    // HINWEIS zu Gate 3 (Zielfenster im Vordergrund):
    // Nicht als Unit-Test abgedeckt, weil dafuer ein echtes HWND und
    // GetForegroundWindow noetig waeren. Der Test gehoert in eine
    // Integrationstest-Suite, die ein Fenster erzeugt, es in den Vordergrund
    // bringt und danach ein anderes aktiviert. Siehe docs/06-build-and-run.md,
    // Abschnitt "Manuelle Testfaelle" - dort ist der Ablauf als Handtest
    // beschrieben, bis die Integrationstests stehen.
}
