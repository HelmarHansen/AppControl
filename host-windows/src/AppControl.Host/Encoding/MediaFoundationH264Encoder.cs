using Windows.Graphics.DirectX.Direct3D11;
using Microsoft.Extensions.Logging;

namespace AppControl.Host.Encoding;

/// <summary>
/// H.264-Encoder ueber Media Foundation.
///
/// Media Foundation waehlt automatisch den Hardware-Encoder der vorhandenen GPU
/// (Intel QuickSync, NVIDIA NVENC, AMD VCE) und faellt sonst auf den
/// Software-Encoder zurueck. Wir muessen keinen Herstellercode einbinden.
///
/// DER ENTSCHEIDENDE PUNKT ist der D3D-Device-Manager (Schritt 2 unten): Ohne ihn
/// akzeptiert der MFT nur Systemspeicher-Puffer, und jedes Frame muesste von der
/// GPU herunter- und wieder hinaufkopiert werden. Mit ihm bleibt die Textur auf
/// der GPU - der gesamte Weg von der Erfassung bis zum NAL-Puffer ist kopierfrei.
/// </summary>
public sealed class MediaFoundationH264Encoder : IVideoEncoder
{
    private readonly ILogger<MediaFoundationH264Encoder> _log;
    private readonly IDirect3DDevice _device;
    private EncoderSettings _settings = new();
    private bool _keyframeRequested;
    private bool _initialized;

    public event Action<EncodedFrame>? FrameEncoded;

    public EncoderSettings CurrentSettings => _settings;

    public MediaFoundationH264Encoder(IDirect3DDevice device, ILogger<MediaFoundationH264Encoder> log)
    {
        _device = device;
        _log = log;
    }

    public void Initialize(EncoderSettings settings)
    {
        _settings = settings;

        // ── IMPLEMENTIERUNGSLEITFADEN ────────────────────────────────────────
        //
        // Vollstaendige Umsetzung mit Vortice.MediaFoundation (bereits als
        // PackageReference eingebunden):
        //
        // 1. MFStartup(MF_VERSION, MFSTARTUP_NOSOCKET)
        //
        // 2. D3D-Device-Manager erzeugen und AN DEN MFT UEBERGEBEN:
        //       MFCreateDXGIDeviceManager(out var resetToken, out var manager);
        //       manager.ResetDevice(d3dDevice, resetToken);
        //       transform.ProcessMessage(MFT_MESSAGE_SET_D3D_MANAGER, manager);
        //    OHNE DIESEN SCHRITT laeuft alles ueber den Systemspeicher, und die
        //    gesamte Zero-Copy-Architektur aus CaptureEngine ist wirkungslos.
        //
        // 3. Encoder-MFT auflisten:
        //       MFTEnumEx(MFT_CATEGORY_VIDEO_ENCODER,
        //                 MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG_SORTANDFILTER,
        //                 inputType: MFVideoFormat_NV12,
        //                 outputType: MFVideoFormat_H264)
        //    Das erste Ergebnis ist der bevorzugte Hardware-Encoder.
        //
        // 4. Ausgabetyp setzen (VOR dem Eingabetyp - Media Foundation verlangt
        //    diese Reihenfolge):
        //       MF_MT_SUBTYPE            = MFVideoFormat_H264
        //       MF_MT_AVG_BITRATE        = settings.BitrateBps
        //       MF_MT_FRAME_SIZE         = (width, height)
        //       MF_MT_FRAME_RATE         = (frameRate, 1)
        //       MF_MT_INTERLACE_MODE     = MFVideoInterlace_Progressive
        //       MF_MT_MPEG2_PROFILE      = eAVEncH264VProfile_Main
        //
        // 5. Eingabetyp: MFVideoFormat_NV12, gleiche Groesse und Rate.
        //    Die Erfassung liefert BGRA - die Konvertierung nach NV12 uebernimmt
        //    ein Video-Processor-MFT davor, ebenfalls auf der GPU.
        //
        // 6. Die Low-Latency-Eigenschaften ueber ICodecAPI setzen:
        //       CODECAPI_AVLowLatencyMode        = true
        //       CODECAPI_AVEncCommonRateControlMode = eAVEncCommonRateControlMode_CBR
        //       CODECAPI_AVEncMPVDefaultBPictureCount = 0        // keine B-Frames
        //       CODECAPI_AVEncMPVGOPSize         = 0             // kein period. Keyframe
        //       CODECAPI_AVEncCommonQualityVsSpeed = 33          // Tempo vor Qualitaet
        //    Diese sechs Werte sind der Unterschied zwischen ~15 ms und ~120 ms
        //    Encoder-Latenz. Siehe docs/02-tech-stack.md §2.1.
        //
        // 7. Bei Hardware-MFTs den asynchronen Modus aktivieren:
        //       MF_TRANSFORM_ASYNC_UNLOCK = true
        //    und ueber IMFMediaEventGenerator auf METransformNeedInput /
        //    METransformHaveOutput reagieren, statt ProcessInput/ProcessOutput
        //    synchron zu pumpen.

        _initialized = true;
        _log.LogInformation(
            "Encoder initialisiert: {W}x{H} @{Fps} fps, {Bitrate} kbps, B-Frames={B}, LowLatency={L}",
            settings.Width, settings.Height, settings.FrameRate,
            settings.BitrateBps / 1000, settings.AllowBFrames, settings.LowLatencyMode);
    }

    public void EncodeFrame(IDirect3DSurface surface, TimeSpan timestamp)
    {
        if (!_initialized) return;

        // PLATZHALTER: Der eigentliche Kodiervorgang.
        //
        //   1. IDirect3DSurface -> ID3D11Texture2D
        //      (ueber IDirect3DDxgiInterfaceAccess.GetInterface)
        //   2. BGRA -> NV12 per Video-Processor-MFT (bleibt auf der GPU)
        //   3. MFCreateVideoSampleFromSurface(texture) -> IMFSample
        //   4. sample.SetSampleTime(timestamp.Ticks)
        //      sample.SetSampleDuration(10_000_000 / frameRate)
        //   5. Wenn _keyframeRequested: MFSampleExtension_CleanPoint setzen
        //      und _keyframeRequested zuruecksetzen
        //   6. transform.ProcessInput(0, sample, 0)
        //   7. transform.ProcessOutput(...) -> IMFSample mit Annex-B-Daten
        //   8. FrameEncoded?.Invoke(new EncodedFrame(...))
        //
        // Der Aufrufer (SessionController) ruft anschliessend
        // CaptureEngine.ReleaseFrame() auf und gibt damit die Backpressure frei.

        if (_keyframeRequested)
        {
            _keyframeRequested = false;
            _log.LogDebug("Keyframe erzwungen bei {Timestamp}", timestamp);
        }
    }

    public void RequestKeyframe()
    {
        _keyframeRequested = true;
        _log.LogDebug("Keyframe angefordert (RTCP-PLI oder Viewer-Anfrage)");
    }

    public void SetBitrate(int bitrateBps)
    {
        if (bitrateBps == _settings.BitrateBps) return;
        _settings = _settings with { BitrateBps = bitrateBps };

        // PLATZHALTER: Zur Laufzeit ueber ICodecAPI setzen -
        //   codecApi.SetValue(CODECAPI_AVEncCommonMeanBitRate, bitrateBps)
        // Ein Neuaufbau des MFT ist dafuer NICHT noetig und waere schaedlich:
        // Er wuerde einen Keyframe erzwingen, und WebRTC passt die Bitrate
        // mehrmals pro Sekunde an.

        _log.LogDebug("Bitrate auf {Kbps} kbps gesetzt", bitrateBps / 1000);
    }

    public void Reconfigure(int width, int height, int frameRate)
    {
        if (width == _settings.Width && height == _settings.Height && frameRate == _settings.FrameRate)
            return;

        _log.LogInformation("Encoder neu konfiguriert: {W}x{H} @{Fps}", width, height, frameRate);
        _settings = _settings with { Width = width, Height = height, FrameRate = frameRate };

        // Aufloesungswechsel erfordert im Gegensatz zur Bitrate tatsaechlich einen
        // Neuaufbau des MFT. Deshalb entprellt der Aufrufer die viewport-Nachrichten
        // (siehe SessionController) - sonst wuerde jedes Ziehen am Fensterrand
        // Dutzende Neuaufbauten ausloesen.
        Initialize(_settings);
    }

    public void Dispose()
    {
        // PLATZHALTER: transform.ProcessMessage(MFT_MESSAGE_NOTIFY_END_OF_STREAM),
        // Ressourcen freigeben, MFShutdown().
        _initialized = false;
    }
}
