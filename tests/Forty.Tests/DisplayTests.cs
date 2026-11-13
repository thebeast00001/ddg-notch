using Forty.Core;

namespace Forty.Tests;

/// <summary>F7 and F8: which display the island lives on.</summary>
public static class DisplayTests
{
    [Test]
    public static void Following_waits_for_the_new_display_to_hold()
    {
        var f = new DisplayFollower("A");
        Check.True(f.Observe("B", 0) == null, "not at once");
        Check.True(f.Observe("B", 0.2) == null, "not before 300 ms");
        Check.Equal("B", f.Observe("B", 0.31), "after it held");
        Check.Equal("B", f.Current, "the island is there now");
    }

    [Test]
    public static void Dragging_a_window_across_screens_does_not_make_it_chase()
    {
        var f = new DisplayFollower("A");
        for (int i = 0; i < 10; i++) Check.True(f.Observe(i % 2 == 0 ? "B" : "A", i * 0.1) == null, $"flip {i}");
        Check.Equal("A", f.Current, "it stayed");
        Check.True(f.NextCheck == null || f.Observe("A", 5) == null, "coming back cancels the pending move");
    }

    [Test]
    public static void It_never_moves_while_you_are_using_it()
    {
        var f = new DisplayFollower("A");
        f.Observe("B", 0);
        Check.True(f.Observe("B", 1, busy: true) == null, "the panel is open");
        Check.Equal("B", f.Observe("B", 1.5), "moves once you are done");
    }

    [Test]
    public static void Fifty_focus_changes_leave_one_island_on_one_display()
    {
        var f = new DisplayFollower("A");
        var random = new Random(7);
        string[] displays = { "A", "B", "C" };
        double t = 0;
        int moves = 0;
        for (int i = 0; i < 50; i++)
        {
            t += random.NextDouble() * 0.6;
            string active = displays[random.Next(3)];
            if (f.Observe(active, t) is { } moved) { moves++; Check.Equal(moved, f.Current, "the island is where it moved"); }
            if (f.NextCheck is { } due && random.Next(2) == 0 && f.Observe(active, due + 0.01) is { }) moves++;
        }
        Check.True(displays.Contains(f.Current), "always exactly one display");
        Check.True(moves < 50, "fewer moves than focus changes");
    }

    [Test]
    public static void A_removed_display_sends_the_island_somewhere_present()
    {
        var present = new[] { "A", "C" };
        Check.Equal("A", DisplayRules.Choose(false, "A", "C", "B", present), "not following: the main display");
        Check.Equal("C", DisplayRules.Choose(true, "A", "C", "B", present), "following: the display you're using");
        Check.Equal("A", DisplayRules.Choose(true, "A", null, "B", present), "nothing known: the main display");
        Check.Equal("C", DisplayRules.Choose(true, "A", "A", "C", present), "its display is still there: it stays");
    }

    [Test]
    public static void A_taskbar_at_the_top_pushes_the_island_below_it()
    {
        Check.Equal(0, DisplayRules.TopEdge(0, 0), "taskbar at the bottom");
        Check.Equal(48, DisplayRules.TopEdge(0, 48), "taskbar at the top");
        Check.Equal(-1080, DisplayRules.TopEdge(-1080, -1080), "a display above the main one");
    }
}
