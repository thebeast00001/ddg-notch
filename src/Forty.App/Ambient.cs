using System.Diagnostics;
using Microsoft.UI.Dispatching;

namespace Forty.App;

/// <summary>
/// Ambient motion (the equaliser and the fluid colours while music plays) stepped from one shared
/// timer instead of run as compositor animations.
///
/// Measured on 13 November: WinUI's in-app compositor costs a fixed amount of CPU for every frame it
/// draws, whatever is in it. Any animation that never ends (four equaliser bars were enough) makes it
/// draw every frame, about 12% of a core at 60 Hz on this PC, the same with or without the colours,
/// with any window size, clip or binding. Stepping the motion at a lower rate means frames are drawn
/// only when something actually changed. Gestures, morphs and every other transition still run on the
/// compositor at the display's full rate; only these slow, never-ending loops are stepped.
/// </summary>
static class Ambient
{
    static readonly List<Action<double>> Ticks = new();
    static readonly Stopwatch Since = Stopwatch.StartNew();
    static DispatcherQueueTimer? _timer;
    static int _hz = Read();

    static int Read() => int.TryParse(Environment.GetEnvironmentVariable("FORTY_AMBIENT_HZ"), out int hz) ? Math.Clamp(hz, 5, 60) : 30;

    /// <summary>Steps per second. Battery saver stops ambient motion altogether (PowerPolicy).</summary>
    public static int Hz
    {
        get => _hz;
        set
        {
            if (Environment.GetEnvironmentVariable("FORTY_AMBIENT_HZ") != null) return;   // a measurement asked for a fixed rate
            _hz = Math.Clamp(value, 5, 60);
            if (_timer != null) _timer.Interval = TimeSpan.FromSeconds(1.0 / _hz);
        }
    }

    /// <summary>False in battery saver (PowerPolicy.Visualiser): the equaliser rests and the colours hold still.</summary>
    public static bool Allowed { get; set; } = true;

    /// <summary>Seconds on the shared ambient clock.</summary>
    public static double Now => Since.Elapsed.TotalSeconds;

    public static void Add(Action<double> tick)
    {
        if (Ticks.Contains(tick)) return;
        Ticks.Add(tick);
        tick(Now);
        if (_timer == null)
        {
            _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
            _timer.Tick += (_, _) => { double now = Now; foreach (var t in Ticks.ToArray()) Guard.Run(() => t(now), "ambient"); };
        }
        _timer.Interval = TimeSpan.FromSeconds(1.0 / _hz);
        if (!_timer.IsRunning) _timer.Start();
    }

    public static void Remove(Action<double> tick)
    {
        Ticks.Remove(tick);
        if (Ticks.Count == 0) _timer?.Stop();   // nothing ambient: no timer, no frames
    }

    /// <summary>Quitting: no more steps, so nothing touches the compositor while it shuts down.</summary>
    public static void StopAll()
    {
        Ticks.Clear();
        _timer?.Stop();
    }

    /// <summary>A gentle ease in and out between two keys (the shape the compositor curves had).</summary>
    public static double Ease(double u) => u * u * (3 - 2 * u);

    /// <summary>The value of a looping keyframe track at <paramref name="seconds"/>.</summary>
    public static double Loop(float[] keys, double period, double seconds, double offset = 0)
    {
        double phase = ((seconds + offset) % period) / period * (keys.Length - 1);
        int k = Math.Min((int)phase, keys.Length - 2);
        double u = Ease(phase - k);
        return keys[k] + (keys[k + 1] - keys[k]) * u;
    }
}
