using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace Forty.Tests;

/// <summary>
/// "bt-events [seconds]": how many updates Windows' Bluetooth device watchers deliver while nothing
/// changes, per watcher (classic and LE), and which properties they carry. Read-only: nothing is
/// connected, paired or changed. Device names are not printed.
/// </summary>
static class BluetoothEvents
{
    public static int Run(double seconds, string which = "both")
    {
        string[] properties = { "System.Devices.Aep.IsConnected", "System.Devices.Aep.ContainerId", "System.Devices.Aep.Bluetooth.Cod.Major" };
        var counts = new Dictionary<string, int> { ["classic added"] = 0, ["classic updated"] = 0, ["le added"] = 0, ["le updated"] = 0 };
        var keys = new Dictionary<string, int>();
        var watchers = new List<DeviceWatcher>();
        var self = System.Diagnostics.Process.GetCurrentProcess();
        foreach (var (selector, name) in new[] { (BluetoothDevice.GetDeviceSelectorFromPairingState(true), "classic"), (BluetoothLEDevice.GetDeviceSelectorFromPairingState(true), "le") })
        {
            if (which != "both" && which != name) continue;
            var watcher = DeviceInformation.CreateWatcher(selector, properties, DeviceInformationKind.AssociationEndpoint);
            watcher.Added += (_, _) => { lock (counts) counts[name + " added"]++; };
            watcher.Updated += (_, update) =>
            {
                lock (counts)
                {
                    counts[name + " updated"]++;
                    foreach (var key in update.Properties.Keys) keys[key] = keys.GetValueOrDefault(key) + 1;
                }
            };
            watcher.Start();
            watchers.Add(watcher);
        }
        Thread.Sleep(TimeSpan.FromSeconds(5));   // enumeration done
        self.Refresh();
        var cpu = self.TotalProcessorTime;
        Thread.Sleep(TimeSpan.FromSeconds(seconds));
        self.Refresh();
        Console.WriteLine($"watchers: {which}; this process used {(self.TotalProcessorTime - cpu).TotalMilliseconds / (seconds * 1000) * 100:0.00}% of a core while they ran");
        foreach (var w in watchers) w.Stop();
        lock (counts)
        {
            foreach (var (k, v) in counts) Console.WriteLine($"{k}: {v}");
            foreach (var (k, v) in keys.OrderByDescending(p => p.Value)) Console.WriteLine($"  property in updates: {k} x{v}");
        }
        return 0;
    }
}
