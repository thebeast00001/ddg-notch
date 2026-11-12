using System.Globalization;
using System.Text.RegularExpressions;

namespace Forty.Core;

/// <summary>A colour as 8-bit alpha, red, green and blue.</summary>
public readonly record struct Argb(byte A, byte R, byte G, byte B)
{
    public static Argb Rgb(byte r, byte g, byte b) => new(255, r, g, b);

    /// <summary>Relative luminance, 0 (black) to 1 (white), as WCAG defines it.</summary>
    public double Luminance
    {
        get
        {
            static double Channel(byte c) { double s = c / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
            return 0.2126 * Channel(R) + 0.7152 * Channel(G) + 0.0722 * Channel(B);
        }
    }

    public Argb WithAlpha(byte alpha) => this with { A = alpha };

    /// <summary>Mixes toward black (amount 0) or keeps the colour (amount 1).</summary>
    public Argb Scale(double amount) => new(A, Clamp(R * amount), Clamp(G * amount), Clamp(B * amount));

    static byte Clamp(double v) => (byte)Math.Clamp(Math.Round(v), 0, 255);

    public (double H, double S, double V) Hsv()
    {
        double r = R / 255.0, g = G / 255.0, b = B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = d == 0 ? 0 : max == r ? 60 * (((g - b) / d) % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        return (h < 0 ? h + 360 : h, max == 0 ? 0 : d / max, max);
    }

    public static Argb FromHsv(double h, double s, double v)
    {
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        var (r, g, b) = h switch { < 60 => (c, x, 0d), < 120 => (x, c, 0d), < 180 => (0d, c, x), < 240 => (0d, x, c), < 300 => (x, 0d, c), _ => (c, 0d, x) };
        return Rgb(Clamp((r + m) * 255), Clamp((g + m) * 255), Clamp((b + m) * 255));
    }
}

/// <summary>
/// The colours of a song, taken from its album art the way Apple Music tints its player: a very dark
/// tinted base for the island body, and three glowing accents for the slow fluid backdrop behind
/// the content. White text stays readable on all of them (the base is kept below 8% luminance and a
/// scrim sits between the glow and the text).
/// </summary>
public sealed record TrackColours(Argb Base, Argb[] Accents, Argb Highlight)
{
    /// <summary>The plain Ink look, for when there is no art.</summary>
    public static readonly TrackColours Ink = new(Argb.Rgb(0, 0, 0),
        new[] { Argb.Rgb(40, 40, 46), Argb.Rgb(28, 28, 34), Argb.Rgb(52, 52, 60) }, Argb.Rgb(255, 255, 255));

    /// <summary>
    /// Picks the colours from album art pixels (BGRA, as Windows decodes them; a small thumbnail of
    /// about 24 × 24 is plenty). Vivid colours win over dull ones; a grey cover gives a grey theme.
    /// </summary>
    public static TrackColours FromPixels(ReadOnlySpan<byte> bgra)
    {
        const int Bins = 18;
        var weight = new double[Bins];
        var sumR = new double[Bins];
        var sumG = new double[Bins];
        var sumB = new double[Bins];
        double greyR = 0, greyG = 0, greyB = 0;
        int greys = 0, pixels = bgra.Length / 4;
        if (pixels == 0) return Ink;

        for (int i = 0; i < pixels; i++)
        {
            byte b = bgra[i * 4], g = bgra[i * 4 + 1], r = bgra[i * 4 + 2], a = bgra[i * 4 + 3];
            if (a < 128) continue;
            var (h, s, v) = Argb.Rgb(r, g, b).Hsv();
            greyR += r; greyG += g; greyB += b; greys++;
            if (s < 0.18 || v < 0.12) continue;          // too grey or too dark to carry a mood
            int bin = (int)(h / 360 * Bins) % Bins;
            double w = s * s * Math.Sqrt(v);              // vivid colours count most
            weight[bin] += w;
            sumR[bin] += r * w; sumG[bin] += g * w; sumB[bin] += b * w;
        }
        if (greys == 0) return Ink;

        var ranked = Enumerable.Range(0, Bins).Where(k => weight[k] > 0).OrderByDescending(k => weight[k]).ToList();
        double total = weight.Sum();
        // A cover that is mostly grey (or whose colour is a tiny speck) gets a grey theme.
        if (ranked.Count == 0 || weight[ranked[0]] < total * 0.05 || total / greys < 0.02)
        {
            var mean = Argb.Rgb((byte)(greyR / greys), (byte)(greyG / greys), (byte)(greyB / greys));
            var (_, _, gv) = mean.Hsv();
            var grey = Argb.FromHsv(0, 0, Math.Clamp(gv, 0.35, 0.6));
            return new TrackColours(Argb.Rgb(8, 8, 9), new[] { grey, grey.Scale(0.75), grey.Scale(1.2) }, Argb.Rgb(255, 255, 255));
        }

        // Distinct hues first: skip bins right next to one already taken.
        var picked = new List<int>();
        foreach (int k in ranked)
        {
            if (picked.Any(p => Math.Min(Math.Abs(p - k), Bins - Math.Abs(p - k)) <= 1)) continue;
            picked.Add(k);
            if (picked.Count == 3) break;
        }
        var accents = picked.Select(k => Glow(Argb.Rgb((byte)(sumR[k] / weight[k]), (byte)(sumG[k] / weight[k]), (byte)(sumB[k] / weight[k])))).ToList();
        while (accents.Count < 3)
        {
            // One colour only: vary its brightness and hue a little so the backdrop still moves.
            var (h, s, v) = accents[0].Hsv();
            accents.Add(Argb.FromHsv((h + (accents.Count == 1 ? 18 : 342)) % 360, Math.Min(1, s * 1.05), v * (accents.Count == 1 ? 0.7 : 1.15)));
        }

        var (bh, bs, _) = accents[0].Hsv();
        var baseColour = Argb.FromHsv(bh, Math.Min(0.75, bs), 0.13);   // very dark, clearly tinted
        var (hh, hs, _) = accents[0].Hsv();
        var highlight = Argb.FromHsv(hh, Math.Min(0.55, hs), 1.0);    // for the equaliser and progress
        return new TrackColours(baseColour, accents.ToArray(), highlight);
    }

    /// <summary>A colour made suitable to glow behind white text: vivid, mid-bright, never neon.</summary>
    static Argb Glow(Argb c)
    {
        var (h, s, v) = c.Hsv();
        return Argb.FromHsv(h, Math.Clamp(s * 1.1, 0.35, 0.85), Math.Clamp(v, 0.45, 0.72));
    }
}

/// <summary>One line of synced lyrics.</summary>
public sealed record LyricLine(TimeSpan At, string Text)
{
    /// <summary>An empty line marks an instrumental stretch.</summary>
    public bool IsInstrumental => Text.Length == 0;
}

/// <summary>Reads synced lyrics in the LRC format ("[01:23.45] a line").</summary>
public static partial class Lrc
{
    [GeneratedRegex(@"\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]")]
    private static partial Regex Stamp();

    [GeneratedRegex(@"^\[offset:\s*([+-]?\d+)\s*\]", RegexOptions.IgnoreCase)]
    private static partial Regex Offset();

    public static List<LyricLine> Parse(string? lrc)
    {
        var lines = new List<LyricLine>();
        if (string.IsNullOrWhiteSpace(lrc)) return lines;
        double offsetMs = 0;
        foreach (string raw in lrc.Split('\n'))
        {
            string line = raw.Trim();
            if (Offset().Match(line) is { Success: true } o) { offsetMs = double.Parse(o.Groups[1].Value, CultureInfo.InvariantCulture); continue; }
            var stamps = Stamp().Matches(line);
            if (stamps.Count == 0) continue;   // metadata such as [ar:] or plain text
            string text = line[(stamps[^1].Index + stamps[^1].Length)..].Trim();
            foreach (Match s in stamps)
            {
                string fraction = s.Groups[3].Success ? s.Groups[3].Value : "0";
                double ms = int.Parse(s.Groups[1].Value, CultureInfo.InvariantCulture) * 60_000 +
                            int.Parse(s.Groups[2].Value, CultureInfo.InvariantCulture) * 1000 +
                            int.Parse(fraction, CultureInfo.InvariantCulture) * Math.Pow(10, 3 - fraction.Length);
                // A positive offset means the lyrics should appear sooner.
                lines.Add(new LyricLine(TimeSpan.FromMilliseconds(Math.Max(0, ms - offsetMs)), text));
            }
        }
        lines.Sort((a, b) => a.At.CompareTo(b.At));
        return lines;
    }

    /// <summary>The line being sung at <paramref name="position"/>, or -1 before the first line.</summary>
    public static int IndexAt(IReadOnlyList<LyricLine> lines, TimeSpan position)
    {
        int low = 0, high = lines.Count - 1, found = -1;
        while (low <= high)
        {
            int mid = (low + high) / 2;
            if (lines[mid].At <= position) { found = mid; low = mid + 1; }
            else high = mid - 1;
        }
        return found;
    }
}

/// <summary>
/// Where a song is right now. Windows reports the position only now and then (when you seek, pause or
/// skip), with the time it was true; between reports the position is worked out from the clock.
/// </summary>
public readonly record struct PlaybackClock(TimeSpan Position, DateTimeOffset ReportedAt, bool Playing, TimeSpan Duration, double Rate = 1)
{
    public TimeSpan At(DateTimeOffset now)
    {
        if (!Playing) return Position;
        var elapsed = now - ReportedAt;
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        var position = Position + elapsed * Rate;
        return Duration > TimeSpan.Zero && position > Duration ? Duration : position;
    }
}
