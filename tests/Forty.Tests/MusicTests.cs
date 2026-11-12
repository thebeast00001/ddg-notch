using Forty.Core;

namespace Forty.Tests;

public static class MusicTests
{
    /// <summary>A made-up cover: <paramref name="fill"/> decides each pixel's colour from its position.</summary>
    static byte[] Cover(int size, Func<int, int, Argb> fill)
    {
        var bgra = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                var c = fill(x, y);
                int i = (y * size + x) * 4;
                bgra[i] = c.B; bgra[i + 1] = c.G; bgra[i + 2] = c.R; bgra[i + 3] = 255;
            }
        return bgra;
    }

    static double Hue(Argb c) => c.Hsv().H;
    static bool Near(double hue, double target) => Math.Min(Math.Abs(hue - target), 360 - Math.Abs(hue - target)) < 25;

    [Test]
    public static void A_red_cover_gives_a_dark_red_island_and_red_glow()
    {
        var colours = TrackColours.FromPixels(Cover(24, (_, _) => Argb.Rgb(200, 30, 40)));
        Check.True(Near(Hue(colours.Accents[0]), 356), $"glow is red (hue {Hue(colours.Accents[0]):0})");
        Check.True(Near(Hue(colours.Base), 356), "body is tinted red");
        Check.True(colours.Base.Luminance < 0.08, $"body is dark enough for white text ({colours.Base.Luminance:0.000})");
        Check.Equal(3, colours.Accents.Length, "three colours for the backdrop even from one");
    }

    [Test]
    public static void A_blue_and_orange_cover_gives_both_colours()
    {
        var colours = TrackColours.FromPixels(Cover(24, (x, _) => x < 12 ? Argb.Rgb(30, 80, 220) : Argb.Rgb(240, 140, 20)));
        Check.True(colours.Accents.Any(a => Near(Hue(a), 224)), "blue is in the backdrop");
        Check.True(colours.Accents.Any(a => Near(Hue(a), 33)), "orange is in the backdrop");
    }

    [Test]
    public static void A_vivid_speck_on_a_dull_cover_wins_over_the_dull_colour()
    {
        // Mostly a dull olive, with a quarter of saturated magenta.
        var colours = TrackColours.FromPixels(Cover(24, (x, y) => x < 6 ? Argb.Rgb(220, 20, 200) : Argb.Rgb(110, 110, 90)));
        Check.True(Near(Hue(colours.Accents[0]), 305), $"magenta leads (hue {Hue(colours.Accents[0]):0})");
    }

    [Test]
    public static void A_black_and_white_cover_gives_a_grey_theme()
    {
        var colours = TrackColours.FromPixels(Cover(24, (x, y) => (x + y) % 2 == 0 ? Argb.Rgb(250, 250, 250) : Argb.Rgb(10, 10, 10)));
        Check.True(colours.Accents.All(a => a.Hsv().S < 0.05), "no invented colour");
        Check.True(colours.Base.Luminance < 0.01, "near-black body");
    }

    [Test]
    public static void Lyrics_are_read_in_time_order_with_offsets_and_repeated_stamps()
    {
        // Invented lines; no real song lyrics are kept in the repository.
        var lines = Lrc.Parse("""
            [ar:Test Artist]
            [offset:+500]
            [00:12.50]First made-up line
            [00:05.00][00:30.00]A chorus that repeats
            [00:20.00]
            [01:02.3]Last line
            """);
        Check.Equal(5, lines.Count, "five timed lines, metadata skipped");
        Check.Equal(TimeSpan.FromMilliseconds(4500), lines[0].At, "offset applied (sooner by 0.5 s)");
        Check.Equal("A chorus that repeats", lines[0].Text, "sorted by time");
        Check.True(lines[2].IsInstrumental, "an empty line is an instrumental stretch");
        Check.Equal(TimeSpan.FromMilliseconds(61800), lines[4].At, "one-digit fraction means tenths");
    }

    [Test]
    public static void The_current_line_is_the_last_one_that_has_started()
    {
        var lines = Lrc.Parse("[00:01.00]a\n[00:03.00]b\n[00:06.00]c");
        Check.Equal(-1, Lrc.IndexAt(lines, TimeSpan.FromSeconds(0.5)), "before the first line");
        Check.Equal(0, Lrc.IndexAt(lines, TimeSpan.FromSeconds(1)), "exactly at a line");
        Check.Equal(1, Lrc.IndexAt(lines, TimeSpan.FromSeconds(5.9)), "between lines");
        Check.Equal(2, Lrc.IndexAt(lines, TimeSpan.FromMinutes(9)), "after the last");
        Check.Equal(-1, Lrc.IndexAt(Lrc.Parse(null), TimeSpan.Zero), "no lyrics");
    }

    [Test]
    public static void The_playback_clock_moves_while_playing_and_stops_at_the_end()
    {
        var reported = new DateTimeOffset(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);
        var clock = new PlaybackClock(TimeSpan.FromSeconds(30), reported, true, TimeSpan.FromSeconds(200));
        Check.Equal(TimeSpan.FromSeconds(42), clock.At(reported.AddSeconds(12)), "twelve seconds later");
        Check.Equal(TimeSpan.FromSeconds(200), clock.At(reported.AddMinutes(10)), "never past the end");
        Check.Equal(TimeSpan.FromSeconds(30), (clock with { Playing = false }).At(reported.AddSeconds(12)), "paused stays put");
    }

    [Test]
    public static void A_toggle_writes_one_flag_and_keeps_the_others()
    {
        string path = Path.Combine(Path.GetTempPath(), $"forty-flags-set-{Environment.ProcessId}.json");
        File.WriteAllText(path, """{ "bluetooth": "on", "glass": "off" }""");
        try
        {
            using (var flags = new FeatureFlags(path, developer: false, watch: false)) flags.Set("lyrics", FlagState.On);
            using var again = new FeatureFlags(path, developer: false, watch: false);
            Check.True(again.IsOn("lyrics") && again.IsOn("bluetooth") && !again.IsOn("glass"), "new flag on, others unchanged");
        }
        finally { File.Delete(path); }
    }
}
