using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AppControl.Host.Audit;

public enum AuditEventKind
{
    AppStarted,
    PairingStarted,
    PeerConnected,
    SasComputed,
    ConsentRequested,
    ConsentGranted,
    ConsentDenied,
    ScopeChanged,
    ControlGranted,
    ControlRevoked,
    Paused,
    Resumed,
    SessionStopped,
    SessionEnded,
    TrustWarning,
    AppExited,
}

public interface IAuditLog
{
    void Write(AuditEventKind kind, string? detail = null);
    IReadOnlyList<string> ReadRecent(int maxLines = 200);
}

/// <summary>
/// Append-only Sitzungsprotokoll in JSON-Lines unter
/// %LOCALAPPDATA%\AppControl\audit\.
///
/// WAS DRIN STEHT: Sitzungsstart und -ende mit Grund, Consent-Entscheidungen,
/// Scope-Wechsel, Steuerungsfreigaben, Peer-Fingerprints, Zahl injizierter und
/// abgelehnter Events.
///
/// WAS NICHT DRIN STEHT: Tastencodes, Mauskoordinaten, Fensterinhalte. Das
/// Protokoll soll nachvollziehbar machen, WAS passiert ist - ohne selbst ein
/// Keylogger zu sein. Ein Sicherheitswerkzeug, das zur Ueberwachung taugt, hat
/// sein Ziel verfehlt. Siehe docs/03-consent-and-transparency.md §3.8.
/// </summary>
public sealed class AuditLog : IAuditLog
{
    private readonly string _directory;
    private readonly ILogger<AuditLog> _log;
    private readonly object _gate = new();

    public AuditLog(ILogger<AuditLog> log, string? directory = null)
    {
        _log = log;
        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AppControl", "audit");
        Directory.CreateDirectory(_directory);
    }

    private string CurrentFile =>
        Path.Combine(_directory, $"audit-{DateTimeOffset.UtcNow:yyyy-MM}.jsonl");

    public void Write(AuditEventKind kind, string? detail = null)
    {
        var entry = new
        {
            ts = DateTimeOffset.UtcNow.ToString("O"),
            kind = kind.ToString(),
            detail,
        };

        try
        {
            lock (_gate)
            {
                // AppendAllText oeffnet, schreibt und schliesst - langsamer als ein
                // offener Stream, aber die Datei ist nach jedem Eintrag konsistent
                // auf der Platte. Bei wenigen Eintraegen pro Minute ist das der
                // richtige Kompromiss: Ein Absturz darf das Protokoll der Sitzung
                // nicht verlieren, die gerade zum Absturz gefuehrt hat.
                File.AppendAllText(CurrentFile,
                    JsonSerializer.Serialize(entry) + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch (Exception ex)
        {
            // Ein nicht schreibbares Protokoll darf die Sitzung nicht verhindern -
            // aber es muss sichtbar sein.
            _log.LogError(ex, "Audit-Eintrag konnte nicht geschrieben werden: {Kind}", kind);
        }
    }

    public IReadOnlyList<string> ReadRecent(int maxLines = 200)
    {
        try
        {
            lock (_gate)
            {
                if (!File.Exists(CurrentFile)) return [];
                return [.. File.ReadLines(CurrentFile).TakeLast(maxLines)];
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Audit-Protokoll konnte nicht gelesen werden");
            return [];
        }
    }
}
