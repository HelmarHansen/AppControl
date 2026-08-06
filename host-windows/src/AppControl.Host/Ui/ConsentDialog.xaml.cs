using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AppControl.Host.Capture;
using AppControl.Host.Core;
using AppControl.Host.Security;

namespace AppControl.Host.Ui;

public sealed record ConsentRequest(
    string ViewerName,
    string Fingerprint,
    string SasEmoji,
    TrustVerdict Trust,
    KnownPeer? KnownPeer,
    bool ViewerWantsControl);

/// <summary>
/// Der Gate-Keeper: Ohne eine bewusste Entscheidung in diesem Dialog verlaesst
/// kein einziges Frame den Rechner.
///
/// Siehe docs/03-consent-and-transparency.md §3.2.
/// </summary>
public partial class ConsentDialog : Window
{
    private readonly ConsentRequest _request;
    private readonly Config.HostSettings _settings;
    private readonly DispatcherTimer _countdown;
    private DateTimeOffset _deadline;

    private ShareScope? _pickedWindowScope;
    private List<CapturableMonitor> _monitors = [];

    /// <summary>Das Ergebnis. Bei Timeout oder Schliessen ist es eine Ablehnung.</summary>
    public ConsentDecision Decision { get; private set; } =
        new(Granted: false, Scope: null, ControlGranted: false, MaxDuration: TimeSpan.Zero);

    public ConsentDialog(ConsentRequest request, Config.HostSettings settings)
    {
        InitializeComponent();
        _request = request;
        _settings = settings;

        PopulateFromRequest();

        _deadline = DateTimeOffset.UtcNow.AddSeconds(settings.ConsentTimeoutSeconds);
        _countdown = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _countdown.Tick += OnCountdownTick;
        _countdown.Start();

        // Fokus explizit auf "Ablehnen". Zusammen mit IsDefault="True" im XAML
        // bedeutet das: Enter, Leertaste und Escape lehnen alle ab. Ein
        // versehentlicher Tastendruck kann nichts freigeben.
        Loaded += (_, _) => DenyButton.Focus();
    }

    private void PopulateFromRequest()
    {
        RequesterText.Text = _request.ViewerWantsControl
            ? $"{_request.ViewerName} möchte deinen Bildschirm sehen und steuern"
            : $"{_request.ViewerName} möchte deinen Bildschirm sehen";

        ControlHeader.Text = $"DARF {_request.ViewerName.ToUpperInvariant()} STEUERN?";
        SasText.Text = _request.SasEmoji;
        FingerprintText.Text = _request.Fingerprint;

        ApplyTrustVerdict();

        // Monitore
        _monitors = WindowEnumerator.EnumerateMonitors();
        foreach (var monitor in _monitors) MonitorCombo.Items.Add(monitor.Name);
        if (MonitorCombo.Items.Count > 0) MonitorCombo.SelectedIndex = 0;

        // Dauer
        foreach (var minutes in new[] { 15, 30, 60, 120 })
            DurationCombo.Items.Add($"{minutes} Minuten");
        DurationCombo.Items.Add("Ohne Begrenzung");
        DurationCombo.SelectedIndex = _settings.DefaultMaxSessionMinutes switch
        {
            15 => 0, 30 => 1, 60 => 2, 120 => 3, _ => 1,
        };
    }

    private void ApplyTrustVerdict()
    {
        switch (_request.Trust)
        {
            case TrustVerdict.Known:
                TrustText.Text = _request.KnownPeer is { } p
                    ? $"✓ bekannt seit {p.FirstSeen.LocalDateTime:d}, {p.Sessions} Sitzungen"
                    : "✓ bekannt";
                TrustBadge.Background = new SolidColorBrush(Color.FromRgb(0x16, 0x65, 0x34));
                break;

            case TrustVerdict.NewPeer:
                TrustText.Text = "neu";
                TrustBadge.Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x40, 0xAF));
                break;

            case TrustVerdict.KeyChanged:
                TrustText.Text = "⚠️ SCHLÜSSEL GEÄNDERT";
                TrustBadge.Background = new SolidColorBrush(Color.FromRgb(0xB9, 0x1C, 0x1C));
                TrustWarning.Visibility = Visibility.Visible;

                // Formulierung ohne Beschoenigung: Der haeufigste Grund ist
                // harmlos (Neuinstallation), der seltene ist ein Angriff. Beide
                // muessen benannt werden, damit die Person selbst entscheiden kann.
                TrustWarningText.Text =
                    $"Dieser Rechner meldet sich als „{_request.ViewerName}\", hat aber einen anderen " +
                    "Schlüssel als beim letzten Mal.\n\n" +
                    "Das passiert bei einer Neuinstallation — oder bei einem Angriff. " +
                    "Frag nach, bevor du freigibst.";

                // Bei geaendertem Schluessel ist der SAS-Vergleich Pflicht.
                SasConfirmed.IsChecked = false;
                break;
        }
    }

    // ── Auswahl ──────────────────────────────────────────────────────────────

    private void OnScopeKindChanged(object sender, RoutedEventArgs e)
    {
        if (ScopeWindowRadio.IsChecked == true && _pickedWindowScope is null)
        {
            // Direkt den Picker oeffnen: "Nur eine Anwendung" ohne Auswahl ist
            // kein sinnvoller Zwischenzustand.
            OnPickApp(sender, e);
        }
        OnSelectionChanged(sender, e);
    }

    private void OnPickApp(object sender, RoutedEventArgs e)
    {
        var picker = new AppPickerWindow { Owner = this };
        if (picker.ShowDialog() == true && picker.SelectedScope is not null)
        {
            _pickedWindowScope = picker.SelectedScope;
            PickedAppText.Text = _pickedWindowScope.DisplayName;
            ScopeWindowRadio.IsChecked = true;
        }
        OnSelectionChanged(sender, e);
    }

    /// <summary>
    /// "Freigeben" wird erst aktiv, wenn alle Vorbedingungen erfuellt sind.
    /// Kein Klick ohne Entscheidung.
    /// </summary>
    private void OnSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (GrantButton is null) return;   // waehrend InitializeComponent

        var scopeChosen = (ScopeScreenRadio.IsChecked == true && MonitorCombo.SelectedIndex >= 0)
                       || (ScopeWindowRadio.IsChecked == true && _pickedWindowScope is not null);

        // Bei geaendertem Schluessel ist die SAS-Bestaetigung zwingend, sonst optional.
        var sasOk = _request.Trust != TrustVerdict.KeyChanged || SasConfirmed.IsChecked == true;

        GrantButton.IsEnabled = scopeChosen && sasOk;
    }

    // ── Countdown ────────────────────────────────────────────────────────────

    private void OnCountdownTick(object? sender, EventArgs e)
    {
        var remaining = _deadline - DateTimeOffset.UtcNow;

        if (remaining <= TimeSpan.Zero)
        {
            // Ein uebersehener Dialog gibt niemals von selbst frei.
            _countdown.Stop();
            Decision = Decision with { Granted = false };
            DialogResult = false;
            Close();
            return;
        }

        CountdownText.Text = $"Anfrage läuft ab in {remaining.Minutes}:{remaining.Seconds:D2}";
        CountdownText.Foreground = remaining.TotalSeconds < 15
            ? new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71))
            : new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8));
    }

    // ── Entscheidung ─────────────────────────────────────────────────────────

    private void OnGrant(object sender, RoutedEventArgs e)
    {
        var scope = ScopeWindowRadio.IsChecked == true
            ? _pickedWindowScope
            : _monitors.ElementAtOrDefault(MonitorCombo.SelectedIndex)?.ToScope();

        if (scope is null)
        {
            MessageBox.Show(this, "Bitte wähle zuerst aus, was freigegeben werden soll.",
                "AppControl", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var duration = DurationCombo.SelectedIndex switch
        {
            0 => TimeSpan.FromMinutes(15),
            1 => TimeSpan.FromMinutes(30),
            2 => TimeSpan.FromMinutes(60),
            3 => TimeSpan.FromMinutes(120),
            _ => TimeSpan.MaxValue,
        };

        Decision = new ConsentDecision(
            Granted: true,
            Scope: scope,
            ControlGranted: ControlYesRadio.IsChecked == true,
            MaxDuration: duration);

        _countdown.Stop();
        DialogResult = true;
        Close();
    }

    private void OnDeny(object sender, RoutedEventArgs e)
    {
        _countdown.Stop();
        Decision = Decision with { Granted = false };
        DialogResult = false;
        Close();
    }

    /// <summary>
    /// Schliessen ueber das X ist eine Ablehnung, keine Zustimmung.
    /// Bei einem Consent-Dialog muss jeder nicht explizit zustimmende Ausgang
    /// eine Ablehnung sein.
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        _countdown.Stop();
        base.OnClosing(e);
    }
}
