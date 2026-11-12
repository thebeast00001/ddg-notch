using System.Text.Json;

namespace Forty.Core;

/// <summary>What Windows says about the screen and the PC that changes which mode Forty is in.</summary>
/// <param name="FullScreenApp">The window in front covers its whole monitor.</param>
/// <param name="Presentation">Windows reports presentation mode (a slideshow, or "presenting" set by the user).</param>
/// <param name="ExclusiveGame">Windows reports a Direct3D full-screen app, or its effective power mode is game mode.</param>
/// <param name="WindowsFocus">Windows Focus (do not disturb) is on.</param>
/// <param name="LowBatteryUnplugged">Below 15% and not plugged in.</param>
public sealed record SystemState(bool FullScreenApp, bool Presentation, bool ExclusiveGame, bool WindowsFocus, bool LowBatteryUnplugged)
{
    public static readonly SystemState Calm = new(false, false, false, false, false);
}

/// <summary>
/// Works out the mode from the system state and your own choice (F132–F134). The strongest signal
/// wins: a game, then a presentation, then a mode you set, then Windows Focus or a low battery.
/// </summary>
public static class ModeResolver
{
    public const int LowBatteryPercent = 15;

    public static Mode Resolve(SystemState state, Mode? chosen)
    {
        if (state.ExclusiveGame) return Mode.Gaming;
        if (state.Presentation) return Mode.Presenting;
        if (chosen is { } mine && mine != Mode.Normal) return mine;
        if (state.WindowsFocus || state.LowBatteryUnplugged) return Mode.Focus;
        return Mode.Normal;
    }
}

/// <summary>Whether a Windows source can be used (F138).</summary>
public enum Availability { Available, Limited, Unavailable }

/// <summary>An adapter's health, shown on the Diagnostics page and used to hide what cannot work.</summary>
public sealed record AdapterStatus(string Name, Availability Availability, string? Reason = null)
{
    public static AdapterStatus Ok(string name) => new(name, Availability.Available);
}

/// <summary>
/// Breaks a source on purpose for tests: FORTY_FAULT=media-down,power-down makes those adapters
/// report themselves unavailable, so the app can be checked with everything failing at once.
/// </summary>
public static class Faults
{
    static readonly HashSet<string> Active = Parse(Environment.GetEnvironmentVariable("FORTY_FAULT"));

    public static HashSet<string> Parse(string? value) => new(
        (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        StringComparer.OrdinalIgnoreCase);

    public static bool IsDown(string adapter) => Active.Contains(adapter + "-down") || Active.Contains("all-down");
}

/// <summary>A feature flag's value (F149).</summary>
public enum FlagState { Off, You, On }

/// <summary>
/// Feature flags: every feature sits behind a named flag that is off, on for you (the developer
/// build), or on. Kept in a small JSON file; editing the file applies at once, with no restart.
/// Unknown flags are off, so unfinished work ships switched off.
/// </summary>
public sealed class FeatureFlags : IDisposable
{
    readonly string _path;
    readonly bool _developer;
    FileSystemWatcher? _watcher;
    Dictionary<string, FlagState> _values = new(StringComparer.OrdinalIgnoreCase);

    public event Action? Changed;

    /// <param name="developer">True for your own builds: flags set to "you" count as on.</param>
    public FeatureFlags(string path, bool developer, bool watch = true)
    {
        _path = path;
        _developer = developer;
        Load();
        if (watch && Path.GetDirectoryName(Path.GetFullPath(path)) is { } folder && Directory.Exists(folder))
        {
            _watcher = new FileSystemWatcher(folder, Path.GetFileName(path)) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName };
            _watcher.Changed += (_, _) => Reload();
            _watcher.Created += (_, _) => Reload();
            _watcher.Renamed += (_, _) => Reload();
            _watcher.EnableRaisingEvents = true;
        }
    }

    public bool IsOn(string flag) => _values.TryGetValue(flag, out var state) && (state == FlagState.On || (state == FlagState.You && _developer));

    public FlagState Get(string flag) => _values.TryGetValue(flag, out var state) ? state : FlagState.Off;

    /// <summary>Whether the file mentions the flag at all (so a sub-feature can default to on until you turn it off).</summary>
    public bool Has(string flag) => _values.ContainsKey(flag);

    /// <summary>
    /// Changes one flag and writes the file, keeping every other flag as it is (the toggles in the
    /// panel use this). Applies at once; the file watcher then sees no difference and stays quiet.
    /// </summary>
    public void Set(string flag, FlagState state)
    {
        var values = new Dictionary<string, FlagState>(_values, StringComparer.OrdinalIgnoreCase) { [flag] = state };
        var json = new System.Text.StringBuilder("{\n");
        int i = 0;
        foreach (var (name, value) in values.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            json.Append($"  \"{JsonEncodedText.Encode(name)}\": \"{value.ToString().ToLowerInvariant()}\"{(++i < values.Count ? "," : "")}\n");
        json.Append("}\n");
        _values = values;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            File.WriteAllText(_path, json.ToString());
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        Changed?.Invoke();
    }

    public void Reload()
    {
        var before = _values;
        Load();
        if (!before.OrderBy(p => p.Key).SequenceEqual(_values.OrderBy(p => p.Key))) Changed?.Invoke();
    }

    void Load()
    {
        var values = new Dictionary<string, FlagState>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(_path))
            {
                // Read without locking: your editor may be saving the file at this very moment, and
                // must never fail because Forty is reading it.
                using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var json = JsonDocument.Parse(stream);
                foreach (var property in json.RootElement.EnumerateObject())
                    if (property.Value.ValueKind == JsonValueKind.String && Enum.TryParse<FlagState>(property.Value.GetString(), true, out var state))
                        values[property.Name] = state;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return;   // a half-written or locked file: keep the last good values
        }
        _values = values;
    }

    public void Dispose() => _watcher?.Dispose();
}
