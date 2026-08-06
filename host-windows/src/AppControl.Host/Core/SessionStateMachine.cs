using System.Diagnostics;
using AppControl.Host.Audit;
using Microsoft.Extensions.Logging;

namespace AppControl.Host.Core;

public enum SessionState { Idle, Pairing, AwaitingConsent, Sharing, Paused, Terminating }

/// <summary>Unveraenderliche Momentaufnahme. Alles, was die UI zeigt, kommt hierher.</summary>
public sealed record SessionSnapshot
{
    public SessionState State { get; init; } = SessionState.Idle;
    public ShareScope? Scope { get; init; }
    public bool ControlGranted { get; init; }
    public PeerIdentity? Peer { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public TimeSpan? MaxDuration { get; init; }
    public long InjectedEventCount { get; init; }
    public long RejectedEventCount { get; init; }

    public TimeSpan Elapsed => StartedAt is null ? TimeSpan.Zero : DateTimeOffset.UtcNow - StartedAt.Value;

    public TimeSpan? Remaining => MaxDuration is null || StartedAt is null
        ? null
        : MaxDuration.Value - Elapsed is var r && r > TimeSpan.Zero ? r : TimeSpan.Zero;

    /// <summary>Fliessen ueberhaupt Frames? Die CaptureEngine fragt das pro Frame.</summary>
    public bool IsStreaming => State == SessionState.Sharing;

    /// <summary>Darf Input injiziert werden? Beides muss stimmen - siehe docs/03, Gates 1+2.</summary>
    public bool IsInputAllowed => State == SessionState.Sharing && ControlGranted;
}

public sealed record PeerIdentity(string Name, string Fingerprint, bool IsKnown, int PreviousSessions);

public sealed record ConsentDecision(
    bool Granted,
    ShareScope? Scope,
    bool ControlGranted,
    TimeSpan MaxDuration);

/// <summary>
/// Die einzige Wahrheitsquelle ueber den Zustand des Hosts.
///
/// ENTWURFSPRINZIP: Kein anderer Teil des Programms haelt eine eigene Kopie dieses
/// Zustands. CaptureEngine und InputInjector besitzen kein Flag "laeuft gerade" -
/// sie fragen bei jedem Frame und jedem Event <see cref="Current"/> ab. Dadurch
/// ist es strukturell unmoeglich, dass gestreamt wird, waehrend das Overlay etwas
/// anderes anzeigt. Genau dieser Fehler wuerde ein Transparenzversprechen wertlos
/// machen.
///
/// Alle Uebergaenge sind hier zentralisiert und protokolliert. Wer Transparenz aus
/// diesem Programm entfernen will, muss diese Datei anfassen - das ist Absicht.
/// Siehe docs/03-consent-and-transparency.md §3.1.
/// </summary>
public sealed class SessionStateMachine
{
    private readonly object _gate = new();
    private readonly IAuditLog _audit;
    private readonly ILogger<SessionStateMachine> _log;
    private SessionSnapshot _current = new();

    /// <summary>
    /// Wird bei JEDER Zustandsaenderung ausgeloest. Overlay, Tray, Dashboard und der
    /// Protokoll-Sender haengen alle hier dran und koennen deshalb nicht auseinanderlaufen.
    /// Handler laufen ausserhalb des Locks (siehe <see cref="Mutate"/>).
    /// </summary>
    public event Action<SessionSnapshot>? SnapshotChanged;

    /// <summary>Signalisiert, dass sofort alles abzuschalten ist. Fuer Notfall-Pfade.</summary>
    public event Action<StopReason>? StopRequested;

    public SessionStateMachine(IAuditLog audit, ILogger<SessionStateMachine> log)
    {
        _audit = audit;
        _log = log;
    }

    public SessionSnapshot Current
    {
        get { lock (_gate) return _current; }
    }

    // ── Uebergaenge ──────────────────────────────────────────────────────────

    public void BeginPairing()
        => Mutate(s => s.State is SessionState.Idle
            ? s with { State = SessionState.Pairing }
            : throw new InvalidOperationException($"Pairing aus Zustand {s.State} nicht moeglich"),
            AuditEventKind.PairingStarted);

    public void PeerConnected(PeerIdentity peer)
        => Mutate(s => s with { State = SessionState.AwaitingConsent, Peer = peer },
            AuditEventKind.PeerConnected,
            $"peer={peer.Name} fp={peer.Fingerprint} bekannt={peer.IsKnown}");

    /// <summary>
    /// Der Host hat im Consent-Dialog entschieden. Das ist der einzige Weg,
    /// in den Zustand <see cref="SessionState.Sharing"/> zu gelangen.
    /// </summary>
    public void ApplyConsent(ConsentDecision decision)
    {
        if (!decision.Granted)
        {
            Stop(StopReason.ConsentDenied);
            return;
        }

        ArgumentNullException.ThrowIfNull(decision.Scope);

        Mutate(s => s with
        {
            State = SessionState.Sharing,
            Scope = decision.Scope,
            ControlGranted = decision.ControlGranted,
            StartedAt = DateTimeOffset.UtcNow,
            MaxDuration = decision.MaxDuration,
            InjectedEventCount = 0,
            RejectedEventCount = 0,
        },
        AuditEventKind.ConsentGranted,
        $"scope={decision.Scope.DisplayName} control={decision.ControlGranted} " +
        $"maxDauer={decision.MaxDuration.TotalMinutes:F0}min");
    }

    public void Pause()
        => Mutate(s => s.State is SessionState.Sharing
            // Steuerung wird beim Pausieren HART entzogen, nicht nur durch Gate 1
            // gefiltert. Doppelt gesichert: Selbst wenn Gate 1 je umgangen wuerde,
            // steht ControlGranted auf false.
            ? s with { State = SessionState.Paused, ControlGranted = false }
            : s,
            AuditEventKind.Paused);

    /// <summary>
    /// Fortsetzen. Steuerung kommt NICHT automatisch zurueck - sie muss erneut
    /// erteilt werden. Ein Klick auf "Fortsetzen" ist eine Zustimmung zum Zeigen,
    /// nicht zum Steuern.
    /// </summary>
    public void Resume()
        => Mutate(s => s.State is SessionState.Paused
            ? s with { State = SessionState.Sharing }
            : s,
            AuditEventKind.Resumed);

    public void SetControlGranted(bool granted, string reason)
        => Mutate(s => s.ControlGranted == granted ? s : s with { ControlGranted = granted },
            granted ? AuditEventKind.ControlGranted : AuditEventKind.ControlRevoked,
            reason);

    /// <summary>Scope-Wechsel mitten in der Sitzung. Der Viewer wird sofort informiert.</summary>
    public void ChangeScope(ShareScope scope)
        => Mutate(s => s.State is SessionState.Sharing or SessionState.Paused
            ? s with { Scope = scope }
            : s,
            AuditEventKind.ScopeChanged, scope.DisplayName);

    public void Stop(StopReason reason)
    {
        bool wasActive;
        lock (_gate)
        {
            wasActive = _current.State is not (SessionState.Idle or SessionState.Terminating);
            if (!wasActive) return;
            // Erst hart alles verbieten, DANN abbauen. Die Reihenfolge zaehlt:
            // Ein Event, das waehrend des Abbaus eintrifft, darf nicht mehr durch.
            _current = _current with { State = SessionState.Terminating, ControlGranted = false };
        }

        _audit.Write(AuditEventKind.SessionStopped, reason.ToString());
        _log.LogInformation("Sitzung beendet: {Reason}", reason);

        SnapshotChanged?.Invoke(Current);
        StopRequested?.Invoke(reason);
    }

    /// <summary>Wird vom Sitzungs-Controller aufgerufen, nachdem alles abgebaut ist.</summary>
    public void ResetToIdle()
        => Mutate(_ => new SessionSnapshot(), AuditEventKind.SessionEnded);

    // ── Zaehler ──────────────────────────────────────────────────────────────
    // Bewusst ohne Snapshot-Event: Bei 200 Events/s wuerde jedes Ereignis die
    // gesamte UI-Kette antriggern. Das Overlay pollt diese Werte stattdessen im
    // Sekundentakt.

    private long _injected;
    private long _rejected;

    public void CountInjected()
    {
        var n = Interlocked.Increment(ref _injected);
        lock (_gate) _current = _current with { InjectedEventCount = n };
    }

    public void CountRejected()
    {
        var n = Interlocked.Increment(ref _rejected);
        lock (_gate) _current = _current with { RejectedEventCount = n };
    }

    // ── Intern ───────────────────────────────────────────────────────────────

    private void Mutate(Func<SessionSnapshot, SessionSnapshot> transition,
                        AuditEventKind kind, string? detail = null)
    {
        SessionSnapshot next;
        lock (_gate)
        {
            var previous = _current;
            next = transition(previous);
            if (ReferenceEquals(previous, next)) return;   // kein echter Uebergang
            _current = next;
            _log.LogInformation("Zustand {From} -> {To} {Detail}", previous.State, next.State, detail);
        }

        _audit.Write(kind, detail);

        // Handler ausserhalb des Locks aufrufen: Sie beruehren die UI, und ein
        // UI-Dispatch unter gehaltenem Lock ist ein Deadlock, der nur unter Last
        // auftritt - also genau dann, wenn er am meisten schadet.
        SnapshotChanged?.Invoke(next);
    }

    [Conditional("DEBUG")]
    public void AssertInvariants()
    {
        var s = Current;
        Debug.Assert(!(s.ControlGranted && s.State != SessionState.Sharing),
            "Steuerung darf ausserhalb von Sharing nie erteilt sein");
        Debug.Assert(!(s.IsStreaming && s.Scope is null),
            "Streaming ohne definierten Scope ist unmoeglich");
    }
}
