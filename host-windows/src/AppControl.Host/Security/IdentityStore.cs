using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NSec.Cryptography;

namespace AppControl.Host.Security;

/// <summary>
/// Langzeit-Identitaet des Hosts: ein statisches X25519-Schluesselpaar, erzeugt
/// beim ersten Start.
///
/// WARUM X25519 UND NICHT ED25519: X25519 kann nicht signieren - aber wir brauchen
/// keine Signaturen. Die Authentifizierung entsteht aus den DH-Operationen selbst
/// ("nur wer den privaten Schluessel hat, kommt auf denselben Sitzungsschluessel").
/// Das ist weniger Code und weniger Gelegenheit, etwas falsch zu machen, als ein
/// Signaturschema mit eigener Nachrichtenzusammensetzung. Der Schluessel wird
/// ausserdem nur fuer genau einen Zweck verwendet - keine Doppelnutzung
/// desselben Materials. Siehe docs/05-security.md §5.3.
///
/// SPEICHERUNG: DPAPI mit CurrentUser-Scope. Der Schluessel ist damit an das
/// Windows-Benutzerkonto gebunden, verlaesst das Geraet nicht und ist ohne
/// Anmeldung des Benutzers nicht lesbar - auch nicht von einem anderen
/// Benutzerkonto auf demselben Rechner.
/// </summary>
public sealed class IdentityStore
{
    private static readonly byte[] DpapiEntropy = "AppControl/identity/v1"u8.ToArray();

    private readonly string _path;
    private readonly ILogger<IdentityStore> _log;
    private Key? _staticKey;

    public IdentityStore(ILogger<IdentityStore> log, string? directory = null)
    {
        _log = log;
        var dir = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppControl");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "identity.key");
    }

    public Key StaticKey => _staticKey ??= LoadOrCreate();

    public byte[] StaticPublicKey => StaticKey.PublicKey.Export(KeyBlobFormat.RawPublicKey);

    public string Fingerprint => Handshake.Fingerprint(StaticPublicKey);

    /// <summary>Anzeigename dieses Geraets. Wird dem Peer im Handshake mitgeteilt.</summary>
    public string DeviceName => $"{Environment.MachineName} (Windows)";

    private Key LoadOrCreate()
    {
        if (File.Exists(_path))
        {
            try
            {
                var protectedBytes = File.ReadAllBytes(_path);
                var raw = ProtectedData.Unprotect(
                    protectedBytes, DpapiEntropy, DataProtectionScope.CurrentUser);
                var key = Key.Import(KeyAgreementAlgorithm.X25519, raw, KeyBlobFormat.RawPrivateKey,
                    new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
                CryptographicOperations.ZeroMemory(raw);
                _log.LogInformation("Identitaet geladen, Fingerprint {Fingerprint}",
                    Handshake.Fingerprint(key.PublicKey.Export(KeyBlobFormat.RawPublicKey)));
                return key;
            }
            catch (Exception ex)
            {
                // Haeufigste Ursache: Die Datei wurde von einem anderen
                // Benutzerkonto kopiert - DPAPI kann sie dann nicht entschluesseln.
                // Neu erzeugen ist hier richtig; der Peer sieht dann eine
                // TOFU-Warnung, was genau das gewuenschte Verhalten ist.
                _log.LogWarning(ex, "Identitaet nicht lesbar - erzeuge eine neue. " +
                                    "Der Peer wird beim naechsten Verbinden eine Warnung sehen.");
            }
        }

        var newKey = Key.Create(KeyAgreementAlgorithm.X25519, new KeyCreationParameters
        {
            ExportPolicy = KeyExportPolicies.AllowPlaintextExport,
        });

        var rawNew = newKey.Export(KeyBlobFormat.RawPrivateKey);
        try
        {
            File.WriteAllBytes(_path,
                ProtectedData.Protect(rawNew, DpapiEntropy, DataProtectionScope.CurrentUser));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(rawNew);
        }

        _log.LogInformation("Neue Identitaet erzeugt, Fingerprint {Fingerprint}",
            Handshake.Fingerprint(newKey.PublicKey.Export(KeyBlobFormat.RawPublicKey)));
        return newKey;
    }
}
