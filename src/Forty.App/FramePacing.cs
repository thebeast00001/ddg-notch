using System.Runtime.InteropServices;
using Forty.Core;

namespace Forty.App;

/// <summary>
/// F9: is motion getting every refresh of the display? Windows' compositor counts the refreshes of
/// the display and the frames it composed. Reading both counters when a morph starts and again when it
/// has settled says how many refreshes went by without a new frame while the island was moving. The
/// result is kept with the other numbers-only diagnostics (F150): the display's rate and the frames
/// missed per move, so a 120 or 144 Hz screen shows its own figures the first time it is used.
/// </summary>
static class FramePacing
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct TimingInfo
    {
        public uint cbSize;
        public uint RefreshNumerator, RefreshDenominator;
        public ulong qpcRefreshPeriod;
        public uint ComposeNumerator, ComposeDenominator;
        public ulong qpcVBlank, cRefresh;
        public uint cDXRefresh;
        public ulong qpcCompose, cFrame;
        public uint cDXPresent;
        public ulong cRefreshFrame, cFrameSubmitted;
        public uint cDXPresentSubmitted;
        public ulong cFrameConfirmed;
        public uint cDXPresentConfirmed;
        public ulong cRefreshConfirmed;
        public uint cDXRefreshConfirmed;
        public ulong cFramesLate;
        public uint cFramesOutstanding;
        public ulong cFrameDisplayed, qpcFrameDisplayed, cRefreshFrameDisplayed, cFrameComplete, qpcFrameComplete,
                     cFramePending, qpcFramePending, cFramesDisplayed, cFramesComplete, cFramesPending, cFramesAvailable,
                     cFramesDropped, cFramesMissed, cRefreshNextDisplayed, cRefreshNextPresented, cRefreshesDisplayed,
                     cRefreshesPresented, cRefreshStarted, cPixelsReceived, cPixelsDrawn, cBuffersEmpty;
    }

    [DllImport("dwmapi.dll")] static extern int DwmGetCompositionTimingInfo(IntPtr hwnd, ref TimingInfo info);

    static (ulong Refreshes, ulong Frames, double Hz)? Read()
    {
        var info = new TimingInfo { cbSize = (uint)Marshal.SizeOf<TimingInfo>() };
        if (DwmGetCompositionTimingInfo(IntPtr.Zero, ref info) != 0 || info.RefreshDenominator == 0) return null;
        return (info.cRefresh, info.cFrame, info.RefreshNumerator / (double)info.RefreshDenominator);
    }

    static (ulong Refreshes, ulong Frames, double Hz)? _start;
    static int _version;

    /// <summary>A morph is starting: note the counters, and read them again once it should have settled.</summary>
    public static void Moving(Meter meter, double settleSeconds)
    {
        if (_start == null) _start = Read();
        int version = ++_version;
        _ = Finish(meter, version, settleSeconds);
    }

    static async Task Finish(Meter meter, int version, double seconds)
    {
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        if (version != _version || _start is not { } start || Read() is not { } end) return;   // a newer move extends this one
        _start = null;
        ulong refreshes = end.Refreshes - start.Refreshes, frames = end.Frames - start.Frames;
        if (refreshes == 0 || refreshes > 10_000) return;
        meter.Record("display.hz", end.Hz);
        meter.Record("motion.missed", Math.Max(0, (double)refreshes - frames));
        meter.Record("motion.fps", frames / seconds);
    }

    /// <summary>The display's rate right now, or null if Windows does not say.</summary>
    public static double? Hz => Read()?.Hz;
}
