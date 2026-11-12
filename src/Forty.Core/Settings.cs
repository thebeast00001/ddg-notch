using System.Text.Json;
using System.Text.Json.Nodes;

namespace Forty.Core;

/// <summary>
/// Your choices that are more than on or off (flags cover those): battery alert levels, apps that
/// hide the island, devices that are never announced. Kept in a small JSON file next to the flags,
/// read without locking, reloaded when it changes, and written whole on every change. A missing or
/// damaged file means the defaults; nothing here can stop the app from starting.
/// </summary>
public sealed class Settings : IDisposable
{
    public const string BatteryAlerts = "battery.alerts", BatteryFull = "battery.full", HiddenApps = "hide.apps", QuietDevices = "devices.quiet";

    /// <summary>The levels a battery alert can be set at, for the settings page.</summary>
    public static readonly int[] AlertChoices = { 30, 20, 15, 10, 5 };

    readonly string _path;
    readonly object _gate = new();
    FileSystemWatcher? _watcher;
    JsonObject _values = new();

    public event Action? Changed;

    public Settings(string path, bool watch = true)
    {
        _path = path;
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

    // ── Typed views of the values ──

    /// <summary>Battery alert levels, highest first. Default 20% and 10%.</summary>
    public PowerAlerts Alerts => new(Ints(BatteryAlerts, new[] { 20, 10 }).Where(l => l is > 0 and < 100).Distinct().OrderByDescending(l => l).ToArray(),
                                     Int(BatteryFull) is int full and > 50 and <= 100 ? full : null);

    /// <summary>Apps (program names such as "vlc") in front of which the island hides, except for recording marks.</summary>
    public IReadOnlyList<string> Hidden => Strings(HiddenApps);

    /// <summary>Devices (by Windows' container id) that are never announced.</summary>
    public IReadOnlyList<string> Quiet => Strings(QuietDevices);

    public bool IsHidden(string? program) => program is { Length: > 0 } p && Hidden.Contains(p, StringComparer.OrdinalIgnoreCase);
    public bool IsQuiet(string deviceId) => Quiet.Contains(deviceId, StringComparer.OrdinalIgnoreCase);

    // ── Changes ──

    /// <summary>Turns one alert level on or off.</summary>
    public void ToggleAlert(int level)
    {
        var levels = Alerts.LowLevels.ToList();
        if (!levels.Remove(level)) levels.Add(level);
        Set(BatteryAlerts, new JsonArray(levels.OrderByDescending(l => l).Select(l => (JsonNode)l).ToArray()));
    }

    public void SetFullAlert(int? level) => Set(BatteryFull, level is { } l ? JsonValue.Create(l) : null);

    public void ToggleIn(string listKey, string item)
    {
        var items = Strings(listKey).ToList();
        int at = items.FindIndex(i => string.Equals(i, item, StringComparison.OrdinalIgnoreCase));
        if (at >= 0) items.RemoveAt(at); else items.Add(item);
        Set(listKey, new JsonArray(items.Select(i => (JsonNode)i).ToArray()));
    }

    void Set(string key, JsonNode? value)
    {
        lock (_gate)
        {
            var copy = (JsonObject)_values.DeepClone();
            copy[key] = value;
            _values = copy;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
                File.WriteAllText(_path, _values.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        Changed?.Invoke();
    }

    // ── Reading ──

    int? Int(string key)
    {
        lock (_gate) return _values[key] is JsonValue v && v.TryGetValue(out int i) ? i : null;
    }

    int[] Ints(string key, int[] fallback)
    {
        lock (_gate)
        {
            if (_values[key] is not JsonArray array) return fallback;
            return array.Select(n => n is JsonValue v && v.TryGetValue(out int i) ? i : (int?)null).Where(i => i != null).Select(i => i!.Value).ToArray();
        }
    }

    List<string> Strings(string key)
    {
        lock (_gate)
        {
            if (_values[key] is not JsonArray array) return new List<string>();
            return array.Select(n => n is JsonValue v && v.TryGetValue(out string? s) ? s : null).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim()).ToList();
        }
    }

    public void Reload()
    {
        string before;
        lock (_gate) before = _values.ToJsonString();
        Load();
        string after;
        lock (_gate) after = _values.ToJsonString();
        if (before != after) Changed?.Invoke();
    }

    void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (JsonNode.Parse(stream) is JsonObject values) lock (_gate) _values = values;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException)
        {
            // half-written or damaged: keep what we had
        }
    }

    public void Dispose() => _watcher?.Dispose();
}
