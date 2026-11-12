using Forty.Core;

namespace Forty.Tests;

public static class QueueTests
{
    static Activity A(string key, Priority priority, bool live = false) => live
        ? Activity.Live(key, "test", priority, key, null, Glyphs.Info, key)
        : Activity.Event(key, "test", priority, key, null, Glyphs.Info, key);

    [Test]
    public static void An_empty_queue_rests()
    {
        var q = new ActivityQueue();
        q.Tick(0);
        Check.True(q.View.Primary == null && q.View.Satellite == null, "nothing to show");
    }

    [Test]
    public static void Higher_priority_takes_the_capsule_and_the_other_becomes_the_satellite()
    {
        var q = new ActivityQueue();
        q.Post(A("song", Priority.Ambient, live: true), 0);
        q.Post(A("approval", Priority.ActNow), 1);
        Check.Equal("approval", q.View.Primary?.Key, "needs-you item in the capsule");
        Check.Equal("song", q.View.Satellite?.Key, "music beside it");
    }

    [Test]
    public static void Posting_the_same_key_updates_in_place()
    {
        var q = new ActivityQueue();
        q.Post(A("media", Priority.Ambient, live: true), 0);
        q.Post(A("media", Priority.Ambient, live: true) with { Title = "Next track" }, 1);
        Check.Equal(1, q.Count, "still one activity");
        Check.Equal("Next track", q.View.Primary?.Title, "showing the new content");
    }

    [Test]
    public static void Good_to_know_leaves_four_seconds_after_it_was_shown()
    {
        var q = new ActivityQueue();
        q.Post(A("device", Priority.Ambient), 0);
        Check.Near(4, q.NextDeadline ?? -1, 1e-9, "deadline is four seconds out");
        q.Tick(3.9);
        Check.Equal("device", q.View.Primary?.Key, "still there at 3.9 s");
        q.Tick(4.0);
        Check.True(q.View.Primary == null, "gone at 4 s");
    }

    [Test]
    public static void A_lifetime_starts_when_shown_not_when_posted()
    {
        var q = new ActivityQueue();
        q.Post(A("first", Priority.Timely), 0);
        q.Post(A("second", Priority.Timely), 0);
        q.Post(A("third", Priority.Timely), 0);
        Check.Equal(1, q.View.Waiting, "third waits");
        q.Tick(12);   // first and second leave
        Check.Equal("third", q.View.Primary?.Key, "third is shown when room appears");
        q.Tick(23.9);
        Check.Equal("third", q.View.Primary?.Key, "and gets its full twelve seconds from then");
        q.Tick(24);
        Check.True(q.View.Primary == null, "then leaves");
    }

    [Test]
    public static void Good_to_know_that_waited_ten_seconds_unseen_is_dropped_as_stale()
    {
        var q = new ActivityQueue();
        q.Post(A("a", Priority.ActNow), 0);
        q.Post(A("b", Priority.ActNow), 0);
        q.Post(A("old news", Priority.Ambient), 0);
        Check.Equal(1, q.View.Waiting, "the ambient item waits behind two approvals");
        q.Tick(10);
        Check.Equal(0, q.Count - 2, "and is dropped after ten seconds unseen");
    }

    [Test]
    public static void What_you_are_pointing_at_is_never_displaced_even_by_something_urgent()
    {
        var q = new ActivityQueue();
        q.Post(A("song", Priority.Ambient, live: true), 0);
        q.Hold("song", 0.5);
        q.Post(A("approval", Priority.ActNow), 1);
        Check.Equal("song", q.View.Primary?.Key, "the capsule under the pointer stays");
        Check.Equal("approval", q.View.Satellite?.Key, "the urgent one waits beside it");
        q.Hold(null, 2);
        Check.Equal("approval", q.View.Primary?.Key, "and takes the capsule when you move away");
    }

    [Test]
    public static void Something_held_past_its_time_leaves_one_second_after_you_let_go()
    {
        var q = new ActivityQueue();
        q.Post(A("device", Priority.Ambient), 0);
        q.Hold("device", 1);
        q.Tick(30);
        Check.Equal("device", q.View.Primary?.Key, "held, so it stays");
        q.Hold(null, 30);
        q.Tick(30.9);
        Check.Equal("device", q.View.Primary?.Key, "a moment of grace after letting go");
        q.Tick(31);
        Check.True(q.View.Primary == null, "then it leaves");
    }

    [Test]
    public static void Among_equals_the_newest_good_to_know_wins_but_approvals_keep_their_order()
    {
        var q = new ActivityQueue();
        q.Post(A("track 1", Priority.Ambient), 0);
        q.Post(A("track 2", Priority.Ambient), 1);
        Check.Equal("track 2", q.View.Primary?.Key, "newest news wins");
        var r = new ActivityQueue();
        r.Post(A("approve 1", Priority.ActNow), 0);
        r.Post(A("approve 2", Priority.ActNow), 1);
        Check.Equal("approve 1", r.View.Primary?.Key, "first approval stays in the capsule");
    }

    [Test]
    public static void A_burst_never_shows_more_than_two_and_never_buries_something_that_needs_you()
    {
        var q = new ActivityQueue();
        var random = new Random(11);
        var urgent = new HashSet<string>();   // act-now items never expire, so every one posted is still present
        double now = 0;
        int changes = 0;
        q.Changed += (_, _) => changes++;
        for (int i = 0; i < 20; i++)
        {
            now += random.NextDouble() * 0.4;
            var priority = (Priority)random.Next(0, 4);
            q.Post(A($"item {i}", priority, live: random.Next(3) == 0), now);
            if (priority == Priority.ActNow) urgent.Add($"item {i}");

            var v = q.View;
            var visible = new[] { v.Primary, v.Satellite }.Where(a => a != null).Select(a => a!).ToList();
            Check.True(visible.Count <= 2, $"at most two visible (step {i})");
            bool urgentHidden = urgent.Any(key => visible.All(a => a.Key != key));
            bool lessUrgentShown = visible.Any(a => a.Priority != Priority.ActNow);
            Check.True(!(urgentHidden && lessUrgentShown), $"no approval waits while something less urgent is shown (step {i})");
            Check.True(v.Waiting >= 0 && v.Waiting <= q.Count - visible.Count, $"waiting count within what is present (step {i})");
            Check.True(v.Waiting == 0 || v.Satellite != null, $"nothing waits while the satellite slot is free (step {i})");
        }
        Check.True(changes > 0 && changes <= 20, $"at most one change event per post ({changes})");
    }

    [Test]
    public static void Routine_items_never_show_and_go_to_the_brief()
    {
        var q = new ActivityQueue();
        q.Post(A("newsletter", Priority.Digest), 0);
        Check.True(q.View.Primary == null, "not shown");
        Check.Equal(1, q.Digest.Count, "kept for the brief");
    }

    [Test]
    public static void Presenting_holds_everything_except_urgent_items_which_show_as_a_dot()
    {
        var q = new ActivityQueue();
        q.SetMode(Mode.Presenting, 0);
        q.Post(A("song", Priority.Ambient, live: true), 0);
        q.Post(A("approval", Priority.ActNow), 0);
        Check.True(q.View.Primary == null, "nothing in the capsule during a presentation");
        Check.Equal("approval", q.View.Dots.SingleOrDefault()?.Key, "only the urgent one, as a dot");
        q.SetMode(Mode.Normal, 5);
        Check.Equal("approval", q.View.Primary?.Key, "everything returns when the presentation ends");
        Check.Equal("song", q.View.Satellite?.Key, "music included");
    }

    [Test]
    public static void Gaming_shows_nothing_at_all()
    {
        var q = new ActivityQueue();
        q.SetMode(Mode.Gaming, 0);
        q.Post(A("approval", Priority.ActNow), 0);
        Check.True(q.View.Primary == null && q.View.Dots.Count == 0, "not even a dot");
    }

    [Test]
    public static void The_rules_read_as_sentences()
    {
        Check.Equal("Things that are good to know show only as a small dot.", ModeRules.Describe(Mode.Focus, Priority.Ambient), "Focus, ambient");
        Check.Equal("Things that need you wait until this mode ends.", ModeRules.Describe(Mode.Gaming, Priority.ActNow), "Gaming, act now");
    }
}
