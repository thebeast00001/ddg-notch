using System.Runtime.InteropServices;
using Forty.Core;
using Forty.Platform;

namespace Forty.Tests;

/// <summary>
/// "Forty.Tests probe": starts every adapter, prints what Windows reports, waits a few seconds for
/// events, and prints again. It only reads: no media key is pressed and no setting is changed.
/// </summary>
static class Probe
{
    public static int Run(double seconds)
    {
        using var power = new PowerAdapter();
        using var media = new MediaAdapter();
        using var focus = new FocusAdapter();
        using var screen = new ScreenAdapter(MonitorFromPoint(new Point(), 1));
        using var bluetooth = new BluetoothAdapter();
        using var audio = new AudioDeviceAdapter();
        Adapter[] all = { power, media, focus, screen, bluetooth, audio };
        bluetooth.Changed += (before, after) =>
        {
            if (before != null && before.Connected != after.Connected) Console.WriteLine($"  event: {after.Name} {(after.Connected ? "connected" : "disconnected")}");
            else if (before != null && before.Battery != after.Battery) Console.WriteLine($"  event: {after.Name} battery {after.Battery?.ToString() ?? "unknown"}");
        };
        audio.OutputChanged += (name, worn) => Console.WriteLine($"  event: output now {name}{(worn ? " (worn)" : "")}");

        var started = System.Diagnostics.Stopwatch.StartNew();
        foreach (var adapter in all) adapter.Start();
        Console.WriteLine($"Started {all.Length} adapters in {started.ElapsedMilliseconds} ms.");

        power.Changed += s => Console.WriteLine($"  event: power {Describe(s)}");
        power.ModeChanged += m => Console.WriteLine($"  event: power mode {m}");
        media.Changed += p => Console.WriteLine($"  event: media {Describe(p)}");
        focus.Changed += a => Console.WriteLine($"  event: Windows Focus {(a ? "on" : "off")}");

        Print();
        Console.WriteLine($"Listening for {seconds:0} s...");
        Thread.Sleep(TimeSpan.FromSeconds(seconds));
        Print();
        return all.Count(a => a.Status.Availability == Availability.Unavailable);

        void Print()
        {
            foreach (var adapter in all)
                Console.WriteLine($"  {adapter.Name,-7} {adapter.Status.Availability}{(adapter.Status.Reason is { } r ? $" ({r})" : "")}");
            Console.WriteLine($"  power:  {Describe(power.Snapshot)}; mode {power.Mode}");
            Console.WriteLine($"  media:  {Describe(media.Current)}");
            Console.WriteLine($"  focus:  {(focus.Active ? "on" : "off")}");
            Console.WriteLine($"  screen: front {screen.Front}, presentation {screen.Presentation}, game {screen.ExclusiveGame}");
            foreach (var device in bluetooth.Devices.OrderBy(d => d.Name))
                Console.WriteLine($"  bluetooth: {device.Name} ({device.Kind}), {(device.Connected ? "connected" : "not connected")}{(device.Battery is { } b ? $", battery {b}%" : "")}");
            var state = new SystemState(screen.Front == FrontState.FullScreen, screen.Presentation,
                                        screen.ExclusiveGame || power.Mode == EffectivePowerMode.GameMode, focus.Active,
                                        power.Snapshot is { Percent: < ModeResolver.LowBatteryPercent, PluggedIn: false });
            Console.WriteLine($"  mode Forty would use: {ModeResolver.Resolve(state, null)}");
        }
    }

    static string Describe(PowerSnapshot s) =>
        s.Percent is not { } p ? "no battery"
        : $"{p}%, {(s.PluggedIn ? "plugged in" : "on battery")}{(s.Charging ? ", charging" : "")}{(s.Saver ? ", battery saver" : "")}" +
          (s.ChargeRateMilliwatts is { } rate ? $", rate {rate / 1000.0:0.#} W" : "") +
          (s.RemainingMilliwattHours is { } left && s.FullMilliwattHours is { } full ? $", {left / 1000.0:0.#} of {full / 1000.0:0.#} Wh" : "");

    // Only whether something is playing and which app; the title and artist are your data and are not printed.
    static string Describe(NowPlaying? p) =>
        p == null ? "nothing reported" : $"{(p.Playing ? "playing" : "paused")} in {p.AppName}, title {(p.Title.Length > 0 ? "present" : "missing")}";

    [StructLayout(LayoutKind.Sequential)] struct Point { public int X, Y; }
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(Point point, uint flags);
}

/// <summary>
/// "Forty.Tests lyrics-check": asks LRCLIB for one well-known song and reports only whether synced
/// lyrics came back and how many timed lines they have. The words themselves are never printed.
/// </summary>
static class LyricsCheck
{
    public static int Run()
    {
        string cache = Path.Combine(Path.GetTempPath(), $"forty-lyrics-check-{Environment.ProcessId}");
        try
        {
            var client = new LyricsClient(cache);
            var started = System.Diagnostics.Stopwatch.StartNew();
            string? lrc = client.FindSyncedAsync("Never Gonna Give You Up", "Rick Astley", "Whenever You Need Somebody", TimeSpan.FromSeconds(213)).GetAwaiter().GetResult();
            var lines = Forty.Core.Lrc.Parse(lrc);
            Console.WriteLine($"LRCLIB answered in {started.ElapsedMilliseconds} ms: synced lyrics {(lrc != null ? "found" : "not found")}, {lines.Count} timed lines" +
                              (lines.Count > 0 ? $", first at {lines[0].At.TotalSeconds:0} s, last at {lines[^1].At.TotalSeconds:0} s" : ""));
            started.Restart();
            client.FindSyncedAsync("Never Gonna Give You Up", "Rick Astley", "Whenever You Need Somebody", TimeSpan.FromSeconds(213)).GetAwaiter().GetResult();
            Console.WriteLine($"Second lookup from the cache on this PC: {started.ElapsedMilliseconds} ms");
            return lrc != null ? 0 : 1;
        }
        finally { if (Directory.Exists(cache)) Directory.Delete(cache, true); }
    }
}
