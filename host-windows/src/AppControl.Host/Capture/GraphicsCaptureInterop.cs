using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using WinRT;

namespace AppControl.Host.Capture;

/// <summary>
/// Bruecke von einem HWND/HMONITOR zu einem WinRT-GraphicsCaptureItem.
///
/// WinRT bietet dafuer keine oeffentliche API - der Weg fuehrt ueber das
/// COM-Interface IGraphicsCaptureItemInterop, das nur ueber die Aktivierungs-
/// factory erreichbar ist. Dieser Code ist die Standardloesung und in Microsofts
/// eigenen Beispielen so dokumentiert.
/// </summary>
internal static class GraphicsCaptureInterop
{
    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        nint CreateForWindow([In] nint window, [In] ref Guid iid);
        nint CreateForMonitor([In] nint monitor, [In] ref Guid iid);
    }

    private static IGraphicsCaptureItemInterop GetInterop()
        => GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();

    /// <summary>Capture-Item fuer ein einzelnes Fenster.</summary>
    public static GraphicsCaptureItem? CreateForWindow(nint hwnd)
    {
        try
        {
            var iid = GraphicsCaptureItemIid;
            var raw = GetInterop().CreateForWindow(hwnd, ref iid);
            return raw == nint.Zero ? null : GraphicsCaptureItem.FromAbi(raw);
        }
        catch (COMException)
        {
            // Haeufigster Fall: Das Fenster wurde zwischen Auswahl und Start
            // geschlossen. Kein Programmfehler - der Aufrufer zeigt eine Meldung.
            return null;
        }
    }

    /// <summary>Capture-Item fuer einen ganzen Monitor.</summary>
    public static GraphicsCaptureItem? CreateForMonitor(nint hmonitor)
    {
        try
        {
            var iid = GraphicsCaptureItemIid;
            var raw = GetInterop().CreateForMonitor(hmonitor, ref iid);
            return raw == nint.Zero ? null : GraphicsCaptureItem.FromAbi(raw);
        }
        catch (COMException)
        {
            return null;
        }
    }

    /// <summary>
    /// Prueft, ob das System Windows.Graphics.Capture unterstuetzt (ab Windows 10 1803).
    /// Beim Start aufzurufen, damit der Nutzer eine klare Meldung bekommt statt
    /// einer Exception mitten in der Sitzung.
    /// </summary>
    public static bool IsSupported()
    {
        try { return GraphicsCaptureSession.IsSupported(); }
        catch { return false; }
    }
}
