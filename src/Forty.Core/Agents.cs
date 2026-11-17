using System.Globalization;

namespace Forty.Core;

/// <summary>
/// The ten states every agent can be in (ROADMAP 2.3, MOTION.md 9). The same words are used in the
/// animation, the run log and the screen-reader label, so a mark can never say "done" while work continues.
/// </summary>
public enum AgentState { Idle, Listening, Working, UsingTool, WaitingForApproval, NeedsInput, Done, Paused, Failed, Speaking }

/// <summary>
/// The shape of a character's eyes, which is most of its face: two eyes, in its hue, that blink, look,
/// squint, widen, smile and droop. Each cast member's eyes are their own, so a face is recognisable
/// in greyscale at 16 DIPs.
/// </summary>
public enum Eyes { Round, Tall, Square, Wide }

/// <summary>
/// One agent's identity (F13): its name, its job, its eyes, its hue and its manner while it works.
/// These stay the same across sessions and updates; the name can be changed, the role never changes
/// without you. <see cref="Id"/> is the role and never changes, so history and settings survive a rename.
/// </summary>
public sealed record Agent(string Id, string Name, string Job, Eyes Eyes, Argb Hue, string Manner, string[] AlsoCalled);

/// <summary>
/// The cast. Forty, the island itself, is the host: its white eyes rest on the island when nothing is
/// happening. The agents step in only while they work, each with a face of its own.
/// </summary>
public static class Cast
{
    public static readonly Agent Echo = new("scribe", "Echo", "Words: dictation, calls, notes", Eyes.Round, new Argb(255, 0x6A, 0xA8, 0xFF),
        "Listens with wide round eyes; while writing, its eyes follow the words left to right", new[] { "Scribe" });
    public static readonly Agent Iris = new("scout", "Iris", "Finding: questions, search, files, memory", Eyes.Tall, new Argb(255, 0x45, 0xD6, 0xCF),
        "Tall, curious eyes that look around while searching", new[] { "Scout" });
    public static readonly Agent Bolt = new("operator", "Bolt", "Doing: apps, windows, media, settings", Eyes.Square, new Argb(255, 0xFF, 0x8F, 0xA3),
        "Square, determined eyes that narrow and get to it", new[] { "Operator" });
    public static readonly Agent Tempo = new("planner", "Tempo", "Time: timers, calendar, brief, follow-ups", Eyes.Wide, new Argb(255, 0xB3, 0x94, 0xFF),
        "Calm, wide eyes that drift up while thinking ahead", new[] { "Planner" });

    /// <summary>The agents that have a working job in phase 2 ("no agent without a working job").</summary>
    public static IReadOnlyList<Agent> Working => new[] { Echo, Iris, Bolt, Tempo };

    public static IReadOnlyList<Agent> All => new[] { Echo, Iris, Bolt, Tempo };

    public static Agent? ById(string? id) => All.FirstOrDefault(a => a.Id == id);
}

/// <summary>
/// One agent's current run, as a state machine (F15). Its state comes only from what the run reports
/// (listening, working, using a tool, waiting, speaking, finishing), never from a timer, with one
/// exception the spec asks for: Done shows for two seconds and then reads as Idle.
/// Time is passed in (seconds on any steady clock) so every rule can be tested exactly.
/// </summary>
public sealed class AgentRun
{
    public const double DoneSeconds = 2;

    AgentState _state = AgentState.Idle;
    AgentState _beforePause = AgentState.Working;
    double _doneAt;

    /// <summary>The tool in use (UsingTool), the question asked (NeedsInput), what needs approving (WaitingForApproval) or why it failed (Failed).</summary>
    public string? Detail { get; private set; }

    /// <summary>How far the work is, 0 to 1, when the run knows; null when it doesn't.</summary>
    public double? Progress { get; private set; }

    /// <summary>The input or output level (0 to 1) while listening or speaking.</summary>
    public double Level { get; private set; }

    public event Action? Changed;

    /// <summary>The state at <paramref name="now"/>: Done becomes Idle after two seconds.</summary>
    public AgentState StateAt(double now) => _state == AgentState.Done && now - _doneAt >= DoneSeconds ? AgentState.Idle : _state;

    public void Listen() => Go(AgentState.Listening, null);
    public void Work(double? progress = null) { Progress = progress is { } p ? Math.Clamp(p, 0, 1) : null; Go(AgentState.Working, null); }
    public void UseTool(string tool) => Go(AgentState.UsingTool, tool);
    public void AskApproval(string what) => Go(AgentState.WaitingForApproval, what);
    public void Ask(string question) => Go(AgentState.NeedsInput, question);
    public void Speak() => Go(AgentState.Speaking, null);
    public void Fail(string reason) => Go(AgentState.Failed, reason);

    /// <summary>The work finished: Done for two seconds, then Idle.</summary>
    public void Finish(double now)
    {
        _doneAt = now;
        Progress = null;
        Go(AgentState.Done, null);
    }

    /// <summary>The run ended without anything to show (cancelled, or nothing was said).</summary>
    public void End() { Progress = null; Level = 0; Go(AgentState.Idle, null); }

    /// <summary>
    /// Everything pauses where it is (F125); only Resume, End or Fail moves it on. <paramref name="evenIdle"/>
    /// is the emergency pause: an idle agent shows Paused too, and starts nothing until resumed.
    /// </summary>
    public void Pause(bool evenIdle = false)
    {
        if (_state == AgentState.Paused) return;
        if (_state is AgentState.Idle or AgentState.Done && !evenIdle) return;
        _beforePause = _state is AgentState.Done ? AgentState.Idle : _state;
        Go(AgentState.Paused, Detail, keepWhilePaused: true);
    }

    public bool IsPaused => _state == AgentState.Paused;

    public void Resume()
    {
        if (_state != AgentState.Paused) return;
        Go(_beforePause, Detail, keepWhilePaused: true);
    }

    /// <summary>The live level, while listening or speaking. Ignored otherwise.</summary>
    public void SetLevel(double level)
    {
        if (_state is not (AgentState.Listening or AgentState.Speaking)) return;
        Level = Math.Clamp(level, 0, 1);
    }

    void Go(AgentState state, string? detail, bool keepWhilePaused = false)
    {
        // While paused nothing moves on by itself; a failure or an end still does.
        if (_state == AgentState.Paused && !keepWhilePaused && state is not (AgentState.Failed or AgentState.Idle)) return;
        if (state is not (AgentState.Listening or AgentState.Speaking)) Level = 0;
        bool changed = state != _state || detail != Detail;
        _state = state;
        Detail = detail;
        if (changed) Changed?.Invoke();
    }
}

/// <summary>Words for each state, the same everywhere (F16): the label under a mark, the run log and what Narrator says.</summary>
public static class AgentWords
{
    public static string Label(AgentState state) => state switch
    {
        AgentState.Idle => "Idle",
        AgentState.Listening => "Listening",
        AgentState.Working => "Working",
        AgentState.UsingTool => "Using a tool",
        AgentState.WaitingForApproval => "Needs your approval",
        AgentState.NeedsInput => "Needs an answer",
        AgentState.Done => "Done",
        AgentState.Paused => "Paused",
        AgentState.Failed => "Couldn't finish",
        AgentState.Speaking => "Speaking",
        _ => state.ToString(),
    };

    /// <summary>The short line under a mark: the tool's name while using one, otherwise the state's word.</summary>
    public static string Line(AgentState state, string? detail, double? progress) => state switch
    {
        AgentState.UsingTool when detail is { Length: > 0 } => "Using " + detail,
        AgentState.Working when progress is { } p => $"Working · {(int)Math.Round(p * 100)}%",
        _ => Label(state),
    };

    /// <summary>What a screen reader announces when the state changes ("Scout is using Files").</summary>
    public static string Announcement(Agent agent, AgentState state, string? detail) => state switch
    {
        AgentState.Idle => $"{agent.Name} is idle",
        AgentState.Listening => $"{agent.Name} is listening",
        AgentState.Working => $"{agent.Name} is working",
        AgentState.UsingTool => detail is { Length: > 0 } ? $"{agent.Name} is using {detail}" : $"{agent.Name} is using a tool",
        AgentState.WaitingForApproval => $"{agent.Name} needs your approval" + (detail is { Length: > 0 } ? ": " + detail : ""),
        AgentState.NeedsInput => $"{agent.Name} needs an answer" + (detail is { Length: > 0 } ? ": " + detail : ""),
        AgentState.Done => $"{agent.Name} is done",
        AgentState.Paused => $"{agent.Name} is paused",
        AgentState.Failed => $"{agent.Name} couldn't finish" + (detail is { Length: > 0 } ? ": " + detail : ""),
        AgentState.Speaking => $"{agent.Name} is speaking",
        _ => agent.Name,
    };

    /// <summary>Approvals, questions and failures interrupt a screen reader; everything else waits its turn.</summary>
    public static bool Assertive(AgentState state) => state is AgentState.WaitingForApproval or AgentState.NeedsInput or AgentState.Failed;
}

/// <summary>
/// Every agent's current run. Scribe's comes from the speech process today: dictation and call notes
/// are its jobs, so its mark listens, works and finishes exactly when they do.
/// </summary>
public sealed class Roster
{
    readonly Dictionary<string, AgentRun> _runs = Cast.All.ToDictionary(a => a.Id, _ => new AgentRun());

    public AgentRun this[Agent agent] => _runs[agent.Id];

    /// <summary>Applies one line from the speech process to Scribe's run. Lines that aren't Scribe's change nothing.</summary>
    public void ApplySpeech(string line, double now)
    {
        line = line.Trim();
        int space = line.IndexOf(' ');
        string kind = (space < 0 ? line : line[..space]).ToLowerInvariant();
        string arg = space < 0 ? "" : line[(space + 1)..].Trim();
        var scribe = this[Cast.Echo];
        switch (kind)
        {
            case "listening": scribe.Listen(); break;
            case "recording": scribe.Listen(); break;   // taking call notes: listening to the call
            case "level":
                if (double.TryParse(arg, NumberStyles.Float, CultureInfo.InvariantCulture, out double level)) scribe.SetLevel(level);
                break;
            case "working":
            case "call-working": scribe.Work(); break;
            case "done":
            case "call-done": scribe.Finish(now); break;
            case "failed": scribe.Fail("nothing was typed"); break;
            case "call-failed": scribe.Fail("the call notes could not be written"); break;
            case "idle":
            case "capture-stopped":
            case "bye":
                if (scribe.StateAt(now) is AgentState.Listening or AgentState.Working) scribe.End();
                break;
        }
    }
}
