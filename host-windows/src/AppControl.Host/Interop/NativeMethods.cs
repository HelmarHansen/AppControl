using System.Runtime.InteropServices;
using System.Windows;

namespace AppControl.Host.Interop;

/// <summary>
/// Win32-P/Invoke-Deklarationen. Bewusst an EINER Stelle gesammelt statt ueber die
/// Module verstreut: Interop-Fehler (falsche Struct-Groesse, fehlendes SetLastError,
/// falsches Charset) sind schwer zu finden und leichter zu vermeiden, wenn alle
/// Deklarationen nebeneinander stehen.
/// </summary>
internal static partial class NativeMethods
{
    // ── SendInput ────────────────────────────────────────────────────────────

    public const uint INPUT_MOUSE = 0;
    public const uint INPUT_KEYBOARD = 1;

    public const uint MOUSEEVENTF_MOVE = 0x0001;
    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP = 0x0004;
    public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    public const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    public const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    public const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    public const uint MOUSEEVENTF_XDOWN = 0x0080;
    public const uint MOUSEEVENTF_XUP = 0x0100;
    public const uint MOUSEEVENTF_WHEEL = 0x0800;
    public const uint MOUSEEVENTF_HWHEEL = 0x1000;
    public const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    public const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;

    public const uint XBUTTON1 = 0x0001;
    public const uint XBUTTON2 = 0x0002;
    public const int WHEEL_DELTA = 120;

    public const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const uint KEYEVENTF_UNICODE = 0x0004;
    public const uint KEYEVENTF_SCANCODE = 0x0008;

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial uint SendInput(uint nInputs,
        [MarshalAs(UnmanagedType.LPArray)] INPUT[] pInputs, int cbSize);

    // ── Bildschirm-Metriken ──────────────────────────────────────────────────

    public const int SM_XVIRTUALSCREEN = 76;
    public const int SM_YVIRTUALSCREEN = 77;
    public const int SM_CXVIRTUALSCREEN = 78;
    public const int SM_CYVIRTUALSCREEN = 79;

    [LibraryImport("user32.dll")]
    public static partial int GetSystemMetrics(int nIndex);

    // ── Fenster ──────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [LibraryImport("user32.dll")]
    public static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetClientRect(nint hWnd, out RECT lpRect);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(nint hWnd, out RECT lpRect);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ClientToScreen(nint hWnd, ref POINT lpPoint);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(nint hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsIconic(nint hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int GetWindowTextW(nint hWnd, [Out] char[] lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int GetWindowTextLengthW(nint hWnd);

    public delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial int GetWindowLongW(nint hWnd, int nIndex);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(nint hWnd, nint hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WS_EX_APPWINDOW = 0x00040000;
    public const int WS_VISIBLE = 0x10000000;

    public static readonly nint HWND_TOPMOST = -1;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;

    /// <summary>
    /// Cloaked-Fenster sind unsichtbare UWP-Huellen; sie erscheinen sonst als
    /// Geistereintraege im App-Picker.
    /// </summary>
    public const int DWMWA_CLOAKED = 14;

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmGetWindowAttribute(nint hwnd, int dwAttribute,
        out int pvAttribute, int cbAttribute);

    // ── Hotkey und Hook ──────────────────────────────────────────────────────

    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_NOREPEAT = 0x4000;
    public const uint VK_X = 0x58;
    public const uint VK_RCONTROL = 0xA3;
    public const int WM_HOTKEY = 0x0312;
    public const int WM_KEYDOWN = 0x0100;
    public const int WM_SYSKEYDOWN = 0x0104;
    public const int WH_KEYBOARD_LL = 13;

    /// <summary>Kennzeichnet synthetische Events. Siehe EmergencyHotkey.</summary>
    public const uint LLKHF_INJECTED = 0x00000010;

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public nint dwExtraInfo;
    }

    public delegate nint LowLevelKeyboardProc(int nCode, nint wParam, nint lParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterHotKey(nint hWnd, int id);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern nint SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn,
        nint hMod, uint dwThreadId);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnhookWindowsHookEx(nint hhk);

    [LibraryImport("user32.dll")]
    public static partial nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint GetModuleHandle(string? lpModuleName);

    // ── Hilfsfunktionen ──────────────────────────────────────────────────────

    /// <summary>
    /// Client-Rechteck eines Fensters in Bildschirmkoordinaten.
    ///
    /// Client-Rect statt Fenster-Rect, weil Rahmen und Titelleiste nicht zum von
    /// WGC erfassten Inhalt gehoeren: Ein Klick auf das Schliessen-X waere ein
    /// Klick ausserhalb dessen, was der Viewer sieht. Siehe ShareScope.SingleWindow.
    /// </summary>
    public static bool TryGetClientRectOnScreen(nint hwnd, out Int32Rect rect)
    {
        rect = Int32Rect.Empty;
        if (hwnd == nint.Zero || !GetClientRect(hwnd, out var client)) return false;

        var origin = new POINT { X = 0, Y = 0 };
        if (!ClientToScreen(hwnd, ref origin)) return false;

        var width = client.Right - client.Left;
        var height = client.Bottom - client.Top;
        if (width <= 0 || height <= 0) return false;

        rect = new Int32Rect(origin.X, origin.Y, width, height);
        return true;
    }

    public static string GetWindowTitle(nint hwnd)
    {
        var length = GetWindowTextLengthW(hwnd);
        if (length <= 0) return string.Empty;
        var buffer = new char[length + 1];
        var written = GetWindowTextW(hwnd, buffer, buffer.Length);
        return written > 0 ? new string(buffer, 0, written) : string.Empty;
    }

    public static bool IsCloaked(nint hwnd)
        => DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0;

    // ── Mauszeiger ───────────────────────────────────────────────────────────

    public const int CURSOR_SHOWING = 0x00000001;

    // Ressourcen-IDs der Systemzeiger aus WinUser.h.
    public const int IDC_ARROW = 32512;
    public const int IDC_IBEAM = 32513;
    public const int IDC_WAIT = 32514;
    public const int IDC_SIZEWE = 32644;
    public const int IDC_SIZENS = 32645;
    public const int IDC_HAND = 32649;
    public const int IDC_APPSTARTING = 32650;

    [StructLayout(LayoutKind.Sequential)]
    public struct CURSORINFO
    {
        public uint cbSize;
        public uint flags;
        public nint hCursor;
        public POINT ptScreenPos;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorInfo(ref CURSORINFO pci);

    /// <summary>
    /// Mit hInstance = 0 und einer Ressourcen-ID liefert die Funktion den
    /// gemeinsam genutzten Systemzeiger. Genau deshalb laesst sich das Ergebnis
    /// direkt mit CURSORINFO.hCursor vergleichen - es ist dasselbe Handle.
    /// </summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial nint LoadCursorW(nint hInstance, nint lpCursorName);

    // ── GDI: Vorschaubilder fuer den App-Picker ──────────────────────────────
    //
    // NUR FUER DIE VORSCHAU IM PICKER, nicht fuer den Stream. Der laeuft ueber
    // Windows.Graphics.Capture und bleibt auf der GPU (siehe CaptureEngine).
    // Die Begruendung fuer die zwei verschiedenen Wege steht in
    // Capture/WindowThumbnailProvider.cs.

    /// <summary>
    /// Laesst PrintWindow den vollstaendigen, per DWM zusammengesetzten Inhalt
    /// zeichnen statt nur das, was die Anwendung selbst per WM_PRINT liefert.
    /// Ohne dieses Flag bleiben Fenster mit Hardwarebeschleunigung schwarz.
    /// </summary>
    public const uint PW_RENDERFULLCONTENT = 0x00000002;

    public const uint DIB_RGB_COLORS = 0;
    public const uint BI_RGB = 0;
    public const uint SRCCOPY = 0x00CC0020;

    /// <summary>Halbton-Skalierung: langsamer als der Standard, aber ohne Treppenstufen.</summary>
    public const int HALFTONE = 4;

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        /// <summary>Negativ = von oben nach unten. Genau die Reihenfolge, die WriteableBitmap erwartet.</summary>
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    /// <summary>
    /// BITMAPINFO ist im SDK ein Header plus eine Farbtabelle variabler Laenge.
    /// Bei BI_RGB mit 32 Bit liest GDI die Tabelle nicht - die drei Felder
    /// stehen trotzdem da, damit die Struktur nicht kleiner ist als das, was
    /// die API im ungueltigen Fall anfassen koennte.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColorReserved0;
        public uint bmiColorReserved1;
        public uint bmiColorReserved2;
    }

    [LibraryImport("user32.dll")]
    public static partial nint GetDC(nint hWnd);

    [LibraryImport("user32.dll")]
    public static partial int ReleaseDC(nint hWnd, nint hDC);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PrintWindow(nint hwnd, nint hdcBlt, uint nFlags);

    [LibraryImport("gdi32.dll")]
    public static partial nint CreateCompatibleDC(nint hdc);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteDC(nint hdc);

    [LibraryImport("gdi32.dll")]
    public static partial nint SelectObject(nint hdc, nint h);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteObject(nint ho);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    public static partial nint CreateDIBSection(
        nint hdc, ref BITMAPINFO pbmi, uint usage, out nint ppvBits, nint hSection, uint offset);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool StretchBlt(
        nint hdcDest, int xDest, int yDest, int wDest, int hDest,
        nint hdcSrc, int xSrc, int ySrc, int wSrc, int hSrc, uint rop);

    [LibraryImport("gdi32.dll")]
    public static partial int SetStretchBltMode(nint hdc, int mode);
}
