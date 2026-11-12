using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace IslandSpike;

/// <summary>
/// A system backdrop that paints nothing, so the window is see-through wherever the island draws nothing.
/// </summary>
sealed class ClearBackdrop : SystemBackdrop
{
    static Windows.UI.Composition.Compositor? s_systemCompositor;
    static IntPtr s_queueController;

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(target, xamlRoot);
        EnsureSystemCompositor();
        target.SystemBackdrop = s_systemCompositor!.CreateColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
    {
        base.OnTargetDisconnected(target);
        target.SystemBackdrop = null;
    }

    static void EnsureSystemCompositor()
    {
        if (s_systemCompositor != null) return;
        if (Windows.System.DispatcherQueue.GetForCurrentThread() == null)
        {
            var options = new Native.DispatcherQueueOptions
            {
                dwSize = Marshal.SizeOf<Native.DispatcherQueueOptions>(),
                threadType = 2,     // current thread
                apartmentType = 2,  // single-threaded apartment
            };
            Native.CreateDispatcherQueueController(options, out s_queueController);
        }
        s_systemCompositor = new Windows.UI.Composition.Compositor();
    }
}

/// <summary>
/// The overlay window: transparent, always on top, never activated by the pointer, absent from
/// Alt-Tab and the taskbar. It has one fixed size. What changes is its window region, which is kept
/// to the island's bounds so that everything beside the island belongs to the app underneath.
/// </summary>
sealed class IslandWindow
{
    public Window Window { get; }
    public Grid Root { get; }
    public Canvas BodyHost { get; }
    public Grid Overlay { get; }
    public IntPtr Hwnd { get; }
    public IntPtr MonitorHandle { get; }
    public ulong DisplayIdValue { get; }

    /// <summary>Physical pixels per device-independent pixel on this window's monitor.</summary>
    public double Scale { get; }
    public RectInt32 MonitorPx { get; }
    public RectInt32 WindowPx { get; }
    public string TransparencyRecipe { get; }
    public double WidthDip => WindowPx.Width / Scale;
    public double HeightDip => WindowPx.Height / Scale;

    public IslandWindow(double widthDip, double heightDip)
    {
        // Two layers: the island's body is drawn by the compositor on BodyHost; text and buttons are
        // ordinary XAML on Overlay, which sits above it.
        Root = new Grid { Background = new SolidColorBrush(Colors.Transparent) };
        BodyHost = new Canvas();
        Overlay = new Grid();
        Root.Children.Add(BodyHost);
        Root.Children.Add(Overlay);
        Window = new Window { SystemBackdrop = new ClearBackdrop(), Content = Root };
        Hwnd = WinRT.Interop.WindowNative.GetWindowHandle(Window);

        // "own": this class asks Windows for always-on-top itself, as the last step, and checks it.
        // "framework": the framework's setting does it (kept only so the two can be compared).
        TopmostMode = Environment.GetEnvironmentVariable("FORTY_TOPMOST_MODE") ?? "own";
        var presenter = OverlappedPresenter.CreateForContextMenu();   // no title bar, no border
        if (TopmostMode == "framework") presenter.IsAlwaysOnTop = true;
        Window.AppWindow.SetPresenter(presenter);
        Window.AppWindow.IsShownInSwitchers = false;

        long ex = Native.GetWindowLongPtr(Hwnd, Native.GWL_EXSTYLE).ToInt64();
        ex |= Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW;
        ex &= ~Native.WS_EX_APPWINDOW;
        Native.SetWindowLongPtr(Hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));

        // Making the empty part of the window see-through. Which of these Windows needs is what the
        // spike is finding out; FORTY_TRANSPARENCY picks the recipe.
        // Measured on Windows 11 build 26300: "none" leaves an opaque black rectangle; "extend" is
        // see-through and clickable; "layered" (colour key) makes a black island click-through.
        TransparencyRecipe = Environment.GetEnvironmentVariable("FORTY_TRANSPARENCY") ?? "extend";
        if (TransparencyRecipe is "extend" or "both")
        {
            var sheet = new Native.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            Native.DwmExtendFrameIntoClientArea(Hwnd, ref sheet);
        }
        if (TransparencyRecipe is "layered" or "both")
        {
            long layered = Native.GetWindowLongPtr(Hwnd, Native.GWL_EXSTYLE).ToInt64() | Native.WS_EX_LAYERED;
            Native.SetWindowLongPtr(Hwnd, Native.GWL_EXSTYLE, new IntPtr(layered));
            Native.SetLayeredWindowAttributes(Hwnd, 0, 255, Native.LWA_COLORKEY | Native.LWA_ALPHA);
        }

        int doNotRound = 1;
        Native.DwmSetWindowAttribute(Hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref doNotRound, sizeof(int));
        int noBorder = unchecked((int)0xFFFFFFFE);
        Native.DwmSetWindowAttribute(Hwnd, Native.DWMWA_BORDER_COLOR, ref noBorder, sizeof(int));

        var area = DisplayArea.Primary;
        MonitorPx = area.OuterBounds;
        DisplayIdValue = area.DisplayId.Value;
        MonitorHandle = Win32Interop.GetMonitorFromDisplayId(area.DisplayId);
        Native.GetDpiForMonitor(MonitorHandle, 0, out uint dpi, out _);
        Scale = dpi / 96.0;

        // Windows draws a one-pixel frame line along the top of a frame-extended window. The window
        // therefore starts TopInsetPx above the monitor and its region never includes those rows.
        int w = (int)Math.Ceiling(widthDip * Scale), h = (int)Math.Ceiling(heightDip * Scale) + TopInsetPx;
        if ((MonitorPx.Width - w) % 2 != 0) w++;   // keep the centre on a whole pixel
        WindowPx = new RectInt32(MonitorPx.X + (MonitorPx.Width - w) / 2, MonitorPx.Y - TopInsetPx, w, h);
        Window.AppWindow.MoveAndResize(WindowPx);
        Native.SetWindowPos(Hwnd, IntPtr.Zero, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED);

        // Always-on-top is requested last, after every style is in place, and then checked.
        if (TopmostMode != "framework") Native.SetWindowPos(Hwnd, HwndTopmost, 0, 0, 0, 0, Quiet);
        TopmostFirstTry = IsTopmost;
        if (!TopmostFirstTry) ZOrderAtFailure = DescribeZOrder();
        EnsureTopmost();
    }

    static readonly IntPtr HwndTopmost = new(-1), HwndNotTopmost = new(-2);
    const uint Quiet = Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE;

    public string TopmostMode { get; }
    /// <summary>Whether the very first request for always-on-top took. Kept for the investigation.</summary>
    public bool TopmostFirstTry { get; }
    public int TopmostRepairs { get; private set; }
    public string ZOrderAtFailure { get; } = "";
    public bool IsTopmost => (ExStyle & Native.WS_EX_TOPMOST) != 0;

    /// <summary>
    /// Makes sure the window really is always-on-top, and says whether it is.
    ///
    /// Asking Windows is not enough. In a few starts in a hundred Windows accepts the request,
    /// reports success, and leaves the flag off; every later request is then ignored the same way.
    /// Asking for "not always-on-top" first clears that state, and the next request takes. So:
    /// ask, look at the flag, and if it is off, clear and ask again.
    /// </summary>
    public bool EnsureTopmost()
    {
        for (int attempt = 0; attempt < 4 && !IsTopmost; attempt++)
        {
            Native.SetWindowPos(Hwnd, HwndTopmost, 0, 0, 0, 0, Quiet);
            if (IsTopmost) break;
            TopmostRepairs++;
            Native.SetWindowPos(Hwnd, HwndNotTopmost, 0, 0, 0, 0, Quiet);
            Native.SetWindowPos(Hwnd, HwndTopmost, 0, 0, 0, 0, Quiet);
        }
        return IsTopmost;
    }

    /// <summary>Where the window sits among all top-level windows. Used to understand a failed request.</summary>
    string DescribeZOrder()
    {
        bool Topmost(IntPtr h) => (Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE).ToInt64() & Native.WS_EX_TOPMOST) != 0;
        int above = 0, aboveTopmost = 0;
        for (IntPtr h = Native.GetWindow(Hwnd, 3 /* previous */); h != IntPtr.Zero && above < 3000; h = Native.GetWindow(h, 3))
        {
            above++;
            if (Topmost(h)) aboveTopmost++;
        }
        IntPtr below = Native.GetWindow(Hwnd, 2 /* next */);
        string belowKind = below == IntPtr.Zero ? "nothing" : Topmost(below) ? "an always-on-top window" : "an ordinary window";
        return $"{above} windows above it, {aboveTopmost} of them always-on-top; just below it is {belowKind}";
    }

    public void Show()
    {
        Window.AppWindow.Show(false);   // false: do not activate
        EnsureTopmost();
    }

    public void Hide() => Window.AppWindow.Hide();
    public bool IsVisible => Native.IsWindowVisible(Hwnd);

    /// <summary>
    /// Limit the window to a rectangle hanging from the top centre. Outside it the window neither
    /// draws nor receives the pointer.
    /// </summary>
    public void SetRegionDip(double widthDip, double heightDip)
    {
        int w = Math.Min(WindowPx.Width, (int)Math.Ceiling(widthDip * Scale));
        int h = Math.Min(WindowPx.Height - TopInsetPx, (int)Math.Ceiling(heightDip * Scale));
        int left = (WindowPx.Width - w) / 2;
        IntPtr region = Native.CreateRectRgn(left, TopInsetPx, left + w, TopInsetPx + h);
        Native.SetWindowRgn(Hwnd, region, true);   // the system owns the region from here
        RegionPx = new RectInt32(left, TopInsetPx, w, h);
    }

    /// <summary>The whole window, minus the hidden rows at the top.</summary>
    public void ClearRegion()
    {
        IntPtr region = Native.CreateRectRgn(0, TopInsetPx, WindowPx.Width, WindowPx.Height);
        Native.SetWindowRgn(Hwnd, region, true);
        RegionPx = new RectInt32(0, TopInsetPx, WindowPx.Width, WindowPx.Height - TopInsetPx);
    }

    /// <summary>Rows at the top of the window that sit above the monitor and are never shown.</summary>
    public const int TopInsetPx = 2;
    public double TopInsetDip => TopInsetPx / Scale;

    /// <summary>The current region, in pixels relative to the window's top-left corner.</summary>
    public RectInt32 RegionPx { get; private set; }

    public long ExStyle => Native.GetWindowLongPtr(Hwnd, Native.GWL_EXSTYLE).ToInt64();
}
