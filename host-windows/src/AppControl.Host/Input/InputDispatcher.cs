using AppControl.Host.Core;
using Microsoft.Extensions.Logging;

namespace AppControl.Host.Input;

/// <summary>
/// Bindeglied zwischen Netzwerk und Injektion: dekodieren, durch die Gates
/// schicken, injizieren.
///
/// Die Trennung von <see cref="InputGuard"/> (entscheidet) und
/// <see cref="InputInjector"/> (fuehrt aus) ist bewusst: Es gibt in diesem
/// Programm keinen Aufruf von InputInjector, der nicht vorher durch den Guard
/// gelaufen ist, und das laesst sich durch Lesen dieser einen Datei ueberpruefen.
/// </summary>
public sealed class InputDispatcher
{
    private readonly InputGuard _guard;
    private readonly InputInjector _injector;
    private readonly SessionStateMachine _state;
    private readonly ILogger<InputDispatcher> _log;

    /// <summary>Meldet dem Viewer, warum Events verworfen wurden (docs/04 §4.4).</summary>
    public event Action<GateRejection, long>? EventRejected;

    private readonly Dictionary<GateRejection, long> _sinceLastReport = new();
    private DateTimeOffset _lastReport = DateTimeOffset.MinValue;

    public InputDispatcher(InputGuard guard, InputInjector injector,
                           SessionStateMachine state, ILogger<InputDispatcher> log)
    {
        _guard = guard;
        _injector = injector;
        _state = state;
        _log = log;
    }

    /// <summary>Einstiegspunkt fuer Rohdaten aus den DataChannels ac-input / ac-cursor.</summary>
    public void HandleRaw(ReadOnlySpan<byte> payload)
    {
        var evt = InputDecoder.TryDecode(payload);
        if (evt is null)
        {
            _log.LogDebug("Undekodierbares Input-Paket ({Bytes} Byte) verworfen", payload.Length);
            return;
        }
        Handle(evt);
    }

    public void Handle(InputEvent evt)
    {
        switch (evt)
        {
            case InputEvent.MouseMove m:
            {
                var gate = _guard.CheckPointer(m.X, m.Y);
                if (!gate.Allowed) { Report(gate.Rejection); return; }
                _injector.MoveTo(gate.ScreenPoint!.Value);
                break;
            }

            case InputEvent.MouseButtonEvent b:
            {
                var gate = _guard.CheckPointer(b.X, b.Y);
                if (!gate.Allowed) { Report(gate.Rejection); return; }
                _injector.MouseButtonAt(gate.ScreenPoint!.Value, b.Button, b.Pressed);
                break;
            }

            case InputEvent.MouseScroll s:
            {
                var gate = _guard.CheckPointer(s.X, s.Y);
                if (!gate.Allowed) { Report(gate.Rejection); return; }
                _injector.Scroll(gate.ScreenPoint!.Value, s.DeltaX, s.DeltaY, s.Precise);
                break;
            }

            case InputEvent.Key k:
            {
                var gate = _guard.CheckKey(k.HidUsage, k.Modifiers);
                if (!gate.Allowed) { Report(gate.Rejection); return; }
                _injector.SendKey(k.HidUsage, k.Pressed);
                break;
            }

            case InputEvent.Text t:
            {
                // Text laeuft durch dieselben Gates wie Tasten - mit HID-Usage 0,
                // die nie auf der Blocklist steht, aber Gate 1-3 durchlaeuft.
                var gate = _guard.CheckKey(0, KeyModifiers.None);
                if (!gate.Allowed) { Report(gate.Rejection); return; }
                _injector.SendText(t.Value);
                break;
            }

            case InputEvent.ReleaseAll:
                // Bewusst OHNE Gate-Pruefung: Tasten freizugeben verringert immer
                // den Zustand, den der Viewer auf dem Host haelt. Das zu blockieren
                // koennte nur schaden - eine haengende Taste waere die Folge.
                _injector.ReleaseAll();
                break;
        }
    }

    /// <summary>
    /// Meldet Ablehnungen gebuendelt an den Viewer, damit dieser erklaeren kann,
    /// warum nichts passiert ("Zielfenster nicht aktiv") statt still zu versagen.
    /// Gebuendelt, weil eine Meldung pro abgelehntem Event bei gehaltener Maus
    /// den Control-Channel fluten wuerde.
    /// </summary>
    private void Report(GateRejection reason)
    {
        long count;
        lock (_sinceLastReport)
        {
            count = _sinceLastReport.GetValueOrDefault(reason) + 1;
            _sinceLastReport[reason] = count;

            var now = DateTimeOffset.UtcNow;
            if (now - _lastReport < TimeSpan.FromMilliseconds(500)) return;
            _lastReport = now;
            _sinceLastReport.Clear();
        }

        EventRejected?.Invoke(reason, count);
    }

    /// <summary>
    /// Vom Sitzungs-Controller bei Pause, Stopp und Steuerungsentzug aufzurufen.
    /// Siehe docs/03 §3.6, Schritt 2.
    /// </summary>
    public void ReleaseEverything() => _injector.ReleaseAll();
}
