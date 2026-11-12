using System.Runtime.InteropServices;
using Forty.Core;
using Windows.Foundation.Metadata;
using Windows.UI.Shell;

namespace Forty.Platform;

/// <summary>
/// Whether Windows Focus is on (F132, F73). Windows lets any app read this; turning Focus on or off
/// is a limited-access feature that needs a token from Microsoft, so this adapter only reads.
/// </summary>
public sealed class FocusAdapter() : Adapter("focus")
{
    FocusSessionManager? _manager;

    public bool Active { get; private set; }
    public event Action<bool>? Changed;

    protected override void OnStart()
    {
        if (!ApiInformation.IsTypePresent("Windows.UI.Shell.FocusSessionManager") || !FocusSessionManager.IsSupported)
        {
            Report(Availability.Unavailable, "this version of Windows does not report Focus");
            return;
        }
        _manager = FocusSessionManager.GetDefault();
        Active = _manager.IsFocusActive;
        _manager.IsFocusActiveChanged += (m, _) =>
        {
            Active = m.IsFocusActive;
            Changed?.Invoke(Active);
        };
    }
}

/// <summary>What kind of window is in front on the island's monitor.</summary>
public enum FrontState { Normal, Maximized, FullScreen }

/// <summary>
/// The window in front and Windows' own notification state (F133, F134): normal, maximised or full
/// screen, and whether Windows reports a presentation or a Direct3D full-screen app. Driven by window
/// events; nothing polls. Must be started on a thread with a message loop (the UI thread).
/// </summary>
public sealed class ScreenAdapter : Adapter
{
    readonly IntPtr _monitor;
    readonly WinEventProc _callback;   // kept alive for as long as the hooks exist
    IntPtr _hookForeground, _hookMinimize, _hookLocation, _front;

    public ScreenAdapter(IntPtr monitor) : base("screen")
    {
        _monitor = monitor;
        _callback = OnEvent;
    }

    public FrontState Front { get; private set; }
    public bool Presentation { get; private set; }
    public bool ExclusiveGame { get; private set; }

    /// <summary>The program name of the window in front ("chrome", "vlc"), for "hide the island in front of…" (F133).</summary>
    public string? FrontProgram { get; private set; }

    /// <summary>Raised when any of the three properties changes.</summary>
    public event Action? Changed;

    protected override void OnStart()
    {
        const uint flags = WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS;
        _hookForeground = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _callback, 0, 0, flags);
        _hookMinimize = SetWinEventHook(EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND, IntPtr.Zero, _callback, 0, 0, flags);
        if (_hookForeground == IntPtr.Zero) Report(Availability.Unavailable, "could not watch the window in front");
        Follow(GetForegroundWindow());
    }

    void OnEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (evt == EVENT_OBJECT_LOCATIONCHANGE) { if (hwnd == _front && idObject == 0) Evaluate(); }
        else Follow(GetForegroundWindow());
    }

    /// <summary>Watches moves and resizes of the window in front only, not of every window on the system.</summary>
    void Follow(IntPtr front)
    {
        if (front != _front)
        {
            if (_hookLocation != IntPtr.Zero) { UnhookWinEvent(_hookLocation); _hookLocation = IntPtr.Zero; }
            _front = front;
            if (front != IntPtr.Zero)
            {
                uint thread = GetWindowThreadProcessId(front, out uint process);
                if (process != (uint)Environment.ProcessId)
                {
                    _hookLocation = SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE, IntPtr.Zero, _callback, process, thread, WINEVENT_OUTOFCONTEXT);
                    string? program = ProgramName(process);
                    if (program != FrontProgram) { FrontProgram = program; _programChanged = true; }
                }
            }
        }
        Evaluate();
    }

    bool _programChanged;

    static string? ProgramName(uint processId)
    {
        try { using var p = System.Diagnostics.Process.GetProcessById((int)processId); return p.ProcessName; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
    }

    void Evaluate()
    {
        var front = Classify(_front, _monitor);
        int quns = SHQueryUserNotificationState(out int state) == 0 ? state : 5;
        bool presentation = quns == QUNS_PRESENTATION_MODE;
        bool game = quns == QUNS_RUNNING_D3D_FULL_SCREEN;
        if (front == Front && presentation == Presentation && game == ExclusiveGame && !_programChanged) return;
        _programChanged = false;
        Front = front;
        Presentation = presentation;
        ExclusiveGame = game;
        Changed?.Invoke();
    }

    /// <summary>Full screen matches the monitor exactly; maximised overhangs it by its frame or stops at the taskbar.</summary>
    static FrontState Classify(IntPtr front, IntPtr monitor)
    {
        if (front == IntPtr.Zero) return FrontState.Normal;
        string cls = ClassName(front);
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return FrontState.Normal;   // desktop and taskbar
        if (MonitorFromWindow(front, MONITOR_DEFAULTTONEAREST) != monitor) return FrontState.Normal;
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfoW(monitor, ref info) || !GetWindowRect(front, out var r)) return FrontState.Normal;
        var m = info.rcMonitor;
        bool exact = Math.Abs(r.Left - m.Left) <= 1 && Math.Abs(r.Top - m.Top) <= 1 && Math.Abs(r.Right - m.Right) <= 1 && Math.Abs(r.Bottom - m.Bottom) <= 1;
        if (exact) return FrontState.FullScreen;
        return IsZoomed(front) ? FrontState.Maximized : FrontState.Normal;
    }

    public override void Dispose()
    {
        foreach (var hook in new[] { _hookForeground, _hookMinimize, _hookLocation })
            if (hook != IntPtr.Zero) UnhookWinEvent(hook);
        _hookForeground = _hookMinimize = _hookLocation = IntPtr.Zero;
        base.Dispose();
    }

    const uint EVENT_SYSTEM_FOREGROUND = 0x0003, EVENT_SYSTEM_MINIMIZESTART = 0x0016, EVENT_SYSTEM_MINIMIZEEND = 0x0017;
    const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B, WINEVENT_OUTOFCONTEXT = 0x0000, WINEVENT_SKIPOWNPROCESS = 0x0002;
    const uint MONITOR_DEFAULTTONEAREST = 2;
    const int QUNS_RUNNING_D3D_FULL_SCREEN = 3, QUNS_PRESENTATION_MODE = 4;

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }
    delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfoW(IntPtr monitor, ref MONITORINFO info);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr hWnd);
    [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr hWnd, [Out] char[] buffer, int max);

    static string ClassName(IntPtr hWnd)
    {
        var buffer = new char[256];
        int length = GetClassNameW(hWnd, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : "";
    }
}
