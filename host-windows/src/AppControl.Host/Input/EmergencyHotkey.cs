using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using AppControl.Host.Core;
using AppControl.Host.Interop;
using Microsoft.Extensions.Logging;

namespace AppControl.Host.Input;

/// <summary>
/// Der Not-Aus. Zwei unabhaengige Wege, absichtlich redundant.
///
/// WEG 1: RegisterHotKey(Strg+Alt+Umschalt+X) - der normale, dokumentierte Weg.
///        Kann fehlschlagen, wenn ein anderer Prozess die Kombination belegt.
///
/// WEG 2: WH_KEYBOARD_LL-Hook, der dreimal schnelles Tippen der RECHTEN Strg-Taste
///        erkennt. Braucht keine Registrierung, kann nicht belegt sein, und
///        funktioniert auch, wenn der Viewer gerade Modifier gedrueckt haelt -
///        was Weg 1 stoeren wuerde.
///
/// ENTSCHEIDEND: Der Hook prueft LLKHF_INJECTED und ignoriert synthetische Events.
/// Ohne diese Pruefung koennte der Viewer den Kill-Switch selbst ausloesen oder
/// - schlimmer - durch Tastenfluten die Erkennung des echten Tastendrucks stoeren.
/// Siehe docs/03-consent-and-transparency.md §3.6.
/// </summary>
public sealed class EmergencyHotkey : IDisposable
{
    private const int HotkeyId = 0xACC0;
    private static readonly TimeSpan TripleTapWindow = TimeSpan.FromMilliseconds(600);

    private readonly SessionStateMachine _state;
    private readonly ILogger<EmergencyHotkey> _log;

    private HwndSource? _source;
    private nint _hookHandle;
    private NativeMethods.LowLevelKeyboardProc? _hookProc;   // Feld: verhindert GC des Delegates
    private readonly Queue<DateTimeOffset> _rightCtrlTaps = new();
    private bool _registered;

    public EmergencyHotkey(SessionStateMachine state, ILogger<EmergencyHotkey> log)
    {
        _state = state;
        _log = log;
    }

    /// <summary>Mit einem beliebigen WPF-Fenster verknuepfen (auch einem unsichtbaren).</summary>
    public void Attach(Window window)
    {
        var helper = new WindowInteropHelper(window);
        if (helper.Handle == nint.Zero)
        {
            window.SourceInitialized += (_, _) => Attach(window);
            return;
        }

        _source = HwndSource.FromHwnd(helper.Handle);
        _source?.AddHook(WndProc);

        _registered = NativeMethods.RegisterHotKey(
            helper.Handle, HotkeyId,
            NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_SHIFT | NativeMethods.MOD_NOREPEAT,
            NativeMethods.VK_X);

        if (_registered)
            _log.LogInformation("Notfall-Hotkey Strg+Alt+Umschalt+X registriert");
        else
            _log.LogWarning("Notfall-Hotkey konnte nicht registriert werden (evtl. belegt). " +
                            "Der Dreifach-Tipp auf die rechte Strg-Taste bleibt verfuegbar.");

        InstallHook();
    }

    private void InstallHook()
    {
        _hookProc = HookCallback;
        var module = NativeMethods.GetModuleHandle(null);
        _hookHandle = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _hookProc, module, 0);

        if (_hookHandle == nint.Zero)
            _log.LogError("Tastatur-Hook konnte nicht installiert werden - " +
                          "der Dreifach-Tipp steht nicht zur Verfuegung.");
    }

    private nint HookCallback(int nCode, nint wParam, nint lParam)
    {
        if (nCode < 0) return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);

        try
        {
            var msg = (int)wParam;
            if (msg is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN)
            {
                var data = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);

                // DAS ist die entscheidende Zeile: synthetische Events - also alles,
                // was ueber SendInput kam, insbesondere unsere eigenen Injektionen -
                // koennen den Not-Aus nicht ausloesen und nicht stoeren.
                var injected = (data.flags & NativeMethods.LLKHF_INJECTED) != 0;

                if (!injected && data.vkCode == NativeMethods.VK_RCONTROL)
                {
                    RegisterRightCtrlTap();
                }
            }
        }
        catch (Exception ex)
        {
            // Eine Exception im Hook wuerde Windows dazu bringen, ihn zu entfernen -
            // und damit den Not-Aus stillschweigend zu deaktivieren. Deshalb hier
            // ein umfassender Catch: Der Hook muss ueberleben, was auch passiert.
            _log.LogError(ex, "Fehler im Tastatur-Hook - Hook bleibt aktiv");
        }

        return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    private void RegisterRightCtrlTap()
    {
        var now = DateTimeOffset.UtcNow;
        lock (_rightCtrlTaps)
        {
            _rightCtrlTaps.Enqueue(now);
            while (_rightCtrlTaps.Count > 0 && now - _rightCtrlTaps.Peek() > TripleTapWindow)
                _rightCtrlTaps.Dequeue();

            if (_rightCtrlTaps.Count < 3) return;
            _rightCtrlTaps.Clear();
        }

        _log.LogWarning("Not-Aus durch Dreifach-Tipp auf die rechte Strg-Taste");
        Trigger();
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && (int)wParam == HotkeyId)
        {
            _log.LogWarning("Not-Aus durch Hotkey Strg+Alt+Umschalt+X");
            Trigger();
            handled = true;
        }
        return nint.Zero;
    }

    private void Trigger()
    {
        // Wenn nichts laeuft, ist der Not-Aus ein No-op - kein Fehler, kein Dialog.
        if (_state.Current.State is SessionState.Idle) return;
        _state.Stop(StopReason.EmergencyHotkey);
    }

    public void Dispose()
    {
        if (_source is not null)
        {
            if (_registered) NativeMethods.UnregisterHotKey(_source.Handle, HotkeyId);
            _source.RemoveHook(WndProc);
        }
        if (_hookHandle != nint.Zero) NativeMethods.UnhookWindowsHookEx(_hookHandle);
        _hookHandle = nint.Zero;
        _hookProc = null;
    }
}
