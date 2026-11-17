using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Forty.Core;

// ── The model, as the brain sees it (F140: agents never name a provider) ──

public sealed record ChatMessage(string Role, string Content);

/// <summary>What one call asks for beyond its messages.</summary>
public sealed record ChatOptions(int MaxTokens = 260, bool Json = true, double TimeoutSeconds = 9);

/// <summary>A model's reply, how long it took, and who gave it (a provider's name, never a key).</summary>
public sealed record ChatReply(string Text, double Seconds, string Provider);

public enum ModelTrouble { Unreachable, TooSlow, Refused, Busy, Unreadable }

/// <summary>A model call failed, in one of the ways the island can explain in a sentence.</summary>
public sealed class ModelException(ModelTrouble trouble, string provider) : Exception($"{provider}: {trouble}")
{
    public ModelTrouble Trouble { get; } = trouble;
    public string Provider { get; } = provider;
}

/// <summary>
/// Any model that speaks the chat-completions format: Sarvam today, a local runtime or another
/// service tomorrow. Changing the provider never changes an agent's name, face or history.
/// </summary>
public interface IChatModel
{
    /// <summary>The provider in words, for the run log and the "where did this go" line: "Sarvam".</summary>
    string Name { get; }
    /// <summary>True when the model runs on this PC, so nothing leaves it.</summary>
    bool OnThisPc { get; }
    Task<ChatReply> CompleteAsync(IReadOnlyList<ChatMessage> messages, ChatOptions options, CancellationToken cancel);
}

// ── Skills (the start of F21): the only things a request can turn into ──

/// <summary>One thing an agent can do: its id, who does it, what it risks, and how to ask for it.</summary>
public sealed record Skill(string Id, Agent? Agent, Risk Risk, string What, string Args, string[] Examples)
{
    /// <summary>The agent as the model names it: "bolt", "iris", or "none" for skills that are Forty's own.</summary>
    public string AgentId => Agent?.Name.ToLowerInvariant() ?? "none";
}

public static class Skills
{
    public static readonly Skill OpenApp = new("open_app", Cast.Bolt, Risk.Local, "Opens an app on this PC.",
        "{\"app\": \"the app's name as it is usually written, in English letters even when the user spoke another language (\\\"Notepad\\\", not \\\"नोटपैड\\\"), or what it is for\"}",
        new[] { "open Spotify", "I need to edit a photo", "bring up my browser" });
    public static readonly Skill Media = new("media", Cast.Bolt, Risk.Local, "Controls whatever is playing.",
        "{\"action\": \"play\" | \"pause\" | \"next\" | \"previous\"}",
        new[] { "pause the music", "skip this song", "go back a track" });
    public static readonly Skill Tell = new("tell", Cast.Iris, Risk.Read, "Tells a fact this PC knows. The PC supplies the value; you only choose which.",
        "{\"fact\": \"time\" | \"date\" | \"battery\"}",
        new[] { "what time is it", "how much battery is left", "what's today's date" });
    public static readonly Skill Answer = new("answer", Cast.Iris, Risk.Read, "Answers from general knowledge, in at most two short sentences, in the user's language.",
        "{\"text\": \"the answer\"}",
        new[] { "what's a metaball?", "how many feet in a mile", "translate good morning to Hindi" });
    public static readonly Skill Timer = new("timer", Cast.Tempo, Risk.Local, "Sets, shows or controls the island's countdown timer (one at a time, up to 24 hours).",
        "{\"seconds\": whole seconds to count down from} to start one, or {\"action\": \"open\" | \"pause\" | \"resume\" | \"cancel\" | \"left\"} (\"open\" shows the timer to set by hand, \"left\" says how long is left)",
        new[] { "set a timer for 10 minutes", "give me 25 minutes to focus", "how long is left on my timer", "cancel the timer" });
    public static readonly Skill Mirror = new("mirror", null, Risk.Read, "Shows the user's camera as a mirror in the island, to check how they look before a call. Nothing is recorded.",
        "{}", new[] { "how do I look", "show me the mirror", "is my hair ok before the meeting" });
    public static readonly Skill Remember = new("remember", Cast.Iris, Risk.Local, "Remembers something about the user that they ask to be remembered, on this PC only.",
        "{\"text\": \"the fact as one sentence to the user, in the user's language (\\\"You prefer short answers.\\\")\", \"topic\": \"About you\" | \"People\" | \"Preferences\" | \"Work\" | \"Apps\" | \"Other\", \"today_only\": true | false}",
        new[] { "remember that I'm vegetarian", "my manager is Ravi, keep that in mind", "note that I'm working from home today" });
    public static readonly Skill Recall = new("recall", Cast.Iris, Risk.Read, "Looks up something the user asked Forty to remember. The PC finds it; you only say what to look for.",
        "{\"query\": \"a few words to look for\"}",
        new[] { "what's my wife's name", "do you remember who my manager is", "when is my mom's birthday" });
    public static readonly Skill Forget = new("forget", Cast.Iris, Risk.Destructive, "Forgets something the user asked Forty to remember. The user confirms first.",
        "{\"query\": \"a few words naming what to forget\"}",
        new[] { "forget my wife's name", "delete what you know about my manager" });
    public static readonly Skill PauseAll = new("pause_all", null, Risk.Local, "Stops everything Forty is doing.", "{}", new[] { "stop everything" });
    public static readonly Skill ResumeAll = new("resume_all", null, Risk.Local, "Resumes after a pause.", "{}", new[] { "carry on" });
    public static readonly Skill Cannot = new("cannot", null, Risk.Read, "For anything no other skill does.",
        "{\"text\": \"one short sentence, in the user's language, saying what Forty can't do yet\"}",
        new[] { "email my manager", "what's on my screen", "book a cab" });

    public static readonly IReadOnlyList<Skill> All = new[] { OpenApp, Media, Tell, Timer, Mirror, Remember, Recall, Forget, Answer, PauseAll, ResumeAll, Cannot };

    public static Skill? ById(string id) => All.FirstOrDefault(s => s.Id == id);
}

/// <summary>What the brain decided: one skill, its argument, and words to show if the skill is to say something.</summary>
public sealed record Decision(Skill Skill, string Argument, string? Say)
{
    public Agent? Agent => Skill.Agent;
    /// <summary>For "remember": the topic the memory page files it under.</summary>
    public string? Topic { get; init; }
}

/// <summary>
/// The instructions the routing model gets, built from the skills table, so what the model is told
/// and what the island will accept can never drift apart. Nothing about you or this PC is in it: no
/// app list, no window titles, no names. Only the request you typed or said follows it.
/// </summary>
public static class BrainPrompt
{
    public static string System()
    {
        var p = new StringBuilder();
        p.Append("You are the router inside Forty, an assistant that lives at the top of a Windows desktop. ");
        p.Append("Read what the user asked and choose exactly one skill. Reply with one JSON object and nothing else.\n\nSkills:\n");
        foreach (var skill in Skills.All)
        {
            p.Append($"- agent \"{skill.AgentId}\", skill \"{skill.Id}\": {skill.What} args: {skill.Args}. ");
            p.Append("For example: " + string.Join("; ", skill.Examples.Select(e => $"\"{e}\"")) + ".\n");
        }
        p.Append("""

            Rules:
            - Choose the one skill that does what was asked. If none does, use "cannot".
            - "answer" is only for things you know for certain without looking anything up: explanations, definitions, arithmetic, wording, translation. Never guess at facts about the user, their files or today's events.
            - You cannot see the screen, files, email, calendar, the web or the news. If the request needs any of those, use "cannot". For the time, the date or the battery, use "tell".
            - Anything about counting down, a timer, or how long is left on one is "timer". Reminders with a message are not timers yet: use "cannot".
            - You know nothing about the user. A question about the user, their family, friends, work or preferences is "recall", never "answer". Something the user tells you to keep is "remember".
            - The agents are Echo, Iris, Bolt and Tempo. If the user addresses one by name, still choose the skill that does what was asked, with that skill's own agent: the right agent takes the job.
            - Reply in the language the user used. Never output anything except the JSON object.

            Shape of the reply: {"agent": "bolt", "skill": "open_app", "args": {"app": "Spotify"}}
            """);
        return p.ToString();
    }
}

/// <summary>
/// Reads the model's reply and accepts it only if it is exactly one skill from the table with
/// arguments of the right shape. Anything else (another skill, another agent, missing or oversized
/// arguments, no JSON at all) is not a decision, and the island says it didn't understand.
/// </summary>
public static class DecisionParser
{
    public const int MaxAnswer = 420, MaxApp = 60, MaxQuery = 80;
    static readonly string[] Actions = { "play", "pause", "next", "previous" };
    static readonly string[] Facts = { "time", "date", "battery" };

    public static Decision? Parse(string reply)
    {
        // Models sometimes wrap the object in a code fence or a sentence: take the outermost braces.
        int start = reply.IndexOf('{'), end = reply.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        try
        {
            using var json = JsonDocument.Parse(reply[start..(end + 1)]);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (Text(root, "skill") is not { } id || Skills.ById(id.Trim().ToLowerInvariant()) is not { } skill) return null;
            if (Text(root, "agent") is not { } agent || !string.Equals(agent.Trim(), skill.AgentId, StringComparison.OrdinalIgnoreCase)) return null;
            var args = root.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Object ? a : default;

            if (skill == Skills.OpenApp) return Clean(Text(args, "app"), MaxApp) is { } app ? new Decision(skill, app, null) : null;
            if (skill == Skills.Media) return Text(args, "action")?.Trim().ToLowerInvariant() is { } action && Actions.Contains(action) ? new Decision(skill, action, null) : null;
            if (skill == Skills.Tell) return Text(args, "fact")?.Trim().ToLowerInvariant() is { } fact && Facts.Contains(fact) ? new Decision(skill, fact, null) : null;
            if (skill == Skills.Timer) return TimerArgument(args) is { } timer ? new Decision(skill, timer, null) : null;
            if (skill == Skills.Mirror) return new Decision(skill, "open", null);
            if (skill == Skills.Remember)
            {
                if (Clean(Text(args, "text"), MemoryStore.MaxText) is not { } fact) return null;
                bool today = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("today_only", out var t) && t.ValueKind == JsonValueKind.True;
                return new Decision(skill, (today ? RequestRules.TodayOnly : "") + fact, null) { Topic = MemoryTopics.Normalize(Text(args, "topic")) };
            }
            if (skill == Skills.Recall || skill == Skills.Forget) return Clean(Text(args, "query"), MaxQuery) is { } query ? new Decision(skill, query, null) : null;
            if (skill == Skills.Answer || skill == Skills.Cannot) return Clean(Text(args, "text"), MaxAnswer) is { } said ? new Decision(skill, "", said) : null;
            return new Decision(skill, "", null);   // pause_all, resume_all
        }
        catch (JsonException) { return null; }
    }

    /// <summary>A timer's arguments: whole seconds from one to a day (a number, or digits in a string), or one known action.</summary>
    static string? TimerArgument(JsonElement args)
    {
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("seconds", out var s))
        {
            double seconds = s.ValueKind == JsonValueKind.Number && s.TryGetDouble(out double n) ? n
                : s.ValueKind == JsonValueKind.String && double.TryParse(s.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double t) ? t
                : -1;
            return seconds >= 1 && seconds <= CountdownTimer.Longest.TotalSeconds ? TimerCommand.Start(TimeSpan.FromSeconds(Math.Round(seconds))) : null;
        }
        return Text(args, "action")?.Trim().ToLowerInvariant() is { } action && TimerCommand.Actions.Contains(action) ? action : null;
    }

    static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>One line of plain text, trimmed to a length; null if nothing is left.</summary>
    static string? Clean(string? text, int max)
    {
        if (text == null) return null;
        string one = Regex.Replace(text, @"[\u0000-\u001F\u007F]+", " ").Trim();
        if (one.Length == 0) return null;
        return one.Length <= max ? one : one[..(max - 1)].TrimEnd() + "…";
    }
}

/// <summary>What the last request was and what was decided, so "no, I meant Edge" can be understood (the start of F35).</summary>
public sealed record Exchange(string Request, string DecisionJson, double At);

/// <summary>How a request was understood: the decision, or why there isn't one; who understood it and how long it took.</summary>
public sealed record BrainResult(Decision? Decision, string Provider, bool OnThisPc, double Seconds, ModelTrouble? Trouble)
{
    /// <summary>The model's decision written back as the JSON it should have sent, for the next request's context.</summary>
    public string? DecisionJson { get; init; }
}

/// <summary>
/// Understands a request the built-in rules don't cover, by asking the routing model to choose one
/// skill (F29, F112). The model chooses; the island checks the choice against the skills table and
/// then does the work itself, with the same approvals as always. A model can't open, send or change
/// anything by itself.
/// </summary>
public sealed class Brain(IChatModel model)
{
    public const int MaxRequest = 500;
    public const double RememberSeconds = 60;

    public IChatModel Model => model;

    public async Task<BrainResult> DecideAsync(string request, Exchange? previous, double now, CancellationToken cancel)
    {
        var messages = new List<ChatMessage> { new("system", BrainPrompt.System()) };
        if (previous != null && now - previous.At <= RememberSeconds)
        {
            messages.Add(new ChatMessage("user", previous.Request));
            messages.Add(new ChatMessage("assistant", previous.DecisionJson));
        }
        messages.Add(new ChatMessage("user", request.Length <= MaxRequest ? request : request[..MaxRequest]));
        try
        {
            var reply = await model.CompleteAsync(messages, new ChatOptions(), cancel).ConfigureAwait(false);
            var decision = DecisionParser.Parse(reply.Text);
            return new BrainResult(decision, reply.Provider, model.OnThisPc, reply.Seconds, decision == null ? ModelTrouble.Unreadable : null)
            {
                DecisionJson = decision == null ? null : ToJson(decision),
            };
        }
        catch (ModelException ex)
        {
            return new BrainResult(null, ex.Provider, model.OnThisPc, 0, ex.Trouble);
        }
    }

    static string ToJson(Decision d)
    {
        string args = d.Skill == Skills.OpenApp ? $"{{\"app\": {JsonSerializer.Serialize(d.Argument)}}}"
            : d.Skill == Skills.Media ? $"{{\"action\": \"{d.Argument}\"}}"
            : d.Skill == Skills.Tell ? $"{{\"fact\": \"{d.Argument}\"}}"
            : d.Skill == Skills.Timer ? (TimerCommand.Duration(d.Argument) is { } t ? $"{{\"seconds\": {(long)t.TotalSeconds}}}" : $"{{\"action\": \"{d.Argument}\"}}")
            : d.Skill == Skills.Remember ? "{\"text\": \"(kept on this PC)\"}"
            : d.Skill == Skills.Recall || d.Skill == Skills.Forget ? $"{{\"query\": {JsonSerializer.Serialize(d.Argument)}}}"
            : d.Say != null ? $"{{\"text\": {JsonSerializer.Serialize(d.Say)}}}" : "{}";
        return $"{{\"agent\": \"{d.Skill.AgentId}\", \"skill\": \"{d.Skill.Id}\", \"args\": {args}}}";
    }

    /// <summary>Why a request wasn't understood, in a sentence for the island.</summary>
    public static string Explain(ModelTrouble trouble, string provider) => trouble switch
    {
        ModelTrouble.Unreachable => $"I couldn't reach {provider}. Check the internet connection.",
        ModelTrouble.TooSlow => $"{provider} took too long to answer. Try again.",
        ModelTrouble.Refused => $"{provider} didn't accept the key. Check it with setup-keys.",
        ModelTrouble.Busy => $"{provider} is busy right now. Try again in a moment.",
        _ => "I didn't understand that. Try saying it another way.",
    };
}

/// <summary>
/// A request spoken with the dictation key held: starting with "Hey Forty" (or "OK Forty") makes it
/// a request for Forty instead of text to type. Both words are needed, so dictating a sentence that
/// merely starts with "Forty" still types it. The speech process uses the same rule.
/// </summary>
public static class VoiceRequest
{
    static readonly Regex Prefix = new(@"^\s*(?:hey|hi|ok|okay)[\s,]+(?:forty|fourty|40)\b[\s,.:;!?-]*(?<rest>.*)$", RegexOptions.IgnoreCase | RegexOptions.Singleline);

    /// <summary>The request after "Hey Forty", or null if this is ordinary dictation.</summary>
    public static string? FromDictation(string text)
    {
        var m = Prefix.Match(text);
        if (!m.Success) return null;
        string rest = m.Groups["rest"].Value.Trim();
        return rest.Length == 0 ? null : rest;
    }
}
