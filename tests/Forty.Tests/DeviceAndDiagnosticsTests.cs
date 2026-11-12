using Forty.Core;

namespace Forty.Tests;

public static class DeviceTests
{
    static BluetoothState Buds(bool connected, int? battery) => new("c1", "Galaxy Buds", DeviceKind.Audio, connected, battery);

    [Test]
    public static void A_device_seen_for_the_first_time_says_nothing()
    {
        Check.True(DeviceRules.Change(null, Buds(true, 80)) == null, "already connected at start-up: no capsule");
    }

    [Test]
    public static void Connecting_and_disconnecting_each_show_one_capsule()
    {
        var on = DeviceRules.Change(Buds(false, null), Buds(true, 80));
        Check.Equal("Galaxy Buds connected", on?.Title, "connect title");
        Check.Equal("80%", on?.Detail, "battery shown when Windows has it");
        Check.Equal(Glyphs.Headphones, on?.Glyph, "headphones glyph for audio devices");
        var off = DeviceRules.Change(Buds(true, 80), Buds(false, 80));
        Check.Equal("Galaxy Buds disconnected", off?.Title, "disconnect title");
        Check.True(off?.Detail == null, "no battery shown for a device that just left");
    }

    [Test]
    public static void Without_a_reading_there_is_no_battery_never_an_estimate()
    {
        var on = DeviceRules.Change(Buds(false, null), Buds(true, null));
        Check.True(on?.Detail == null, "no percentage");
        Check.True(on?.Narration.Contains("battery") == false, "and Narrator does not mention one");
    }

    [Test]
    public static void A_late_first_reading_updates_the_same_capsule_and_low_battery_is_timely()
    {
        var late = DeviceRules.Change(Buds(true, null), Buds(true, 64));
        Check.Equal(DeviceRules.Key("c1"), late?.Key, "same key, so it updates in place");
        Check.Equal("64%", late?.Detail, "with the reading");
        Check.True(DeviceRules.Change(Buds(true, 64), Buds(true, 63)) == null, "ordinary drops say nothing");
        var low = DeviceRules.Change(Buds(true, 21), Buds(true, 20));
        Check.Equal(Priority.Timely, low?.Priority, "crossing 20% is timely");
        Check.Equal(Accent.Error, low?.Accent, "in red");
    }

    [Test]
    public static void A_phone_syncing_in_the_background_is_never_announced()
    {
        var filter = new DeviceNewsFilter();
        var phone = new BluetoothState("p1", "Test Phone", DeviceKind.Phone, false, null);
        Check.True(!filter.Allows(phone, phone with { Connected = true }, 0), "phone connect is not news");
        Check.True(!filter.Allows(phone with { Connected = true }, phone, 5), "nor its disconnect");
    }

    [Test]
    public static void A_flapping_headset_is_silenced_for_ten_minutes_after_two_changes()
    {
        var filter = new DeviceNewsFilter();
        var off = Buds(false, null);
        var on = Buds(true, null);
        Check.True(filter.Allows(off, on, 0), "first connect is news");
        Check.True(filter.Allows(on, off, 10), "first disconnect is news");
        Check.True(!filter.Allows(off, on, 20), "a third change within a minute is not");
        Check.True(!filter.Allows(on, off, 300), "and it stays quiet for ten minutes");
        Check.True(filter.Allows(off, on, 700), "after which it is news again");
    }

    [Test]
    public static void Battery_saver_switches_the_whole_app_to_the_saver_policy()
    {
        var saver = new PowerSnapshot(40, false, false, true);
        Check.Equal(PowerPolicy.Saver, PowerPolicy.For(saver, efficiencyMode: false), "battery saver");
        Check.Equal(60, PowerPolicy.For(saver, false).SpeechIdleExitSeconds, "speech process leaves after a minute");
        Check.Equal(PowerPolicy.Saver, PowerPolicy.For(saver with { Saver = false }, efficiencyMode: true), "efficiency mode on battery");
        Check.Equal(PowerPolicy.Full, PowerPolicy.For(saver with { Saver = false, PluggedIn = true }, efficiencyMode: true), "efficiency mode while plugged in is not saver");
    }
}

public static class DiagnosticsTests
{
    [Test]
    public static void The_meter_refuses_names_outside_its_list()
    {
        var meter = new Meter(null);
        bool refused = false;
        try { meter.Record("Priya's meeting", 1); } catch (ArgumentException) { refused = true; }
        Check.True(refused, "free text cannot become a measurement name");
    }

    [Test]
    public static void The_meter_reports_typical_and_slow_values()
    {
        var meter = new Meter(null);
        for (int i = 1; i <= 100; i++) meter.Record("render.ms", i);
        var summary = meter.Summary("render.ms");
        Check.Near(51, summary!.Value.P50, 1, "middle");
        Check.Near(96, summary.Value.P95, 1, "slowest in twenty");
    }

    [Test]
    public static void Meter_files_hold_only_numbers_and_allowed_names()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"forty-meter-{Environment.ProcessId}");
        try
        {
            var meter = new Meter(folder);
            meter.Record("expand.ms", 12.345);
            string text = File.ReadAllText(Directory.GetFiles(folder).Single());
            Check.True(System.Text.RegularExpressions.Regex.IsMatch(text, "^\\{\"t\":\\d+,\"n\":\"expand\\.ms\",\"v\":12\\.345\\}\\n$"), $"exact line shape ({text.Trim()})");
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Test]
    public static void A_crash_report_carries_no_personal_data()
    {
        // Personal data planted where real code would put it: in messages, in an inner exception and in adapter reasons.
        string[] planted = { "priya@example.com", "C:\\Users\\Someone\\Documents\\salary.xlsx", "Quarterly review with Priya", "sk_live_123456" };
        Exception error;
        try
        {
            try { throw new IOException($"Could not open {planted[1]} for {planted[0]}"); }
            catch (IOException inner) { throw new InvalidOperationException($"Meeting '{planted[2]}' failed, key {planted[3]}", inner); }
        }
        catch (InvalidOperationException e) { error = e; }

        var meter = new Meter(null);
        meter.Record("render.ms", 3);
        string report = CrashReport.Build(error, "0.1.0",
            new[] { new AdapterStatus("media", Availability.Limited, $"session for {planted[2]} failed") }, meter);
        foreach (string secret in planted)
            Check.True(!report.Contains(secret, StringComparison.OrdinalIgnoreCase), $"\"{secret}\" is not in the report");
        Check.True(report.Contains("System.InvalidOperationException") && report.Contains("System.IO.IOException"), "exception types are kept");
        Check.True(report.Contains("A_crash_report_carries_no_personal_data"), "method names on the stack are kept");
        Check.True(report.Contains("\"media\": \"Limited\""), "adapter states are kept, their reasons are not");
    }
}
