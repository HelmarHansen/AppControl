using System.Runtime.InteropServices;
using System.Windows;
using AppControl.Host.Core;
using AppControl.Host.Interop;
using Microsoft.Extensions.Logging;

namespace AppControl.Host.Input;

/// <summary>
/// Injiziert freigegebene Eingaben ueber SendInput.
///
/// Zwei Dinge machen diese Klasse mehr als einen duennen P/Invoke-Wrapper:
///
/// 1. Sie fuehrt Buch ueber JEDE gedrueckte Taste und JEDEN gedrueckten Button.
///    Ohne dieses Buch bliebe der Host beim Notfall-Stopp mit einer klemmenden
///    Umschalttaste oder gedrueckten Maustaste zurueck, wenn der Viewer im Moment
///    des Abbruchs etwas gehalten hat. Siehe docs/03 §3.6, Schritt 2.
///
/// 2. Sie rechnet Koordinaten korrekt auf den VIRTUELLEN Desktop um.
///    MOUSEEVENTF_ABSOLUTE erwartet 0-65535 ueber alle Monitore zusammen, nicht
///    ueber den primaeren. Das ist die haeufigste Fehlerquelle bei
///    Multi-Monitor-Setups. Siehe docs/02-tech-stack.md §2.2.
/// </summary>
public sealed class InputInjector : IDisposable
{
    private readonly SessionStateMachine _state;
    private readonly ILogger<InputInjector> _log;
    private readonly object _gate = new();

    private readonly HashSet<ushort> _heldKeys = [];
    private readonly HashSet<MouseButton> _heldButtons = [];

    public InputInjector(SessionStateMachine state, ILogger<InputInjector> log)
    {
        _state = state;
        _log = log;
    }

    // ── Maus ─────────────────────────────────────────────────────────────────

    public void MoveTo(Point screenPoint)
    {
        var (nx, ny) = ToVirtualDesktopAbsolute(screenPoint);
        var input = new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_MOUSE,
            u = new NativeMethods.InputUnion
            {
                mi = new NativeMethods.MOUSEINPUT
                {
                    dx = nx,
                    dy = ny,
                    dwFlags = NativeMethods.MOUSEEVENTF_MOVE
                            | NativeMethods.MOUSEEVENTF_ABSOLUTE
                            | NativeMethods.MOUSEEVENTF_VIRTUALDESK,
                },
            },
        };
        Send(input);
    }

    public void MouseButtonAt(Point screenPoint, MouseButton button, bool pressed)
    {
        var (nx, ny) = ToVirtualDesktopAbsolute(screenPoint);

        var (downFlag, upFlag, mouseData) = button switch
        {
            MouseButton.Left    => (NativeMethods.MOUSEEVENTF_LEFTDOWN,   NativeMethods.MOUSEEVENTF_LEFTUP,   0u),
            MouseButton.Right   => (NativeMethods.MOUSEEVENTF_RIGHTDOWN,  NativeMethods.MOUSEEVENTF_RIGHTUP,  0u),
            MouseButton.Middle  => (NativeMethods.MOUSEEVENTF_MIDDLEDOWN, NativeMethods.MOUSEEVENTF_MIDDLEUP, 0u),
            MouseButton.Back    => (NativeMethods.MOUSEEVENTF_XDOWN,      NativeMethods.MOUSEEVENTF_XUP,      NativeMethods.XBUTTON1),
            MouseButton.Forward => (NativeMethods.MOUSEEVENTF_XDOWN,      NativeMethods.MOUSEEVENTF_XUP,      NativeMethods.XBUTTON2),
            _ => (0u, 0u, 0u),
        };
        if (downFlag == 0) return;

        // Bewegung und Klick in EINEM SendInput-Aufruf: Zwischen beiden kann sich
        // kein echtes Benutzer-Event draengeln, sonst landet der Klick woanders.
        var inputs = new[]
        {
            new NativeMethods.INPUT
            {
                type = NativeMethods.INPUT_MOUSE,
                u = new NativeMethods.InputUnion
                {
                    mi = new NativeMethods.MOUSEINPUT
                    {
                        dx = nx, dy = ny,
                        dwFlags = NativeMethods.MOUSEEVENTF_MOVE
                                | NativeMethods.MOUSEEVENTF_ABSOLUTE
                                | NativeMethods.MOUSEEVENTF_VIRTUALDESK,
                    },
                },
            },
            new NativeMethods.INPUT
            {
                type = NativeMethods.INPUT_MOUSE,
                u = new NativeMethods.InputUnion
                {
                    mi = new NativeMethods.MOUSEINPUT
                    {
                        dx = nx, dy = ny,
                        mouseData = mouseData,
                        dwFlags = (pressed ? downFlag : upFlag)
                                | NativeMethods.MOUSEEVENTF_ABSOLUTE
                                | NativeMethods.MOUSEEVENTF_VIRTUALDESK,
                    },
                },
            },
        };

        lock (_gate)
        {
            if (pressed) _heldButtons.Add(button);
            else _heldButtons.Remove(button);
        }

        Send(inputs);
    }

    public void Scroll(Point screenPoint, float deltaX, float deltaY, bool precise)
    {
        MoveTo(screenPoint);

        // macOS liefert positive deltaY fuer "nach oben", Windows erwartet dasselbe
        // Vorzeichen - aber in WHEEL_DELTA-Einheiten (120 pro Rastung).
        // Trackpad-Deltas sind Pixel und werden proportional umgerechnet.
        var scale = precise ? 4.0f : NativeMethods.WHEEL_DELTA;

        var inputs = new List<NativeMethods.INPUT>(2);

        if (Math.Abs(deltaY) > 0.01f)
        {
            inputs.Add(WheelInput(NativeMethods.MOUSEEVENTF_WHEEL, (int)(deltaY * scale)));
        }
        if (Math.Abs(deltaX) > 0.01f)
        {
            inputs.Add(WheelInput(NativeMethods.MOUSEEVENTF_HWHEEL, (int)(deltaX * scale)));
        }
        if (inputs.Count > 0) Send(inputs.ToArray());

        static NativeMethods.INPUT WheelInput(uint flag, int amount) => new()
        {
            type = NativeMethods.INPUT_MOUSE,
            u = new NativeMethods.InputUnion
            {
                mi = new NativeMethods.MOUSEINPUT { mouseData = unchecked((uint)amount), dwFlags = flag },
            },
        };
    }

    // ── Tastatur ─────────────────────────────────────────────────────────────

    public bool SendKey(ushort hidUsage, bool pressed)
    {
        if (!HidKeyMap.TryGetScanCode(hidUsage, out var scanCode, out var extended))
        {
            _log.LogDebug("Keine Scancode-Zuordnung fuer HID-Usage 0x{Usage:X4}", hidUsage);
            return false;
        }

        var flags = NativeMethods.KEYEVENTF_SCANCODE;
        if (extended) flags |= NativeMethods.KEYEVENTF_EXTENDEDKEY;
        if (!pressed) flags |= NativeMethods.KEYEVENTF_KEYUP;

        var input = new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_KEYBOARD,
            u = new NativeMethods.InputUnion
            {
                ki = new NativeMethods.KEYBDINPUT { wVk = 0, wScan = scanCode, dwFlags = flags },
            },
        };

        lock (_gate)
        {
            if (pressed) _heldKeys.Add(hidUsage);
            else _heldKeys.Remove(hidUsage);
        }

        Send(input);
        return true;
    }

    /// <summary>
    /// Text direkt als Unicode einfuegen - umgeht das Tastaturlayout vollstaendig.
    /// Fuer Zeichen, die ueber Tastencodes nicht sinnvoll darstellbar sind:
    /// Emoji, IME-Eingaben, Zeichen aus der macOS-Zeichenpalette.
    /// </summary>
    public void SendText(string text)
    {
        // Surrogatpaare (Emoji) muessen als zwei UTF-16-Einheiten gesendet werden;
        // die Iteration ueber char macht genau das richtige.
        var inputs = new List<NativeMethods.INPUT>(text.Length * 2);
        foreach (var ch in text)
        {
            inputs.Add(UnicodeInput(ch, keyUp: false));
            inputs.Add(UnicodeInput(ch, keyUp: true));
        }
        if (inputs.Count > 0) Send(inputs.ToArray());

        static NativeMethods.INPUT UnicodeInput(char ch, bool keyUp) => new()
        {
            type = NativeMethods.INPUT_KEYBOARD,
            u = new NativeMethods.InputUnion
            {
                ki = new NativeMethods.KEYBDINPUT
                {
                    wVk = 0,
                    wScan = ch,
                    dwFlags = NativeMethods.KEYEVENTF_UNICODE
                            | (keyUp ? NativeMethods.KEYEVENTF_KEYUP : 0),
                },
            },
        };
    }

    // ── Aufraeumen ───────────────────────────────────────────────────────────

    /// <summary>
    /// Gibt alle gehaltenen Tasten und Buttons frei.
    ///
    /// Wird aufgerufen bei: Notfall-Stopp, Pause, Steuerungsentzug,
    /// Verbindungsverlust, und bei ReleaseAll vom Viewer (wenn dessen Fenster den
    /// Fokus verliert). Ohne diesen Aufruf bliebe der Host mit klemmenden Tasten
    /// zurueck - ein Zustand, den nur ein Neustart oder manuelles Druecken loest.
    /// </summary>
    public void ReleaseAll()
    {
        ushort[] keys;
        MouseButton[] buttons;
        lock (_gate)
        {
            keys = [.. _heldKeys];
            buttons = [.. _heldButtons];
            _heldKeys.Clear();
            _heldButtons.Clear();
        }

        if (keys.Length == 0 && buttons.Length == 0) return;
        _log.LogInformation("Gebe {Keys} Tasten und {Buttons} Maustasten frei", keys.Length, buttons.Length);

        var inputs = new List<NativeMethods.INPUT>(keys.Length + buttons.Length);

        foreach (var hid in keys)
        {
            if (!HidKeyMap.TryGetScanCode(hid, out var scan, out var ext)) continue;
            var flags = NativeMethods.KEYEVENTF_SCANCODE | NativeMethods.KEYEVENTF_KEYUP;
            if (ext) flags |= NativeMethods.KEYEVENTF_EXTENDEDKEY;
            inputs.Add(new NativeMethods.INPUT
            {
                type = NativeMethods.INPUT_KEYBOARD,
                u = new NativeMethods.InputUnion
                {
                    ki = new NativeMethods.KEYBDINPUT { wScan = scan, dwFlags = flags },
                },
            });
        }

        foreach (var button in buttons)
        {
            var upFlag = button switch
            {
                MouseButton.Left => NativeMethods.MOUSEEVENTF_LEFTUP,
                MouseButton.Right => NativeMethods.MOUSEEVENTF_RIGHTUP,
                MouseButton.Middle => NativeMethods.MOUSEEVENTF_MIDDLEUP,
                _ => NativeMethods.MOUSEEVENTF_XUP,
            };
            var data = button switch
            {
                MouseButton.Back => NativeMethods.XBUTTON1,
                MouseButton.Forward => NativeMethods.XBUTTON2,
                _ => 0u,
            };
            inputs.Add(new NativeMethods.INPUT
            {
                type = NativeMethods.INPUT_MOUSE,
                u = new NativeMethods.InputUnion
                {
                    mi = new NativeMethods.MOUSEINPUT { mouseData = data, dwFlags = upFlag },
                },
            });
        }

        Send(inputs.ToArray());
    }

    public void Dispose() => ReleaseAll();

    // ── Intern ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Rechnet einen Bildschirmpunkt in SendInput-Absolutkoordinaten um.
    ///
    /// Der Bereich 0-65535 spannt den VIRTUELLEN Desktop auf - also alle Monitore
    /// zusammen, mit dem Ursprung an der oberen linken Ecke des am weitesten
    /// links/oben liegenden Monitors. Bei einem zweiten Monitor LINKS vom
    /// primaeren sind dessen Koordinaten negativ; ohne die Verschiebung um
    /// SM_XVIRTUALSCREEN landen alle Klicks auf dem falschen Bildschirm.
    /// </summary>
    private static (int X, int Y) ToVirtualDesktopAbsolute(Point screenPoint)
    {
        var vx = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
        var vy = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
        var vw = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
        var vh = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);

        if (vw <= 0 || vh <= 0) return (0, 0);

        // -1 im Nenner: 65535 muss der LETZTEN Pixelspalte entsprechen, nicht der
        // ersten jenseits des Randes. Ohne das ist die rechte/untere Pixelreihe
        // nicht erreichbar.
        var nx = (int)Math.Round((screenPoint.X - vx) * 65535.0 / Math.Max(1, vw - 1));
        var ny = (int)Math.Round((screenPoint.Y - vy) * 65535.0 / Math.Max(1, vh - 1));

        return (Math.Clamp(nx, 0, 65535), Math.Clamp(ny, 0, 65535));
    }

    private void Send(params NativeMethods.INPUT[] inputs)
    {
        var sent = NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
        if (sent != inputs.Length)
        {
            // Haeufigste Ursache: Das Zielfenster laeuft erhoeht (UIPI blockiert die
            // Injektion). Das ist eine Windows-Sicherheitsgrenze, die wir bewusst
            // nicht umgehen - siehe docs/02-tech-stack.md §2.2.
            _log.LogWarning("SendInput hat nur {Sent}/{Total} Events angenommen (Win32-Fehler {Error}). " +
                            "Moeglicherweise ist das Zielfenster ein erhoehter Prozess.",
                            sent, inputs.Length, Marshal.GetLastWin32Error());
        }
        else
        {
            _state.CountInjected();
        }
    }
}
