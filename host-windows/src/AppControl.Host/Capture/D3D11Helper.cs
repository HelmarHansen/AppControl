using System.Runtime.InteropServices;
using Windows.Graphics.DirectX.Direct3D11;

namespace AppControl.Host.Capture;

/// <summary>
/// Erzeugt das IDirect3DDevice, das WinRT-Capture und Media Foundation gemeinsam
/// nutzen.
///
/// DASS ES DASSELBE DEVICE IST, IST DER GANZE PUNKT: Capture-Oberflaechen und
/// Encoder-Eingaben muessen auf demselben Geraet liegen, sonst braucht es eine
/// Kopie ueber den Systemspeicher - und die Zero-Copy-Architektur waere hinfaellig.
/// </summary>
public static class D3D11Helper
{
    /// <summary>
    /// Erzeugt ein Hardware-Device mit den beiden Flags, die AppControl braucht:
    ///   BgraSupport  - WGC liefert B8G8R8A8UIntNormalized
    ///   VideoSupport - Voraussetzung fuer die Media-Foundation-Video-Pipeline
    /// </summary>
    public static IDirect3DDevice CreateDevice()
    {
        // PLATZHALTER - vollstaendige Umsetzung mit Vortice.Direct3D11:
        //
        //   var flags = DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport;
        //   #if DEBUG
        //       flags |= DeviceCreationFlags.Debug;   // erfordert die Grafiktools von Windows
        //   #endif
        //
        //   var result = D3D11.D3D11CreateDevice(
        //       adapter: null, DriverType.Hardware, flags,
        //       [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0],
        //       out ID3D11Device device);
        //
        //   if (result.Failure)
        //   {
        //       // Fallback auf WARP (Software-Rasterizer). Deutlich langsamer,
        //       // aber lauffaehig in VMs und auf Systemen ohne brauchbaren Treiber.
        //       D3D11.D3D11CreateDevice(null, DriverType.Warp, flags, ..., out device);
        //   }
        //
        //   // Multithread-Schutz aktivieren: Capture liefert Frames auf einem
        //   // Threadpool-Thread, der Encoder laeuft auf einem eigenen. Ohne
        //   // SetMultithreadProtected sind gleichzeitige Zugriffe undefiniert.
        //   using var multithread = device.QueryInterface<ID3D11Multithread>();
        //   multithread.SetMultithreadProtected(true);
        //
        //   var dxgiDevice = device.QueryInterface<IDXGIDevice>();
        //   CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out var inspectable);
        //   return MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);

        throw new NotImplementedException(
            "D3D11Helper.CreateDevice(): siehe Implementierungsleitfaden im Kommentar. " +
            "Erfordert Vortice.Direct3D11 (bereits als PackageReference eingebunden).");
    }

    /// <summary>
    /// Bruecke von einem DXGI-Device zu einem WinRT-IDirect3DDevice.
    /// Wird von CreateDevice benoetigt.
    /// </summary>
    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice",
               SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern uint CreateDirect3D11DeviceFromDXGIDevice(
        nint dxgiDevice, out nint graphicsDevice);

    /// <summary>
    /// Gegenrichtung: aus einer WinRT-Oberflaeche die native D3D-Textur holen.
    /// Wird im Encoder gebraucht (EncodeFrame, Schritt 1).
    /// </summary>
    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDirect3DDxgiInterfaceAccess
    {
        nint GetInterface([In] ref Guid iid);
    }
}
