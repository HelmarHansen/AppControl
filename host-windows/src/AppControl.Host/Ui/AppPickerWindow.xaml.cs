using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AppControl.Host.Capture;
using AppControl.Host.Core;
using Microsoft.Extensions.Logging;

namespace AppControl.Host.Ui;

/// <summary>
/// Auswahl von Fenster oder Monitor mit Live-Vorschau.
///
/// Die Vorschauen sind echte Live-Streams, keine Icons und keine Standbilder: Der
/// Host soll sehen, was tatsaechlich uebertragen wuerde, bevor er zustimmt -
/// inklusive dessen, was gerade im Fenster steht.
/// Siehe docs/03-consent-and-transparency.md §3.3.
/// </summary>
public partial class AppPickerWindow : Window
{
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _previewTimer;
    private readonly WindowThumbnailProvider _thumbnails;

    private List<CapturableWindow> _windows = [];
    private List<CapturableMonitor> _monitors = [];
    private Border? _selectedTile;

    /// <summary>
    /// Kennzeichnet, welche Fenster gerade in der Liste stehen. Solange sie sich
    /// nicht aendert, werden die Kacheln NICHT neu gebaut - sonst wuerden alle
    /// Vorschauen alle zwei Sekunden neu starten, sichtbar flackern und die
    /// Auswahl verlieren.
    /// </summary>
    private string _listSignature = "";

    public ShareScope? SelectedScope { get; private set; }

    public AppPickerWindow(ILoggerFactory? loggerFactory = null)
    {
        InitializeComponent();

        _thumbnails = new WindowThumbnailProvider(
            loggerFactory?.CreateLogger<WindowThumbnailProvider>());

        // Die Fensterliste aendert sich waehrend der Auswahl (Apps werden
        // geoeffnet und geschlossen). Zwei Sekunden sind haeufig genug, um nicht
        // veraltet zu wirken, und selten genug, um die Auswahl nicht wegspringen
        // zu lassen.
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _refreshTimer.Tick += (_, _) => RefreshLists();

        // Die Vorschaubilder brauchen eine eigene, schnellere Taktung als die
        // Liste. Zwei Bilder je Sekunde reichen, um zu erkennen, was in einem
        // Fenster passiert; mehr waere fuer eine Auswahlhilfe verschwendet.
        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _previewTimer.Tick += (_, _) => _thumbnails.Refresh();

        Loaded += (_, _) =>
        {
            RefreshLists();
            _refreshTimer.Start();
            _previewTimer.Start();
        };

        Closed += (_, _) =>
        {
            _refreshTimer.Stop();
            _previewTimer.Stop();

            // WICHTIG: Ohne dieses Aufraeumen liefen die Vorschauen weiter und
            // die GDI-Objekte blieben liegen - bei jedem Oeffnen des Pickers ein
            // paar mehr.
            _thumbnails.Dispose();
        };
    }

    private void RefreshLists()
    {
        var filter = SearchBox.Text?.Trim() ?? "";

        _windows = WindowEnumerator.EnumerateWindows();
        _monitors = WindowEnumerator.EnumerateMonitors();

        var filtered = string.IsNullOrEmpty(filter)
            ? _windows
            : [.. _windows.Where(w =>
                w.Title.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                w.ProcessName.Contains(filter, StringComparison.OrdinalIgnoreCase))];

        var signature = BuildSignature(filtered, _monitors);
        if (signature == _listSignature && WindowList.Items.Count > 0) return;
        _listSignature = signature;

        // Die Kacheln werden neu gebaut, also sind auch alle Vorschauen hinfaellig.
        _thumbnails.StopAll();
        _selectedTile = null;

        WindowList.Items.Clear();
        foreach (var window in filtered)
            WindowList.Items.Add(BuildTile(window.Title, window.ProcessName, window.ToScope()));

        MonitorList.Items.Clear();
        foreach (var monitor in _monitors)
            MonitorList.Items.Add(BuildTile(
                monitor.Name, $"{monitor.Bounds.Width} × {monitor.Bounds.Height}", monitor.ToScope()));

        RestoreSelection();
    }

    private static string BuildSignature(
        IEnumerable<CapturableWindow> windows, IEnumerable<CapturableMonitor> monitors)
    {
        // Titel gehoeren dazu: Wechselt der Tab im Browser, aendert sich der
        // Titel, und die Kachel soll das zeigen.
        var parts = windows.Select(w => $"w{w.Hwnd:X}:{w.Title}")
            .Concat(monitors.Select(m => $"m{m.Handle:X}:{m.Bounds.Width}x{m.Bounds.Height}"));
        return string.Join("|", parts);
    }

    /// <summary>
    /// Stellt die Markierung nach einem Neuaufbau wieder her. ShareScope ist ein
    /// Record, der Vergleich also inhaltlich - dieselbe Auswahl bleibt markiert,
    /// auch wenn die Kachel ein anderes Objekt ist.
    /// </summary>
    private void RestoreSelection()
    {
        if (SelectedScope is null) return;

        foreach (var item in WindowList.Items.Cast<object>().Concat(MonitorList.Items.Cast<object>()))
        {
            if (item is Border tile && Equals(tile.Tag, SelectedScope))
            {
                _selectedTile = tile;
                tile.BorderBrush = new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8));
                return;
            }
        }

        // Die zuvor gewaehlte Quelle gibt es nicht mehr - das Fenster wurde
        // geschlossen. Lieber die Auswahl zuruecksetzen als eine Freigabe fuer
        // etwas starten, das es nicht mehr gibt.
        SelectedScope = null;
        ConfirmButton.IsEnabled = false;
    }

    private Border BuildTile(string title, string subtitle, ShareScope scope)
    {
        var preview = new Border
        {
            Height = 120,
            Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x11, 0x20)),
            CornerRadius = new CornerRadius(4),
            Margin = new Thickness(0, 0, 0, 8),
        };

        // Live-Vorschau. Liefert das Fenster kein Bild (eigener Renderpfad, siehe
        // WindowThumbnailProvider), bleibt es beim Platzhalter darunter.
        var thumbnail = scope switch
        {
            ShareScope.SingleWindow window => _thumbnails.StartPreview(window.Hwnd),
            ShareScope.FullScreen screen => _thumbnails.StartMonitorPreview(screen.Bounds),
            _ => null,
        };

        preview.Child = thumbnail is not null
            ? new Image { Source = thumbnail, Stretch = Stretch.Uniform }
            : new TextBlock
            {
                Text = "▦",
                FontSize = 36,
                Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0x41, 0x55)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };

        var tile = new Border
        {
            Width = 210,
            Margin = new Thickness(0, 0, 12, 12),
            Padding = new Thickness(10),
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x29, 0x3B)),
            BorderThickness = new Thickness(2),
            BorderBrush = Brushes.Transparent,
            Cursor = System.Windows.Input.Cursors.Hand,
            Tag = scope,
            Child = new StackPanel
            {
                Children =
                {
                    preview,
                    new TextBlock
                    {
                        Text = title, Foreground = Brushes.White, FontWeight = FontWeights.SemiBold,
                        FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis,
                    },
                    new TextBlock
                    {
                        Text = subtitle,
                        Foreground = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8)),
                        FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis,
                    },
                },
            },
        };

        tile.MouseLeftButtonUp += (_, _) => Select(tile, scope);
        return tile;
    }

    private void Select(Border tile, ShareScope scope)
    {
        if (_selectedTile is not null) _selectedTile.BorderBrush = Brushes.Transparent;
        _selectedTile = tile;
        tile.BorderBrush = new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8));
        SelectedScope = scope;
        ConfirmButton.IsEnabled = true;
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => RefreshLists();

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        if (SelectedScope is null) return;
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        SelectedScope = null;
        DialogResult = false;
        Close();
    }
}
