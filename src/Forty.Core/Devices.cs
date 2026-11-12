namespace Forty.Core;

/// <summary>What kind of device it is, from its Bluetooth class.</summary>
public enum DeviceKind { Audio, Phone, Input, Other }

/// <summary>
/// One Bluetooth device as Windows reports it. <see cref="Battery"/> is null unless Windows has a
/// value from the device itself; nothing is ever estimated (F46, MOTION.md 14).
/// </summary>
/// <param name="Id">Windows' container id: one physical device, even when it has classic and LE parts.</param>
public sealed record BluetoothState(string Id, string Name, DeviceKind Kind, bool Connected, int? Battery);

/// <summary>Turns changes in Bluetooth devices into island activities (F45, F46).</summary>
public static class DeviceRules
{
    public static string Key(string id) => "device." + id;

    /// <summary>
    /// What to post when a device changes. A device seen for the first time (at start-up, or newly
    /// paired) says nothing: only a change of connection is news. A new battery reading on a device
    /// that is already showing updates it in place, through the same key.
    /// </summary>
    public static Activity? Change(BluetoothState? before, BluetoothState after)
    {
        if (before == null) return null;
        string glyph = Glyph(after.Kind);
        if (!before.Connected && after.Connected)
            return Activity.Event(Key(after.Id), "bluetooth", Priority.Ambient, $"{after.Name} connected", Percent(after.Battery), glyph,
                                  $"{after.Name} connected" + (after.Battery is { } b ? $", battery {b} percent" : ""));
        if (before.Connected && !after.Connected)
            return Activity.Event(Key(after.Id), "bluetooth", Priority.Ambient, $"{after.Name} disconnected", null, glyph, $"{after.Name} disconnected");
        if (after.Connected && before.Battery != after.Battery && after.Battery is { } level)
        {
            // A first reading usually arrives a few seconds after connecting; later ones only matter when low.
            if (before.Battery == null)
                return Activity.Event(Key(after.Id), "bluetooth", Priority.Ambient, $"{after.Name} connected", Percent(level), glyph,
                                      $"{after.Name}, battery {level} percent");
            if (level <= 20 && before.Battery > 20)
                return Activity.Event(Key(after.Id) + ".low", "bluetooth", Priority.Timely, $"{after.Name} battery low", Percent(level), glyph,
                                      $"{after.Name} battery low, {level} percent", Accent.Error);
        }
        return null;
    }

    public static string Glyph(DeviceKind kind) => kind switch
    {
        DeviceKind.Audio => Glyphs.Headphones,
        DeviceKind.Phone => Glyphs.Phone,
        DeviceKind.Input => Glyphs.Keyboard,
        _ => Glyphs.Bluetooth,
    };

    static string? Percent(int? level) => level is { } l ? $"{l}%" : null;

    /// <summary>The default output changed (F136): a quiet note saying where sound goes now.</summary>
    public static Activity OutputChanged(string name, bool headphones) =>
        Activity.Event("audio.output", "audio", Priority.Ambient, $"Now using {name}", null,
                       headphones ? Glyphs.Headphones : Glyphs.Speakers, $"Sound now plays through {name}");
}

/// <summary>
/// Keeps device news worth hearing. Found on 12 November 2026: a paired phone connected and
/// disconnected every few seconds for background sync, and every flap became a capsule. So: phones
/// and other devices that aren't headsets, speakers, keyboards or mice are not announced at all, and a
/// device that changes connection more than twice in a minute is silenced for ten minutes.
/// </summary>
public sealed class DeviceNewsFilter
{
    public const double WindowSeconds = 60, SilenceSeconds = 600;
    const int MaxChangesInWindow = 2;

    readonly Dictionary<string, List<double>> _changes = new();
    readonly Dictionary<string, double> _silencedUntil = new();

    /// <summary>Whether a change from <paramref name="before"/> to <paramref name="after"/> at <paramref name="now"/> should reach the island.</summary>
    public bool Allows(BluetoothState? before, BluetoothState after, double now)
    {
        if (before == null) return false;
        bool connectionChange = before.Connected != after.Connected;
        if (connectionChange && after.Kind is not (DeviceKind.Audio or DeviceKind.Input)) return false;
        if (!connectionChange) return !IsSilenced(after.Id, now);   // battery news from a device that is not flapping

        if (!_changes.TryGetValue(after.Id, out var times)) _changes[after.Id] = times = new List<double>();
        times.Add(now);
        times.RemoveAll(t => now - t > WindowSeconds);
        if (times.Count > MaxChangesInWindow) _silencedUntil[after.Id] = now + SilenceSeconds;
        return !IsSilenced(after.Id, now);
    }

    bool IsSilenced(string id, double now) => _silencedUntil.TryGetValue(id, out double until) && now < until;
}

/// <summary>
/// What battery saver and the power mode change in Forty's own behaviour (F127, F128). One table, so
/// every part of the app asks the same question and gets the same answer.
/// </summary>
/// <param name="SpeechIdleExitSeconds">How long the speech process stays loaded with nothing to do.</param>
/// <param name="PreloadModels">Load speech models ahead of time, so the first dictation is fast.</param>
/// <param name="Indexing">Index files for search in the background.</param>
/// <param name="PeriodicFactor">Multiplier on anything that runs periodically.</param>
/// <param name="IdleReactions">Agent marks turn toward the pointer at idle.</param>
/// <param name="Visualiser">The music visualiser may run.</param>
/// <param name="Glass">The Glass look may be used.</param>
public sealed record PowerPolicy(int SpeechIdleExitSeconds, bool PreloadModels, bool Indexing, int PeriodicFactor, bool IdleReactions, bool Visualiser, bool Glass)
{
    public static readonly PowerPolicy Full = new(300, true, true, 1, true, true, true);
    public static readonly PowerPolicy Saver = new(60, false, false, 2, false, false, false);

    /// <summary>Battery saver, or "best power efficiency" while unplugged, means the saver policy.</summary>
    public static PowerPolicy For(PowerSnapshot power, bool efficiencyMode) =>
        power.Saver || (efficiencyMode && !power.PluggedIn) ? Saver : Full;
}
