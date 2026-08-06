namespace AppControl.Host.Security;

/// <summary>
/// Die 64 Symbole der SAS-Darstellung. 6 Bit pro Emoji, 5 Emojis = 30 Bit.
///
/// AUSWAHLKRITERIEN: visuell klar unterscheidbar, auf macOS und Windows aehnlich
/// dargestellt, in beiden Sprachen leicht benennbar (die Nutzer lesen sie sich am
/// Telefon vor). Keine Flaggen, keine Hautfarben-Modifier, keine Symbole, die
/// sich zwischen Plattformen stark unterscheiden.
///
/// DIE REIHENFOLGE IST TEIL DES PROTOKOLLS. Aendert sie sich, zeigen Host und
/// Viewer unterschiedliche Symbole fuer denselben Schluessel, und die Nutzer
/// brechen eine voellig gesunde Verbindung ab. Gegenstueck: EmojiTable.swift und
/// tools/crypto-vectors/generate.mjs - alle drei muessen identisch sein.
/// </summary>
public static class EmojiTable
{
    public static readonly string[] Symbols =
    [
        "\U0001F419", "\U0001F352", "\U0001F680", "\U0001F514", "\U0001F3A9", "\U0001F335", "\U0001F418", "\U0001F355",
        "\U00002693", "\U0001F3B8", "\U0001F98A", "\U0001F344", "\U0001F6B2", "\U0001F319", "\U0001F427", "\U0001F34B",
        "\U0001F3F0", "\U0001F3BA", "\U0001F98B", "\U0001F345", "\U0001F682", "\U0001F31F", "\U0001F422", "\U0001F347",
        "\U000026FA", "\U0001F3BB", "\U0001F981", "\U0001F349", "\U0001F6F5", "\U0001F308", "\U0001F433", "\U0001F35E",
        "\U0001F5FF", "\U0001F941", "\U0001F989", "\U0001F351", "\U0001F681", "\U00002600", "\U0001F41D", "\U0001F955",
        "\U0001F3D4", "\U0001F3B9", "\U0001F99C", "\U0001F369", "\U000026F5", "\U0001F33B", "\U0001F42C", "\U0001F9C0",
        "\U0001F3AA", "\U0001F3B7", "\U0001F9A9", "\U0001F353", "\U0001F69C", "\U00002744", "\U0001F43F", "\U0001F330",
        "\U0001F5FC", "\U0001FA95", "\U0001F994", "\U0001F34D", "\U0001F6F8", "\U0001F340", "\U0001F9AD", "\U0001F968",
    ];

    static EmojiTable()
    {
        // Ein Tippfehler in der Tabelle wuerde sich sonst erst beim Nutzer als
        // "die Emojis stimmen nicht ueberein" aeussern - dem am schwersten zu
        // diagnostizierenden Fehlerbild, das dieses Protokoll hat.
        if (Symbols.Length != 64)
            throw new InvalidOperationException($"SAS-Tabelle muss 64 Symbole haben, hat {Symbols.Length}");
        if (Symbols.Distinct().Count() != 64)
            throw new InvalidOperationException("SAS-Tabelle enthaelt Duplikate");
    }
}
