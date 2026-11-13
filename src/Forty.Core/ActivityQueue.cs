namespace Forty.Core;

/// <summary>What the island should show at one moment.</summary>
/// <param name="Primary">The activity in the capsule, or null for rest.</param>
/// <param name="Satellite">A second activity, shown as the small circle beside the capsule.</param>
/// <param name="Waiting">How many more would show if there were room; drawn as "+N" on the satellite.</param>
/// <param name="Dots">Activities the current mode allows only as the sliver dot.</param>
/// <param name="Mode">The mode these rules were applied in.</param>
/// <param name="Marks">Everything that is listening, recording or watching right now, most important first (F116).</param>
public sealed record IslandView(Activity? Primary, Activity? Satellite, int Waiting, IReadOnlyList<Activity> Dots, Mode Mode, IReadOnlyList<Activity> Marks)
{
    public static readonly IslandView Rest = new(null, null, 0, Array.Empty<Activity>(), Mode.Normal, Array.Empty<Activity>());

    public bool SameAs(IslandView other) =>
        Equals(Primary, other.Primary) && Equals(Satellite, other.Satellite) && Waiting == other.Waiting &&
        Mode == other.Mode && Dots.SequenceEqual(other.Dots) && Marks.SequenceEqual(other.Marks);
}

/// <summary>
/// Decides what the island shows: one activity in the capsule, one more as a satellite, the rest
/// waiting in order (ROADMAP.md 2.1, feature F5).
///
/// <list type="bullet">
/// <item>Higher priority wins the capsule. Among equals, the one already showing keeps it for things
/// that need you or matter soon (answer them in order), and the newest wins for things that are good
/// to know (the latest track is the one that matters).</item>
/// <item>The activity you are pointing at or typing in is never displaced, not even by something more
/// urgent; the newcomer waits beside it as the satellite.</item>
/// <item>A lifetime starts when an activity is first shown, not when it was posted. Something good to
/// know that waited more than 10 seconds without being shown is dropped as stale.</item>
/// <item>Routine (digest) activities never show; they are kept for the brief.</item>
/// <item>A mark (something listening, recording or watching, F116) is always visible, in every mode:
/// as the capsule when it is the thing you are doing, as the satellite beside anything else, or as
/// the dot when nothing else shows. No mode, priority or hold can hide it.</item>
/// </list>
///
/// Time is passed in by the caller (seconds on any steady clock), so every rule can be tested exactly.
/// Nothing here runs on a timer: <see cref="NextDeadline"/> says when <see cref="Tick"/> is next needed.
/// </summary>
public sealed class ActivityQueue
{
    public const double StaleSeconds = 10;
    /// <summary>When you stop pointing at something whose time has run out, it stays this long longer.</summary>
    public const double GraceSeconds = 1;

    sealed class Entry(Activity activity, double postedAt, long order)
    {
        public Activity Activity = activity;
        public double PostedAt = postedAt;
        public double? ShownAt;
        public long Order = order;
    }

    readonly Dictionary<string, Entry> _entries = new();
    readonly List<Activity> _digest = new();
    long _order;
    string? _held;

    public Mode Mode { get; private set; } = Mode.Normal;
    public IslandView View { get; private set; } = IslandView.Rest;
    public IReadOnlyList<Activity> Digest => _digest;
    public int Count => _entries.Count;
    public bool Contains(string key) => _entries.ContainsKey(key);

    /// <summary>Raised with the previous and the new view whenever what should be shown changes.</summary>
    public event Action<IslandView, IslandView>? Changed;

    /// <summary>Adds an activity, or updates the one with the same key in place.</summary>
    public void Post(Activity activity, double now)
    {
        if (activity.Priority == Priority.Digest)
        {
            _digest.RemoveAll(a => a.Key == activity.Key);
            _digest.Add(activity);
            Remove(activity.Key, now);
            return;
        }
        if (_entries.TryGetValue(activity.Key, out var entry))
        {
            entry.Activity = activity;
            // Something posted again while visible starts its lifetime over, so a fresh update gets
            // its full time on screen.
            if (entry.ShownAt != null && IsVisible(activity.Key)) entry.ShownAt = now;
        }
        else
        {
            _entries[activity.Key] = new Entry(activity, now, _order++);
        }
        Recompute(now);
    }

    public bool Remove(string key, double now)
    {
        if (!_entries.Remove(key)) return false;
        if (_held == key) _held = null;
        Recompute(now);
        return true;
    }

    public void SetMode(Mode mode, double now)
    {
        if (mode == Mode) return;
        Mode = mode;
        Recompute(now);
    }

    /// <summary>
    /// Marks the activity under the pointer or keyboard focus, or null when there is none. A held
    /// activity never expires and is never displaced.
    /// </summary>
    public void Hold(string? key, double now)
    {
        if (key == _held) return;
        if (_held != null && _entries.TryGetValue(_held, out var released) &&
            released.ShownAt is { } shown && released.Activity.Lifetime is { } life)
        {
            // Let go of something whose time ran out while you held it: it leaves shortly after.
            double latest = now - life.TotalSeconds + GraceSeconds;
            if (shown < latest) released.ShownAt = latest;
        }
        _held = key != null && _entries.ContainsKey(key) ? key : null;
        Recompute(now);
    }

    public string? Held => _held;

    /// <summary>Applies lifetimes and staleness. Call at <see cref="NextDeadline"/>.</summary>
    public void Tick(double now) => Recompute(now);

    /// <summary>The next moment something will expire, or null if nothing will by itself.</summary>
    public double? NextDeadline
    {
        get
        {
            double? next = null;
            foreach (var (key, e) in _entries)
            {
                if (key == _held) continue;
                double? due = e.ShownAt is { } shown
                    ? e.Activity.Lifetime is { } life ? shown + life.TotalSeconds : null
                    : e.Activity.Priority == Priority.Ambient && e.Activity.Lifetime != null ? e.PostedAt + StaleSeconds : null;
                if (due is { } d && (next == null || d < next)) next = d;
            }
            return next;
        }
    }

    bool IsVisible(string key) => View.Primary?.Key == key || View.Satellite?.Key == key;

    void Recompute(double now)
    {
        // 1. Lifetimes and staleness.
        foreach (var (key, e) in _entries.ToList())
        {
            if (key == _held) continue;
            bool expired = e.ShownAt is { } shown && e.Activity.Lifetime is { } life && now >= shown + life.TotalSeconds;
            bool stale = e.ShownAt == null && e.Activity.Priority == Priority.Ambient && e.Activity.Lifetime != null &&
                         now - e.PostedAt >= StaleSeconds;
            if (expired || stale) _entries.Remove(key);
        }

        // 2. What the mode allows.
        var candidates = new List<Entry>();
        var dots = new List<Activity>();
        foreach (var e in _entries.Values)
        {
            if (e.Activity.QuietMark) continue;   // marks are placed in step 5
            switch (ModeRules.For(Mode, e.Activity.Priority))
            {
                case Presentation.Capsule: candidates.Add(e); break;
                case Presentation.Dot: dots.Add(e.Activity); break;
            }
        }
        dots.Sort((a, b) => a.Priority.CompareTo(b.Priority));

        // 3. The capsule.
        string? current = View.Primary?.Key;
        Entry? primary = null;
        if (_held != null && View.Primary?.Key == _held)
            primary = candidates.FirstOrDefault(e => e.Activity.Key == _held);
        primary ??= Best(candidates, current);

        // 4. The satellite, then everything else waits.
        var rest = candidates.Where(e => e != primary).ToList();
        Entry? satellite = Best(rest, View.Satellite?.Key);
        int waiting = Math.Max(0, rest.Count - (satellite == null ? 0 : 1));

        if (primary != null) primary.ShownAt ??= now;
        if (satellite != null) satellite.ShownAt ??= now;

        // 5. Marks: the most important one not already on show takes the satellite beside the capsule,
        // or the dot when the capsule is empty. Whatever held the satellite waits.
        var marks = _entries.Values.Select(e => e.Activity).Where(a => a.Mark != null).OrderBy(a => a.Mark).ThenBy(a => a.Key, StringComparer.Ordinal).ToList();
        Activity? shownSatellite = satellite?.Activity;
        var pending = marks.FirstOrDefault(m => m.Key != primary?.Activity.Key && m.Key != shownSatellite?.Key);
        if (pending != null)
        {
            if (primary != null)
            {
                if (shownSatellite != null) waiting++;
                shownSatellite = pending;
            }
            else
            {
                dots.RemoveAll(d => d.Key == pending.Key);
                dots.Insert(0, pending);
            }
            if (_entries.TryGetValue(pending.Key, out var markEntry)) markEntry.ShownAt ??= now;
        }

        var view = new IslandView(primary?.Activity, shownSatellite, waiting, dots, Mode, marks);
        if (view.SameAs(View)) return;
        var before = View;
        View = view;
        Changed?.Invoke(before, view);
    }

    /// <summary>The highest-priority entry, breaking ties as the class summary describes.</summary>
    static Entry? Best(List<Entry> entries, string? incumbent)
    {
        if (entries.Count == 0) return null;
        var top = entries.Min(e => e.Activity.Priority);
        var tied = entries.Where(e => e.Activity.Priority == top).ToList();
        if (top is Priority.ActNow or Priority.Timely)
            return tied.FirstOrDefault(e => e.Activity.Key == incumbent) ?? tied.MinBy(e => e.Order);
        return tied.MaxBy(e => e.Order);   // good to know: the newest wins
    }
}
