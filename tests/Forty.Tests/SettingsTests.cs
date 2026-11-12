using Forty.Core;

namespace Forty.Tests;

public static class SettingsTests
{
    static string TempFile(string name) => Path.Combine(Path.GetTempPath(), $"forty-settings-{name}-{Environment.ProcessId}.json");

    [Test]
    public static void A_missing_file_means_the_defaults()
    {
        using var settings = new Settings(TempFile("missing"), watch: false);
        Check.True(settings.Alerts.LowLevels.SequenceEqual(new[] { 20, 10 }), "alerts at 20% and 10%");
        Check.True(settings.Alerts.FullLevel == null, "no full-charge note");
        Check.Equal(0, settings.Hidden.Count, "no hidden apps");
    }

    [Test]
    public static void Choices_are_saved_and_survive_a_restart()
    {
        string path = TempFile("save");
        try
        {
            using (var settings = new Settings(path, watch: false))
            {
                settings.ToggleAlert(30);
                settings.ToggleAlert(10);            // off
                settings.SetFullAlert(80);
                settings.ToggleIn(Settings.HiddenApps, "vlc");
                settings.ToggleIn(Settings.QuietDevices, "phone-1");
            }
            using var again = new Settings(path, watch: false);
            Check.True(again.Alerts.LowLevels.SequenceEqual(new[] { 30, 20 }), $"alerts now 30% and 20% ({string.Join(",", again.Alerts.LowLevels)})");
            Check.Equal(80, again.Alerts.FullLevel, "full-charge note at 80%");
            Check.True(again.IsHidden("VLC") && !again.IsHidden("chrome"), "hidden app, any case");
            Check.True(again.IsQuiet("phone-1"), "quiet device");
            again.ToggleIn(Settings.HiddenApps, "vlc");
            Check.True(!again.IsHidden("vlc"), "toggling again removes it");
        }
        finally { File.Delete(path); }
    }

    [Test]
    public static void A_damaged_or_odd_file_falls_back_without_an_error()
    {
        string path = TempFile("damaged");
        try
        {
            File.WriteAllText(path, """{ "battery.alerts": ["x", 15, 150], "battery.full": 20, "hide.apps": [7, " mpv "] """);
            using (var broken = new Settings(path, watch: false))
                Check.True(broken.Alerts.LowLevels.SequenceEqual(new[] { 20, 10 }), "unparseable file: defaults");
            File.WriteAllText(path, """{ "battery.alerts": ["x", 15, 150], "battery.full": 20, "hide.apps": [7, " mpv "] }""");
            using var odd = new Settings(path, watch: false);
            Check.True(odd.Alerts.LowLevels.SequenceEqual(new[] { 15 }), "only sensible levels kept");
            Check.True(odd.Alerts.FullLevel == null, "a full-charge note below 50% is ignored");
            Check.True(odd.IsHidden("mpv") && odd.Hidden.Count == 1, "non-text entries skipped, spaces trimmed");
        }
        finally { File.Delete(path); }
    }

    [Test]
    public static void Alerts_from_settings_drive_the_battery_rules()
    {
        string path = TempFile("alerts");
        try
        {
            using var settings = new Settings(path, watch: false);
            settings.ToggleAlert(30);
            var before = new PowerSnapshot(31, false, false, false);
            Check.Equal(1, PowerRules.Changes(before, before with { Percent = 30 }, settings.Alerts, null).Count(), "the new 30% level fires");
        }
        finally { File.Delete(path); }
    }
}
