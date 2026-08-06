using System.Drawing;
using System.Windows.Forms;
using AppControl.Host.Core;

namespace AppControl.Host.Ui;

/// <summary>
/// Tray-Icon: die zweite permanente Zustandsanzeige neben dem Overlay.
///
/// Redundanz ist hier Absicht. Das Overlay kann theoretisch von einem
/// Vollbild-Programm verdeckt werden (der Watchdog korrigiert das, aber es gibt
/// ein Zeitfenster); der Tray-Bereich bleibt sichtbar. Zwei unabhaengige Anzeigen
/// bedeuten, dass ein Fehler in einer nicht dazu fuehrt, dass der Host gar nichts
/// mehr sieht.
///
/// FARBE ALLEIN TRAEGT KEINE INFORMATION: Der Tooltip nennt den Zustand immer
/// ausgeschrieben, damit das bei Farbfehlsichtigkeit funktioniert.
/// Siehe docs/03-consent-and-transparency.md §3.5.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly SessionStateMachine _state;
    private readonly System.Windows.Forms.Timer _pulseTimer;
    private bool _pulseOn;

    public event Action? ShowDashboardRequested;
    public event Action? PauseToggleRequested;
    public event Action? StopRequested;
    public event Action? ChangeScopeRequested;
    public event Action? ShowAuditLogRequested;
    public event Action? ExitRequested;

    public TrayIcon(SessionStateMachine state)
    {
        _state = state;

        _icon = new NotifyIcon
        {
            Visible = true,
            Text = "AppControl — nicht verbunden",
            Icon = CreateStateIcon(Color.Gray, ring: false),
            ContextMenuStrip = BuildMenu(),
        };

        _icon.DoubleClick += (_, _) => ShowDashboardRequested?.Invoke();

        _pulseTimer = new System.Windows.Forms.Timer { Interval = 600 };
        _pulseTimer.Tick += (_, _) => Pulse();

        _state.SnapshotChanged += Apply;
        Apply(_state.Current);
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();

        menu.Items.Add("Status öffnen", null, (_, _) => ShowDashboardRequested?.Invoke());
        menu.Items.Add(new ToolStripSeparator());

        var pause = new ToolStripMenuItem("Pause", null, (_, _) => PauseToggleRequested?.Invoke())
        { Name = "pause" };
        menu.Items.Add(pause);

        // Fett hervorgehoben: Im Zweifel soll die Person genau diesen Eintrag
        // finden, ohne lesen zu muessen.
        var stop = new ToolStripMenuItem("Sofort beenden", null, (_, _) => StopRequested?.Invoke())
        { Name = "stop", Font = new Font(SystemFonts.MenuFont!, System.Drawing.FontStyle.Bold) };
        menu.Items.Add(stop);

        menu.Items.Add("Freigabe wechseln …", null, (_, _) => ChangeScopeRequested?.Invoke());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Sitzungsprotokoll …", null, (_, _) => ShowAuditLogRequested?.Invoke());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("AppControl beenden", null, (_, _) => ExitRequested?.Invoke());

        menu.Opening += (_, _) => UpdateMenuState(menu);
        return menu;
    }

    private void UpdateMenuState(ContextMenuStrip menu)
    {
        var snapshot = _state.Current;
        var active = snapshot.State is SessionState.Sharing or SessionState.Paused;

        if (menu.Items["pause"] is ToolStripMenuItem pause)
        {
            pause.Enabled = active;
            pause.Text = snapshot.State == SessionState.Paused ? "Fortsetzen" : "Pause";
        }
        if (menu.Items["stop"] is ToolStripMenuItem stop) stop.Enabled = active;
    }

    private void Apply(SessionSnapshot snapshot)
    {
        var (color, ring, text) = snapshot.State switch
        {
            SessionState.Idle =>
                (Color.Gray, false, "AppControl — nicht verbunden"),

            SessionState.Pairing or SessionState.AwaitingConsent =>
                (Color.Goldenrod, false, "AppControl — wartet auf deine Bestätigung"),

            SessionState.Sharing when snapshot.ControlGranted =>
                (Color.Firebrick, true,
                 $"TEILT: {Shorten(snapshot.Scope?.DisplayName)} · STEUERUNG AKTIV · {Format(snapshot.Elapsed)}"),

            SessionState.Sharing =>
                (Color.Firebrick, false,
                 $"TEILT: {Shorten(snapshot.Scope?.DisplayName)} · nur Ansicht · {Format(snapshot.Elapsed)}"),

            SessionState.Paused =>
                (Color.DarkOrange, false, "PAUSIERT — dein Gegenüber sieht ein Standbild"),

            _ => (Color.Gray, false, "AppControl"),
        };

        _icon.Icon?.Dispose();
        _icon.Icon = CreateStateIcon(color, ring);

        // NotifyIcon.Text ist auf 63 Zeichen begrenzt; laengere Werte werfen.
        _icon.Text = text.Length > 63 ? text[..60] + "…" : text;

        var shouldPulse = snapshot.State is SessionState.Pairing or SessionState.AwaitingConsent;
        if (shouldPulse && !_pulseTimer.Enabled) _pulseTimer.Start();
        else if (!shouldPulse && _pulseTimer.Enabled) { _pulseTimer.Stop(); _pulseOn = false; }
    }

    private void Pulse()
    {
        _pulseOn = !_pulseOn;
        _icon.Icon?.Dispose();
        _icon.Icon = CreateStateIcon(_pulseOn ? Color.Goldenrod : Color.DimGray, ring: false);
    }

    /// <summary>
    /// Zeichnet das Icon zur Laufzeit. Spart eingebettete Ressourcen und macht die
    /// Zustandsfarbe direkt aus dem Code ablesbar.
    /// </summary>
    private static Icon CreateStateIcon(Color color, bool ring)
    {
        using var bitmap = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var brush = new SolidBrush(color);
            g.FillEllipse(brush, 2, 2, 12, 12);

            if (ring)
            {
                // Weisser Ring = Steuerung aktiv. Eine zweite, von der Farbe
                // unabhaengige visuelle Dimension.
                using var pen = new Pen(Color.White, 2f);
                g.DrawEllipse(pen, 1, 1, 14, 14);
            }
        }
        return Icon.FromHandle(bitmap.GetHicon());
    }

    private static string Shorten(string? text)
        => string.IsNullOrEmpty(text) ? "—"
         : text.Length > 24 ? text[..21] + "…"
         : text;

    private static string Format(TimeSpan t)
        => t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}"
                             : $"{t.Minutes}:{t.Seconds:D2}";

    /// <summary>Ballon-Benachrichtigung fuer Ereignisse, die der Host sehen muss.</summary>
    public void Notify(string title, string message, ToolTipIcon icon = ToolTipIcon.Info)
        => _icon.ShowBalloonTip(4000, title, message, icon);

    public void Dispose()
    {
        _state.SnapshotChanged -= Apply;
        _pulseTimer.Stop();
        _pulseTimer.Dispose();
        _icon.Visible = false;
        _icon.Icon?.Dispose();
        _icon.Dispose();
    }
}
