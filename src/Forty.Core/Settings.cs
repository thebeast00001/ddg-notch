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
    public const string BatteryAlerts = "battery.alerts", BatteryFull = "battery.full", HiddenApps = "hide.apps", QuietDevices = "devices.quiet",
                        ApprovedApps = "operator.approved", AppAliases = "operator.aliases";

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

    /// <summary>Apps Operator may open without asking (F40), by normalised name.</summary>
    public IReadOnlyList<string> Approved => Strings(ApprovedApps);

    public bool IsApproved(string appName) => Approved.Contains(AppNames.Normalize(appName), StringComparer.OrdinalIgnoreCase);

    /// <summary>Adds an app to Operator's approved list ("Always").</summary>
    public void Approve(string appName)
    {
        if (!IsApproved(appName)) ToggleIn(ApprovedApps, AppNames.Normalize(appName));
    }

    /// <summary>Your own names for apps (F39): "notes" → "Notion".</summary>
    public IReadOnlyDictionary<string, string> Aliases
    {
        get
        {
            lock (_gate)
            {
                var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (_values[AppAliases] is JsonObject map)
                    foreach (var (alias, node) in map)
                        if (node is JsonValue v && v.TryGetValue(out string? target) && !string.IsNullOrWhiteSpace(target)) aliases[alias] = target.Trim();
                return aliases;
            }
        }
    }

    // ── The agents' pages (F18, F21, F23) ──

    public const string SkillsOff = "skills.off", DataOn = "data.on", DataOff = "data.off", Interrupt = "agents.interrupt";

    /// <summary>A skill is on unless you turned it off on its agent's page (F21).</summary>
    public bool SkillOn(string skillId) => !Strings(SkillsOff).Contains(skillId, StringComparer.OrdinalIgnoreCase);

    public void SetSkill(string skillId, bool on) => SetIn(SkillsOff, skillId, !on);

    /// <summary>A data source an agent may use (F18): your choice if you made one, else its default.</summary>
    public bool DataAllowed(DataSource data) =>
        Strings(DataOn).Contains(data.Key, StringComparer.OrdinalIgnoreCase) || (!Strings(DataOff).Contains(data.Key, StringComparer.OrdinalIgnoreCase) && data.OnByDefault);

    public void SetData(DataSource data, bool on)
    {
        SetIn(DataOn, data.Key, on);
        SetIn(DataOff, data.Key, !on);
    }

    /// <summary>How much an agent may interrupt you (F23). Interrupt me unless you chose otherwise.</summary>
    public Interruption InterruptionFor(Agent agent)
    {
        lock (_gate)
            return _values[Interrupt] is JsonObject map && map[agent.Id] is JsonValue v && v.TryGetValue(out string? level)
                ? level switch { "quiet" => Interruption.Quietly, "brief" => Interruption.BriefOnly, _ => Interruption.InterruptMe }
                : Interruption.InterruptMe;
    }

    public void SetInterruption(Agent agent, Interruption level)
    {
        JsonObject map;
        lock (_gate) map = _values[Interrupt] is JsonObject m ? (JsonObject)m.DeepClone() : new JsonObject();
        map[agent.Id] = level switch { Interruption.Quietly => "quiet", Interruption.BriefOnly => "brief", _ => "interrupt" };
        Set(Interrupt, map);
    }

    /// <summary>Puts an item in a list or takes it out, writing only if that changes anything.</summary>
    void SetIn(string listKey, string item, bool present)
    {
        var items = Strings(listKey);
        bool has = items.Contains(item, StringComparer.OrdinalIgnoreCase);
        if (has == present) return;
        if (present) items.Add(item); else items.RemoveAll(i => string.Equals(i, item, StringComparison.OrdinalIgnoreCase));
        Set(listKey, new JsonArray(items.Select(i => (JsonNode)i).ToArray()));
    }

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
