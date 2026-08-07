using System.Runtime.InteropServices;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace AppControl.Host.Capture;

/// <summary>
/// Erzeugt das IDirect3DDevice, das WinRT-Capture und Media Foundation gemeinsam
/// nutzen.
///
/// DASS ES DASSELBE DEVICE IST, IST DER GANZE PUNKT: Capture-Oberflaechen und
/// Encoder-Eingaben muessen auf demselben Geraet liegen, sonst braucht es eine
/// Kopie ueber den Systemspeicher - und die Zero-Copy-Architektur waere hinfaellig.
///
/// UMSETZUNG MIT REINEM P/INVOKE statt ueber eine Wrapper-Bibliothek: Die
/// nativen Signaturen von D3D11CreateDevice und CreateDirect3D11DeviceFromDXGIDevice
/// sind stabil und gut dokumentiert; QueryInterface erledigt Marshal. Das spart
/// eine Abhaengigkeit an der Stelle, an der sie am wenigsten bringt - es sind
/// drei Aufrufe, keine Bibliothek.
/// </summary>
public static class D3D11Helper
{
    // ── Konstanten aus d3d11.h / d3dcommon.h ─────────────────────────────────

    private const uint D3D11_SDK_VERSION = 7;

    private enum DriverType : uint { Hardware = 1, Warp = 5 }

    [Flags]
    private enum CreationFlags : uint
    {
        None = 0,
        Debug = 0x0002,
        /// <summary>Pflicht: WGC liefert B8G8R8A8UIntNormalized.</summary>
        BgraSupport = 0x0020,
        /// <summary>Pflicht fuer die Media-Foundation-Video-Pipeline.</summary>
        VideoSupport = 0x0800,
    }

    private enum FeatureLevel : uint { Level_11_0 = 0xB000, Level_11_1 = 0xB100 }

    private static readonly Guid IID_IDXGIDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    private static readonly Guid IID_ID3D11Multithread = new("9B7E4E00-342C-4106-A19F-4F2704F689F0");

    // ── Oeffentliche API ─────────────────────────────────────────────────────

    /// <summary>
    /// Erzeugt ein Hardware-Device mit BGRA- und Video-Unterstuetzung und faellt
    /// bei Bedarf auf den Software-Rasterizer WARP zurueck.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Wenn weder Hardware noch WARP ein Device liefern - dann ist der Rechner
    /// fuer diese Anwendung ungeeignet, und eine klare Meldung beim Start ist
    /// besser als ein Absturz mitten in der Sitzung.
    /// </exception>
    public static IDirect3DDevice CreateDevice()
    {
        var flags = CreationFlags.BgraSupport | CreationFlags.VideoSupport;
#if DEBUG
        // Erfordert die installierten Grafiktools von Windows. Fehlen sie,
        // schlaegt die Erzeugung fehl - deshalb weiter unten der Retry ohne Debug.
        flags |= CreationFlags.Debug;
#endif

        var devicePtr = TryCreate(DriverType.Hardware, flags);

#if DEBUG
        if (devicePtr == nint.Zero)
        {
            // Zweiter Versuch ohne Debug-Layer: Auf Entwicklungsrechnern ohne
            // installierte Grafiktools waere die Anwendung sonst unbenutzbar.
            devicePtr = TryCreate(DriverType.Hardware, flags & ~CreationFlags.Debug);
        }
#endif

        if (devicePtr == nint.Zero)
        {
            // WARP ist der Software-Rasterizer. Deutlich langsamer, aber lauffaehig
            // in VMs und auf Systemen ohne brauchbaren Grafiktreiber. Fuer einen
            // ersten Test besser als gar nichts.
            devicePtr = TryCreate(DriverType.Warp, CreationFlags.BgraSupport | CreationFlags.VideoSupport);
        }

        if (devicePtr == nint.Zero)
        {
            throw new InvalidOperationException(
                "Es konnte kein Direct3D-11-Gerät erstellt werden - weder über die Grafikkarte " +
                "noch über den Software-Rasterizer WARP. AppControl kann auf diesem System " +
                "nicht laufen. Prüfe den Grafiktreiber.");
        }

        try
        {
            EnableMultithreadProtection(devicePtr);
            return WrapAsWinRtDevice(devicePtr);
        }
        finally
        {
            // Die WinRT-Huelle haelt jetzt ihre eigene Referenz; unsere geben wir frei.
            Marshal.Release(devicePtr);
        }
    }

    // ── Intern ───────────────────────────────────────────────────────────────

    private static nint TryCreate(DriverType driverType, CreationFlags flags)
    {
        // 11_1 zuerst, 11_0 als Rueckfall. Aeltere Level brauchen wir nicht:
        // Windows.Graphics.Capture setzt ohnehin Windows 10 1803 voraus, und
        // jede Hardware dieser Generation kann mindestens 11_0.
        var featureLevels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 };

        var hr = D3D11CreateDevice(
            pAdapter: nint.Zero,
            DriverType: driverType,
            Software: nint.Zero,
            Flags: (uint)flags,
            pFeatureLevels: featureLevels,
            FeatureLevels: (uint)featureLevels.Length,
            SDKVersion: D3D11_SDK_VERSION,
            ppDevice: out var device,
            pFeatureLevel: out _,
            ppImmediateContext: out var context);

        // Den Immediate-Context brauchen wir hier nicht: Die Capture-Pipeline
        // laeuft ueber WinRT, der Encoder holt sich seinen eigenen. Nicht
        // freizugeben waere ein Leck, das erst nach Stunden auffaellt.
        if (context != nint.Zero) Marshal.Release(context);

        if (hr < 0)
        {
            if (device != nint.Zero) { Marshal.Release(device); }
            return nint.Zero;
        }

        return device;
    }

    /// <summary>
    /// Aktiviert den Multithread-Schutz des Geraets.
    ///
    /// NOTWENDIG, WEIL: Capture liefert Frames auf einem Threadpool-Thread
    /// (Direct3D11CaptureFramePool.CreateFreeThreaded), waehrend der Encoder auf
    /// einem eigenen Thread laeuft. Ohne SetMultithreadProtected sind gleichzeitige
    /// Zugriffe auf dasselbe Device undefiniert - und aeussern sich als sporadische
    /// Abstuerze unter Last, also genau dann, wenn die Fehlersuche am schwersten ist.
    /// </summary>
    private static void EnableMultithreadProtection(nint devicePtr)
    {
        var iid = IID_ID3D11Multithread;
        if (Marshal.QueryInterface(devicePtr, ref iid, out var multithreadPtr) < 0) return;

        try
        {
            var multithread = (ID3D11Multithread)Marshal.GetObjectForIUnknown(multithreadPtr);
            multithread.SetMultithreadProtected(true);
        }
        finally
        {
            Marshal.Release(multithreadPtr);
        }
    }

    /// <summary>Bruecke vom nativen ID3D11Device zum WinRT-IDirect3DDevice.</summary>
    private static IDirect3DDevice WrapAsWinRtDevice(nint devicePtr)
    {
        var iid = IID_IDXGIDevice;
        if (Marshal.QueryInterface(devicePtr, ref iid, out var dxgiDevicePtr) < 0)
        {
            throw new InvalidOperationException(
                "Das Direct3D-Gerät stellt kein IDXGIDevice bereit. " +
                "Das sollte nicht vorkommen - vermutlich ein defekter Grafiktreiber.");
        }

        nint inspectablePtr = nint.Zero;
        try
        {
            var hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevicePtr, out inspectablePtr);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);

            return MarshalInspectable<IDirect3DDevice>.FromAbi(inspectablePtr);
        }
        finally
        {
            if (inspectablePtr != nint.Zero) Marshal.Release(inspectablePtr);
            Marshal.Release(dxgiDevicePtr);
        }
    }

    // ── Native Deklarationen ─────────────────────────────────────────────────

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(
        nint pAdapter,
        DriverType DriverType,
        nint Software,
        uint Flags,
        [MarshalAs(UnmanagedType.LPArray)] FeatureLevel[] pFeatureLevels,
        uint FeatureLevels,
        uint SDKVersion,
        out nint ppDevice,
        out FeatureLevel pFeatureLevel,
        out nint ppImmediateContext);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(
        nint dxgiDevice, out nint graphicsDevice);

    [ComImport]
    [Guid("9B7E4E00-342C-4106-A19F-4F2704F689F0")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ID3D11Multithread
    {
        void Enter();
        void Leave();
        [PreserveSig] bool SetMultithreadProtected([MarshalAs(UnmanagedType.Bool)] bool bMTProtect);
        [PreserveSig] bool GetMultithreadProtected();
    }

    /// <summary>
    /// Holt die native ID3D11Texture2D aus einer WinRT-Capture-Oberflaeche.
    /// Wird vom Encoder gebraucht, um das Frame ohne Kopie weiterzureichen.
    /// </summary>
    public static nint GetNativeSurface(IDirect3DSurface surface)
    {
        var access = surface.As<IDirect3DDxgiInterfaceAccess>();
        var iid = IID_ID3D11Texture2D;
        return access.GetInterface(ref iid);
    }

    private static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    /// <summary>
    /// Holt das native ID3D11Device aus der WinRT-Huelle.
    ///
    /// Der Encoder braucht es fuer zwei Dinge: den DXGI-Device-Manager, ueber den
    /// Media Foundation auf derselben GPU arbeitet, und das Anlegen von
    /// NV12-Texturen. Der Aufrufer MUSS den Zeiger per Marshal.Release freigeben.
    /// </summary>
    public static nint GetNativeDevice(IDirect3DDevice device)
    {
        var access = device.As<IDirect3DDxgiInterfaceAccess>();
        var iid = IID_ID3D11Device;
        return access.GetInterface(ref iid);
    }

    private static readonly Guid IID_ID3D11Device = new("db6f6ddb-ac77-4e88-8253-819df9bbf140");

    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDirect3DDxgiInterfaceAccess
    {
        nint GetInterface([In] ref Guid iid);
    }
}
