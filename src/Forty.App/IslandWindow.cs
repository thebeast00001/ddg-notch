using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace Forty.App;

/// <summary>A system backdrop that paints nothing, so the window is see-through wherever the island draws nothing.</summary>
sealed partial class ClearBackdrop : SystemBackdrop
{
    static Windows.UI.Composition.Compositor? s_systemCompositor;
    static IntPtr s_queueController;

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(target, xamlRoot);
        if (s_systemCompositor == null)
        {
            if (Windows.System.DispatcherQueue.GetForCurrentThread() == null)
            {
                var options = new Native.DispatcherQueueOptions { dwSize = Marshal.SizeOf<Native.DispatcherQueueOptions>(), threadType = 2, apartmentType = 2 };
                Native.CreateDispatcherQueueController(options, out s_queueController);
            }
            s_systemCompositor = new Windows.UI.Composition.Compositor();
        }
        target.SystemBackdrop = s_systemCompositor.CreateColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
    {
        base.OnTargetDisconnected(target);
        target.SystemBackdrop = null;
    }
}

/// <summary>
/// The overlay window (PLAN.md 7.1, proven in spike S1): transparent, always on top, never activated
/// by the pointer, absent from Alt-Tab and the taskbar. It has one fixed size; what changes is its
/// window region, kept to the island's bounds so everything beside the island belongs to the app
/// underneath.
/// </summary>
sealed class IslandWindow : IDisposable
{
    /// <summary>Rows at the top of the window that sit above the monitor and are never shown.
    /// Windows draws a one-pixel frame line along the top of a frame-extended window otherwise.</summary>
    public const int TopInsetPx = 2;
    const int HotkeyId = 0x4F01;

    static readonly IntPtr HwndTopmost = new(-1), HwndNotTopmost = new(-2);
    const uint Quiet = Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE;

    readonly Native.SubclassProc _subclass;   // kept alive for as long as the subclass exists
    bool _hotkeyRegistered;

    public Window Window { get; }
    public Grid Root { get; }
    public Canvas BodyHost { get; }
    public Grid Overlay { get; }
    public IntPtr Hwnd { get; }
    public IntPtr MonitorHandle { get; private set; }
    public ulong DisplayIdValue { get; private set; }
    public double Scale { get; private set; }
    public RectInt32 MonitorPx { get; private set; }
    public RectInt32 WindowPx { get; private set; }
    readonly double _widthDip, _heightDip;
    public double WidthDip => WindowPx.Width / Scale;
    public double HeightDip => WindowPx.Height / Scale;
    public double TopInsetDip => TopInsetPx / Scale;
    public Compositor Compositor { get; }

    /// <summary>The global shortcut was pressed (F6).</summary>
    public event Action? Hotkey;

    /// <summary>A display was added, removed or rescaled (F7).</summary>
    public event Action? DisplaysChanged;

    /// <summary>A Windows setting changed: contrast theme, colours, theme (F11).</summary>
    public event Action? SettingsChanged;

    public IslandWindow(double widthDip, double heightDip)
    {
        // Two layers: the body is drawn by the compositor on BodyHost; text and buttons are XAML on Overlay.
        Root = new Grid { Background = new SolidColorBrush(Colors.Transparent), RequestedTheme = ElementTheme.Dark };
        BodyHost = new Canvas();
        Overlay = new Grid();
        Root.Children.Add(BodyHost);
        Root.Children.Add(Overlay);
        Window = new Window { SystemBackdrop = new ClearBackdrop(), Content = Root, Title = "Forty" };
        Hwnd = WinRT.Interop.WindowNative.GetWindowHandle(Window);
        Compositor = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(Root).Compositor;

        Window.AppWindow.SetPresenter(OverlappedPresenter.CreateForContextMenu());   // no title bar, no border
        Window.AppWindow.IsShownInSwitchers = false;

        long ex = Native.GetWindowLongPtr(Hwnd, Native.GWL_EXSTYLE).ToInt64();
        ex = (ex | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW) & ~Native.WS_EX_APPWINDOW;
        Native.SetWindowLongPtr(Hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));

        // See-through needs the clear backdrop and the frame extended over the whole client area.
        var sheet = new Native.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        Native.DwmExtendFrameIntoClientArea(Hwnd, ref sheet);
        int doNotRound = 1;
        Native.DwmSetWindowAttribute(Hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref doNotRound, sizeof(int));
        int noBorder = unchecked((int)0xFFFFFFFE);
        Native.DwmSetWindowAttribute(Hwnd, Native.DWMWA_BORDER_COLOR, ref noBorder, sizeof(int));

        _widthDip = widthDip;
        _heightDip = heightDip;
        Place(DisplayArea.Primary);
        Native.SetWindowPos(Hwnd, IntPtr.Zero, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED);

        _subclass = WindowMessage;
        Native.SetWindowSubclass(Hwnd, _subclass, (UIntPtr)1, UIntPtr.Zero);

        // Always-on-top is asked for last, after every style is in place, and then read back.
        EnsureTopmost();
    }

    /// <summary>
    /// Puts the island at the top centre of a display, at that display's scaling, below a taskbar
    /// docked at the top (F7). Called at start, when displays change, and when it follows you (F8).
    /// </summary>
    public void Place(DisplayArea area, int demoOffsetPx = 0)
    {
        MonitorPx = area.OuterBounds;
        DisplayIdValue = area.DisplayId.Value;
        MonitorHandle = Win32Interop.GetMonitorFromDisplayId(area.DisplayId);
        Native.GetDpiForMonitor(MonitorHandle, 0, out uint dpi, out _);
        Scale = dpi / 96.0;
        int top = Forty.Core.DisplayRules.TopEdge(MonitorPx.Y, area.WorkArea.Y);

        int w = (int)Math.Ceiling(_widthDip * Scale), h = (int)Math.Ceiling(_heightDip * Scale) + TopInsetPx;
        if ((MonitorPx.Width - w) % 2 != 0) w++;   // keep the centre on a whole pixel
        WindowPx = new RectInt32(MonitorPx.X + (MonitorPx.Width - w) / 2, top - TopInsetPx + demoOffsetPx, w, h);
        Window.AppWindow.MoveAndResize(WindowPx);
    }

    /// <summary>The display a window is on, or null.</summary>
    public static DisplayArea? DisplayOf(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return null;
        IntPtr monitor = Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONULL);
        if (monitor == IntPtr.Zero) return null;
        try { return DisplayArea.GetFromDisplayId(Win32Interop.GetDisplayIdFromMonitor(monitor)); }
        catch (ArgumentException) { return null; }
    }

    /// <summary>
    /// Registers the global shortcut (default Ctrl+Alt+Space). Returns false if another app holds it,
    /// so the settings page can say so and offer another.
    /// </summary>
    public bool RegisterHotkey(uint modifiers = Native.MOD_CONTROL | Native.MOD_ALT, uint key = Native.VK_SPACE)
    {
        if (_hotkeyRegistered) Native.UnregisterHotKey(Hwnd, HotkeyId);
        _hotkeyRegistered = Native.RegisterHotKey(Hwnd, HotkeyId, modifiers | Native.MOD_NOREPEAT, key);
        return _hotkeyRegistered;
    }

    IntPtr WindowMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr refData)
    {
        if (msg == Native.WM_HOTKEY && wParam.ToInt32() == HotkeyId) Hotkey?.Invoke();
        else if (msg is Native.WM_DISPLAYCHANGE or Native.WM_DPICHANGED) DisplaysChanged?.Invoke();
        else if (msg == Native.WM_SETTINGCHANGE && wParam.ToInt64() == Native.SPI_SETWORKAREA) DisplaysChanged?.Invoke();   // a taskbar moved
        else if (msg is Native.WM_SETTINGCHANGE or Native.WM_THEMECHANGED or Native.WM_SYSCOLORCHANGE) SettingsChanged?.Invoke();
        return Native.DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    public bool IsTopmost => (Native.GetWindowLongPtr(Hwnd, Native.GWL_EXSTYLE).ToInt64() & Native.WS_EX_TOPMOST) != 0;

    /// <summary>
    /// Makes sure the window really is always on top. In a few starts in a hundred Windows accepts the
    /// request, reports success and leaves the flag off; asking for "not always on top" first clears
    /// that state (spike S1). So: ask, read the flag back, and if it is off, clear and ask again.
    /// </summary>
    public bool EnsureTopmost()
    {
        for (int attempt = 0; attempt < 4 && !IsTopmost; attempt++)
        {
            Native.SetWindowPos(Hwnd, HwndTopmost, 0, 0, 0, 0, Quiet);
            if (IsTopmost) break;
            Native.SetWindowPos(Hwnd, HwndNotTopmost, 0, 0, 0, 0, Quiet);
            Native.SetWindowPos(Hwnd, HwndTopmost, 0, 0, 0, 0, Quiet);
        }
        return IsTopmost;
    }

    public void Show()
    {
        Window.AppWindow.Show(false);   // false: do not activate
        EnsureTopmost();
    }

    public void Hide() => Window.AppWindow.Hide();

    /// <summary>
    /// Keeps the island out of screen sharing and recordings while you still see it (F134). Uses
    /// Windows' exclude-from-capture display affinity.
    /// </summary>
    public bool SetExcludedFromCapture(bool exclude) =>
        Native.SetWindowDisplayAffinity(Hwnd, exclude ? Native.WDA_EXCLUDEFROMCAPTURE : Native.WDA_NONE);
    public bool IsVisible => Native.IsWindowVisible(Hwnd);

    /// <summary>
    /// Takes keyboard focus (the shortcut opened the panel), remembering who had it so it can be
    /// handed back when the panel closes.
    /// </summary>
    public IntPtr TakeFocus()
    {
        IntPtr previous = Native.GetForegroundWindow();
        Window.Activate();
        Native.SetForegroundWindow(Hwnd);
        return previous;
    }

    public static void GiveFocusBack(IntPtr previous)
    {
        if (previous != IntPtr.Zero) Native.SetForegroundWindow(previous);
    }

    /// <summary>Limits the window to a rectangle hanging from the top centre. Outside it the window neither draws nor takes the pointer.</summary>
    public void SetRegionDip(double widthDip, double heightDip)
    {
        int w = Math.Min(WindowPx.Width, (int)Math.Ceiling(widthDip * Scale));
        int h = Math.Min(WindowPx.Height - TopInsetPx, (int)Math.Ceiling(heightDip * Scale));
        int left = (WindowPx.Width - w) / 2;
        Native.SetWindowRgn(Hwnd, Native.CreateRectRgn(left, TopInsetPx, left + w, TopInsetPx + h), true);   // the system owns the region from here
        RegionPx = new RectInt32(left, TopInsetPx, w, h);
        RegionChanged?.Invoke();
    }

    /// <summary>A mirror's region, copied from the main island's (F7).</summary>
    public void SetRegionPx(RectInt32 region)
    {
        Native.SetWindowRgn(Hwnd, Native.CreateRectRgn(region.X, region.Y, region.X + region.Width, region.Y + region.Height), true);
        RegionPx = region;
    }

    /// <summary>The region changed (mirrors on other displays follow it).</summary>
    public event Action? RegionChanged;

    /// <summary>The current region, in pixels relative to the window's top-left corner.</summary>
    public RectInt32 RegionPx { get; private set; }

    public void Dispose()
    {
        if (_hotkeyRegistered) Native.UnregisterHotKey(Hwnd, HotkeyId);
        Native.RemoveWindowSubclass(Hwnd, _subclass, (UIntPtr)1);
    }
}
