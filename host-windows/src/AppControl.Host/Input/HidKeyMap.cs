namespace AppControl.Host.Input;

/// <summary>
/// Abbildung HID Usage-ID (Usage Page 0x07) -> Windows-Scancode.
///
/// WARUM HID STATT VIRTUAL-KEY-CODES:
/// macOS liefert CGKeyCode (an ANSI-Positionen gebunden), Windows will VK_* oder
/// Scancodes. Eine direkte Umrechnung muesste beide Tastaturlayouts kennen und
/// waere bei abweichenden Layouts (US-Viewer, DE-Host) falsch.
///
/// HID Usage-IDs sind der gemeinsame Standard UNTERHALB beider Systeme: 0x04 ist
/// die Taste an der Position von "A" auf einem US-Layout, unabhaengig davon, was
/// daraufgedruckt ist. Der Viewer bildet CGKeyCode -> HID ab, der Host HID ->
/// Scancode, und Windows wendet das HOST-Layout an.
///
/// Ergebnis: Der Host bekommt das, was seine eigene Tastaturbelegung erzeugen
/// wuerde. Wer auf einem US-Mac die Taste rechts neben "L" drueckt, erzeugt auf
/// einem deutschen Host ein "oe" - genau wie ein Mensch an dieser Tastatur.
/// Siehe docs/04-protocol.md §4.5.
/// </summary>
public static class HidKeyMap
{
    // Haeufig gebrauchte Usages als Konstanten (fuer die Blocklist in InputGuard)
    public const ushort Tab      = 0x2B;
    public const ushort Escape   = 0x29;
    public const ushort Delete   = 0x4C;   // "Delete Forward"
    public const ushort X        = 0x1B;
    public const ushort LeftGui  = 0xE3;   // linke Windows-/Command-Taste
    public const ushort RightGui = 0xE7;

    /// <summary>
    /// HID Usage -> PS/2-Set-1-Scancode. Werte >= 0xE000 sind "extended" und
    /// brauchen KEYEVENTF_EXTENDEDKEY beim Injizieren.
    /// </summary>
    private static readonly Dictionary<ushort, ushort> Map = new()
    {
        // Buchstaben (HID 0x04-0x1D = A-Z in alphabetischer Reihenfolge)
        [0x04] = 0x1E, [0x05] = 0x30, [0x06] = 0x2E, [0x07] = 0x20, [0x08] = 0x12,
        [0x09] = 0x21, [0x0A] = 0x22, [0x0B] = 0x23, [0x0C] = 0x17, [0x0D] = 0x24,
        [0x0E] = 0x25, [0x0F] = 0x26, [0x10] = 0x32, [0x11] = 0x31, [0x12] = 0x18,
        [0x13] = 0x19, [0x14] = 0x10, [0x15] = 0x13, [0x16] = 0x1F, [0x17] = 0x14,
        [0x18] = 0x16, [0x19] = 0x2F, [0x1A] = 0x11, [0x1B] = 0x2D, [0x1C] = 0x15,
        [0x1D] = 0x2C,

        // Ziffernreihe 1-9, 0
        [0x1E] = 0x02, [0x1F] = 0x03, [0x20] = 0x04, [0x21] = 0x05, [0x22] = 0x06,
        [0x23] = 0x07, [0x24] = 0x08, [0x25] = 0x09, [0x26] = 0x0A, [0x27] = 0x0B,

        [0x28] = 0x1C,   // Enter
        [0x29] = 0x01,   // Escape
        [0x2A] = 0x0E,   // Backspace
        [0x2B] = 0x0F,   // Tab
        [0x2C] = 0x39,   // Leertaste
        [0x2D] = 0x0C,   // - / ss
        [0x2E] = 0x0D,   // = / '
        [0x2F] = 0x1A,   // [ / ue
        [0x30] = 0x1B,   // ] / +
        [0x31] = 0x2B,   // Backslash / #
        [0x33] = 0x27,   // ; / oe
        [0x34] = 0x28,   // ' / ae
        [0x35] = 0x29,   // ` / ^
        [0x36] = 0x33,   // ,
        [0x37] = 0x34,   // .
        [0x38] = 0x35,   // / / -
        [0x39] = 0x3A,   // Feststelltaste

        // Funktionstasten F1-F12
        [0x3A] = 0x3B, [0x3B] = 0x3C, [0x3C] = 0x3D, [0x3D] = 0x3E, [0x3E] = 0x3F,
        [0x3F] = 0x40, [0x40] = 0x41, [0x41] = 0x42, [0x42] = 0x43, [0x43] = 0x44,
        [0x44] = 0x57, [0x45] = 0x58,

        // Navigation (extended - fuehrendes 0xE0 im Scancode)
        [0x46] = 0xE037,   // Druck
        [0x47] = 0x46,     // Rollen
        [0x48] = 0x45,     // Pause
        [0x49] = 0xE052,   // Einfg
        [0x4A] = 0xE047,   // Pos1
        [0x4B] = 0xE049,   // Bild auf
        [0x4C] = 0xE053,   // Entf
        [0x4D] = 0xE04F,   // Ende
        [0x4E] = 0xE051,   // Bild ab
        [0x4F] = 0xE04D,   // rechts
        [0x50] = 0xE04B,   // links
        [0x51] = 0xE050,   // runter
        [0x52] = 0xE048,   // hoch

        // Ziffernblock
        [0x53] = 0x45, [0x54] = 0xE035, [0x55] = 0x37, [0x56] = 0x4A, [0x57] = 0x4E,
        [0x58] = 0xE01C, [0x59] = 0x4F, [0x5A] = 0x50, [0x5B] = 0x51, [0x5C] = 0x4B,
        [0x5D] = 0x4C, [0x5E] = 0x4D, [0x5F] = 0x47, [0x60] = 0x48, [0x61] = 0x49,
        [0x62] = 0x52, [0x63] = 0x53,

        [0x64] = 0x56,   // ISO-Taste links neben Y (auf DE-Layouts "<>|")

        // Modifier
        [0xE0] = 0x1D,   [0xE1] = 0x2A,   [0xE2] = 0x38,   [0xE3] = 0xE05B,
        [0xE4] = 0xE01D, [0xE5] = 0x36,   [0xE6] = 0xE038, [0xE7] = 0xE05C,
    };

    public static bool TryGetScanCode(ushort hidUsage, out ushort scanCode, out bool extended)
    {
        if (!Map.TryGetValue(hidUsage, out var raw))
        {
            scanCode = 0;
            extended = false;
            return false;
        }
        extended = (raw & 0xFF00) == 0xE000;
        scanCode = (ushort)(raw & 0x00FF);
        return true;
    }

    public static int Count => Map.Count;

    // TODO(Erweiterung): Consumer-Page-Usages (0x0C) fuer Medientasten -
    // Lautstaerke, Play/Pause, Helligkeit. Der Viewer erfasst sie bereits als
    // NSEvent.systemDefined; hier fehlt nur die Abbildung auf die entsprechenden
    // VK_VOLUME_*/VK_MEDIA_*-Codes.
}
