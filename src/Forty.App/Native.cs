using System.Runtime.InteropServices;

namespace Forty.App;

/// <summary>The Win32 calls the overlay needs, and nothing else.</summary>
internal static class Native
{
    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_TOPMOST = 0x00000008, WS_EX_TOOLWINDOW = 0x00000080, WS_EX_APPWINDOW = 0x00040000, WS_EX_NOACTIVATE = 0x08000000;
    public const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010, SWP_FRAMECHANGED = 0x0020;
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWA_BORDER_COLOR = 34;
    public const uint WM_HOTKEY = 0x0312, WM_DISPLAYCHANGE = 0x007E, WM_DPICHANGED = 0x02E0, WM_SETTINGCHANGE = 0x001A;
    public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_NOREPEAT = 0x4000;
    public const uint VK_SPACE = 0x20;

    [StructLayout(LayoutKind.Sequential)] public struct MARGINS { public int Left, Right, Top, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct DispatcherQueueOptions { public int dwSize; public int threadType; public int apartmentType; }

    public delegate IntPtr SubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr refData);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern int SetWindowRgn(IntPtr hWnd, IntPtr region, bool redraw);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hWnd, int attribute, ref int value, int size);
    [DllImport("dwmapi.dll")] public static extern int DwmExtendFrameIntoClientArea(IntPtr hWnd, ref MARGINS margins);
    [DllImport("comctl32.dll")] public static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc proc, UIntPtr id, UIntPtr refData);
    [DllImport("comctl32.dll")] public static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc proc, UIntPtr id);
    [DllImport("comctl32.dll")] public static extern IntPtr DefSubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("CoreMessaging.dll")] public static extern int CreateDispatcherQueueController(DispatcherQueueOptions options, out IntPtr controller);

    public const uint WM_THEMECHANGED = 0x031A, WM_SYSCOLORCHANGE = 0x0015;
    const uint SPI_GETHIGHCONTRAST = 0x0042, HCF_HIGHCONTRASTON = 0x1;
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct HIGHCONTRAST { public uint cbSize; public uint dwFlags; public IntPtr lpszDefaultScheme; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool SystemParametersInfoW(uint action, uint param, ref HIGHCONTRAST value, uint winIni);

    /// <summary>Whether a Windows contrast theme is on. Read the classic way: the WinRT change event needs a UWP window.</summary>
    public static bool HighContrastOn()
    {
        var hc = new HIGHCONTRAST { cbSize = (uint)Marshal.SizeOf<HIGHCONTRAST>() };
        return SystemParametersInfoW(SPI_GETHIGHCONTRAST, hc.cbSize, ref hc, 0) && (hc.dwFlags & HCF_HIGHCONTRASTON) != 0;
    }

    public const uint WDA_NONE = 0x0, WDA_EXCLUDEFROMCAPTURE = 0x11;
    [DllImport("user32.dll")] public static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);
}
