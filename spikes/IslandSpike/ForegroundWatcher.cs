namespace IslandSpike;

enum FrontState { Normal, Maximized, FullScreen }

/// <summary>
/// Tells the island what kind of window is in front on its monitor, so it can rest as a sliver over a
/// maximised window and disappear for a full-screen one. Driven by window events from Windows;
/// nothing polls and nothing sits in the input path.
/// </summary>
sealed class ForegroundWatcher : IDisposable
{
    readonly IntPtr _selfMonitor;
    readonly Native.WinEventProc _callback;   // kept alive for as long as the hooks exist
    readonly IntPtr _hookForeground, _hookMinimize;
    IntPtr _hookLocation;
    IntPtr _front;

    public FrontState State { get; private set; }
    public string FrontClass { get; private set; } = "";
    public event Action<FrontState>? Changed;

    public ForegroundWatcher(IntPtr selfMonitor)
    {
        _selfMonitor = selfMonitor;
        _callback = OnEvent;
        const uint flags = Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS;
        _hookForeground = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _callback, 0, 0, flags);
        _hookMinimize = Native.SetWinEventHook(Native.EVENT_SYSTEM_MINIMIZESTART, Native.EVENT_SYSTEM_MINIMIZEEND,
            IntPtr.Zero, _callback, 0, 0, flags);
        Follow(Native.GetForegroundWindow());
    }

    void OnEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (evt == Native.EVENT_OBJECT_LOCATIONCHANGE)
        {
            // Only the front window itself: this fires for carets and cursors too.
            if (hwnd == _front && idObject == 0) Evaluate();
        }
        else
        {
            Follow(Native.GetForegroundWindow());
        }
    }

    /// <summary>Watch moves and resizes of the new front window only, not of every window on the system.</summary>
    void Follow(IntPtr front)
    {
        if (front != _front)
        {
            if (_hookLocation != IntPtr.Zero) { Native.UnhookWinEvent(_hookLocation); _hookLocation = IntPtr.Zero; }
            _front = front;
            if (front != IntPtr.Zero)
            {
                uint threadId = Native.GetWindowThreadProcessId(front, out uint processId);
                if (processId != (uint)Environment.ProcessId)
                    _hookLocation = Native.SetWinEventHook(Native.EVENT_OBJECT_LOCATIONCHANGE, Native.EVENT_OBJECT_LOCATIONCHANGE,
                        IntPtr.Zero, _callback, processId, threadId, Native.WINEVENT_OUTOFCONTEXT);
            }
        }
        Evaluate();
    }

    void Evaluate()
    {
        var state = Classify(_front, _selfMonitor, out string cls);
        FrontClass = cls;
        if (state == State) return;
        State = state;
        Changed?.Invoke(state);
    }

    public static FrontState Classify(IntPtr front, IntPtr selfMonitor, out string cls)
    {
        cls = Native.ClassName(front);
        if (front == IntPtr.Zero) return FrontState.Normal;
        // The desktop and the taskbar are not apps.
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return FrontState.Normal;
        IntPtr monitor = Native.MonitorFromWindow(front, Native.MONITOR_DEFAULTTONEAREST);
        if (monitor != selfMonitor) return FrontState.Normal;

        var info = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
        if (!Native.GetMonitorInfoW(monitor, ref info) || !Native.GetWindowRect(front, out var r)) return FrontState.Normal;
        var m = info.rcMonitor;

        // A full-screen window matches the monitor exactly. A maximised one overhangs it by its
        // frame, or stops at the taskbar.
        bool exact = Math.Abs(r.Left - m.Left) <= 1 && Math.Abs(r.Top - m.Top) <= 1 &&
                     Math.Abs(r.Right - m.Right) <= 1 && Math.Abs(r.Bottom - m.Bottom) <= 1;
        if (exact) return FrontState.FullScreen;

        // Windows' own verdict covers exclusive full-screen games and presentation mode.
        if (Native.SHQueryUserNotificationState(out int quns) == 0 && quns is 2 or 3 or 4) return FrontState.FullScreen;

        return Native.IsZoomed(front) ? FrontState.Maximized : FrontState.Normal;
    }

    public void Dispose()
    {
        if (_hookForeground != IntPtr.Zero) Native.UnhookWinEvent(_hookForeground);
        if (_hookMinimize != IntPtr.Zero) Native.UnhookWinEvent(_hookMinimize);
        if (_hookLocation != IntPtr.Zero) Native.UnhookWinEvent(_hookLocation);
    }
}
