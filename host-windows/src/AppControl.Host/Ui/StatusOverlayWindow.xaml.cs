using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using AppControl.Host.Core;
using AppControl.Host.Interop;
using Microsoft.Extensions.Logging;

namespace AppControl.Host.Ui;

/// <summary>
/// Das permanente Statusband.
///
/// AUS EINER ANZEIGE WIRD HIER EINE GARANTIE: Ein Watchdog prueft alle zwei
/// Sekunden, ob das Fenster existiert, sichtbar und ganz oben ist. Schlaegt die
/// Wiederherstellung dreimal fehl, wird die SITZUNG BEENDET. Es gibt keinen
/// Zustand "streamt, aber Overlay ist weg".
///
/// Siehe docs/03-consent-and-transparency.md §3.4.
/// </summary>
public partial class StatusOverlayWindow : Window
{
    private readonly SessionStateMachine _state;
    private readonly ILogger _log;
    private readonly DispatcherTimer _watchdog;
    private readonly DispatcherTimer _clock;

    private bool _allowClose;
    private int _watchdogFailures;

    public event Action? PauseRequested;
    public event Action? ResumeRequested;
    public event Action? StopRequested;

    public StatusOverlayWindow(SessionStateMachine state, ILogger log)
    {
        InitializeComponent();
        _state = state;
        _log = log;

        _watchdog = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        _watchdog.Tick += (_, _) => EnforceVisibility();

        _clock = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _clock.Tick += (_, _) => UpdateElapsed();

        Loaded += OnLoaded;
        _state.SnapshotChanged += OnSnapshotChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyWindowStyles();
        PositionAtTopCenter();
        Apply(_state.Current);
        _watchdog.Start();
        _clock.Start();
    }

    /// <summary>
    /// Erweiterte Fensterstile, die das Overlay unaufdringlich, aber
    /// unentrinnbar machen.
    /// </summary>
    private void ApplyWindowStyles()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == nint.Zero) return;

        var exStyle = NativeMethods.GetWindowLongW(hwnd, NativeMethods.GWL_EXSTYLE);

        // WS_EX_TOOLWINDOW: erscheint nicht in Alt+Tab - kann also nicht
        //                   versehentlich weggetabbt werden.
        // WS_EX_NOACTIVATE: stiehlt beim Klicken nicht den Fokus, sonst wuerde
        //                   Gate 3 (Zielfenster im Vordergrund) bei jedem Klick
        //                   auf das Overlay fehlschlagen.
        exStyle |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;

        SetWindowLongW(hwnd, NativeMethods.GWL_EXSTYLE, exStyle);

        // PLATZHALTER: Maus-Durchlaessigkeit ausserhalb der Buttons.
        //
        // Der saubere Weg ist ein Hit-Test-Callback (WM_NCHITTEST), der HTTRANSPARENT
        // zurueckgibt, ausser der Punkt liegt ueber einem Button. WS_EX_TRANSPARENT
        // pauschal zu setzen ist einfacher, macht aber auch die Buttons
        // unerreichbar - also genau das Gegenteil dessen, was wir brauchen.
        //
        // Bis dahin: Das Band ist schmal und sitzt oben mittig, wo es kaum stoert.
    }

    private void PositionAtTopCenter()
    {
        var work = SystemParameters.WorkArea;
        Left = work.Left + (work.Width - ActualWidth) / 2;
        Top = work.Top + 8;
    }

    /// <summary>
    /// Der Watchdog. Stellt Sichtbarkeit und Topmost wieder her - und beendet die
    /// Sitzung, wenn das dauerhaft nicht gelingt.
    /// </summary>
    private void EnforceVisibility()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == nint.Zero) { RegisterFailure("Fensterhandle verloren"); return; }

            if (!IsVisible)
            {
                _log.LogWarning("Overlay war unsichtbar - wird wiederhergestellt");
                Show();
            }

            // Topmost erneut durchsetzen: Andere Anwendungen (Vollbildspiele,
            // Praesentationsmodi) koennen sich darueberlegen. Ein einmaliges
            // Topmost=true beim Start reicht deshalb nicht.
            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);

            // Position auf den sichtbaren Arbeitsbereich klemmen - verhindert
            // "nach unten aus dem Bild ziehen".
            var work = SystemParameters.WorkArea;
            if (Top < work.Top - 2 || Top > work.Bottom - ActualHeight ||
                Left < work.Left - 2 || Left > work.Right - ActualWidth)
            {
                PositionAtTopCenter();
            }

            _watchdogFailures = 0;
        }
        catch (Exception ex)
        {
            RegisterFailure(ex.Message);
        }
    }

    private void RegisterFailure(string reason)
    {
        _watchdogFailures++;
        _log.LogError("Overlay-Watchdog fehlgeschlagen ({Count}/3): {Reason}", _watchdogFailures, reason);

        if (_watchdogFailures < 3) return;

        // Der Kernsatz dieser Klasse: Kann das Overlay nicht sichtbar sein,
        // endet die Freigabe. Transparenz ist keine Anzeige, sondern eine
        // Vorbedingung.
        _log.LogError("Overlay kann nicht dargestellt werden - Sitzung wird beendet");
        _state.Stop(StopReason.OverlayUnavailable);
    }

    private void OnSnapshotChanged(SessionSnapshot snapshot)
        => Dispatcher.BeginInvoke(() => Apply(snapshot));

    private void Apply(SessionSnapshot snapshot)
    {
        switch (snapshot.State)
        {
            case SessionState.Sharing:
                RootBorder.Background = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));
                StateText.Text = "LIVE";
                PauseButton.Content = "⏸ Pause";
                StartPulse();
                break;

            case SessionState.Paused:
                RootBorder.Background = new SolidColorBrush(Color.FromRgb(0xEA, 0x58, 0x0C));
                StateText.Text = "PAUSIERT";
                PauseButton.Content = "▶ Fortsetzen";
                StopPulse();
                break;

            default:
                Hide();
                return;
        }

        ScopeText.Text = snapshot.Scope?.DisplayName ?? "—";
        PeerText.Text = snapshot.Peer?.Name ?? "—";

        if (snapshot.ControlGranted)
        {
            ControlText.Text = "STEUERUNG AKTIV";
            ControlBadge.Background = new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24));
            ControlText.Foreground = new SolidColorBrush(Color.FromRgb(0x11, 0x18, 0x27));
        }
        else
        {
            ControlText.Text = "NUR ANSICHT";
            ControlBadge.Background = new SolidColorBrush(Color.FromRgb(0x7F, 0x1D, 0x1D));
            ControlText.Foreground = Brushes.White;
        }

        if (!IsVisible) Show();
        UpdateElapsed();
    }

    private void UpdateElapsed()
    {
        var snapshot = _state.Current;
        if (snapshot.StartedAt is null) return;

        var elapsed = snapshot.Elapsed;
        ElapsedText.Text = elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}"
            : $"{elapsed.Minutes}:{elapsed.Seconds:D2}";
    }

    private void StartPulse()
    {
        // Pulsierender Punkt nur bei aktiver Steuerung: Die Bewegung soll
        // Aufmerksamkeit genau dann ziehen, wenn sie wichtig ist.
        if (!_state.Current.ControlGranted) { StopPulse(); return; }

        var animation = new DoubleAnimation(1.0, 0.35, TimeSpan.FromSeconds(0.8))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
        };
        LiveDot.BeginAnimation(OpacityProperty, animation);
    }

    private void StopPulse()
    {
        LiveDot.BeginAnimation(OpacityProperty, null);
        LiveDot.Opacity = 1.0;
    }

    private void OnPauseClick(object sender, RoutedEventArgs e)
    {
        if (_state.Current.State == SessionState.Paused) ResumeRequested?.Invoke();
        else PauseRequested?.Invoke();
    }

    private void OnStopClick(object sender, RoutedEventArgs e) => StopRequested?.Invoke();

    /// <summary>
    /// Das Overlay kann nicht geschlossen werden, solange geteilt wird - der
    /// Versuch beendet stattdessen die Freigabe. Ohne diese Kopplung waere das
    /// Overlay eine Anzeige, die man wegklicken kann, und damit wertlos.
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose && _state.Current.State is SessionState.Sharing or SessionState.Paused)
        {
            e.Cancel = true;
            StopRequested?.Invoke();
            return;
        }

        _watchdog.Stop();
        _clock.Stop();
        _state.SnapshotChanged -= OnSnapshotChanged;
        base.OnClosing(e);
    }

    /// <summary>Vom Sitzungs-Controller beim Beenden der Anwendung aufzurufen.</summary>
    public void AllowClose()
    {
        _allowClose = true;
        Close();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLongW(nint hWnd, int nIndex, int dwNewLong);
}
