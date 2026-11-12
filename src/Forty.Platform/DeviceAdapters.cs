using Forty.Core;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Media.Devices;

namespace Forty.Platform;

/// <summary>
/// Paired Bluetooth devices, whether each is connected, and its battery when Windows has a reading
/// (F45, F46). Driven by Windows' device watchers; nothing polls except a battery re-read every five
/// minutes while a device is connected. A device's classic and LE parts are one device, grouped by
/// Windows' container id.
/// </summary>
public sealed class BluetoothAdapter() : Adapter("bluetooth")
{
    /// <summary>The device property Windows' own Settings page reads for headset battery (a byte, 0–100).</summary>
    public const string BatteryProperty = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
    const string Connected = "System.Devices.Aep.IsConnected", Container = "System.Devices.Aep.ContainerId", Major = "System.Devices.Aep.Bluetooth.Cod.Major";

    sealed class Endpoint { public string Container = ""; public string Name = ""; public bool Connected; public int Major; public bool Le; }

    readonly object _gate = new();
    readonly Dictionary<string, Endpoint> _endpoints = new();
    readonly Dictionary<string, BluetoothState> _devices = new();
    readonly List<DeviceWatcher> _watchers = new();
    Timer? _reread;

    /// <summary>Raised with the previous state (null if first seen) and the new one.</summary>
    public event Action<BluetoothState?, BluetoothState>? Changed;

    public IReadOnlyList<BluetoothState> Devices { get { lock (_gate) return _devices.Values.ToList(); } }

    protected override void OnStart()
    {
        string[] properties = { Connected, Container, Major };
        foreach (var (selector, le) in new[] { (BluetoothDevice.GetDeviceSelectorFromPairingState(true), false), (BluetoothLEDevice.GetDeviceSelectorFromPairingState(true), true) })
        {
            var watcher = DeviceInformation.CreateWatcher(selector, properties, DeviceInformationKind.AssociationEndpoint);
            watcher.Added += (_, info) => Upsert(info.Id, info.Name, info.Properties, le);
            watcher.Updated += (_, update) => Upsert(update.Id, null, update.Properties, le);
            watcher.Removed += (_, update) => { lock (_gate) _endpoints.Remove(update.Id); };
            watcher.Start();
            _watchers.Add(watcher);
        }
        _reread = new Timer(_ => { foreach (var d in Devices.Where(d => d.Connected)) _ = ReadBatteryAsync(d.Id); }, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    void Upsert(string id, string? name, IReadOnlyDictionary<string, object> properties, bool le)
    {
        BluetoothState? before, after;
        lock (_gate)
        {
            if (!_endpoints.TryGetValue(id, out var endpoint)) _endpoints[id] = endpoint = new Endpoint { Le = le };
            if (!string.IsNullOrEmpty(name)) endpoint.Name = name;
            if (properties.TryGetValue(Container, out var c) && c is Guid g) endpoint.Container = g.ToString();
            if (properties.TryGetValue(Connected, out var on) && on is bool b) endpoint.Connected = b;
            if (properties.TryGetValue(Major, out var m) && m != null) endpoint.Major = Convert.ToInt32(m);
            if (endpoint.Container.Length == 0) return;
            (before, after) = Recompute(endpoint.Container);
        }
        if (after != null && after != before) Changed?.Invoke(before, after);
        // Newly connected, or already connected when first seen: read its battery.
        if (after is { Connected: true } && before?.Connected != true) _ = ReadSoonAfterConnecting(after.Id);
    }

    /// <summary>Folds the endpoints of one container into one device. Caller holds the lock.</summary>
    (BluetoothState? Before, BluetoothState? After) Recompute(string container)
    {
        var parts = _endpoints.Values.Where(e => e.Container == container).ToList();
        _devices.TryGetValue(container, out var before);
        if (parts.Count == 0) return (before, null);
        string name = (parts.FirstOrDefault(e => !e.Le) ?? parts[0]).Name;
        int major = parts.Select(e => e.Major).FirstOrDefault(m => m != 0);
        var kind = major switch { 4 => DeviceKind.Audio, 2 => DeviceKind.Phone, 5 => DeviceKind.Input, _ => DeviceKind.Other };
        bool connected = parts.Any(e => e.Connected);
        // A battery reading belongs to a connection: it is dropped when the device leaves.
        var after = new BluetoothState(container, name, kind, connected, connected ? before?.Battery : null);
        _devices[container] = after;
        return (before, after);
    }

    /// <summary>Headsets usually report their battery a few seconds after connecting, so it is read three times.</summary>
    async Task ReadSoonAfterConnecting(string container)
    {
        foreach (int delay in new[] { 0, 4000, 12000 })
        {
            if (delay > 0) await Task.Delay(delay);
            await ReadBatteryAsync(container);
        }
    }

    /// <summary>
    /// Reads the battery level Windows holds for the device's parts. When parts disagree, the lowest is
    /// used, so the island never overstates what is left.
    /// </summary>
    public async Task<int?> ReadBatteryAsync(string container)
    {
        try
        {
            var parts = await DeviceInformation.FindAllAsync($"System.Devices.ContainerId:=\"{{{container}}}\"", new[] { BatteryProperty }, DeviceInformationKind.Device);
            int? level = null;
            foreach (var part in parts)
                if (part.Properties.TryGetValue(BatteryProperty, out var value) && value != null && Convert.ToInt32(value) is int v and >= 0 and <= 100)
                    level = level is { } l ? Math.Min(l, v) : v;
            BluetoothState? before = null, after = null;
            lock (_gate)
            {
                if (_devices.TryGetValue(container, out before) && before.Connected && before.Battery != level)
                    _devices[container] = after = before with { Battery = level };
            }
            if (after != null) Changed?.Invoke(before, after);
            return level;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException)
        {
            Report(Availability.Limited, "could not read device battery: " + ex.Message);
            return null;
        }
    }

    public override void Dispose()
    {
        _reread?.Dispose();
        foreach (var watcher in _watchers)
            if (watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted) watcher.Stop();
        base.Dispose();
    }
}

/// <summary>
/// Where sound plays and where the microphone listens (F136). Raises an event when the default output
/// changes, for example when headphones are plugged in or a Bluetooth headset connects. The speech
/// process follows the same change to move its capture to the new device.
/// </summary>
public sealed class AudioDeviceAdapter() : Adapter("audio")
{
    /// <summary>The form factor of an audio endpoint; 3, 5 and 6 are headphones, headset and handset.</summary>
    const string FormFactor = "{1da5d803-d492-4edd-8c23-e0c0ffee7f0e} 0";

    string? _outputId;

    /// <summary>Raised with the new output's name and whether it is worn on the head.</summary>
    public event Action<string, bool>? OutputChanged;

    protected override void OnStart()
    {
        _outputId = MediaDevice.GetDefaultAudioRenderId(AudioDeviceRole.Default);
        MediaDevice.DefaultAudioRenderDeviceChanged += async (_, e) =>
        {
            if (e.Role != AudioDeviceRole.Default || e.Id == _outputId) return;
            _outputId = e.Id;
            try
            {
                var device = await DeviceInformation.CreateFromIdAsync(e.Id, new[] { FormFactor });
                bool worn = device.Properties.TryGetValue(FormFactor, out var f) && f != null && Convert.ToInt32(f) is 3 or 5 or 6;
                OutputChanged?.Invoke(Shorten(device.Name), worn);
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException)
            {
                Report(Availability.Limited, "could not read the new output device: " + ex.Message);
            }
        };
    }

    /// <summary>"Headphones (Galaxy Buds3 Pro)" reads better as "Galaxy Buds3 Pro".</summary>
    public static string Shorten(string name)
    {
        int open = name.IndexOf('('), close = name.LastIndexOf(')');
        return open > 0 && close > open + 1 ? name[(open + 1)..close].Trim() : name;
    }
}
