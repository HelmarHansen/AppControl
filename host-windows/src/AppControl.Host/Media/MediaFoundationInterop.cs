using System.Runtime.InteropServices;

namespace AppControl.Host.Media;

/// <summary>
/// COM-Deklarationen und Konstanten fuer Media Foundation.
///
/// KEIN WRAPPER-PAKET, aus demselben Grund wie in Capture/D3D11Helper.cs: Es
/// geht um eine Handvoll Schnittstellen mit stabilen, dokumentierten Signaturen.
/// Vortice.MediaFoundation waere ~30 MB Abhaengigkeit fuer Code, den man hier
/// auf zwei Bildschirmseiten sieht - und bei Interop ist es hilfreich, genau zu
/// sehen, was ueber die Grenze geht.
///
/// ── ZU DEN PLATZHALTER-METHODEN ──────────────────────────────────────────────
///
/// COM loest Methoden ueber ihre POSITION in der Vtable auf, nicht ueber ihren
/// Namen. Eine abgeleitete Schnittstelle wie IMFSample beginnt deshalb erst
/// hinter allen 30 Methoden von IMFAttributes - und C# kann COM-Vtables nicht
/// ueber Interface-Vererbung zusammensetzen, jede Methode muss neu deklariert
/// werden.
///
/// Diese 30 Signaturen dreimal fehlerfrei abzuschreiben ist Arbeit ohne Ertrag:
/// Wir rufen von den Attribut-Methoden nur eine Handvoll auf. Alle anderen
/// Plaetze stehen deshalb als <c>SlotNN()</c> da. Ein Platzhalter belegt genau
/// einen Vtable-Eintrag, unabhaengig von seiner Signatur - er darf nur niemals
/// aufgerufen werden. Die Nummerierung macht sichtbar, dass kein Platz fehlt;
/// eine ausgelassene Zeile wuerde jede Methode danach um eins verschieben, und
/// der Fehler zeigte sich als Absturz an voellig anderer Stelle.
/// </summary>
internal static class MfInterop
{
    // ── Versionen und Startflags ─────────────────────────────────────────────

    /// <summary>MF_SDK_VERSION (0x0002) &lt;&lt; 16 | MF_API_VERSION (0x0070).</summary>
    public const uint MF_VERSION = 0x00020070;

    /// <summary>
    /// Ohne Netzwerk-Unterstuetzung starten. AppControl liest keine Streams von
    /// URLs - der Socket-Stack von Media Foundation waere reine Angriffsflaeche.
    /// </summary>
    public const uint MFSTARTUP_NOSOCKET = 1;

    // ── Medientyp-Attribute ──────────────────────────────────────────────────

    public static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    public static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    public static readonly Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
    public static readonly Guid MF_MT_FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    public static readonly Guid MF_MT_PIXEL_ASPECT_RATIO = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    public static readonly Guid MF_MT_INTERLACE_MODE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    public static readonly Guid MF_MT_AVG_BITRATE = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    public static readonly Guid MF_MT_MPEG2_PROFILE = new("ad76a80b-2d5c-4e0b-b375-64e520137036");
    public static readonly Guid MF_MT_ALL_SAMPLES_INDEPENDENT = new("c9173739-5e56-461c-b713-46fb995cb95f");

    /// <summary>
    /// SPS und PPS als Blob am Ausgabetyp. Wird gebraucht, falls der Encoder die
    /// Parametersaetze NICHT vor jeden IDR-Frame legt - siehe
    /// <see cref="MediaFoundationH264Encoder"/>.
    /// </summary>
    public static readonly Guid MF_MT_MPEG_SEQUENCE_HEADER = new("3c036de7-3ad0-4c9e-9216-ee6d6ac21cb3");

    public static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00aa00389b71");

    /// <summary>FourCC 'NV12'. Das Eingabeformat praktisch jedes Hardware-Encoders.</summary>
    public static readonly Guid MFVideoFormat_NV12 = new("3231564e-0000-0010-8000-00aa00389b71");

    /// <summary>FourCC 'H264', Annex-B-Bytestream mit Startcodes.</summary>
    public static readonly Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00aa00389b71");

    /// <summary>
    /// D3DFMT_A8R8G8B8 (21). Trotz des Namens liegen die Bytes als BGRA im
    /// Speicher - genau das, was Windows.Graphics.Capture als
    /// B8G8R8A8UIntNormalized liefert.
    /// </summary>
    public static readonly Guid MFVideoFormat_ARGB32 = new("00000015-0000-0010-8000-00aa00389b71");

    public const uint MFVideoInterlace_Progressive = 2;
    public const uint eAVEncH264VProfile_Main = 77;
    public const uint eAVEncCommonRateControlMode_CBR = 0;

    // ── Transform-Attribute ──────────────────────────────────────────────────

    public static readonly Guid MF_TRANSFORM_ASYNC = new("f81a699a-649a-497d-8c73-29f8fed6ad7a");
    public static readonly Guid MF_TRANSFORM_ASYNC_UNLOCK = new("e5666d6b-3422-4eb6-a421-da7db1f8e207");
    public static readonly Guid MF_SA_D3D11_AWARE = new("206b4fc8-fcf9-4c51-afe3-9764369e33a0");

    // ── Codec-API (Encoder-Feineinstellung) ──────────────────────────────────

    public static readonly Guid CODECAPI_AVLowLatencyMode = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    public static readonly Guid CODECAPI_AVEncCommonRateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    public static readonly Guid CODECAPI_AVEncCommonMeanBitRate = new("f7222374-2144-4815-b550-a37f8e12ee52");
    public static readonly Guid CODECAPI_AVEncCommonQualityVsSpeed = new("98332df8-03cd-476b-89fa-3f9e442dec9f");
    public static readonly Guid CODECAPI_AVEncMPVGOPSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");
    public static readonly Guid CODECAPI_AVEncMPVDefaultBPictureCount = new("8d390aac-dc5c-4200-b57f-814d04bab74c");
    public static readonly Guid CODECAPI_AVEncVideoForceKeyFrame = new("398c1b98-8353-475a-9ef2-8f265d260345");

    // ── Kategorien, Klassen, Schnittstellen-IDs ──────────────────────────────

    public static readonly Guid MFT_CATEGORY_VIDEO_ENCODER = new("f79eac7d-e545-4387-bdee-d647d7bde42a");
    public static readonly Guid CLSID_VideoProcessorMFT = new("88753b26-5b24-49bd-b2e7-c902d6c2a4b7");
    public static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    public static readonly Guid IID_ID3D11Device = new("db6f6ddb-ac77-4e88-8253-819df9bbf140");
    public static readonly Guid IID_IMFTransform = new("bf94c121-5b05-4e6f-8000-ba598961414d");

    // ── Aufzaehlungsflags fuer MFTEnumEx ─────────────────────────────────────

    public const uint MFT_ENUM_FLAG_SYNCMFT = 0x00000001;
    public const uint MFT_ENUM_FLAG_ASYNCMFT = 0x00000002;
    public const uint MFT_ENUM_FLAG_HARDWARE = 0x00000004;
    public const uint MFT_ENUM_FLAG_SORTANDFILTER = 0x00000040;

    // ── Transform-Nachrichten ────────────────────────────────────────────────

    public const uint MFT_MESSAGE_COMMAND_FLUSH = 0x00000000;
    public const uint MFT_MESSAGE_COMMAND_DRAIN = 0x00000001;
    public const uint MFT_MESSAGE_SET_D3D_MANAGER = 0x00000002;
    public const uint MFT_MESSAGE_NOTIFY_BEGIN_STREAMING = 0x10000000;
    public const uint MFT_MESSAGE_NOTIFY_END_STREAMING = 0x10000001;
    public const uint MFT_MESSAGE_NOTIFY_END_OF_STREAM = 0x10000002;
    public const uint MFT_MESSAGE_NOTIFY_START_OF_STREAM = 0x10000003;

    /// <summary>Der MFT stellt seine Ausgabe-Samples selbst bereit.</summary>
    public const uint MFT_OUTPUT_STREAM_PROVIDES_SAMPLES = 0x00000100;

    // ── Ereignisse asynchroner MFTs ──────────────────────────────────────────

    public const uint METransformNeedInput = 601;
    public const uint METransformHaveOutput = 602;
    public const uint METransformDrainComplete = 603;

    // ── HRESULTs, die wir auswerten statt zu werfen ──────────────────────────

    public const int MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);
    public const int MF_E_TRANSFORM_STREAM_CHANGE = unchecked((int)0xC00D6D61);
    public const int MF_E_SHUTDOWN = unchecked((int)0xC00D3E85);
    public const int MF_E_ATTRIBUTENOTFOUND = unchecked((int)0xC00D36E6);
    public const int E_NOTIMPL = unchecked((int)0x80004001);

    // ── Strukturen ───────────────────────────────────────────────────────────
    //
    // CS0649 ("Feld wird nie zugewiesen") ist hier falsch: Diese Felder fuellt
    // nativer Code. Die Warnung punktuell abzuschalten ist ehrlicher, als die
    // Strukturen kuenstlich oeffentlich zu machen, damit sie durchrutscht.
#pragma warning disable CS0649

    [StructLayout(LayoutKind.Sequential)]
    public struct MftRegisterTypeInfo
    {
        public Guid guidMajorType;
        public Guid guidSubtype;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MftInputStreamInfo
    {
        public long hnsMaxLatency;
        public uint dwFlags;
        public uint cbSize;
        public uint cbMaxLookahead;
        public uint cbAlignment;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MftOutputStreamInfo
    {
        public uint dwFlags;
        public uint cbSize;
        public uint cbAlignment;
    }

    /// <summary>
    /// Auf x64 deckt sich das natuerliche Alignment mit dem der nativen
    /// Struktur: 4 Byte + 4 Byte Fuellung + 8 + 4 + 4 Fuellung + 8 = 32 Byte.
    /// Das Projekt ist auf x64 festgelegt (siehe csproj), sonst waere hier
    /// explizites Pack noetig.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MftOutputDataBuffer
    {
        public uint dwStreamID;
        public nint pSample;
        public uint dwStatus;
        public nint pEvents;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DxgiSampleDesc
    {
        public uint Count;
        public uint Quality;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct D3D11Texture2DDesc
    {
        public uint Width;
        public uint Height;
        public uint MipLevels;
        public uint ArraySize;
        public uint Format;
        public DxgiSampleDesc SampleDesc;
        public uint Usage;
        public uint BindFlags;
        public uint CPUAccessFlags;
        public uint MiscFlags;
    }

#pragma warning restore CS0649

    public const uint DXGI_FORMAT_NV12 = 103;
    public const uint D3D11_USAGE_DEFAULT = 0;
    public const uint D3D11_BIND_SHADER_RESOURCE = 0x8;
    public const uint D3D11_BIND_RENDER_TARGET = 0x20;

    // ── Native Funktionen ────────────────────────────────────────────────────

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFStartup(uint version, uint flags);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFShutdown();

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFCreateMediaType(out IMFMediaType ppMFType);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFCreateSample(out IMFSample ppIMFSample);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFCreateMemoryBuffer(uint cbMaxLength, out IMFMediaBuffer ppBuffer);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFCreateDXGIDeviceManager(out uint resetToken, out IMFDXGIDeviceManager ppDeviceManager);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFCreateDXGISurfaceBuffer(
        [In] ref Guid riid,
        [MarshalAs(UnmanagedType.IUnknown)] object punkSurface,
        uint uSubresourceIndex,
        [MarshalAs(UnmanagedType.Bool)] bool fBottomUpWhenLinear,
        out IMFMediaBuffer ppBuffer);

    /// <summary>
    /// Liefert ein CoTaskMem-Array aus IMFActivate-Zeigern. Beides muss der
    /// Aufrufer freigeben: die einzelnen Zeiger per Release, das Array per
    /// CoTaskMemFree.
    /// </summary>
    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFTEnumEx(
        Guid guidCategory,
        uint Flags,
        [In] ref MftRegisterTypeInfo pInputType,
        [In] ref MftRegisterTypeInfo pOutputType,
        out nint pppMFTActivate,
        out uint pnumMFTActivate);

    // ── Schnittstellen ───────────────────────────────────────────────────────

    [ComImport]
    [Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFAttributes
    {
        [PreserveSig] int Slot00();
        [PreserveSig] int Slot01();
        [PreserveSig] int Slot02();
        [PreserveSig] int Slot03();
        [PreserveSig] int GetUINT32([In] ref Guid guidKey, out uint punValue);
        [PreserveSig] int GetUINT64([In] ref Guid guidKey, out ulong punValue);
        [PreserveSig] int Slot06();
        [PreserveSig] int GetGUID([In] ref Guid guidKey, out Guid pguidValue);
        [PreserveSig] int Slot08();
        [PreserveSig] int Slot09();
        [PreserveSig] int Slot10();
        [PreserveSig] int GetBlobSize([In] ref Guid guidKey, out uint pcbBlobSize);
        [PreserveSig] int GetBlob([In] ref Guid guidKey, [Out] byte[] pBuf, uint cbBufSize, out uint pcbBlobSize);
        [PreserveSig] int Slot13();
        [PreserveSig] int Slot14();
        [PreserveSig] int Slot15();
        [PreserveSig] int Slot16();
        [PreserveSig] int Slot17();
        [PreserveSig] int SetUINT32([In] ref Guid guidKey, uint unValue);
        [PreserveSig] int SetUINT64([In] ref Guid guidKey, ulong unValue);
        [PreserveSig] int Slot20();
        [PreserveSig] int SetGUID([In] ref Guid guidKey, [In] ref Guid guidValue);
        [PreserveSig] int Slot22();
        [PreserveSig] int Slot23();
        [PreserveSig] int SetUnknown([In] ref Guid guidKey, [MarshalAs(UnmanagedType.IUnknown)] object? pUnknown);
        [PreserveSig] int Slot25();
        [PreserveSig] int Slot26();
        [PreserveSig] int Slot27();
        [PreserveSig] int Slot28();
        [PreserveSig] int Slot29();
    }

    /// <summary>
    /// IMFMediaType leitet von IMFAttributes ab; die ersten 30 Plaetze sind
    /// deshalb identisch. Die eigenen Methoden ab Platz 30 brauchen wir nicht -
    /// die Deklaration endet hier. Abschneiden ist unbedenklich, solange nichts
    /// dahinter aufgerufen wird; die Plaetze davor stimmen.
    /// </summary>
    [ComImport]
    [Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFMediaType
    {
        [PreserveSig] int Slot00();
        [PreserveSig] int Slot01();
        [PreserveSig] int Slot02();
        [PreserveSig] int Slot03();
        [PreserveSig] int GetUINT32([In] ref Guid guidKey, out uint punValue);
        [PreserveSig] int GetUINT64([In] ref Guid guidKey, out ulong punValue);
        [PreserveSig] int Slot06();
        [PreserveSig] int GetGUID([In] ref Guid guidKey, out Guid pguidValue);
        [PreserveSig] int Slot08();
        [PreserveSig] int Slot09();
        [PreserveSig] int Slot10();
        [PreserveSig] int GetBlobSize([In] ref Guid guidKey, out uint pcbBlobSize);
        [PreserveSig] int GetBlob([In] ref Guid guidKey, [Out] byte[] pBuf, uint cbBufSize, out uint pcbBlobSize);
        [PreserveSig] int Slot13();
        [PreserveSig] int Slot14();
        [PreserveSig] int Slot15();
        [PreserveSig] int Slot16();
        [PreserveSig] int Slot17();
        [PreserveSig] int SetUINT32([In] ref Guid guidKey, uint unValue);
        [PreserveSig] int SetUINT64([In] ref Guid guidKey, ulong unValue);
        [PreserveSig] int Slot20();
        [PreserveSig] int SetGUID([In] ref Guid guidKey, [In] ref Guid guidValue);
    }

    [ComImport]
    [Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFSample
    {
        [PreserveSig] int Slot00();
        [PreserveSig] int Slot01();
        [PreserveSig] int Slot02();
        [PreserveSig] int Slot03();
        [PreserveSig] int GetUINT32([In] ref Guid guidKey, out uint punValue);
        [PreserveSig] int Slot05();
        [PreserveSig] int Slot06();
        [PreserveSig] int Slot07();
        [PreserveSig] int Slot08();
        [PreserveSig] int Slot09();
        [PreserveSig] int Slot10();
        [PreserveSig] int Slot11();
        [PreserveSig] int Slot12();
        [PreserveSig] int Slot13();
        [PreserveSig] int Slot14();
        [PreserveSig] int Slot15();
        [PreserveSig] int Slot16();
        [PreserveSig] int Slot17();
        [PreserveSig] int SetUINT32([In] ref Guid guidKey, uint unValue);
        [PreserveSig] int Slot19();
        [PreserveSig] int Slot20();
        [PreserveSig] int Slot21();
        [PreserveSig] int Slot22();
        [PreserveSig] int Slot23();
        [PreserveSig] int Slot24();
        [PreserveSig] int Slot25();
        [PreserveSig] int Slot26();
        [PreserveSig] int Slot27();
        [PreserveSig] int Slot28();
        [PreserveSig] int Slot29();
        // ── ab hier IMFSample selbst ──
        [PreserveSig] int GetSampleFlags(out uint pdwSampleFlags);
        [PreserveSig] int SetSampleFlags(uint dwSampleFlags);
        [PreserveSig] int GetSampleTime(out long phnsSampleTime);
        [PreserveSig] int SetSampleTime(long hnsSampleTime);
        [PreserveSig] int GetSampleDuration(out long phnsSampleDuration);
        [PreserveSig] int SetSampleDuration(long hnsSampleDuration);
        [PreserveSig] int GetBufferCount(out uint pdwBufferCount);
        [PreserveSig] int GetBufferByIndex(uint dwIndex, out IMFMediaBuffer ppBuffer);
        [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer ppBuffer);
        [PreserveSig] int AddBuffer(IMFMediaBuffer pBuffer);
        [PreserveSig] int RemoveBufferByIndex(uint dwIndex);
        [PreserveSig] int RemoveAllBuffers();
        [PreserveSig] int GetTotalLength(out uint pcbTotalLength);
    }

    [ComImport]
    [Guid("045fa593-8799-42b8-bc8d-8968c6453507")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFMediaBuffer
    {
        [PreserveSig] int Lock(out nint ppbBuffer, out uint pcbMaxLength, out uint pcbCurrentLength);
        [PreserveSig] int Unlock();
        [PreserveSig] int GetCurrentLength(out uint pcbCurrentLength);
        [PreserveSig] int SetCurrentLength(uint cbCurrentLength);
        [PreserveSig] int GetMaxLength(out uint pcbMaxLength);
    }

    [ComImport]
    [Guid("7dc9d5f9-9ed9-44ec-9bbf-0600bb589fbb")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMF2DBuffer
    {
        [PreserveSig] int Lock2D(out nint pbScanline0, out int plPitch);
        [PreserveSig] int Unlock2D();
        [PreserveSig] int GetScanline0AndPitch(out nint pbScanline0, out int plPitch);
        [PreserveSig] int IsContiguousFormat([MarshalAs(UnmanagedType.Bool)] out bool pfIsContiguous);
        [PreserveSig] int GetContiguousLength(out uint pcbLength);
        [PreserveSig] int ContiguousCopyTo(nint pbDestBuffer, uint cbDestBuffer);
        [PreserveSig] int ContiguousCopyFrom(nint pbSrcBuffer, uint cbSrcBuffer);
    }

    [ComImport]
    [Guid("bf94c121-5b05-4e6f-8000-ba598961414d")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFTransform
    {
        [PreserveSig] int GetStreamLimits(out uint pdwInputMinimum, out uint pdwInputMaximum,
                                          out uint pdwOutputMinimum, out uint pdwOutputMaximum);
        [PreserveSig] int GetStreamCount(out uint pcInputStreams, out uint pcOutputStreams);
        [PreserveSig] int GetStreamIDs(uint dwInputIDArraySize, [Out] uint[] pdwInputIDs,
                                       uint dwOutputIDArraySize, [Out] uint[] pdwOutputIDs);
        [PreserveSig] int GetInputStreamInfo(uint dwInputStreamID, out MftInputStreamInfo pStreamInfo);
        [PreserveSig] int GetOutputStreamInfo(uint dwOutputStreamID, out MftOutputStreamInfo pStreamInfo);
        [PreserveSig] int GetAttributes(out IMFAttributes pAttributes);
        [PreserveSig] int GetInputStreamAttributes(uint dwInputStreamID, out IMFAttributes pAttributes);
        [PreserveSig] int GetOutputStreamAttributes(uint dwOutputStreamID, out IMFAttributes pAttributes);
        [PreserveSig] int DeleteInputStream(uint dwStreamID);
        [PreserveSig] int AddInputStreams(uint cStreams, [In] uint[] adwStreamIDs);
        [PreserveSig] int GetInputAvailableType(uint dwInputStreamID, uint dwTypeIndex, out IMFMediaType ppType);
        [PreserveSig] int GetOutputAvailableType(uint dwOutputStreamID, uint dwTypeIndex, out IMFMediaType ppType);
        [PreserveSig] int SetInputType(uint dwInputStreamID, IMFMediaType? pType, uint dwFlags);
        [PreserveSig] int SetOutputType(uint dwOutputStreamID, IMFMediaType? pType, uint dwFlags);
        [PreserveSig] int GetInputCurrentType(uint dwInputStreamID, out IMFMediaType ppType);
        [PreserveSig] int GetOutputCurrentType(uint dwOutputStreamID, out IMFMediaType ppType);
        [PreserveSig] int GetInputStatus(uint dwInputStreamID, out uint pdwFlags);
        [PreserveSig] int GetOutputStatus(out uint pdwFlags);
        [PreserveSig] int SetOutputBounds(long hnsLowerBound, long hnsUpperBound);
        [PreserveSig] int ProcessEvent(uint dwInputStreamID, nint pEvent);
        [PreserveSig] int ProcessMessage(uint eMessage, nint ulParam);
        [PreserveSig] int ProcessInput(uint dwInputStreamID, IMFSample pSample, uint dwFlags);
        [PreserveSig] int ProcessOutput(uint dwFlags, uint cOutputBufferCount,
                                        ref MftOutputDataBuffer pOutputSamples, out uint pdwStatus);
    }

    [ComImport]
    [Guid("2cd0bd52-bcd5-4b89-b62c-eadc0c031e7d")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFMediaEventGenerator
    {
        /// <summary>
        /// Blockiert, bis ein Ereignis vorliegt. Genau deshalb braucht der
        /// asynchrone Encoder-Pfad einen eigenen Thread - und genau deshalb
        /// kommt er ohne IMFAsyncCallback aus.
        /// </summary>
        [PreserveSig] int GetEvent(uint dwFlags, out IMFMediaEvent ppEvent);
        [PreserveSig] int BeginGetEvent(nint pCallback, nint punkState);
        [PreserveSig] int EndGetEvent(nint pResult, out IMFMediaEvent ppEvent);
        [PreserveSig] int QueueEvent(uint met, [In] ref Guid guidExtendedType, int hrStatus, nint pvValue);
    }

    [ComImport]
    [Guid("df598932-f10c-4e39-bba2-c308f101daa3")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFMediaEvent
    {
        [PreserveSig] int Slot00();
        [PreserveSig] int Slot01();
        [PreserveSig] int Slot02();
        [PreserveSig] int Slot03();
        [PreserveSig] int Slot04();
        [PreserveSig] int Slot05();
        [PreserveSig] int Slot06();
        [PreserveSig] int Slot07();
        [PreserveSig] int Slot08();
        [PreserveSig] int Slot09();
        [PreserveSig] int Slot10();
        [PreserveSig] int Slot11();
        [PreserveSig] int Slot12();
        [PreserveSig] int Slot13();
        [PreserveSig] int Slot14();
        [PreserveSig] int Slot15();
        [PreserveSig] int Slot16();
        [PreserveSig] int Slot17();
        [PreserveSig] int Slot18();
        [PreserveSig] int Slot19();
        [PreserveSig] int Slot20();
        [PreserveSig] int Slot21();
        [PreserveSig] int Slot22();
        [PreserveSig] int Slot23();
        [PreserveSig] int Slot24();
        [PreserveSig] int Slot25();
        [PreserveSig] int Slot26();
        [PreserveSig] int Slot27();
        [PreserveSig] int Slot28();
        [PreserveSig] int Slot29();
        // ── ab hier IMFMediaEvent selbst ──
        [PreserveSig] int GetTypeInfo(out uint pmet);
        [PreserveSig] int GetExtendedType(out Guid pguidExtendedType);
        [PreserveSig] int GetStatus(out int phrStatus);
    }

    [ComImport]
    [Guid("7fee9e9a-4a89-47a6-899c-b6a53a70fb67")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFActivate
    {
        [PreserveSig] int Slot00();
        [PreserveSig] int Slot01();
        [PreserveSig] int Slot02();
        [PreserveSig] int Slot03();
        [PreserveSig] int Slot04();
        [PreserveSig] int Slot05();
        [PreserveSig] int Slot06();
        [PreserveSig] int Slot07();
        [PreserveSig] int Slot08();
        [PreserveSig] int GetString([In] ref Guid guidKey, [Out] char[] pwszValue,
                                    uint cchBufSize, out uint pcchLength);
        [PreserveSig] int Slot10();
        [PreserveSig] int Slot11();
        [PreserveSig] int Slot12();
        [PreserveSig] int Slot13();
        [PreserveSig] int Slot14();
        [PreserveSig] int Slot15();
        [PreserveSig] int Slot16();
        [PreserveSig] int Slot17();
        [PreserveSig] int Slot18();
        [PreserveSig] int Slot19();
        [PreserveSig] int Slot20();
        [PreserveSig] int Slot21();
        [PreserveSig] int Slot22();
        [PreserveSig] int Slot23();
        [PreserveSig] int Slot24();
        [PreserveSig] int Slot25();
        [PreserveSig] int Slot26();
        [PreserveSig] int Slot27();
        [PreserveSig] int Slot28();
        [PreserveSig] int Slot29();
        // ── ab hier IMFActivate selbst ──
        [PreserveSig] int ActivateObject([In] ref Guid riid,
                                         [MarshalAs(UnmanagedType.Interface)] out object ppv);
        [PreserveSig] int ShutdownObject();
        [PreserveSig] int DetachObject();
    }

    [ComImport]
    [Guid("eb533d5d-2db6-40f8-97a9-494692014f07")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFDXGIDeviceManager
    {
        [PreserveSig] int CloseDeviceHandle(nint hDevice);
        [PreserveSig] int GetVideoService(nint hDevice, [In] ref Guid riid, out nint ppService);
        [PreserveSig] int LockDevice(nint hDevice, [In] ref Guid riid, out nint ppUnkDevice,
                                     [MarshalAs(UnmanagedType.Bool)] bool fBlock);
        [PreserveSig] int OpenDeviceHandle(out nint phDevice);
        [PreserveSig] int ResetDevice([MarshalAs(UnmanagedType.IUnknown)] object pUnkDevice, uint resetToken);
        [PreserveSig] int TestDevice(nint hDevice);
        [PreserveSig] int UnlockDevice(nint hDevice, [MarshalAs(UnmanagedType.Bool)] bool fSaveState);
    }

    [ComImport]
    [Guid("901db4c7-31ce-41a2-85dc-8fa0bf41b8da")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ICodecAPI
    {
        [PreserveSig] int IsSupported([In] ref Guid Api);
        [PreserveSig] int IsModifiable([In] ref Guid Api);
        [PreserveSig] int GetParameterRange([In] ref Guid Api,
                                            [MarshalAs(UnmanagedType.Struct)] out object ValueMin,
                                            [MarshalAs(UnmanagedType.Struct)] out object ValueMax,
                                            [MarshalAs(UnmanagedType.Struct)] out object SteppingDelta);
        [PreserveSig] int GetParameterValues([In] ref Guid Api, out nint Values, out uint ValuesCount);
        [PreserveSig] int GetDefaultValue([In] ref Guid Api,
                                          [MarshalAs(UnmanagedType.Struct)] out object Value);
        [PreserveSig] int GetValue([In] ref Guid Api,
                                   [MarshalAs(UnmanagedType.Struct)] out object Value);
        [PreserveSig] int SetValue([In] ref Guid Api,
                                   [In, MarshalAs(UnmanagedType.Struct)] ref object Value);
    }

    /// <summary>
    /// ID3D11Device, ABGESCHNITTEN NACH CreateTexture2D.
    ///
    /// Die vollstaendige Schnittstelle hat ueber 40 Methoden; wir brauchen genau
    /// eine. Die beiden davor muessen trotzdem dastehen, weil sie die Position
    /// bestimmen - alles danach entfaellt. Ein Aufruf einer nicht deklarierten
    /// Methode ist damit unmoeglich, was hier ein Vorteil ist.
    /// </summary>
    [ComImport]
    [Guid("db6f6ddb-ac77-4e88-8253-819df9bbf140")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ID3D11DevicePartial
    {
        [PreserveSig] int CreateBuffer(nint pDesc, nint pInitialData, out nint ppBuffer);
        [PreserveSig] int CreateTexture1D(nint pDesc, nint pInitialData, out nint ppTexture1D);
        [PreserveSig] int CreateTexture2D([In] ref D3D11Texture2DDesc pDesc, nint pInitialData,
                                          [MarshalAs(UnmanagedType.IUnknown)] out object ppTexture2D);
    }

    // ── Hilfsfunktionen ──────────────────────────────────────────────────────

    /// <summary>
    /// MFSetAttributeSize und MFSetAttributeRatio sind im SDK Inline-Funktionen,
    /// keine exportierten Symbole: Beide packen zwei 32-Bit-Werte in ein UINT64.
    /// </summary>
    public static ulong PackRatio(uint high, uint low) => ((ulong)high << 32) | low;

    public static void Check(int hr, string what)
    {
        if (hr < 0)
        {
            throw new InvalidOperationException(
                $"{what} ist fehlgeschlagen (HRESULT 0x{hr:X8}).",
                Marshal.GetExceptionForHR(hr));
        }
    }
}
