using System.Windows;
using AppControl.Host.Audit;
using AppControl.Host.Capture;
using AppControl.Host.Config;
using AppControl.Host.Core;
using AppControl.Host.Media;
using AppControl.Host.Input;
using AppControl.Host.Security;
using AppControl.Host.Ui;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace AppControl.Host;

/// <summary>
/// Anwendungseinstieg. Baut die Objekte auf, verdrahtet UI und Controller und
/// setzt die Startbedingungen durch, ohne die AppControl nicht laufen darf.
/// </summary>
public partial class App : Application
{
    private ILoggerFactory _loggerFactory = null!;
    private SessionStateMachine _state = null!;
    private SessionController _controller = null!;
    private TrayIcon _tray = null!;
    private MainWindow _dashboard = null!;
    private StatusOverlayWindow? _overlay;
    private EmergencyHotkey _hotkey = null!;
    private IAuditLog _audit = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // ── STARTBEDINGUNG 1: keine unsichtbare Ausfuehrung ──────────────────
        //
        // Es gibt kein --silent, kein --no-ui, kein Flag, das eine Sitzung ohne
        // Klick startet. Ohne interaktiven Desktop beendet sich die Anwendung.
        // Damit ist ausgeschlossen, dass AppControl als Dienst, als geplante
        // Aufgabe oder in einer Session-0-Umgebung laeuft - also genau in den
        // Konstellationen, in denen niemand am Bildschirm sitzt.
        // Siehe docs/03-consent-and-transparency.md §3.9.
        if (!Environment.UserInteractive)
        {
            Console.Error.WriteLine(
                "AppControl benötigt eine interaktive Desktop-Sitzung und läuft nicht als Dienst.");
            Shutdown(1);
            return;
        }

        if (e.Args.Any(a => a is "--silent" or "--no-ui" or "--headless" or "--autostart"))
        {
            MessageBox.Show(
                "AppControl kennt keinen unsichtbaren Modus.\n\n" +
                "Bildschirmfreigabe ohne sichtbare Anzeige ist genau das, wogegen dieses " +
                "Programm gebaut ist. Siehe docs/03-consent-and-transparency.md.",
                "AppControl", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(1);
            return;
        }

        _loggerFactory = LoggerFactory.Create(builder => builder
            .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; })
            .SetMinimumLevel(LogLevel.Information));

        var log = _loggerFactory.CreateLogger<App>();
        var settings = HostSettings.Load();

        // ── STARTBEDINGUNG 2: Capture muss unterstuetzt sein ─────────────────
        if (!GraphicsCaptureInterop.IsSupported())
        {
            MessageBox.Show(
                "Windows.Graphics.Capture wird auf diesem System nicht unterstützt.\n\n" +
                "AppControl benötigt Windows 10 Version 1803 (Build 17134) oder neuer.",
                "AppControl", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        _audit = new AuditLog(_loggerFactory.CreateLogger<AuditLog>());
        _audit.Write(AuditEventKind.AppStarted);

        _state = new SessionStateMachine(_audit, _loggerFactory.CreateLogger<SessionStateMachine>());

        var identity = new IdentityStore(_loggerFactory.CreateLogger<IdentityStore>());
        var trust = new TrustStore(_loggerFactory.CreateLogger<TrustStore>());

        // PLATZHALTER: Direct3D-Device erzeugen.
        //
        //   var d3d = D3D11.D3D11CreateDevice(DriverType.Hardware,
        //                 DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport);
        //   var device = Direct3D11Helper.CreateDirect3DDeviceFromD3D11Device(d3d);
        //
        // BgraSupport ist Pflicht (WGC liefert B8G8R8A8), VideoSupport wird vom
        // Media-Foundation-Encoder gebraucht. Siehe Capture/D3D11Helper.cs.
        var device = D3D11Helper.CreateDevice();

        var capture = new CaptureEngine(_state, device, _loggerFactory.CreateLogger<CaptureEngine>());
        var encoder = new MediaFoundationH264Encoder(
            device, _loggerFactory.CreateLogger<MediaFoundationH264Encoder>());

        _controller = new SessionController(
            _state, settings, identity, trust, _audit, capture, encoder, _loggerFactory);

        BuildUi(settings);

        log.LogInformation("AppControl bereit. Identität: {Fingerprint}", identity.Fingerprint);
    }

    private void BuildUi(HostSettings settings)
    {
        _dashboard = new MainWindow(_state, _audit);
        _dashboard.ConnectRequested += ticket =>
            _ = _controller.StartPairingAsync(ticket);

        _tray = new TrayIcon(_state);
        _tray.ShowDashboardRequested += () => { _dashboard.Show(); _dashboard.Activate(); };
        _tray.PauseToggleRequested += () =>
        {
            if (_state.Current.State == SessionState.Paused) _controller.Resume();
            else _controller.Pause();
        };
        _tray.StopRequested += () => _state.Stop(StopReason.UserStopped);
        _tray.ChangeScopeRequested += OnChangeScope;
        _tray.ShowAuditLogRequested += () => { _dashboard.Show(); _dashboard.Activate(); };
        _tray.ExitRequested += OnExit;

        _hotkey = new EmergencyHotkey(_state, _loggerFactory.CreateLogger<EmergencyHotkey>());
        _hotkey.Attach(_dashboard);

        // Overlay erscheint und verschwindet mit dem Sharing-Zustand.
        _state.SnapshotChanged += snapshot => Dispatcher.BeginInvoke(() => SyncOverlay(snapshot));

        // Bildschirmsperre: Der Sperrbildschirm darf nie im Stream landen.
        if (settings.StopOnScreenLock)
        {
            SystemEvents.SessionSwitch += (_, args) =>
            {
                if (args.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.SessionLogoff)
                    _state.Stop(StopReason.ScreenLocked);
            };
        }

        _dashboard.Show();
    }

    private void SyncOverlay(SessionSnapshot snapshot)
    {
        var shouldShow = snapshot.State is SessionState.Sharing or SessionState.Paused;

        if (shouldShow && _overlay is null)
        {
            _overlay = new StatusOverlayWindow(_state, _loggerFactory.CreateLogger<StatusOverlayWindow>());
            _overlay.PauseRequested += () => _controller.Pause();
            _overlay.ResumeRequested += () => _controller.Resume();
            _overlay.StopRequested += () => _state.Stop(StopReason.UserStopped);
            _overlay.Show();
        }
        else if (!shouldShow && _overlay is not null)
        {
            _overlay.AllowClose();
            _overlay = null;
        }
    }

    private void OnChangeScope()
    {
        if (_state.Current.State is not (SessionState.Sharing or SessionState.Paused)) return;

        var picker = new AppPickerWindow(_loggerFactory);
        if (picker.ShowDialog() == true && picker.SelectedScope is not null)
        {
            _state.ChangeScope(picker.SelectedScope);
            // Der Viewer erfaehrt den Wechsel automatisch ueber share-state,
            // das an SnapshotChanged haengt.
        }
    }

    private void OnExit()
    {
        if (_state.Current.State is SessionState.Sharing or SessionState.Paused)
        {
            var answer = MessageBox.Show(
                "Es läuft gerade eine Freigabe. Wirklich beenden?",
                "AppControl", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;
        }

        _state.Stop(StopReason.ApplicationExit);
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _audit?.Write(AuditEventKind.AppExited);
        _overlay?.AllowClose();
        _hotkey?.Dispose();
        _tray?.Dispose();
        _ = _controller?.DisposeAsync();
        _loggerFactory?.Dispose();
        base.OnExit(e);
    }
}
