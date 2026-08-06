using System.Windows;

namespace AppControl.Host.Core;

/// <summary>
/// Was genau freigegeben ist. Diese Typen sind die Antwort auf die Frage
/// "was sieht der Viewer gerade?" - sie werden im Overlay, im Tray-Tooltip
/// und im Consent-Dialog wortgleich angezeigt.
/// </summary>
public abstract record ShareScope
{
    /// <summary>Kurzer Text fuer Overlay und Tray. Muss ohne Kontext verstaendlich sein.</summary>
    public abstract string DisplayName { get; }

    /// <summary>
    /// Aktuelles Zielrechteck in physischen Bildschirmkoordinaten.
    /// Wird bei JEDEM Input-Event neu abgefragt - verschiebt der Host das Fenster,
    /// folgt die Eingabebegrenzung sofort. Siehe docs/03: Gate 4.
    /// </summary>
    public abstract Int32Rect GetTargetRect();

    public sealed record FullScreen(nint MonitorHandle, string MonitorName, Int32Rect Bounds) : ShareScope
    {
        public override string DisplayName => $"Gesamter Bildschirm ({MonitorName})";
        public override Int32Rect GetTargetRect() => Bounds;
    }

    public sealed record SingleWindow(nint Hwnd, string Title, string ProcessName) : ShareScope
    {
        public override string DisplayName => $"{ProcessName} — {Title}";

        public override Int32Rect GetTargetRect()
        {
            // Client-Rect, nicht Fenster-Rect: Der Rahmen und die Titelleiste gehoeren
            // nicht zum erfassten Inhalt, und ein Klick auf das X waere ein Klick
            // ausserhalb dessen, was der Viewer sieht.
            return Interop.NativeMethods.TryGetClientRectOnScreen(Hwnd, out var rect)
                ? rect
                : Int32Rect.Empty;
        }
    }
}

/// <summary>Grund, warum eine Sitzung endete. Landet unveraendert im Audit-Log.</summary>
public enum StopReason
{
    UserStopped,
    EmergencyHotkey,
    PeerDisconnected,
    ConnectionFailed,
    MaxDurationReached,
    ScreenLocked,
    ConsentDenied,
    ConsentTimeout,
    OverlayUnavailable,
    ProtocolViolation,
    ApplicationExit,
}
