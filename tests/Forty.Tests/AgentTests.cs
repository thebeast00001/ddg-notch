using Forty.Core;

namespace Forty.Tests;

public static class AgentTests
{
    [Test]
    public static void A_run_moves_through_its_states_only_when_told()
    {
        var run = new AgentRun();
        Check.Equal(AgentState.Idle, run.StateAt(0), "starts idle");
        run.Listen();
        run.SetLevel(0.6);
        Check.Equal(AgentState.Listening, run.StateAt(1), "listening");
        Check.Equal(0.6, run.Level, "the level follows while listening");
        run.Work(0.25);
        Check.Equal(AgentState.Working, run.StateAt(100), "still working a long time later: no timer moves it on");
        Check.Equal(0.0, run.Level, "the level drops when listening stops");
        run.SetLevel(0.9);
        Check.Equal(0.0, run.Level, "a level means nothing while working");
        run.UseTool("Files");
        Check.Equal("Files", run.Detail, "the tool in use");
        run.Finish(now: 200);
        Check.Equal(AgentState.Done, run.StateAt(201.9), "done for two seconds");
        Check.Equal(AgentState.Idle, run.StateAt(202), "then idle");
    }

    [Test]
    public static void A_paused_run_holds_until_resumed_but_still_fails()
    {
        var run = new AgentRun();
        run.UseTool("Calendar");
        run.Pause();
        Check.Equal(AgentState.Paused, run.StateAt(0), "paused");
        run.Work();
        run.Speak();
        Check.Equal(AgentState.Paused, run.StateAt(0), "nothing moves it on by itself while paused");
        run.Resume();
        Check.Equal(AgentState.UsingTool, run.StateAt(0), "resumes where it was");
        Check.Equal("Calendar", run.Detail, "with the same tool");
        run.Pause();
        run.Fail("the network is down");
        Check.Equal(AgentState.Failed, run.StateAt(0), "a failure still shows while paused");
    }

    [Test]
    public static void Words_are_the_same_for_the_label_and_the_announcement()
    {
        Check.Equal("Scout is using Files", AgentWords.Announcement(Cast.Scout, AgentState.UsingTool, "Files"), "a tool");
        Check.Equal("Scribe needs your approval", AgentWords.Announcement(Cast.Scribe, AgentState.WaitingForApproval, null), "approval");
        Check.Equal("Using Files", AgentWords.Line(AgentState.UsingTool, "Files", null), "the line under the mark");
        Check.Equal("Working · 40%", AgentWords.Line(AgentState.Working, null, 0.4), "known progress");
        Check.True(AgentWords.Assertive(AgentState.NeedsInput) && !AgentWords.Assertive(AgentState.Working), "questions interrupt, work doesn't");
        foreach (AgentState state in Enum.GetValues<AgentState>())
            Check.True(AgentWords.Label(state).Length > 0 && AgentWords.Announcement(Cast.Operator, state, null).StartsWith("Operator", StringComparison.Ordinal), $"{state} has words");
    }

    [Test]
    public static void Scribe_follows_dictation_and_call_notes()
    {
        var roster = new Roster();
        var scribe = roster[Cast.Scribe];
        roster.ApplySpeech("listening", 0);
        roster.ApplySpeech("level 0.42", 0.1);
        Check.Equal(AgentState.Listening, scribe.StateAt(0.1), "listening while the key is held");
        Check.Equal(0.42, scribe.Level, "with the microphone's level");
        roster.ApplySpeech("working", 1);
        Check.Equal(AgentState.Working, scribe.StateAt(1), "writing it down");
        roster.ApplySpeech("done", 2);
        roster.ApplySpeech("idle", 2.5);
        Check.Equal(AgentState.Done, scribe.StateAt(2.5), "an idle after done leaves the check showing");
        Check.Equal(AgentState.Idle, scribe.StateAt(4), "then idle");

        roster.ApplySpeech("recording Teams", 10);
        Check.Equal(AgentState.Listening, scribe.StateAt(10), "taking call notes is listening to the call");
        roster.ApplySpeech("call-failed", 20);
        Check.Equal(AgentState.Failed, scribe.StateAt(20), "failed");
        Check.Equal(AgentState.Idle, roster[Cast.Scout].StateAt(20), "the others are untouched");
    }

    [Test]
    public static void The_cast_has_distinct_hues_and_silhouettes()
    {
        Check.Equal(4, Cast.All.Select(a => a.Hue).Distinct().Count(), "four hues");
        Check.Equal(4, Cast.All.Select(a => a.Silhouette).Distinct().Count(), "four silhouettes");
        Check.True(!Cast.Working.Contains(Cast.Planner), "Planner waits for its job in phase 3");
        // ROADMAP 2.3 asks for 3:1 contrast on black; the relative luminance of each hue is checked here.
        foreach (var agent in Cast.All)
        {
            double L(byte c) { double s = c / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
            double luminance = 0.2126 * L(agent.Hue.R) + 0.7152 * L(agent.Hue.G) + 0.0722 * L(agent.Hue.B);
            Check.True((luminance + 0.05) / 0.05 >= 3, $"{agent.Name} has at least 3:1 contrast on black");
        }
    }
}
