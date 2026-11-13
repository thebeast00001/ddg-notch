namespace Forty.Core;

/// <summary>
/// Which display the island lives on (F7, F8). By default the main display. With "follow the display
/// you're using" on, it moves to the display of the window you are working in, once that has held
/// for 300 ms, so dragging a window across screens doesn't make the island chase it.
/// Time is passed in (seconds on any steady clock) so the rules can be tested exactly.
/// </summary>
public sealed class DisplayFollower
{
    public const double SettleSeconds = 0.3;

    string? _candidate;
    double _since;

    public DisplayFollower(string current) => Current = current;

    /// <summary>The display the island is on.</summary>
    public string Current { get; private set; }

    /// <summary>When <see cref="Observe"/> should be called again to settle a pending move, or null.</summary>
    public double? NextCheck => _candidate == null ? null : _since + SettleSeconds;

    /// <summary>
    /// Reports the display you are using now. Returns the display to move to once it has held for
    /// <see cref="SettleSeconds"/>, otherwise null.
    /// </summary>
    /// <param name="busy">True while you are using the island itself (the panel is open or the pointer is on it): it never moves then.</param>
    public string? Observe(string? active, double now, bool busy = false)
    {
        if (active == null || active == Current || busy)
        {
            if (active == Current) _candidate = null;
            return null;
        }
        if (active != _candidate)
        {
            _candidate = active;
            _since = now;
            return null;
        }
        if (now - _since < SettleSeconds) return null;
        Current = active;
        _candidate = null;
        return active;
    }

    /// <summary>The island was put on a display for another reason (a display was removed, or following was turned off).</summary>
    public void PlacedOn(string display)
    {
        Current = display;
        _candidate = null;
    }
}

/// <summary>Where the island belongs when the set of displays changes (F7).</summary>
public static class DisplayRules
{
    /// <param name="follow">"Follow the display you're using" is on.</param>
    /// <param name="main">The main display now.</param>
    /// <param name="active">The display of the window you are using, if known.</param>
    /// <param name="current">The display the island is on, if it still exists.</param>
    /// <param name="present">Every display connected now.</param>
    public static string Choose(bool follow, string main, string? active, string? current, IReadOnlyCollection<string> present)
    {
        if (!follow) return main;
        if (current != null && present.Contains(current)) return current;   // following moves happen through the follower
        if (active != null && present.Contains(active)) return active;
        return main;
    }

    /// <summary>
    /// The island's top edge in pixels: the top of the display, or below a taskbar docked at the top
    /// (the work area starts lower then).
    /// </summary>
    public static int TopEdge(int displayTop, int workAreaTop) => Math.Max(displayTop, workAreaTop);
}
