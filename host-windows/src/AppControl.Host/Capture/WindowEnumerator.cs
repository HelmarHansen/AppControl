using System.Diagnostics;
using System.Windows;
using AppControl.Host.Core;
using AppControl.Host.Interop;

namespace AppControl.Host.Capture;

/// <summary>Ein im App-Picker anzeigbares Fenster.</summary>
public sealed record CapturableWindow(nint Hwnd, string Title, string ProcessName, Int32Rect Bounds)
{
    public ShareScope ToScope() => new ShareScope.SingleWindow(Hwnd, Title, ProcessName);
}

/// <summary>Ein im App-Picker anzeigbarer Monitor.</summary>
public sealed record CapturableMonitor(nint Handle, string Name, Int32Rect Bounds, bool IsPrimary)
{
    public ShareScope ToScope() => new ShareScope.FullScreen(Handle, Name, Bounds);
}

/// <summary>
/// Sammelt die Fenster und Monitore, die der Host im App-Picker auswaehlen kann.
///
/// ALTERNATIVE: Windows bringt mit `GraphicsCapturePicker` einen eigenen
/// Auswahldialog mit, der diesen ganzen Code ersparen wuerde. Er ist aber nicht
/// anpassbar - kein Warnhinweis, keine gemeinsame Darstellung von Fenstern und
/// Monitoren, keine Suche. Da die Auswahl Teil des Consent-Erlebnisses ist und
/// nicht nur eine technische Notwendigkeit, ist der eigene Picker die richtige
/// Wahl. Siehe docs/03-consent-and-transparency.md §3.3.
///
/// Wer den Systemdialog bevorzugt, ersetzt den Aufruf in AppPickerWindow durch:
///     var picker = new GraphicsCapturePicker();
///     InitializeWithWindow.Initialize(picker, hwnd);
///     var item = await picker.PickSingleItemAsync();
/// </summary>
public static class WindowEnumerator
{
    /// <summary>
    /// Alle sichtbaren Top-Level-Fenster, die als Freigabeziel taugen.
    /// Gefiltert wird gegen die drei Kategorien, die im Picker sonst als
    /// Geistereintraege erscheinen.
    /// </summary>
    public static List<CapturableWindow> EnumerateWindows()
    {
        var result = new List<CapturableWindow>();
        var ownProcessId = (uint)Environment.ProcessId;

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!IsCapturable(hwnd, ownProcessId)) return true;

            var title = NativeMethods.GetWindowTitle(hwnd);
            if (string.IsNullOrWhiteSpace(title)) return true;

            if (!NativeMethods.TryGetClientRectOnScreen(hwnd, out var bounds)) return true;
            // Winzige Fenster sind meist unsichtbare Hilfsfenster von Frameworks.
            if (bounds.Width < 80 || bounds.Height < 60) return true;

            result.Add(new CapturableWindow(hwnd, title, GetProcessName(hwnd), bounds));
            return true;
        }, nint.Zero);

        return result.OrderBy(w => w.ProcessName).ThenBy(w => w.Title).ToList();
    }

    private static bool IsCapturable(nint hwnd, uint ownProcessId)
    {
        // 1. Unsichtbare und minimierte Fenster
        if (!NativeMethods.IsWindowVisible(hwnd)) return false;
        if (NativeMethods.IsIconic(hwnd)) return false;

        // 2. "Cloaked" Fenster - unsichtbare UWP-Huellen, die Windows fuer jede
        //    Store-App vorhaelt. Sie melden sich als sichtbar, sind es aber nicht.
        if (NativeMethods.IsCloaked(hwnd)) return false;

        // 3. Werkzeugfenster ohne App-Fenster-Flag: Tooltips, Popups, Splash-Screens
        var exStyle = NativeMethods.GetWindowLongW(hwnd, NativeMethods.GWL_EXSTYLE);
        if ((exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0 &&
            (exStyle & NativeMethods.WS_EX_APPWINDOW) == 0) return false;

        // 4. Unsere eigenen Fenster. Sonst koennte der Host das Overlay oder den
        //    Picker selbst freigeben - im besten Fall verwirrend, im schlimmsten
        //    ein Endlos-Spiegel.
        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == ownProcessId) return false;

        return true;
    }

    private static string GetProcessName(nint hwnd)
    {
        try
        {
            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch
        {
            // Prozess bereits beendet oder Zugriff verweigert (erhoehter Prozess).
            // Kein Grund, das Fenster auszublenden - der Titel reicht zur Auswahl.
            return "unbekannt";
        }
    }

    /// <summary>Alle angeschlossenen Monitore.</summary>
    public static List<CapturableMonitor> EnumerateMonitors()
    {
        var result = new List<CapturableMonitor>();
        var index = 1;

        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
        {
            var bounds = new Int32Rect(
                screen.Bounds.X, screen.Bounds.Y, screen.Bounds.Width, screen.Bounds.Height);

            result.Add(new CapturableMonitor(
                Handle: MonitorHandleFor(screen),
                Name: screen.Primary ? $"Monitor {index} (primaer)" : $"Monitor {index}",
                Bounds: bounds,
                IsPrimary: screen.Primary));
            index++;
        }

        return result;
    }

    private static nint MonitorHandleFor(System.Windows.Forms.Screen screen)
    {
        // System.Windows.Forms.Screen kapselt das HMONITOR in einem internen Feld.
        // MonitorFromPoint auf der Mitte des Monitors liefert dasselbe Handle
        // zuverlaessig und ohne Reflection.
        var center = new NativeMethods.POINT
        {
            X = screen.Bounds.X + screen.Bounds.Width / 2,
            Y = screen.Bounds.Y + screen.Bounds.Height / 2,
        };
        return MonitorFromPoint(center, MONITOR_DEFAULTTONEAREST);
    }

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(NativeMethods.POINT pt, uint dwFlags);
}
