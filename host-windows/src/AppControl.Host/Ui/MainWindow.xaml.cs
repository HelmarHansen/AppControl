using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using AppControl.Host.Audit;
using AppControl.Host.Core;
using AppControl.Host.Security;

namespace AppControl.Host.Ui;

/// <summary>
/// Das Dashboard: Einladung erzeugen, Zustand sehen, Protokoll lesen.
///
/// Das Fenster laesst sich schliessen (dann lebt die App im Tray weiter), aber
/// die Anwendung selbst hat keinen Modus ohne sichtbare Praesenz - siehe
/// App.xaml.cs und docs/03-consent-and-transparency.md §3.9.
/// </summary>
public partial class MainWindow : Window
{
    private readonly SessionStateMachine _state;
    private readonly IAuditLog _audit;
    private readonly DispatcherTimer _refresh;

    private PairingTicket? _ticket;

    public event Action<PairingTicket>? ConnectRequested;

    public MainWindow(SessionStateMachine state, IAuditLog audit)
    {
        InitializeComponent();
        _state = state;
        _audit = audit;

        _refresh = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _refresh.Tick += (_, _) => { Apply(_state.Current); LoadAudit(); };

        _state.SnapshotChanged += OnSnapshotChanged;

        Loaded += (_, _) =>
        {
            GenerateTicket();
            Apply(_state.Current);
            LoadAudit();
            _refresh.Start();
        };
    }

    private void OnSnapshotChanged(SessionSnapshot snapshot)
        => Dispatcher.BeginInvoke(() => { Apply(snapshot); LoadAudit(); });

    private void Apply(SessionSnapshot snapshot)
    {
        var (color, title, detail) = snapshot.State switch
        {
            SessionState.Idle => (
                Color.FromRgb(0x64, 0x74, 0x8B), "Nicht verbunden",
                "Erzeuge eine Einladung und schick sie deinem Gegenüber."),

            SessionState.Pairing => (
                Color.FromRgb(0xEA, 0xB3, 0x08), "Warte auf Verbindung",
                "Die Einladung ist aktiv. Sobald sich jemand verbindet, wirst du gefragt."),

            SessionState.AwaitingConsent => (
                Color.FromRgb(0xEA, 0xB3, 0x08), "Anfrage wartet auf dich",
                "Ein Dialog fragt dich, was freigegeben werden soll. Ohne deine Zustimmung passiert nichts."),

            SessionState.Sharing => (
                Color.FromRgb(0xDC, 0x26, 0x26),
                snapshot.ControlGranted ? "TEILT — Steuerung aktiv" : "TEILT — nur Ansicht",
                $"Freigegeben: {snapshot.Scope?.DisplayName}\n" +
                $"Gegenüber: {snapshot.Peer?.Name}\n" +
                $"Laufzeit: {Format(snapshot.Elapsed)}" +
                (snapshot.Remaining is { } r ? $" · endet in {Format(r)}" : "") +
                $"\nEingaben: {snapshot.InjectedEventCount} ausgeführt, {snapshot.RejectedEventCount} blockiert"),

            SessionState.Paused => (
                Color.FromRgb(0xEA, 0x58, 0x0C), "PAUSIERT",
                "Dein Gegenüber sieht ein Standbild mit Hinweis. Eingaben sind blockiert."),

            SessionState.Terminating => (
                Color.FromRgb(0x64, 0x74, 0x8B), "Wird beendet …", ""),

            _ => (Color.FromRgb(0x64, 0x74, 0x8B), "—", ""),
        };

        StatusDot.Fill = new SolidColorBrush(color);
        StatusText.Text = title;
        StatusDetail.Text = detail;

        var idle = snapshot.State == SessionState.Idle;
        NewTicketButton.IsEnabled = idle;
        ConnectButton.IsEnabled = idle && _ticket is not null;
        ConnectButton.Content = idle ? "Auf Verbindung warten" : "Verbindung aktiv";
    }

    private void GenerateTicket()
    {
        _ticket = PairingTicket.Generate();
        TicketBox.Text = _ticket.ToDisplayString();
    }

    private void LoadAudit()
    {
        var lines = _audit.ReadRecent(60);
        // Nur neu befuellen, wenn sich etwas geaendert hat - sonst springt die
        // Auswahl bei jedem Timer-Tick.
        if (AuditList.Items.Count == lines.Count) return;

        AuditList.Items.Clear();
        foreach (var line in lines.Reverse())
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(line);
                var root = doc.RootElement;
                var ts = root.GetProperty("ts").GetDateTimeOffset().LocalDateTime;
                var kind = root.GetProperty("kind").GetString();
                var detail = root.TryGetProperty("detail", out var d) ? d.GetString() : null;
                AuditList.Items.Add($"{ts:HH:mm:ss}  {kind}{(detail is null ? "" : "  " + detail)}");
            }
            catch
            {
                AuditList.Items.Add(line);
            }
        }
    }

    private void OnNewTicket(object sender, RoutedEventArgs e) => GenerateTicket();

    private void OnCopyTicket(object sender, RoutedEventArgs e)
    {
        if (_ticket is null) return;
        try
        {
            Clipboard.SetText(_ticket.ToDisplayString());
            CopyTicketButton.Content = "Kopiert ✓";
            var reset = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            reset.Tick += (s, _) => { CopyTicketButton.Content = "Kopieren"; ((DispatcherTimer)s!).Stop(); };
            reset.Start();
        }
        catch (Exception ex)
        {
            // Die Zwischenablage kann von anderen Prozessen blockiert sein - das
            // ist ein bekanntes Windows-Verhalten und kein Grund abzustuerzen.
            MessageBox.Show(this, $"Kopieren fehlgeschlagen: {ex.Message}", "AppControl");
        }
    }

    private void OnConnect(object sender, RoutedEventArgs e)
    {
        if (_ticket is null) return;
        ConnectRequested?.Invoke(_ticket);
    }

    private static string Format(TimeSpan t)
        => t == TimeSpan.MaxValue ? "unbegrenzt"
         : t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}"
         : $"{t.Minutes}:{t.Seconds:D2}";

    protected override void OnClosing(CancelEventArgs e)
    {
        // Schliessen minimiert in den Tray statt zu beenden - eine laufende
        // Freigabe darf nicht versehentlich abgebrochen werden, und die App darf
        // nicht unsichtbar weiterlaufen, ohne dass das Tray-Icon da ist.
        e.Cancel = true;
        Hide();
    }

    public void ForceClose()
    {
        _refresh.Stop();
        _state.SnapshotChanged -= OnSnapshotChanged;
        Closing -= (_, _) => { };
        Application.Current.Shutdown();
    }
}
