namespace Forty.Core;

/// <summary>A form of the island body, in device-independent pixels (MOTION.md section 2).</summary>
public readonly record struct BodyShape(string Name, double Width, double Height, double Radius, double Top)
{
    public static readonly BodyShape Sliver = new("sliver", 56, 4, 2, 0);
    public static readonly BodyShape Rest = new("rest", 148, 32, 16, 6);
    public static readonly BodyShape Peek = new("peek", 154, 33.3, 16.65, 6);
    public static readonly BodyShape Panel = new("panel", 420, 200, 32, 6);
    public static readonly BodyShape Full = new("full", 480, 560, 36, 6);

    /// <summary>The same shape with a corner radius of exactly half its height, so it is always a true pill.</summary>
    public BodyShape AsPill() => this with { Radius = Height / 2 };
}

/// <summary>
/// The capsule's size from its content (feature F1). Width is the leading slot, the label, the
/// trailing slot and the padding, held between 200 and 320; height is 36, or 44 when the last input
/// was touch; the corner radius is always half the height.
/// </summary>
public static class CapsuleLayout
{
    public const double MinWidth = 200, MaxWidth = 320;
    public const double Height = 36, TouchHeight = 44;
    public const double LeadingSlot = 24, Gap = 8, Inset = 10;
    public const double SatelliteSize = 36, SatelliteGap = 8;

    /// <param name="labelWidth">The label's natural width at its final size, measured once before the morph.</param>
    /// <param name="trailingWidth">The trailing slot's natural width, or 0 when there is none.</param>
    public static BodyShape Measure(double labelWidth, double trailingWidth, bool touch)
    {
        double natural = Inset + LeadingSlot + Gap + labelWidth + (trailingWidth > 0 ? Gap + trailingWidth : 0) + Inset + 8;   // +4 padding, +4 slack for rounding
        double width = Math.Clamp(Math.Ceiling(natural), MinWidth, MaxWidth);
        double height = touch ? TouchHeight : Height;
        return new BodyShape("capsule", width, height, height / 2, 6);
    }

    /// <summary>How wide the label may be inside a capsule of <paramref name="width"/>; longer labels end in an ellipsis.</summary>
    public static double LabelRoom(double width, double trailingWidth) =>
        Math.Max(0, width - 2 * Inset - 8 - LeadingSlot - Gap - (trailingWidth > 0 ? Gap + trailingWidth : 0));
}
