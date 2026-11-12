namespace Forty.Core;

/// <summary>What Windows says about power at one moment. Values Windows does not provide are null.</summary>
/// <param name="Percent">Battery charge, 0–100, or null on a PC without a battery.</param>
/// <param name="PluggedIn">On mains power.</param>
/// <param name="Charging">Charge is going up.</param>
/// <param name="Saver">Battery saver is on.</param>
/// <param name="ChargeRateMilliwatts">Positive while charging, negative while draining, from the battery report.</param>
/// <param name="RemainingMilliwattHours">Energy left in the battery.</param>
/// <param name="FullMilliwattHours">Energy the battery holds when full.</param>
/// <param name="DesignMilliwattHours">Energy it held when new; full ÷ design is the battery's health.</param>
public sealed record PowerSnapshot(
    int? Percent,
    bool PluggedIn,
    bool Charging,
    bool Saver,
    int? ChargeRateMilliwatts = null,
    int? RemainingMilliwattHours = null,
    int? FullMilliwattHours = null,
    int? DesignMilliwattHours = null);

/// <summary>Your choices for battery alerts (F47). Defaults: 20% and 10%, and no "80% reached".</summary>
public sealed record PowerAlerts(int[] LowLevels, int? FullLevel)
{
    public static readonly PowerAlerts Default = new(new[] { 20, 10 }, null);
}

/// <summary>
/// Time to full (F47), shown only when it can be trusted: the charge rate must have held steady,
/// within a fifth of its average, for two minutes. Until then there is no estimate, never a guess.
/// </summary>
public sealed class ChargeEstimator
{
    public const double SteadySeconds = 120;
    readonly List<(double T, double Rate, double Remaining, double Full)> _readings = new();

    public void Reset() => _readings.Clear();

    public void Add(double t, PowerSnapshot snapshot)
    {
        if (!snapshot.Charging || snapshot.ChargeRateMilliwatts is not > 0 ||
            snapshot.RemainingMilliwattHours is not { } remaining || snapshot.FullMilliwattHours is not > 0)
        {
            Reset();
            return;
        }
        _readings.Add((t, snapshot.ChargeRateMilliwatts.Value, remaining, snapshot.FullMilliwattHours.Value));
        while (_readings.Count > 2 && t - _readings[0].T > SteadySeconds * 2) _readings.RemoveAt(0);
    }

    /// <summary>Time to full at <paramref name="now"/>, or null when the readings do not support one yet.</summary>
    public TimeSpan? TimeToFull(double now)
    {
        if (_readings.Count < 3) return null;
        var recent = _readings.Where(r => now - r.T <= SteadySeconds * 1.25).ToList();
        if (recent.Count < 3 || recent[^1].T - recent[0].T < SteadySeconds * 0.98) return null;
        double mean = recent.Average(r => r.Rate);
        if (recent.Any(r => Math.Abs(r.Rate - mean) > mean * 0.2)) return null;
        var last = recent[^1];
        double hours = Math.Max(0, last.Full - last.Remaining) / mean;
        // Lithium cells slow down near full; the last tenth takes longer than the rate suggests.
        double taper = last.Remaining / last.Full > 0.8 ? 1.35 : 1.15;
        return TimeSpan.FromHours(hours * taper);
    }
}

/// <summary>
/// Turns changes in power into activities (F47). Plugging in or out shows a capsule; crossing one of
/// your alert levels on the way down shows a timely alert; reaching your "full" level shows a note.
/// </summary>
public static class PowerRules
{
    public const string ChargingKey = "power.charging", AlertKey = "power.alert";

    public static IEnumerable<Activity> Changes(PowerSnapshot? before, PowerSnapshot after, PowerAlerts alerts, TimeSpan? timeToFull)
    {
        if (after.Percent is not { } percent) yield break;   // no battery: nothing to say

        if (before != null && before.PluggedIn != after.PluggedIn)
        {
            yield return after.PluggedIn
                ? Activity.Event(ChargingKey, "power", Priority.Ambient, "Charging", $"{percent}%", Glyphs.BatteryCharging(percent),
                                 $"Charging, {percent} percent" + (timeToFull is { } t ? $", full in {Describe(t)}" : ""), Accent.Done)
                : Activity.Event(ChargingKey, "power", Priority.Ambient, "On battery", $"{percent}%", Glyphs.Battery(percent),
                                 $"On battery, {percent} percent");
        }

        if (before?.Percent is { } was && !after.PluggedIn)
        {
            foreach (int level in alerts.LowLevels.OrderBy(l => l))
            {
                if (was > level && percent <= level)
                {
                    yield return Activity.Event(AlertKey, "power", Priority.Timely, "Battery low", $"{percent}%", Glyphs.Battery(percent),
                                                $"Battery low, {percent} percent", Accent.Error);
                    break;
                }
            }
        }

        if (alerts.FullLevel is { } full && before?.Percent is { } prior && after.PluggedIn && prior < full && percent >= full)
            yield return Activity.Event(AlertKey, "power", Priority.Ambient, "Charged", $"{percent}%", Glyphs.BatteryCharging(percent),
                                        $"Charged to {percent} percent", Accent.Done);
    }

    public static string Describe(TimeSpan t) =>
        t.TotalMinutes < 1 ? "under a minute"
        : t.TotalHours < 1 ? $"{(int)Math.Round(t.TotalMinutes)} min"
        : $"{(int)t.TotalHours} h {t.Minutes} min";
}

/// <summary>Segoe Fluent Icons code points used by the island.</summary>
public static class Glyphs
{
    public const string Music = "", Play = "", Pause = "", Next = "", Previous = "";
    public const string Bluetooth = "", Headphones = "", Focus = "", Info = "";
    public const string Phone = "", Keyboard = "", Speakers = "";

    // Battery0 … Battery9 are E850 … E859 and Battery10 is E83F; BatteryCharging0 … 8 are E85A … E862
    // and the fullest charging glyph is E83E (Microsoft's icon tables; checked by eye in the demo pictures).
    public static string Battery(int percent)
    {
        int level = Math.Clamp((percent + 5) / 10, 0, 10);
        return ((char)(level == 10 ? 0xE83F : 0xE850 + level)).ToString();
    }

    public static string BatteryCharging(int percent)
    {
        int level = Math.Clamp((percent + 5) / 10, 0, 10);
        return ((char)(level >= 9 ? 0xE83E : 0xE85A + level)).ToString();
    }
}
