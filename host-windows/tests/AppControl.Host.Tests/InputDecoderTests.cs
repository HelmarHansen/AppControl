using System.Buffers.Binary;
using AppControl.Host.Input;
using Xunit;

namespace AppControl.Host.Tests;

/// <summary>
/// Prueft den Binaerdecoder gegen die Spezifikation aus docs/04-protocol.md §4.5.
///
/// Der Decoder verarbeitet Daten, die direkt von der Gegenseite kommen. Jeder Test
/// hier ist zugleich ein Test gegen boesartige Eingaben: Ein Absturz beim
/// Dekodieren waere ein Denial-of-Service, ein Pufferueberlauf schlimmer.
/// </summary>
public sealed class InputDecoderTests
{
    private static byte[] Header(InputMessageType type, uint seq, int bodySize)
    {
        var buffer = new byte[6 + bodySize];
        buffer[0] = (byte)type;
        buffer[1] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(2), seq);
        return buffer;
    }

    [Fact]
    public void MouseMove_wird_korrekt_dekodiert()
    {
        var buffer = Header(InputMessageType.MouseMove, seq: 42, bodySize: 12);
        BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(6), 0.25f);
        BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(10), 0.75f);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(14), 1234u);

        var evt = Assert.IsType<InputEvent.MouseMove>(InputDecoder.TryDecode(buffer));

        Assert.Equal(42u, evt.Sequence);
        Assert.Equal(0.25f, evt.X);
        Assert.Equal(0.75f, evt.Y);
        Assert.Equal(1234u, evt.TimestampMs);
    }

    [Fact]
    public void MouseButton_wird_korrekt_dekodiert()
    {
        var buffer = Header(InputMessageType.MouseButton, seq: 7, bodySize: 12);
        BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(6), 0.5f);
        BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(10), 0.5f);
        buffer[14] = (byte)MouseButton.Right;
        buffer[15] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(16), 2);

        var evt = Assert.IsType<InputEvent.MouseButtonEvent>(InputDecoder.TryDecode(buffer));

        Assert.Equal(MouseButton.Right, evt.Button);
        Assert.True(evt.Pressed);
        Assert.Equal(2, evt.ClickCount);
    }

    [Fact]
    public void Key_wird_korrekt_dekodiert()
    {
        var buffer = Header(InputMessageType.Key, seq: 1, bodySize: 5);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(6), 0x04);   // HID "A"
        buffer[8] = 1;                                                       // gedrueckt
        buffer[9] = (byte)(KeyModifiers.Shift | KeyModifiers.Control);
        buffer[10] = 0;

        var evt = Assert.IsType<InputEvent.Key>(InputDecoder.TryDecode(buffer));

        Assert.Equal(0x04, evt.HidUsage);
        Assert.True(evt.Pressed);
        Assert.Equal(KeyModifiers.Shift | KeyModifiers.Control, evt.Modifiers);
        Assert.False(evt.IsRepeat);
    }

    [Fact]
    public void Text_wird_als_UTF8_dekodiert()
    {
        var text = "Grüße 🎉"u8.ToArray();
        var buffer = Header(InputMessageType.Text, seq: 3, bodySize: 2 + text.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(6), (ushort)text.Length);
        text.CopyTo(buffer.AsSpan(8));

        var evt = Assert.IsType<InputEvent.Text>(InputDecoder.TryDecode(buffer));

        Assert.Equal("Grüße 🎉", evt.Value);
    }

    [Fact]
    public void ReleaseAll_braucht_keinen_Rumpf()
    {
        var buffer = Header(InputMessageType.ReleaseAll, seq: 9, bodySize: 0);
        Assert.IsType<InputEvent.ReleaseAll>(InputDecoder.TryDecode(buffer));
    }

    [Fact]
    public void Zu_kurzer_Puffer_gibt_null_statt_zu_werfen()
    {
        Assert.Null(InputDecoder.TryDecode([]));
        Assert.Null(InputDecoder.TryDecode([0x01, 0x00]));
        Assert.Null(InputDecoder.TryDecode(Header(InputMessageType.MouseMove, 1, bodySize: 3)));
    }

    [Fact]
    public void Unbekannter_Typ_gibt_null_statt_zu_werfen()
    {
        // Vorwaertskompatibilitaet: Ein neuerer Viewer darf einen aelteren Host
        // nicht zum Trennen bringen.
        var buffer = Header((InputMessageType)0xFE, seq: 1, bodySize: 10);
        Assert.Null(InputDecoder.TryDecode(buffer));
    }

    [Fact]
    public void Text_mit_luegender_Laengenangabe_wird_abgewiesen()
    {
        // Angriff: Laenge behauptet 1000 Byte, Puffer hat 4. Ohne die Pruefung
        // waere das ein Lesezugriff ueber die Puffergrenze hinaus.
        var buffer = Header(InputMessageType.Text, seq: 1, bodySize: 6);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(6), 1000);

        Assert.Null(InputDecoder.TryDecode(buffer));
    }

    [Fact]
    public void Uebergrosser_Text_wird_abgewiesen()
    {
        // Obergrenze gegen Speicherdruck durch einen boesartigen Peer.
        var length = 5000;
        var buffer = Header(InputMessageType.Text, seq: 1, bodySize: 2 + length);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(6), (ushort)length);

        Assert.Null(InputDecoder.TryDecode(buffer));
    }
}
