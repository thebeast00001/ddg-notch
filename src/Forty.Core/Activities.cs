namespace Forty.Core;

/// <summary>How much an activity matters right now (PLAN.md 5.3).</summary>
public enum Priority
{
    /// <summary>Needs you: an approval, a timer that finished, an incoming call. Stays until answered.</summary>
    ActNow = 0,
    /// <summary>Matters soon: a meeting in five minutes, a battery alert.</summary>
    Timely = 1,
    /// <summary>Good to know: a track changed, a device connected. Leaves by itself.</summary>
    Ambient = 2,
    /// <summary>Never shown alone; collected for the brief.</summary>
    Digest = 3,
}

/// <summary>
/// Something the island can show. Features create activities; they never draw on the island directly.
/// <para><see cref="Key"/> names the thing, not the moment: posting an activity with a key that is
/// already present updates it in place (a new track, a new battery level) instead of adding another.</para>
/// </summary>
/// <param name="Key">Stable identity, such as "power.charging" or "media.now-playing".</param>
/// <param name="Source">The adapter or agent that posted it, for the audit log and for per-source rules.</param>
/// <param name="Title">The capsule's label.</param>
/// <param name="Detail">The trailing slot: a percentage, a time, a count. Optional.</param>
/// <param name="Glyph">A Segoe Fluent Icons code point for the leading slot.</param>
/// <param name="Narration">What Narrator says when it appears; also the run-log wording.</param>
/// <param name="Lifetime">How long it stays once shown. Null means until it is removed (a live activity).</param>
/// <param name="Accent">A state colour role; null means the default white content.</param>
public sealed record Activity(
    string Key,
    string Source,
    Priority Priority,
    string Title,
    string? Detail,
    string Glyph,
    string Narration,
    TimeSpan? Lifetime,
    Accent Accent = Accent.None)
{
    /// <summary>Default lifetimes when a feature does not choose one.</summary>
    public static TimeSpan? DefaultLifetime(Priority priority) => priority switch
    {
        Priority.Ambient => TimeSpan.FromSeconds(4),
        Priority.Timely => TimeSpan.FromSeconds(12),
        _ => null,
    };

    /// <summary>A transient activity with the default lifetime for its priority.</summary>
    public static Activity Event(string key, string source, Priority priority, string title, string? detail, string glyph, string narration, Accent accent = Accent.None) =>
        new(key, source, priority, title, detail, glyph, narration, DefaultLifetime(priority), accent);

    /// <summary>An activity that stays while its condition holds (music playing, a capture running).</summary>
    public static Activity Live(string key, string source, Priority priority, string title, string? detail, string glyph, string narration, Accent accent = Accent.None) =>
        new(key, source, priority, title, detail, glyph, narration, null, accent);
}

/// <summary>The reserved state colours (MOTION.md 8). Nothing else uses them.</summary>
public enum Accent { None, Done, NeedsYou, Error }

/// <summary>Modes decide which priorities may show, and as what (ROADMAP.md 2.1).</summary>
public enum Mode { Normal, Focus, Meeting, Presenting, Gaming }

/// <summary>How an activity of a given priority appears in a given mode.</summary>
public enum Presentation
{
    /// <summary>In the capsule, or as the satellite beside it.</summary>
    Capsule,
    /// <summary>Only as a 4 DIP dot at the end of the sliver.</summary>
    Dot,
    /// <summary>Held until the mode allows it, then shown.</summary>
    Later,
    /// <summary>Never shown; kept for the brief.</summary>
    Digest,
}

/// <summary>The table each mode applies. One place, readable as sentences on the settings page.</summary>
public static class ModeRules
{
    public static Presentation For(Mode mode, Priority priority) => (mode, priority) switch
    {
        (_, Priority.Digest) => Presentation.Digest,
        (Mode.Normal, _) => Presentation.Capsule,
        (Mode.Focus, Priority.ActNow) => Presentation.Capsule,
        (Mode.Focus, _) => Presentation.Dot,
        (Mode.Meeting, Priority.ActNow) => Presentation.Capsule,
        (Mode.Meeting, Priority.Timely) => Presentation.Dot,
        (Mode.Meeting, _) => Presentation.Later,
        (Mode.Presenting, Priority.ActNow) => Presentation.Dot,
        (Mode.Presenting, _) => Presentation.Later,
        (Mode.Gaming, _) => Presentation.Later,
        _ => Presentation.Capsule,
    };

    /// <summary>The rule as a sentence, for the settings page.</summary>
    public static string Describe(Mode mode, Priority priority)
    {
        string what = priority switch
        {
            Priority.ActNow => "Things that need you",
            Priority.Timely => "Things that matter soon",
            Priority.Ambient => "Things that are good to know",
            _ => "Routine things",
        };
        string how = For(mode, priority) switch
        {
            Presentation.Capsule => "show in the island",
            Presentation.Dot => "show only as a small dot",
            Presentation.Later => "wait until this mode ends",
            _ => "go to your brief",
        };
        return $"{what} {how}.";
    }
}
