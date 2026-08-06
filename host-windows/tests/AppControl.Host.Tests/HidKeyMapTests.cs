using AppControl.Host.Input;
using Xunit;

namespace AppControl.Host.Tests;

/// <summary>
/// Prueft die HID-zu-Scancode-Abbildung.
///
/// Die Abbildung ist der Grund, warum Tippen ueber Plattformgrenzen hinweg
/// funktioniert: Der Viewer schickt HID-Usages, der Host uebersetzt sie in
/// Scancodes, und Windows wendet das HOST-Tastaturlayout an. Siehe
/// docs/04-protocol.md §4.5.
/// </summary>
public sealed class HidKeyMapTests
{
    [Theory]
    [InlineData(0x04, 0x1E)]   // A
    [InlineData(0x1D, 0x2C)]   // Z (US-Position)
    [InlineData(0x28, 0x1C)]   // Enter
    [InlineData(0x2C, 0x39)]   // Leertaste
    [InlineData(0x29, 0x01)]   // Escape
    [InlineData(0x3A, 0x3B)]   // F1
    public void Standardtasten_werden_korrekt_abgebildet(ushort hid, ushort expectedScan)
    {
        Assert.True(HidKeyMap.TryGetScanCode(hid, out var scan, out var extended));
        Assert.Equal(expectedScan, scan);
        Assert.False(extended);
    }

    [Theory]
    [InlineData(0x4F)]   // Pfeil rechts
    [InlineData(0x50)]   // Pfeil links
    [InlineData(0x51)]   // Pfeil runter
    [InlineData(0x52)]   // Pfeil hoch
    [InlineData(0x4C)]   // Entf
    [InlineData(0xE3)]   // linke Windows-Taste
    public void Erweiterte_Tasten_werden_als_extended_markiert(ushort hid)
    {
        // Ohne KEYEVENTF_EXTENDEDKEY wuerden Pfeiltasten als Ziffernblock-Tasten
        // interpretiert - ein klassischer, schwer zu findender Fehler.
        Assert.True(HidKeyMap.TryGetScanCode(hid, out _, out var extended));
        Assert.True(extended, $"HID 0x{hid:X2} muss als extended markiert sein");
    }

    [Fact]
    public void Unbekannte_Usage_wird_sauber_abgelehnt()
    {
        Assert.False(HidKeyMap.TryGetScanCode(0xFFFF, out var scan, out var extended));
        Assert.Equal(0, scan);
        Assert.False(extended);
    }

    [Fact]
    public void Alle_Buchstaben_und_Ziffern_sind_abgedeckt()
    {
        for (ushort hid = 0x04; hid <= 0x27; hid++)   // A-Z und 1-0
        {
            Assert.True(HidKeyMap.TryGetScanCode(hid, out _, out _),
                $"HID 0x{hid:X2} fehlt in der Abbildung");
        }
    }

    [Fact]
    public void Die_Abbildung_ist_umkehrbar_eindeutig()
    {
        // Zwei HID-Usages, die auf denselben Scancode zeigen, waeren ein Fehler:
        // Eine Taste wuerde eine andere ausloesen.
        var seen = new Dictionary<(ushort Scan, bool Ext), ushort>();

        for (ushort hid = 0; hid < 0xFF; hid++)
        {
            if (!HidKeyMap.TryGetScanCode(hid, out var scan, out var ext)) continue;
            var key = (scan, ext);

            // Bekannte, korrekte Ausnahme: HID 0x48 (Pause) und 0x53 (NumLock)
            // teilen sich historisch den Scancode 0x45.
            if (seen.TryGetValue(key, out var other) && !(other is 0x48 && hid is 0x53))
            {
                Assert.Fail($"Scancode 0x{scan:X2} (ext={ext}) ist doppelt belegt: " +
                            $"HID 0x{other:X2} und HID 0x{hid:X2}");
            }
            seen[key] = hid;
        }
    }
}
