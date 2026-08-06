using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace AppControl.Host.Security;

public sealed record KnownPeer
{
    [JsonPropertyName("staticKey")]   public string StaticKeyBase64 { get; init; } = "";
    [JsonPropertyName("name")]        public string Name { get; init; } = "";
    [JsonPropertyName("fingerprint")] public string Fingerprint { get; init; } = "";
    [JsonPropertyName("firstSeen")]   public DateTimeOffset FirstSeen { get; init; }
    [JsonPropertyName("lastSeen")]    public DateTimeOffset LastSeen { get; init; }
    [JsonPropertyName("sessions")]    public int Sessions { get; init; }
}

public enum TrustVerdict
{
    /// <summary>Unbekannter Schluessel und unbekannter Name - normales erstes Pairing.</summary>
    NewPeer,
    /// <summary>Schluessel bekannt. Der Consent-Dialog zeigt "[bekannt seit ...]".</summary>
    Known,
    /// <summary>
    /// Bekannter Name, ABER anderer Schluessel. Rote Warnung im Consent-Dialog.
    /// Passiert bei Neuinstallation - oder bei einem Angriff.
    /// </summary>
    KeyChanged,
}

/// <summary>
/// Trust On First Use. Speichert die statischen Schluessel bereits gesehener Peers.
///
/// WAS VERTRAUEN HIER BEDEUTET: "Ich weiss, wer anfragt" - NICHT "er darf
/// jederzeit". Ein bekannter Peer erspart keinen einzigen Klick; der
/// Consent-Dialog erscheint bei jeder Sitzung, auch bei der hundertsten mit
/// demselben Schluessel. Siehe docs/03-consent-and-transparency.md §3.2.
/// </summary>
public sealed class TrustStore
{
    private readonly string _path;
    private readonly ILogger<TrustStore> _log;
    private readonly object _gate = new();
    private List<KnownPeer> _peers = [];

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public TrustStore(ILogger<TrustStore> log, string? directory = null)
    {
        _log = log;
        var dir = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppControl");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "peers.json");
        Load();
    }

    public IReadOnlyList<KnownPeer> Peers { get { lock (_gate) return [.. _peers]; } }

    public (TrustVerdict Verdict, KnownPeer? Existing) Evaluate(ReadOnlySpan<byte> staticKey, string name)
    {
        var encoded = Convert.ToBase64String(staticKey);
        lock (_gate)
        {
            var byKey = _peers.FirstOrDefault(p => p.StaticKeyBase64 == encoded);
            if (byKey is not null) return (TrustVerdict.Known, byKey);

            var byName = _peers.FirstOrDefault(
                p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            if (byName is not null) return (TrustVerdict.KeyChanged, byName);

            return (TrustVerdict.NewPeer, null);
        }
    }

    /// <summary>
    /// Nach erfolgreicher, vom Host bestaetigter Sitzung aufzurufen.
    /// Wichtig: NICHT nach dem blossen Handshake - sonst wuerde ein abgelehnter
    /// Verbindungsversuch den Schluessel eines Angreifers als "bekannt" ablegen.
    /// </summary>
    public void Remember(ReadOnlySpan<byte> staticKey, string name)
    {
        var encoded = Convert.ToBase64String(staticKey);
        var fingerprint = Handshake.Fingerprint(staticKey);
        var now = DateTimeOffset.UtcNow;

        lock (_gate)
        {
            var index = _peers.FindIndex(p => p.StaticKeyBase64 == encoded);
            if (index >= 0)
            {
                _peers[index] = _peers[index] with
                {
                    Name = name,
                    LastSeen = now,
                    Sessions = _peers[index].Sessions + 1,
                };
            }
            else
            {
                _peers.Add(new KnownPeer
                {
                    StaticKeyBase64 = encoded,
                    Name = name,
                    Fingerprint = fingerprint,
                    FirstSeen = now,
                    LastSeen = now,
                    Sessions = 1,
                });
            }
            Save();
        }
    }

    /// <summary>Entfernt einen Peer. Er wird beim naechsten Mal wieder als neu behandelt.</summary>
    public void Forget(string staticKeyBase64)
    {
        lock (_gate)
        {
            _peers.RemoveAll(p => p.StaticKeyBase64 == staticKeyBase64);
            Save();
        }
    }

    private void Load()
    {
        if (!File.Exists(_path)) return;
        try
        {
            var json = File.ReadAllText(_path);
            var doc = JsonSerializer.Deserialize<PeerFile>(json);
            _peers = doc?.Peers ?? [];
            _log.LogInformation("{Count} bekannte Peers geladen", _peers.Count);
        }
        catch (Exception ex)
        {
            // Fail-safe: Bei kaputter Datei mit LEERER Liste weitermachen, nicht
            // abstuerzen. Die Folge ist eine TOFU-Warnung beim naechsten Verbinden -
            // also mehr Vorsicht, nicht weniger.
            _log.LogError(ex, "peers.json nicht lesbar - starte mit leerer Liste");
            _peers = [];
        }
    }

    private void Save()
    {
        try
        {
            // Atomar schreiben: Ein Absturz mitten im Schreiben darf keine
            // halbe Datei hinterlassen.
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new PeerFile { Peers = _peers }, JsonOptions));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "peers.json konnte nicht geschrieben werden");
        }
    }

    private sealed class PeerFile
    {
        [JsonPropertyName("peers")] public List<KnownPeer> Peers { get; set; } = [];
    }
}
