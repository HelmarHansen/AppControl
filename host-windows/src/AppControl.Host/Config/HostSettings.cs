using System.Text.Json;
using System.Text.Json.Serialization;

namespace AppControl.Host.Config;

/// <summary>
/// Einstellungen des Hosts.
///
/// BEACHTE, WAS HIER FEHLT: Es gibt keine Option, das Overlay auszublenden, keine
/// Option fuer Autostart, keine Option, den Consent-Dialog zu ueberspringen, und
/// keine Option, dem Peer dauerhaft zu vertrauen. Diese Auslassungen sind der
/// eigentliche Inhalt dieser Datei - siehe docs/03-consent-and-transparency.md §3.9.
/// </summary>
public sealed record HostSettings
{
    [JsonPropertyName("signalingUrl")]
    public string SignalingUrl { get; init; } = "wss://localhost:8787/ws";

    /// <summary>Standarddauer im Consent-Dialog. Der Host kann sie dort aendern.</summary>
    [JsonPropertyName("defaultMaxSessionMinutes")]
    public int DefaultMaxSessionMinutes { get; init; } = 30;

    /// <summary>Steuerung wird nach so langer Untaetigkeit automatisch entzogen.</summary>
    [JsonPropertyName("controlIdleTimeoutMinutes")]
    public int ControlIdleTimeoutMinutes { get; init; } = 5;

    /// <summary>Wie lange der Consent-Dialog auf eine Antwort wartet, bevor er ablehnt.</summary>
    [JsonPropertyName("consentTimeoutSeconds")]
    public int ConsentTimeoutSeconds { get; init; } = 60;

    /// <summary>
    /// Blocklist fuer Systemtasten auch bei Vollbild-Freigabe erzwingen.
    /// Bei App-Freigabe ist sie IMMER aktiv und nicht abschaltbar - dort wuerde
    /// ein Fensterwechsel den freigegebenen Bereich verlassen.
    /// </summary>
    [JsonPropertyName("blockSystemKeysOnFullScreen")]
    public bool BlockSystemKeysOnFullScreen { get; init; } = false;

    /// <summary>Mauszeiger des Hosts im Stream mit uebertragen.</summary>
    [JsonPropertyName("captureCursor")]
    public bool CaptureCursor { get; init; } = true;

    [JsonPropertyName("maxBitrateKbps")]  public int MaxBitrateKbps { get; init; } = 12_000;
    [JsonPropertyName("initialBitrateKbps")] public int InitialBitrateKbps { get; init; } = 6_000;
    [JsonPropertyName("maxFrameRate")]    public int MaxFrameRate { get; init; } = 60;

    /// <summary>Sitzung sofort beenden, wenn der Bildschirm gesperrt wird.</summary>
    [JsonPropertyName("stopOnScreenLock")]
    public bool StopOnScreenLock { get; init; } = true;

    /// <summary>Uebertragung automatisch pausieren, wenn ein UAC-Prompt erscheint.</summary>
    [JsonPropertyName("pauseOnElevatedWindow")]
    public bool PauseOnElevatedWindow { get; init; } = true;

    public static HostSettings Load(string? path = null)
    {
        path ??= Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (!File.Exists(path)) return new HostSettings();

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<HostSettings>(json) ?? new HostSettings();
        }
        catch
        {
            // Fail-safe zu den Standardwerten. Eine kaputte Konfigurationsdatei
            // darf nicht dazu fuehren, dass das Programm gar nicht startet -
            // und die Standardwerte sind die restriktiven.
            return new HostSettings();
        }
    }
}
