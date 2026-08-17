using System.Runtime.InteropServices;
using Windows.Graphics.DirectX.Direct3D11;
using Microsoft.Extensions.Logging;
using AppControl.Host.Capture;
using static AppControl.Host.Media.MfInterop;

namespace AppControl.Host.Media;

/// <summary>
/// H.264-Encoder ueber Media Foundation.
///
/// Media Foundation waehlt automatisch den Hardware-Encoder der vorhandenen GPU
/// (Intel QuickSync, NVIDIA NVENC, AMD VCE) und faellt sonst auf den
/// Software-Encoder zurueck. Wir muessen keinen Herstellercode einbinden.
///
/// ── DIE PIPELINE ─────────────────────────────────────────────────────────────
///
///   WGC-Oberflaeche (BGRA, auf der GPU)
///     -> Video-Processor-MFT   : BGRA -> NV12, bleibt auf der GPU
///     -> H.264-Encoder-MFT     : NV12 -> Annex-B-NALs
///     -> FrameEncoded          : an PeerConnectionManager.SendVideo
///
/// DER ENTSCHEIDENDE PUNKT ist der DXGI-Device-Manager: Ohne ihn akzeptieren die
/// MFTs nur Systemspeicher-Puffer, und jedes Frame muesste von der GPU herunter-
/// und wieder hinaufkopiert werden. Mit ihm bleibt die Textur auf der GPU - der
/// gesamte Weg von der Erfassung bis zum NAL-Puffer ist kopierfrei, bis auf die
/// eine unvermeidliche Kopie des fertigen Bitstreams in den verwalteten Speicher.
///
/// ── SYNCHRON ODER ASYNCHRON ──────────────────────────────────────────────────
///
/// Hardware-Encoder sind asynchrone MFTs: Man schiebt nicht Bild fuer Bild
/// hinein, sondern wartet auf METransformNeedInput und METransformHaveOutput.
/// Software-Encoder sind synchron. Diese Klasse beherrscht beides, weil
/// D3D11Helper im Notfall auf den Software-Rasterizer WARP zurueckfaellt - dann
/// waere ein Encoder-Pfad, der Hardware voraussetzt, genau der falsche.
///
/// ── STAND ────────────────────────────────────────────────────────────────────
///
/// Diese Klasse ist vollstaendig ausgefuehrt, aber NICHT auf echter Hardware
/// erprobt: Ein CI-Runner hat weder GPU noch Bildschirm zum Erfassen, er kann
/// den Code nur uebersetzen. Erste Inbetriebnahme siehe docs/06-build-and-run.md,
/// Testfall O1.
/// </summary>
public sealed class MediaFoundationH264Encoder : IVideoEncoder
{
    private readonly ILogger<MediaFoundationH264Encoder> _log;
    private readonly IDirect3DDevice _device;

    /// <summary>Schuetzt Aufbau, Abbau und den Video-Processor.</summary>
    private readonly object _gate = new();

    /// <summary>Schuetzt ProcessInput und die Warteschlange davor.</summary>
    private readonly object _pumpGate = new();

    private EncoderSettings _settings = new();
    private bool _initialized;

    private bool _mfStarted;
    private IMFDXGIDeviceManager? _deviceManager;
    private IMFTransform? _processor;
    private IMFTransform? _encoder;
    private ICodecAPI? _codecApi;
    private IMFMediaEventGenerator? _encoderEvents;
    private ID3D11DevicePartial? _d3dDevice;
    private nint _d3dDevicePtr;

    private bool _encoderIsAsync;
    private bool _processorProvidesSamples;
    private bool _encoderProvidesSamples;
    private uint _encoderOutputSize = 1 << 20;

    /// <summary>
    /// SPS und PPS aus dem Ausgabetyp. Rueckfall fuer Encoder, die die
    /// Parametersaetze nicht von sich aus vor jeden IDR-Frame legen - ein Viewer,
    /// der spaeter dazukommt, koennte den Stream sonst nie dekodieren.
    /// </summary>
    private byte[]? _sequenceHeader;

    /// <summary>
    /// Offene NeedInput-Zusagen des asynchronen MFT. Eine Zusage zu verbrauchen,
    /// ohne ein Bild zu liefern, legt die Pipeline still - deshalb wird gezaehlt
    /// statt blockierend auf ein Bild gewartet.
    /// </summary>
    private int _needInputTokens;

    private readonly Queue<IMFSample> _queue = new();

    /// <summary>
    /// Mehr als drei Bilder zu puffern hiesse, Latenz aufzubauen statt Bilder zu
    /// verwerfen. Bei einer Fernsteuerung ist ein verworfenes Bild unsichtbar,
    /// eine wachsende Verzoegerung dagegen sofort spuerbar.
    /// </summary>
    private const int MaxQueuedFrames = 3;

    private Thread? _eventThread;
    private volatile bool _running;

    private volatile bool _keyframeRequested;

    public event Action<EncodedFrame>? FrameEncoded;

    public EncoderSettings CurrentSettings => _settings;

    public MediaFoundationH264Encoder(IDirect3DDevice device, ILogger<MediaFoundationH264Encoder> log)
    {
        _device = device;
        _log = log;
    }

    // ── Aufbau ───────────────────────────────────────────────────────────────

    public void Initialize(EncoderSettings settings)
    {
        lock (_gate)
        {
            ShutdownPipeline();
            _settings = settings;

            if (!_mfStarted)
            {
                Check(MFStartup(MF_VERSION, MFSTARTUP_NOSOCKET), "MFStartup");
                _mfStarted = true;
            }

            CreateDeviceManager();
            CreateProcessor();
            CreateEncoder();

            _initialized = true;
            _running = true;

            if (_encoderIsAsync)
            {
                // Hintergrund-Thread: Blockiert er beim Herunterfahren in
                // GetEvent, darf er den Prozess nicht am Leben halten.
                _eventThread = new Thread(EventLoop)
                {
                    IsBackground = true,
                    Name = "AppControl-Encoder",
                    Priority = ThreadPriority.AboveNormal,
                };
                _eventThread.Start();
            }

            _log.LogInformation(
                "Encoder initialisiert: {W}x{H} @{Fps} fps, {Bitrate} kbps, {Mode}, B-Frames={B}",
                settings.Width, settings.Height, settings.FrameRate,
                settings.BitrateBps / 1000,
                _encoderIsAsync ? "asynchron (Hardware)" : "synchron (Software)",
                settings.AllowBFrames);
        }
    }

    private void CreateDeviceManager()
    {
        _d3dDevicePtr = D3D11Helper.GetNativeDevice(_device);
        var deviceObject = Marshal.GetObjectForIUnknown(_d3dDevicePtr);
        _d3dDevice = (ID3D11DevicePartial)deviceObject;

        Check(MFCreateDXGIDeviceManager(out var resetToken, out var manager), "MFCreateDXGIDeviceManager");
        Check(manager.ResetDevice(deviceObject, resetToken), "IMFDXGIDeviceManager.ResetDevice");
        _deviceManager = manager;
    }

    /// <summary>
    /// Der Video-Processor-MFT wandelt BGRA nach NV12 - das Eingabeformat
    /// praktisch jedes Hardware-Encoders.
    ///
    /// Warum nicht die Erfassung gleich NV12 liefern lassen? Weil
    /// Windows.Graphics.Capture ausschliesslich B8G8R8A8UIntNormalized und
    /// R16G16B16A16Float anbietet. Die Wandlung ist unvermeidlich; sie gehoert
    /// nur auf die GPU statt auf die CPU.
    /// </summary>
    private void CreateProcessor()
    {
        var type = Type.GetTypeFromCLSID(CLSID_VideoProcessorMFT)
                   ?? throw new InvalidOperationException(
                       "Der Video-Processor-MFT ist auf diesem System nicht registriert.");

        var processor = Activator.CreateInstance(type) as IMFTransform
                        ?? throw new InvalidOperationException(
                            "Der Video-Processor-MFT liess sich nicht erzeugen.");

        SetDeviceManager(processor, "Video-Processor");

        // Beim Processor zuerst der EINGABE-, dann der Ausgabetyp. Beim Encoder
        // ist es genau andersherum (siehe CreateEncoder) - Media Foundation ist
        // an dieser Stelle inkonsistent, und die falsche Reihenfolge liefert
        // MF_E_TRANSFORM_TYPE_NOT_SET statt einer verstaendlichen Meldung.
        using (var input = CreateVideoType(MFVideoFormat_ARGB32))
            Check(processor.SetInputType(0, input.Type, 0), "Processor.SetInputType(BGRA)");

        using (var output = CreateVideoType(MFVideoFormat_NV12))
            Check(processor.SetOutputType(0, output.Type, 0), "Processor.SetOutputType(NV12)");

        Check(processor.GetOutputStreamInfo(0, out var info), "Processor.GetOutputStreamInfo");
        _processorProvidesSamples = (info.dwFlags & MFT_OUTPUT_STREAM_PROVIDES_SAMPLES) != 0;

        processor.ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, nint.Zero);
        processor.ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM, nint.Zero);

        _processor = processor;
    }

    private void CreateEncoder()
    {
        var encoder = ActivateEncoder();

        // MF_TRANSFORM_ASYNC_UNLOCK MUSS vor allem anderen gesetzt werden.
        // Danach verhaelt sich der MFT asynchron; davor lehnt er die meisten
        // Aufrufe mit MF_E_TRANSFORM_ASYNC_LOCKED ab.
        if (encoder.GetAttributes(out var attributes) >= 0)
        {
            var asyncKey = MF_TRANSFORM_ASYNC;
            if (attributes.GetUINT32(ref asyncKey, out var isAsync) >= 0 && isAsync != 0)
            {
                var unlockKey = MF_TRANSFORM_ASYNC_UNLOCK;
                Check(attributes.SetUINT32(ref unlockKey, 1), "MF_TRANSFORM_ASYNC_UNLOCK");
                _encoderIsAsync = true;
            }

            var awareKey = MF_SA_D3D11_AWARE;
            if (attributes.GetUINT32(ref awareKey, out var d3dAware) >= 0 && d3dAware != 0)
            {
                SetDeviceManager(encoder, "Encoder");
            }
            else
            {
                _log.LogInformation(
                    "Der Encoder ist nicht D3D11-faehig - die NV12-Bilder werden ueber den " +
                    "Systemspeicher gereicht. Das kostet Leistung, funktioniert aber.");
            }

            Marshal.ReleaseComObject(attributes);
        }

        // Beim Encoder zuerst der AUSGABE-, dann der Eingabetyp: Ohne bekannten
        // Ausgabetyp weiss der Encoder nicht, welche Eingaben er akzeptieren kann.
        using (var output = CreateVideoType(MFVideoFormat_H264, forOutput: true))
            Check(encoder.SetOutputType(0, output.Type, 0), "Encoder.SetOutputType(H264)");

        using (var input = CreateVideoType(MFVideoFormat_NV12))
            Check(encoder.SetInputType(0, input.Type, 0), "Encoder.SetInputType(NV12)");

        Check(encoder.GetOutputStreamInfo(0, out var info), "Encoder.GetOutputStreamInfo");
        _encoderProvidesSamples = (info.dwFlags & MFT_OUTPUT_STREAM_PROVIDES_SAMPLES) != 0;
        if (info.cbSize > 0) _encoderOutputSize = info.cbSize;

        _codecApi = encoder as ICodecAPI;
        ApplyLowLatencySettings();

        _encoder = encoder;
        ReadSequenceHeader();

        if (_encoderIsAsync)
        {
            _encoderEvents = encoder as IMFMediaEventGenerator;
            if (_encoderEvents is null)
            {
                // Ein asynchroner MFT ohne Ereignisquelle waere ein Widerspruch;
                // dann lieber synchron pumpen als gar nicht kodieren.
                _log.LogWarning("Asynchroner Encoder ohne IMFMediaEventGenerator - schalte auf synchron um.");
                _encoderIsAsync = false;
            }
        }

        encoder.ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, nint.Zero);
        encoder.ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM, nint.Zero);
    }

    /// <summary>
    /// Sucht den Encoder-MFT: erst Hardware, dann alles andere.
    ///
    /// MFT_ENUM_FLAG_SORTANDFILTER sortiert nach Eignung und filtert
    /// Registrierungen heraus, die der Nutzer abgewaehlt hat - der erste Treffer
    /// ist damit der bevorzugte Encoder.
    /// </summary>
    private IMFTransform ActivateEncoder()
    {
        var found = TryEnumerate(MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG_SORTANDFILTER)
                    ?? TryEnumerate(MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG_ASYNCMFT | MFT_ENUM_FLAG_SORTANDFILTER)
                    ?? throw new InvalidOperationException(
                        "Auf diesem System ist kein H.264-Encoder registriert. Ohne Encoder kann " +
                        "AppControl kein Bild uebertragen.");

        return found;
    }

    private IMFTransform? TryEnumerate(uint flags)
    {
        var input = new MftRegisterTypeInfo { guidMajorType = MFMediaType_Video, guidSubtype = MFVideoFormat_NV12 };
        var output = new MftRegisterTypeInfo { guidMajorType = MFMediaType_Video, guidSubtype = MFVideoFormat_H264 };

        var hr = MFTEnumEx(MFT_CATEGORY_VIDEO_ENCODER, flags, ref input, ref output,
                           out var arrayPtr, out var count);
        if (hr < 0 || count == 0 || arrayPtr == nint.Zero)
        {
            if (arrayPtr != nint.Zero) Marshal.FreeCoTaskMem(arrayPtr);
            return null;
        }

        IMFTransform? result = null;
        try
        {
            for (uint i = 0; i < count; i++)
            {
                var activatePtr = Marshal.ReadIntPtr(arrayPtr, (int)i * nint.Size);
                if (activatePtr == nint.Zero) continue;

                try
                {
                    // Nur der erste Treffer wird aktiviert; die uebrigen Eintraege
                    // muessen trotzdem freigegeben werden, sonst leckt jede
                    // Neukonfiguration ein paar COM-Objekte.
                    if (result is null &&
                        Marshal.GetObjectForIUnknown(activatePtr) is IMFActivate activate)
                    {
                        var iid = IID_IMFTransform;
                        if (activate.ActivateObject(ref iid, out var transform) >= 0)
                        {
                            result = transform as IMFTransform;
                        }
                        Marshal.ReleaseComObject(activate);
                    }
                }
                finally
                {
                    Marshal.Release(activatePtr);
                }
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(arrayPtr);
        }

        return result;
    }

    private void SetDeviceManager(IMFTransform transform, string what)
    {
        if (_deviceManager is null) return;

        var managerPtr = Marshal.GetIUnknownForObject(_deviceManager);
        try
        {
            var hr = transform.ProcessMessage(MFT_MESSAGE_SET_D3D_MANAGER, managerPtr);
            if (hr < 0)
            {
                // E_NOTIMPL heisst schlicht "kann kein D3D" - kein Fehler, nur
                // langsamer. Alles andere ist einer.
                if (hr == E_NOTIMPL)
                    _log.LogInformation("{What} arbeitet ohne D3D-Device-Manager.", what);
                else
                    Check(hr, $"{what}: MFT_MESSAGE_SET_D3D_MANAGER");
            }
        }
        finally
        {
            Marshal.Release(managerPtr);
        }
    }

    /// <summary>
    /// Diese sechs Werte sind der Unterschied zwischen ~15 ms und ~120 ms
    /// Encoder-Latenz. Siehe docs/02-tech-stack.md §2.1.
    ///
    /// Jeder Wert einzeln und fehlertolerant: Nicht jeder Treiber kennt jeden
    /// Regler, und ein Encoder ohne QualityVsSpeed ist immer noch ein Encoder.
    /// Die Sitzung daran scheitern zu lassen waere unverhaeltnismaessig.
    /// </summary>
    private void ApplyLowLatencySettings()
    {
        if (_codecApi is null)
        {
            _log.LogWarning("Der Encoder stellt keine ICodecAPI bereit - Latenzeinstellungen entfallen.");
            return;
        }

        TrySetCodecValue(CODECAPI_AVLowLatencyMode, true, "LowLatencyMode");
        TrySetCodecValue(CODECAPI_AVEncCommonRateControlMode, eAVEncCommonRateControlMode_CBR, "RateControlMode=CBR");
        TrySetCodecValue(CODECAPI_AVEncCommonMeanBitRate, (uint)_settings.BitrateBps, "MeanBitRate");

        // Keine B-Frames: Sie referenzieren KUENFTIGE Bilder, der Encoder muesste
        // also mindestens eines zurueckhalten. Das ist bei Interaktion direkt
        // spuerbare Zusatzlatenz.
        TrySetCodecValue(CODECAPI_AVEncMPVDefaultBPictureCount,
                         _settings.AllowBFrames ? 1u : 0u, "BPictureCount");

        // GOP-Groesse 0 = kein periodischer Keyframe. Der Viewer fordert einen
        // per RTCP-PLI an, wenn er ihn braucht - siehe EncoderSettings.
        TrySetCodecValue(CODECAPI_AVEncMPVGOPSize,
                         (uint)Math.Max(0, _settings.KeyframeIntervalFrames), "GOPSize");

        // 0 = beste Qualitaet, 100 = hoechstes Tempo. 33 haelt die Kodierzeit
        // niedrig, ohne das Bild sichtbar zu beschaedigen.
        if (_settings.LowLatencyMode)
            TrySetCodecValue(CODECAPI_AVEncCommonQualityVsSpeed, 33u, "QualityVsSpeed");
    }

    private void TrySetCodecValue(Guid key, object value, string what)
    {
        if (_codecApi is null) return;
        try
        {
            var boxed = value;
            var hr = _codecApi.SetValue(ref key, ref boxed);
            if (hr < 0)
                _log.LogDebug("Encoder-Einstellung {What} nicht unterstuetzt (0x{Hr:X8}).", what, hr);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Encoder-Einstellung {What} nicht unterstuetzt.", what);
        }
    }

    /// <summary>Liest SPS/PPS aus dem ausgehandelten Ausgabetyp, falls vorhanden.</summary>
    private void ReadSequenceHeader()
    {
        _sequenceHeader = null;
        if (_encoder is null) return;

        if (_encoder.GetOutputCurrentType(0, out var type) < 0) return;
        try
        {
            var key = MF_MT_MPEG_SEQUENCE_HEADER;
            if (type.GetBlobSize(ref key, out var size) < 0 || size == 0) return;

            var blob = new byte[size];
            if (type.GetBlob(ref key, blob, size, out _) < 0) return;

            _sequenceHeader = blob;
            _log.LogDebug("Parametersaetze aus dem Ausgabetyp gelesen: {Bytes} Byte.", size);
        }
        finally
        {
            Marshal.ReleaseComObject(type);
        }
    }

    /// <summary>Erzeugt einen Videomedientyp mit unseren Groessen- und Ratenangaben.</summary>
    private MediaTypeHandle CreateVideoType(Guid subtype, bool forOutput = false)
    {
        Check(MFCreateMediaType(out var type), "MFCreateMediaType");

        var majorKey = MF_MT_MAJOR_TYPE;
        var majorValue = MFMediaType_Video;
        Check(type.SetGUID(ref majorKey, ref majorValue), "MF_MT_MAJOR_TYPE");

        var subtypeKey = MF_MT_SUBTYPE;
        Check(type.SetGUID(ref subtypeKey, ref subtype), "MF_MT_SUBTYPE");

        var sizeKey = MF_MT_FRAME_SIZE;
        Check(type.SetUINT64(ref sizeKey, PackRatio((uint)_settings.Width, (uint)_settings.Height)),
              "MF_MT_FRAME_SIZE");

        var rateKey = MF_MT_FRAME_RATE;
        Check(type.SetUINT64(ref rateKey, PackRatio((uint)_settings.FrameRate, 1)), "MF_MT_FRAME_RATE");

        var aspectKey = MF_MT_PIXEL_ASPECT_RATIO;
        Check(type.SetUINT64(ref aspectKey, PackRatio(1, 1)), "MF_MT_PIXEL_ASPECT_RATIO");

        var interlaceKey = MF_MT_INTERLACE_MODE;
        Check(type.SetUINT32(ref interlaceKey, MFVideoInterlace_Progressive), "MF_MT_INTERLACE_MODE");

        if (forOutput)
        {
            var bitrateKey = MF_MT_AVG_BITRATE;
            Check(type.SetUINT32(ref bitrateKey, (uint)_settings.BitrateBps), "MF_MT_AVG_BITRATE");

            var profileKey = MF_MT_MPEG2_PROFILE;
            Check(type.SetUINT32(ref profileKey, eAVEncH264VProfile_Main), "MF_MT_MPEG2_PROFILE");
        }

        return new MediaTypeHandle(type);
    }

    /// <summary>
    /// Gibt den Medientyp nach dem Setzen wieder frei. Ein Medientyp ist nach
    /// SetInputType/SetOutputType vom MFT kopiert - unsere Referenz noch zu
    /// halten waere ein Leck pro Neukonfiguration, und neu konfiguriert wird bei
    /// jeder Groessenaenderung des Viewer-Fensters.
    /// </summary>
    private readonly struct MediaTypeHandle(IMFMediaType type) : IDisposable
    {
        public IMFMediaType Type { get; } = type;
        public void Dispose() => Marshal.ReleaseComObject(Type);
    }

    // ── Der Bildpfad ─────────────────────────────────────────────────────────

    public void EncodeFrame(IDirect3DSurface surface, TimeSpan timestamp)
    {
        if (!_initialized || !_running) return;

        IMFSample? nv12;
        lock (_gate)
        {
            if (!_running || _processor is null) return;
            nv12 = ConvertToNv12(surface, timestamp);
        }
        if (nv12 is null) return;

        if (_encoderIsAsync)
        {
            lock (_pumpGate)
            {
                if (_queue.Count >= MaxQueuedFrames)
                {
                    // Das AELTESTE verwerfen: Es ist bereits veraltet, und der
                    // Nutzer will den aktuellen Bildschirm sehen, nicht den von
                    // vor drei Bildern.
                    Marshal.ReleaseComObject(_queue.Dequeue());
                }
                _queue.Enqueue(nv12);
            }
            DispatchPending();
        }
        else
        {
            lock (_pumpGate)
            {
                ApplyPendingKeyframe();
                var hr = _encoder!.ProcessInput(0, nv12, 0);
                Marshal.ReleaseComObject(nv12);
                if (hr < 0)
                {
                    _log.LogWarning("ProcessInput fehlgeschlagen (0x{Hr:X8}).", hr);
                    return;
                }
                while (ProcessOutputOnce()) { }
            }
        }
    }

    /// <summary>BGRA-Capture-Oberflaeche in ein NV12-Sample wandeln - auf der GPU.</summary>
    private IMFSample? ConvertToNv12(IDirect3DSurface surface, TimeSpan timestamp)
    {
        var texturePtr = D3D11Helper.GetNativeSurface(surface);
        if (texturePtr == nint.Zero) return null;

        // Die drei Referenzen werden im finally freigegeben. Sie liegen bewusst
        // ausserhalb des try: Was dort erzeugt wurde, muss auch dann freigegeben
        // werden, wenn eine der spaeteren Zeilen wirft.
        object? textureTracked = null;
        IMFMediaBuffer? bufferTracked = null;
        IMFSample? sampleTracked = null;

        try
        {
            var textureObject = Marshal.GetObjectForIUnknown(texturePtr);
            textureTracked = textureObject;

            var iid = IID_ID3D11Texture2D;
            Check(MFCreateDXGISurfaceBuffer(ref iid, textureObject, 0, false, out var inputBuffer),
                  "MFCreateDXGISurfaceBuffer");
            bufferTracked = inputBuffer;
            SetContiguousLength(inputBuffer);

            Check(MFCreateSample(out var inputSample), "MFCreateSample");
            sampleTracked = inputSample;
            Check(inputSample.AddBuffer(inputBuffer), "IMFSample.AddBuffer");
            inputSample.SetSampleTime(timestamp.Ticks);
            inputSample.SetSampleDuration(FrameDurationTicks);

            var hr = _processor!.ProcessInput(0, inputSample, 0);
            if (hr < 0)
            {
                _log.LogWarning("Farbwandlung: ProcessInput fehlgeschlagen (0x{Hr:X8}).", hr);
                return null;
            }

            return ReceiveProcessorOutput(timestamp);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Farbwandlung fehlgeschlagen - Bild wird verworfen.");
            return null;
        }
        finally
        {
            if (sampleTracked is not null) Marshal.ReleaseComObject(sampleTracked);
            if (bufferTracked is not null) Marshal.ReleaseComObject(bufferTracked);
            if (textureTracked is not null) Marshal.ReleaseComObject(textureTracked);
            Marshal.Release(texturePtr);
        }
    }

    private IMFSample? ReceiveProcessorOutput(TimeSpan timestamp)
    {
        IMFSample? allocated = _processorProvidesSamples ? null : AllocateNv12Sample();

        var buffer = new MftOutputDataBuffer { dwStreamID = 0 };
        if (allocated is not null) buffer.pSample = Marshal.GetIUnknownForObject(allocated);

        var hr = _processor!.ProcessOutput(0, 1, ref buffer, out _);
        if (buffer.pEvents != nint.Zero) Marshal.Release(buffer.pEvents);

        if (hr < 0)
        {
            if (buffer.pSample != nint.Zero) Marshal.Release(buffer.pSample);
            if (allocated is not null) Marshal.ReleaseComObject(allocated);
            if (hr != MF_E_TRANSFORM_NEED_MORE_INPUT)
                _log.LogWarning("Farbwandlung: ProcessOutput fehlgeschlagen (0x{Hr:X8}).", hr);
            return null;
        }

        IMFSample? result = allocated;
        if (result is null && buffer.pSample != nint.Zero)
            result = Marshal.GetObjectForIUnknown(buffer.pSample) as IMFSample;

        if (buffer.pSample != nint.Zero) Marshal.Release(buffer.pSample);

        result?.SetSampleTime(timestamp.Ticks);
        result?.SetSampleDuration(FrameDurationTicks);
        return result;
    }

    /// <summary>
    /// Rueckfall, falls der Video-Processor keine eigenen Samples stellt.
    /// Auf D3D11-faehigen Systemen tut er das; deshalb ist dieser Weg selten
    /// und darf eine Textur pro Bild anlegen.
    /// </summary>
    private IMFSample? AllocateNv12Sample()
    {
        if (_d3dDevice is null) return null;

        var desc = new D3D11Texture2DDesc
        {
            Width = (uint)_settings.Width,
            Height = (uint)_settings.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = DXGI_FORMAT_NV12,
            SampleDesc = new DxgiSampleDesc { Count = 1, Quality = 0 },
            Usage = D3D11_USAGE_DEFAULT,
            BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE,
            CPUAccessFlags = 0,
            MiscFlags = 0,
        };

        if (_d3dDevice.CreateTexture2D(ref desc, nint.Zero, out var texture) < 0) return null;

        try
        {
            var iid = IID_ID3D11Texture2D;
            if (MFCreateDXGISurfaceBuffer(ref iid, texture, 0, false, out var buffer) < 0) return null;

            try
            {
                SetContiguousLength(buffer);
                if (MFCreateSample(out var sample) < 0) return null;
                sample.AddBuffer(buffer);
                return sample;
            }
            finally
            {
                Marshal.ReleaseComObject(buffer);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(texture);
        }
    }

    /// <summary>
    /// Ein DXGI-Puffer meldet Laenge 0, bis man sie setzt - und ein MFT, der 0
    /// Byte sieht, verwirft das Bild kommentarlos. IMF2DBuffer kennt die
    /// tatsaechliche Groesse der Oberflaeche.
    /// </summary>
    private static void SetContiguousLength(IMFMediaBuffer buffer)
    {
        if (buffer is IMF2DBuffer twoD && twoD.GetContiguousLength(out var length) >= 0)
            buffer.SetCurrentLength(length);
    }

    private long FrameDurationTicks => TimeSpan.TicksPerSecond / Math.Max(1, _settings.FrameRate);

    // ── Asynchroner Pfad ─────────────────────────────────────────────────────

    private void EventLoop()
    {
        var events = _encoderEvents;
        if (events is null) return;

        while (_running)
        {
            var hr = events.GetEvent(0, out var mediaEvent);
            if (hr < 0)
            {
                if (hr != MF_E_SHUTDOWN && _running)
                    _log.LogWarning("Encoder-Ereignisschleife endet (0x{Hr:X8}).", hr);
                return;
            }

            mediaEvent.GetTypeInfo(out var eventType);
            Marshal.ReleaseComObject(mediaEvent);

            try
            {
                switch (eventType)
                {
                    case METransformNeedInput:
                        lock (_pumpGate) { _needInputTokens++; }
                        DispatchPending();
                        break;

                    case METransformHaveOutput:
                        lock (_pumpGate) { ProcessOutputOnce(); }
                        break;

                    case METransformDrainComplete:
                        return;
                }
            }
            catch (Exception ex)
            {
                // Ein einzelnes fehlgeschlagenes Bild darf die Schleife nicht
                // beenden - sonst steht der Stream still, waehrend die Sitzung
                // fuer beide Seiten weiterlaeuft und "verbunden" anzeigt.
                _log.LogWarning(ex, "Fehler in der Encoder-Ereignisschleife.");
            }
        }
    }

    private void DispatchPending()
    {
        lock (_pumpGate)
        {
            while (_needInputTokens > 0 && _queue.Count > 0 && _encoder is not null)
            {
                var sample = _queue.Dequeue();
                _needInputTokens--;

                ApplyPendingKeyframe();
                var hr = _encoder.ProcessInput(0, sample, 0);
                Marshal.ReleaseComObject(sample);

                if (hr < 0) _log.LogWarning("Encoder.ProcessInput fehlgeschlagen (0x{Hr:X8}).", hr);
            }
        }
    }

    /// <summary>Holt ein fertiges Paket ab. Rueckgabe: ob ein Paket kam.</summary>
    private bool ProcessOutputOnce()
    {
        if (_encoder is null) return false;

        IMFSample? allocated = null;
        if (!_encoderProvidesSamples)
        {
            if (MFCreateMemoryBuffer(_encoderOutputSize, out var memory) < 0) return false;
            if (MFCreateSample(out allocated) < 0) { Marshal.ReleaseComObject(memory); return false; }
            allocated.AddBuffer(memory);
            Marshal.ReleaseComObject(memory);
        }

        var buffer = new MftOutputDataBuffer { dwStreamID = 0 };
        if (allocated is not null) buffer.pSample = Marshal.GetIUnknownForObject(allocated);

        var hr = _encoder.ProcessOutput(0, 1, ref buffer, out _);
        if (buffer.pEvents != nint.Zero) Marshal.Release(buffer.pEvents);

        try
        {
            if (hr == MF_E_TRANSFORM_NEED_MORE_INPUT) return false;

            if (hr == MF_E_TRANSFORM_STREAM_CHANGE)
            {
                // Der Encoder hat den Ausgabetyp neu ausgehandelt - typisch nach
                // einer Aufloesungsaenderung. Neu setzen und die Parametersaetze
                // erneut lesen, sonst passt der Stream nicht mehr zum Bild.
                using var output = CreateVideoType(MFVideoFormat_H264, forOutput: true);
                _encoder.SetOutputType(0, output.Type, 0);
                ReadSequenceHeader();
                return false;
            }

            if (hr < 0)
            {
                _log.LogWarning("Encoder.ProcessOutput fehlgeschlagen (0x{Hr:X8}).", hr);
                return false;
            }

            var sample = allocated;
            if (sample is null && buffer.pSample != nint.Zero)
                sample = Marshal.GetObjectForIUnknown(buffer.pSample) as IMFSample;

            if (sample is null) return false;

            try
            {
                Emit(sample);
            }
            finally
            {
                if (!ReferenceEquals(sample, allocated)) Marshal.ReleaseComObject(sample);
            }
            return true;
        }
        finally
        {
            if (buffer.pSample != nint.Zero) Marshal.Release(buffer.pSample);
            if (allocated is not null) Marshal.ReleaseComObject(allocated);
        }
    }

    private void Emit(IMFSample sample)
    {
        if (sample.ConvertToContiguousBuffer(out var buffer) < 0) return;

        byte[]? data = null;
        try
        {
            if (buffer.Lock(out var pointer, out _, out var length) < 0) return;
            try
            {
                if (length == 0) return;
                data = new byte[length];
                Marshal.Copy(pointer, data, 0, (int)length);
            }
            finally
            {
                buffer.Unlock();
            }
        }
        finally
        {
            Marshal.ReleaseComObject(buffer);
        }

        if (data is null) return;

        sample.GetSampleTime(out var ticks);

        var isKeyframe = ContainsIdr(data);
        if (isKeyframe && _sequenceHeader is not null && !ContainsParameterSet(data))
        {
            // Ein Viewer, der mitten im Stream dazukommt, braucht SPS und PPS vor
            // dem ersten IDR - sonst kann er kein einziges Bild dekodieren.
            var combined = new byte[_sequenceHeader.Length + data.Length];
            _sequenceHeader.CopyTo(combined, 0);
            data.CopyTo(combined, _sequenceHeader.Length);
            data = combined;
        }

        FrameEncoded?.Invoke(new EncodedFrame(
            data,
            isKeyframe,
            TimeSpan.FromTicks(ticks),
            TimeSpan.FromTicks(FrameDurationTicks)));
    }

    // ── Annex-B-Auswertung ───────────────────────────────────────────────────
    //
    // Der Bitstream besteht aus NAL-Units, getrennt durch Startcodes
    // (00 00 01 oder 00 00 00 01). Die unteren fuenf Bit des ersten Bytes nach
    // dem Startcode sind der Typ: 5 = IDR, 7 = SPS, 8 = PPS.

    public static bool ContainsIdr(ReadOnlySpan<byte> data) => ContainsNalType(data, 5);

    public static bool ContainsParameterSet(ReadOnlySpan<byte> data) => ContainsNalType(data, 7);

    private static bool ContainsNalType(ReadOnlySpan<byte> data, byte nalType)
    {
        for (var i = 0; i + 3 < data.Length; i++)
        {
            if (data[i] != 0x00 || data[i + 1] != 0x00) continue;

            int headerIndex;
            if (data[i + 2] == 0x01) headerIndex = i + 3;
            else if (data[i + 2] == 0x00 && data[i + 3] == 0x01) headerIndex = i + 4;
            else continue;

            if (headerIndex >= data.Length) break;
            if ((data[headerIndex] & 0x1F) == nalType) return true;
        }
        return false;
    }

    // ── Steuerung zur Laufzeit ───────────────────────────────────────────────

    public void RequestKeyframe()
    {
        // Nur vormerken. Angewendet wird der Wunsch unmittelbar vor dem naechsten
        // Bild, das tatsaechlich in den Encoder geht (siehe ApplyPendingKeyframe):
        // Ein ForceKeyFrame, das Sekunden vor dem naechsten Bild gesetzt wird,
        // beachten manche Treiber nicht mehr.
        _keyframeRequested = true;
        _log.LogDebug("Keyframe angefordert (RTCP-PLI oder Viewer-Anfrage)");
    }

    /// <summary>
    /// Setzt einen vorgemerkten Keyframe-Wunsch um. Der dokumentierte Weg fuer
    /// Media-Foundation-Encoder; das Attribut MFSampleExtension_CleanPoint am
    /// Eingabesample waere die Alternative, wird aber laengst nicht von jedem
    /// Hardware-Encoder beachtet.
    /// </summary>
    private void ApplyPendingKeyframe()
    {
        if (!_keyframeRequested) return;
        _keyframeRequested = false;
        TrySetCodecValue(CODECAPI_AVEncVideoForceKeyFrame, 1u, "ForceKeyFrame");
    }

    public void SetBitrate(int bitrateBps)
    {
        if (bitrateBps == _settings.BitrateBps) return;
        _settings = _settings with { BitrateBps = bitrateBps };

        // Zur Laufzeit ueber ICodecAPI. Ein Neuaufbau des MFT waere hier
        // schaedlich: Er wuerde einen Keyframe erzwingen, und WebRTC passt die
        // Bitrate mehrmals pro Sekunde an.
        TrySetCodecValue(CODECAPI_AVEncCommonMeanBitRate, (uint)bitrateBps, "MeanBitRate");
        _log.LogDebug("Bitrate auf {Kbps} kbps gesetzt", bitrateBps / 1000);
    }

    public void Reconfigure(int width, int height, int frameRate)
    {
        if (width == _settings.Width && height == _settings.Height && frameRate == _settings.FrameRate)
            return;

        _log.LogInformation("Encoder neu konfiguriert: {W}x{H} @{Fps}", width, height, frameRate);

        // Aufloesungswechsel erfordert im Gegensatz zur Bitrate tatsaechlich einen
        // Neuaufbau. Deshalb entprellt der Aufrufer die viewport-Nachrichten
        // (siehe SessionController) - sonst wuerde jedes Ziehen am Fensterrand
        // Dutzende Neuaufbauten ausloesen.
        Initialize(_settings with { Width = width, Height = height, FrameRate = frameRate });
    }

    // ── Abbau ────────────────────────────────────────────────────────────────

    public void Dispose()
    {
        lock (_gate)
        {
            ShutdownPipeline();

            if (_mfStarted)
            {
                MFShutdown();
                _mfStarted = false;
            }
        }
    }

    private void ShutdownPipeline()
    {
        _running = false;
        _initialized = false;

        _encoder?.ProcessMessage(MFT_MESSAGE_NOTIFY_END_OF_STREAM, nint.Zero);
        _encoder?.ProcessMessage(MFT_MESSAGE_NOTIFY_END_STREAMING, nint.Zero);
        _processor?.ProcessMessage(MFT_MESSAGE_NOTIFY_END_STREAMING, nint.Zero);

        if (_eventThread is not null)
        {
            // Der Thread haengt moeglicherweise in GetEvent. Er ist ein
            // Hintergrund-Thread und blockiert das Programmende nicht; zwei
            // Sekunden zu warten ist die Hoeflichkeit, nicht die Notwendigkeit.
            if (!_eventThread.Join(TimeSpan.FromSeconds(2)))
                _log.LogDebug("Encoder-Thread reagiert nicht - wird als Hintergrund-Thread beendet.");
            _eventThread = null;
        }

        lock (_pumpGate)
        {
            while (_queue.Count > 0) Marshal.ReleaseComObject(_queue.Dequeue());
            _needInputTokens = 0;
        }

        _encoderEvents = null;
        _codecApi = null;

        ReleaseCom(ref _encoder);
        ReleaseCom(ref _processor);
        ReleaseCom(ref _deviceManager);
        ReleaseCom(ref _d3dDevice);

        if (_d3dDevicePtr != nint.Zero)
        {
            Marshal.Release(_d3dDevicePtr);
            _d3dDevicePtr = nint.Zero;
        }

        _sequenceHeader = null;
        _encoderIsAsync = false;
        _keyframeRequested = false;
    }

    private static void ReleaseCom<T>(ref T? reference) where T : class
    {
        if (reference is null) return;
        if (Marshal.IsComObject(reference)) Marshal.ReleaseComObject(reference);
        reference = null;
    }
}
