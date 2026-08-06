using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AppControl.Host.Capture;
using AppControl.Host.Core;

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
    private List<CapturableWindow> _windows = [];
    private List<CapturableMonitor> _monitors = [];
    private Border? _selectedTile;

    public ShareScope? SelectedScope { get; private set; }

    public AppPickerWindow()
    {
        InitializeComponent();

        // Die Fensterliste aendert sich waehrend der Auswahl (Apps werden
        // geoeffnet und geschlossen). Zwei Sekunden sind haeufig genug, um nicht
        // veraltet zu wirken, und selten genug, um die Auswahl nicht wegspringen
        // zu lassen.
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _refreshTimer.Tick += (_, _) => RefreshLists();

        Loaded += (_, _) => { RefreshLists(); _refreshTimer.Start(); };
        Closed += (_, _) => _refreshTimer.Stop();
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

        WindowList.Items.Clear();
        foreach (var window in filtered)
            WindowList.Items.Add(BuildTile(window.Title, window.ProcessName, window.ToScope(), window.Hwnd));

        MonitorList.Items.Clear();
        foreach (var monitor in _monitors)
            MonitorList.Items.Add(BuildTile(
                monitor.Name, $"{monitor.Bounds.Width} × {monitor.Bounds.Height}",
                monitor.ToScope(), nint.Zero));
    }

    private Border BuildTile(string title, string subtitle, ShareScope scope, nint hwnd)
    {
        var preview = new Border
        {
            Height = 120,
            Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x11, 0x20)),
            CornerRadius = new CornerRadius(4),
            Margin = new Thickness(0, 0, 0, 8),
        };

        // PLATZHALTER: Live-Vorschau einhaengen.
        //
        //   var bitmap = _thumbnails.StartPreview(hwnd);
        //   if (bitmap is not null)
        //       preview.Child = new Image { Source = bitmap, Stretch = Stretch.Uniform };
        //
        // Siehe Capture/WindowThumbnailProvider.cs - dort steht der vollstaendige
        // Implementierungsleitfaden. Bis dahin zeigt die Kachel einen Platzhalter.
        preview.Child = new TextBlock
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
