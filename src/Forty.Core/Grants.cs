namespace Forty.Core;

/// <summary>How much an agent may interrupt you (F23).</summary>
public enum Interruption
{
    /// <summary>Its results show in the capsule, and an answer opens the panel to be read.</summary>
    InterruptMe,
    /// <summary>Its results show in the capsule, but never open the panel or speak up.</summary>
    Quietly,
    /// <summary>Its results wait under the ask field and on its page (in the brief, from phase 3); the island stays as it was.</summary>
    BriefOnly,
}

/// <summary>A skill as its agent's page lists it (F21): what it does, its risk, examples of asking, and the data it needs.</summary>
public sealed record SkillInfo(string Id, Agent Agent, string Name, string What, Risk Risk, string[] Examples, string[] Needs);

/// <summary>
/// Every skill each agent has, with a switch each on the agent's page (F21). A skill switched off is
/// refused when it is asked for, with one line saying so and a button to turn it back on: the check is
/// made at call time, not when the page is drawn.
/// </summary>
public static class SkillBook
{
    public static readonly IReadOnlyList<SkillInfo> All = new[]
    {
        new SkillInfo("dictation", Cast.Echo, "Dictation", "Types what you say while you hold Right Ctrl.", Risk.Local, new[] { "hold Right Ctrl and speak" }, new[] { "microphone" }),
        new SkillInfo("call_notes", Cast.Echo, "Call notes", "Offers notes when a call starts, and records only after you accept.", Risk.Local, new[] { "accept the offer when a call starts" }, new[] { "call-audio" }),
        new SkillInfo("tell", Cast.Iris, "Facts from this PC", "The time, the date and the battery.", Risk.Read, new[] { "what time is it", "how much battery is left" }, new[] { "this-pc" }),
        new SkillInfo("answer", Cast.Iris, "Answers", "General knowledge in a sentence or two, from the model.", Risk.External, new[] { "what does HTTP stand for" }, Array.Empty<string>()),
        new SkillInfo("memory", Cast.Iris, "Memory", "Remembers what you ask it to, finds it again, and forgets it.", Risk.Local, new[] { "remember that I prefer short answers", "what's my wife's name", "forget that" }, new[] { "personal-memory" }),
        new SkillInfo("open_app", Cast.Bolt, "Launch apps", "Opens apps from your Start menu; ones you haven't approved ask first.", Risk.Local, new[] { "open Spotify", "something to edit photos" }, new[] { "apps" }),
        new SkillInfo("media", Cast.Bolt, "Music controls", "Plays, pauses and skips what's playing.", Risk.Local, new[] { "pause the music", "skip this song" }, new[] { "playing" }),
        new SkillInfo("timer", Cast.Tempo, "Timer", "One countdown, set on the ruler or by asking.", Risk.Local, new[] { "set a timer for 10 minutes" }, new[] { "clock" }),
    };

    public static SkillInfo? ById(string id) => All.FirstOrDefault(s => s.Id == id);

    public static IReadOnlyList<SkillInfo> Of(Agent agent) => All.Where(s => s.Agent.Id == agent.Id).ToList();

    /// <summary>The switch that governs a brain skill: remembering, recalling and forgetting share Iris's one Memory switch.</summary>
    public static string For(Skill skill) => skill.Id is "remember" or "recall" or "forget" ? "memory" : skill.Id;
}

/// <summary>Something an agent can read or hear (F18), with a switch on its page. <see cref="Key"/> is how it is saved.</summary>
public sealed record DataSource(string Id, Agent Agent, string Name, string What, bool OnByDefault)
{
    public string Key => $"{Agent.Id}:{Id}";
}

/// <summary>
/// The data each agent can use. Your personal memory is off for every agent until you allow it (F17,
/// F92); the rest is what each job needs, and is on. Every source is checked when it is used.
/// </summary>
public static class DataBook
{
    public const string PersonalMemory = "personal-memory";

    public static readonly IReadOnlyList<DataSource> All = new[]
    {
        new DataSource("microphone", Cast.Echo, "Microphone", "Only while you hold the dictation key, or in a call you accepted.", true),
        new DataSource("call-audio", Cast.Echo, "Call audio", "The other side of a call, only after you accept call notes.", true),
        new DataSource("this-pc", Cast.Iris, "This PC's facts", "The time, the date and the battery level.", true),
        new DataSource(PersonalMemory, Cast.Iris, "Your memory", "What you've asked Forty to remember. Needed to answer “what's my…”.", false),
        new DataSource("apps", Cast.Bolt, "Your Start menu", "The names of your apps, to open the one you ask for.", true),
        new DataSource("playing", Cast.Bolt, "What's playing", "The song, and its play, pause and skip controls.", true),
        new DataSource(PersonalMemory, Cast.Bolt, "Your memory", "What you've asked Forty to remember.", false),
        new DataSource("clock", Cast.Tempo, "The clock", "To count down.", true),
        new DataSource(PersonalMemory, Cast.Tempo, "Your memory", "What you've asked Forty to remember.", false),
    };

    public static IReadOnlyList<DataSource> Of(Agent agent) => All.Where(d => d.Agent.Id == agent.Id).ToList();

    public static DataSource? Find(Agent agent, string id) => All.FirstOrDefault(d => d.Agent.Id == agent.Id && d.Id == id);

    public static DataSource? ByKey(string key) => All.FirstOrDefault(d => d.Key == key);
}
