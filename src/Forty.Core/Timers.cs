using System.Globalization;
using System.Text.RegularExpressions;

namespace Forty.Core;

public enum TimerPhase { Off, Running, Paused, Ringing }

/// <summary>
/// The island's timer (Tempo's job): set, running, paused, or ringing when time is up. Time is passed
/// in (seconds on any steady clock), so every rule can be tested exactly. One timer at a time, from
/// one second to a day.
/// </summary>
public sealed class CountdownTimer
{
    public static readonly TimeSpan Shortest = TimeSpan.FromSeconds(1), Longest = TimeSpan.FromHours(24);
    /// <summary>How long it rings before it stops by itself.</summary>
    public const double RingSeconds = 60;

    double _endsAt, _left, _rangAt;

    public TimerPhase Phase { get; private set; } = TimerPhase.Off;
    public TimeSpan Duration { get; private set; }

    public void Start(TimeSpan duration, double now)
    {
        Duration = duration < Shortest ? Shortest : duration > Longest ? Longest : duration;
        _endsAt = now + Duration.TotalSeconds;
        Phase = TimerPhase.Running;
    }

    public void Pause(double now)
    {
        if (Phase != TimerPhase.Running) return;
        _left = Math.Max(0, _endsAt - now);
        Phase = TimerPhase.Paused;
    }

    public void Resume(double now)
    {
        if (Phase != TimerPhase.Paused) return;
        _endsAt = now + _left;
        Phase = TimerPhase.Running;
    }

    /// <summary>More time on a running or paused timer ("+1 min"), up to a day in all.</summary>
    public void Add(TimeSpan extra, double now)
    {
        if (Phase is not (TimerPhase.Running or TimerPhase.Paused)) return;
        var room = Longest - Duration;
        if (extra > room) extra = room;
        if (extra <= TimeSpan.Zero) return;
        Duration += extra;
        if (Phase == TimerPhase.Running) _endsAt += extra.TotalSeconds;
        else _left += extra.TotalSeconds;
    }

    /// <summary>Stops it, whether running, paused or ringing.</summary>
    public void Cancel() => Phase = TimerPhase.Off;

    public TimeSpan Remaining(double now) => Phase switch
    {
        TimerPhase.Running => TimeSpan.FromSeconds(Math.Max(0, _endsAt - now)),
        TimerPhase.Paused => TimeSpan.FromSeconds(_left),
        TimerPhase.Ringing => TimeSpan.Zero,
        _ => Duration,
    };

    /// <summary>How much of the time has gone, 0 to 1.</summary>
    public double Progress(double now) => Duration.TotalSeconds <= 0 ? 0 : 1 - Remaining(now).TotalSeconds / Duration.TotalSeconds;

    /// <summary>
    /// Moves the timer on to <paramref name="now"/>: a running timer whose time is up starts ringing
    /// (returns true, once); a timer that has rung for a minute stops by itself.
    /// </summary>
    public bool Tick(double now)
    {
        if (Phase == TimerPhase.Running && now >= _endsAt) { Phase = TimerPhase.Ringing; _rangAt = now; return true; }
        if (Phase == TimerPhase.Ringing && now - _rangAt >= RingSeconds) Phase = TimerPhase.Off;
        return false;
    }

    /// <summary>"4:59", "14:59" or "1:04:59", rounded up so a timer never shows 0:00 while it is still running.</summary>
    public static string Format(TimeSpan left)
    {
        long total = (long)Math.Ceiling(left.TotalSeconds - 1e-6);
        if (total < 0) total = 0;
        long h = total / 3600, m = total / 60 % 60, s = total % 60;
        return h > 0 ? $"{h}:{m:00}:{s:00}" : $"{m}:{s:00}";
    }

    /// <summary>A duration in words, for what Narrator says and for "set for …": "1 hour 30 minutes", "45 seconds".</summary>
    public static string Words(TimeSpan d)
    {
        var parts = new List<string>();
        if (d.Hours + d.Days * 24 > 0) parts.Add(Plural(d.Hours + d.Days * 24, "hour"));
        if (d.Minutes > 0) parts.Add(Plural(d.Minutes, "minute"));
        if (d.Seconds > 0) parts.Add(Plural(d.Seconds, "second"));
        return parts.Count == 0 ? "0 seconds" : string.Join(" ", parts);
        static string Plural(int n, string unit) => $"{n} {unit}{(n == 1 ? "" : "s")}";
    }
}

/// <summary>
/// Durations as people say them: "10 minutes", "1 hour 30 minutes", "1h30m", "90 sec", "half an hour",
/// "a minute and a half", "25 min".
/// </summary>
public static class Durations
{
    static readonly Dictionary<string, double> Words = new(StringComparer.OrdinalIgnoreCase)
    {
        ["a"] = 1, ["an"] = 1, ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5, ["six"] = 6, ["seven"] = 7,
        ["eight"] = 8, ["nine"] = 9, ["ten"] = 10, ["fifteen"] = 15, ["twenty"] = 20, ["thirty"] = 30, ["forty"] = 40, ["forty-five"] = 45, ["sixty"] = 60,
    };

    // A number (digits, or a word that starts a word) and a unit that ends one, so "1h30m" reads as two
    // parts but "Pandas" is not "a s(econd)".
    static readonly Regex Part = new(@"(?:(?<![\d.])(?<n>\d+(?:\.\d+)?)|\b(?<n>a|an|one|two|three|four|five|six|seven|eight|nine|ten|fifteen|twenty|thirty|forty-five|forty|sixty))\s*(?<u>hours?|hrs?|h|minutes?|mins?|m|seconds?|secs?|s)(?![A-Za-z])(?<half>\s+and\s+a\s+half)?",
        RegexOptions.IgnoreCase);
    static readonly Regex HalfHour = new(@"\bhalf\s+an?\s+hour\b", RegexOptions.IgnoreCase);
    static readonly Regex QuarterHour = new(@"\ba\s+quarter\s+of\s+an\s+hour\b|\bquarter\s+hour\b", RegexOptions.IgnoreCase);

    /// <summary>The duration in a phrase, or null if it names none.</summary>
    public static TimeSpan? Find(string text)
    {
        double seconds = 0;
        bool found = false;
        string rest = text;
        if (HalfHour.IsMatch(rest)) { seconds += 1800; found = true; rest = HalfHour.Replace(rest, " "); }
        if (QuarterHour.IsMatch(rest)) { seconds += 900; found = true; rest = QuarterHour.Replace(rest, " "); }
        foreach (Match m in Part.Matches(rest))
        {
            string n = m.Groups["n"].Value;
            double value = double.TryParse(n, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : Words.TryGetValue(n, out double w) ? w : 0;
            if (m.Groups["half"].Success) value += 0.5;
            string u = m.Groups["u"].Value.ToLowerInvariant();
            double unit = u.StartsWith('h') ? 3600 : u.StartsWith('m') ? 60 : 1;
            seconds += value * unit;
            found = true;
        }
        return found && seconds > 0 ? TimeSpan.FromSeconds(Math.Round(seconds)) : null;
    }
}

/// <summary>
/// What a timer request asks for, as one argument string shared by the rules and the brain:
/// "start:600", or one of the actions.
/// </summary>
public static class TimerCommand
{
    public const string Open = "open", Pause = "pause", Resume = "resume", Cancel = "cancel", Left = "left";
    public static readonly IReadOnlyList<string> Actions = new[] { Open, Pause, Resume, Cancel, Left };

    public static string Start(TimeSpan duration) => "start:" + (long)Math.Round(duration.TotalSeconds);

    /// <summary>The duration a "start:" argument carries, or null for an action.</summary>
    public static TimeSpan? Duration(string argument) =>
        argument.StartsWith("start:", StringComparison.Ordinal) && long.TryParse(argument.AsSpan(6), NumberStyles.None, CultureInfo.InvariantCulture, out long s) && s > 0
            ? TimeSpan.FromSeconds(s) : null;
}
