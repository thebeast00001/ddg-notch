using System.Diagnostics;
using Forty.Platform;

namespace Forty.Tests;

/// <summary>
/// "bt-cost [seconds]": what the Bluetooth adapter costs this process. Watches for the given time and
/// counts watcher events and connection changes, then times battery reads for each connected device.
/// Prints counts and timings only: never a device name or id.
/// </summary>
static class BluetoothCost
{
    public static int Run(double seconds)
    {
        var self = Process.GetCurrentProcess();
        using var bt = new BluetoothAdapter();
        int connects = 0, disconnects = 0, changes = 0;
        bt.Changed += (before, after) =>
        {
            Interlocked.Increment(ref changes);
            if (before is { Connected: false } && after.Connected) Interlocked.Increment(ref connects);
            if (before is { Connected: true } && !after.Connected) Interlocked.Increment(ref disconnects);
        };
        bt.Start();
        Thread.Sleep(3000);
        self.Refresh();
        var cpu0 = self.TotalProcessorTime;
        long events0 = bt.Events, reads0 = bt.Reads;
        int connects0 = connects;
        Thread.Sleep(TimeSpan.FromSeconds(seconds));
        self.Refresh();
        double cpuMs = (self.TotalProcessorTime - cpu0).TotalMilliseconds;
        Console.WriteLine($"watched {seconds:0} s: {bt.Events - events0} watcher events, {connects - connects0} connects, {bt.Reads - reads0} battery reads; " +
                          $"process CPU {cpuMs:0} ms ({cpuMs / seconds / 10:0.000}% of a core)");
        if (Environment.GetEnvironmentVariable("FORTY_BT_COST_TIME_READS") != "1") return 0;

        var devices = bt.Devices;
        Console.WriteLine($"devices: {devices.Count} paired, {devices.Count(d => d.Connected)} connected; kinds of connected: {string.Join(", ", devices.Where(d => d.Connected).Select(d => d.Kind))}");
        // The read is the same search whether or not the device is connected, so every paired device is timed.
        foreach (var device in devices)
        {
            self.Refresh();
            var before = self.TotalProcessorTime;
            var clock = Stopwatch.StartNew();
            for (int i = 0; i < 5; i++) bt.ReadBatteryAsync(device.Id).GetAwaiter().GetResult();
            self.Refresh();
            Console.WriteLine($"  a {(device.Connected ? "connected" : "disconnected")} {device.Kind} device: one battery read costs {(self.TotalProcessorTime - before).TotalMilliseconds / 5:0} ms of CPU, {clock.Elapsed.TotalMilliseconds / 5:0} ms of waiting");
        }
        return 0;
    }
}
