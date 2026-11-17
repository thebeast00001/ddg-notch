using System.Text.RegularExpressions;

namespace Forty.Core;

public enum RequestKind { OpenApp, Media, Tell, Timer, Mirror, Remember, Recall, Forget, Memory, PauseEverything, ResumeEverything, Unknown }

/// <summary>What a line you typed or said asks for, and who it is for.</summary>
/// <param name="Kind">What to do.</param>
/// <param name="Agent">The agent whose job it is (Operator opens apps), or null when nobody's is.</param>
/// <param name="Argument">What it is about (the app's name for OpenApp).</param>
/// <param name="Named">The agent you addressed by name, if any ("Scout, …").</param>
/// <param name="Text">The line as it came.</param>
public sealed record Request(RequestKind Kind, Agent? Agent, string Argument, Agent? Named, string Text);

/// <summary>
/// The quick-command rules (F29, F112). Rules come first: they are instant, need no network, and cover
/// the requests every agent has from day one. An agent you name wins; anything the rules don't know
/// goes to the routing model when the runtime has one, and until then is answered honestly.
/// </summary>
public static class RequestRules
{
    static readonly Regex Polite = new(@"^(?:(?:hey|ok|okay)\s+forty[,\s]+|forty[,\s]+)?(?:please\s+|can you\s+|could you\s+|would you\s+|will you\s+|i want to\s+|i'd like to\s+|let's\s+)*", RegexOptions.IgnoreCase);
    static readonly Regex Trailing = new(@"(?:\s+(?:please|for me|now|thanks|thank you))+$", RegexOptions.IgnoreCase);
    static readonly Regex Open = new(@"^(?:open|launch|start|run|bring up|fire up|show me|switch to|go to)\s+(?<app>.+)$", RegexOptions.IgnoreCase);
    static readonly Regex Pause = new(@"^(?:stop|pause|freeze|halt)\s+(?:everything|it all|all|all agents)$", RegexOptions.IgnoreCase);
    static readonly Regex Resume = new(@"^(?:resume|continue|unpause|carry on)(?:\s+(?:everything|all|all agents))?$", RegexOptions.IgnoreCase);

    // Quick commands that need no model (F29): what's playing, and facts this PC already knows.
    const string Song = @"(?:\s+(?:the|this|my))?(?:\s+(?:music|song|track|playback|audio))";
    static readonly (Regex Pattern, string Action)[] MediaRules =
    {
        (new Regex($"^(?:pause|stop){Song}$", RegexOptions.IgnoreCase), "pause"),
        (new Regex($"^(?:play|resume|unpause|continue){Song}$|^play$", RegexOptions.IgnoreCase), "play"),
        (new Regex($"^(?:next|skip)(?:{Song})?$|^skip (?:this|it)(?: one)?$|^next one$", RegexOptions.IgnoreCase), "next"),
        (new Regex($"^(?:previous|last)(?:{Song})?$|^go back (?:a|one) (?:song|track)$", RegexOptions.IgnoreCase), "previous"),
    };
    static readonly (Regex Pattern, string Fact)[] TellRules =
    {
        (new Regex(@"^(?:what(?:'s| is) the time|what time is it|time)(?: now| right now)?$", RegexOptions.IgnoreCase), "time"),
        (new Regex(@"^(?:what(?:'s| is) (?:the|today's) date|what day is it|what(?:'s| is) today|date)(?: today)?$", RegexOptions.IgnoreCase), "date"),
        (new Regex(@"^(?:how much battery(?: is left| do i have)?|what(?:'s| is) (?:the|my) battery(?: level| at)?|battery)$", RegexOptions.IgnoreCase), "battery"),
    };

    // The timer (Tempo's): any line about a timer or countdown, read for what it asks.
    static readonly Regex TimerWord = new(@"\b(?:timer|countdown|count down)\b", RegexOptions.IgnoreCase);
    static readonly Regex TimerCancel = new(@"^(?:cancel|stop|end|kill|clear|dismiss|delete|remove|turn off|switch off|silence)\b", RegexOptions.IgnoreCase);
    static readonly Regex TimerPause = new(@"^(?:pause|hold|freeze)\b", RegexOptions.IgnoreCase);
    static readonly Regex TimerResume = new(@"^(?:resume|continue|unpause|restart)\b", RegexOptions.IgnoreCase);
    static readonly Regex TimerLeft = new(@"\b(?:how (?:much|long)|left|remaining|to go)\b", RegexOptions.IgnoreCase);
    static readonly Regex TimerOpen = new(@"^(?:(?:open|show|show me|set|start|bring up|new|make)\s+)?(?:(?:a|an|the|my)\s+)?(?:timer|countdown)$", RegexOptions.IgnoreCase);
    static readonly Regex TimeLeft = new(@"^(?:how (?:much time|long)(?: is)? (?:left|remaining|to go)|time (?:left|remaining))$", RegexOptions.IgnoreCase);

    // The mirror: the camera as a mirror, before a call.
    static readonly Regex MirrorOpen = new(@"^(?:(?:open|show|show me|turn on|start|bring up)\s+)?(?:(?:the|my|a)\s+)?mirror$|^how do i look$|^(?:show me|check|let me see|let me check)\s+(?:my face|myself|my hair|how i look)$|^am i camera ready$", RegexOptions.IgnoreCase);
    static readonly Regex MirrorClose = new(@"^(?:close|hide|turn off|stop|put away)\s+(?:(?:the|my)\s+)?mirror$|^mirror off$", RegexOptions.IgnoreCase);

    // Memory (Iris's): what you ask to be remembered, looked up or forgotten, and the memory page.
    static readonly Regex Reminder = new(@"^(?:remember|don't forget|do not forget)\s+to\b", RegexOptions.IgnoreCase);
    static readonly Regex RememberRule = new(@"^(?:remember|note|keep in mind)(?:\s+that)?\s+(?<what>.+)$", RegexOptions.IgnoreCase);
    static readonly Regex ForToday = new(@"[,\s]+(?:just\s+)?(?:for today|for now|until tomorrow)$", RegexOptions.IgnoreCase);
    static readonly Regex MemoryPage = new(@"^(?:what do you (?:know|remember) about me|(?:show|open)(?: me)?(?: my| your| the)? memor(?:y|ies)|my memor(?:y|ies)|memory)$", RegexOptions.IgnoreCase);
    static readonly Regex RecallRule = new(@"^(?:(?:what|who|when|where)(?:'s| is| are| was)\s+(?<what>my\s+.+)|what do you (?:know|remember) about\s+(?<what>.+)|do you remember\s+(?<what>.+))$", RegexOptions.IgnoreCase);
    static readonly Regex ForgetAll = new(@"^forget\s+(?:everything|all of it|all)(?:\s+(?:about me|you know|you remember))?$", RegexOptions.IgnoreCase);
    static readonly Regex ForgetLast = new(@"^forget\s+(?:that|it|this)$", RegexOptions.IgnoreCase);
    static readonly Regex ForgetRule = new(@"^forget\s+(?:that\s+|about\s+)?(?<what>.+)$", RegexOptions.IgnoreCase);

    /// <summary>"today:" before a fact keeps it only for the day (F93).</summary>
    public const string TodayOnly = "today:";
    /// <summary>The forget arguments for "forget that" (the newest memory) and "forget everything".</summary>
    public const string ForgetNewest = "", ForgetEverything = "*";

    static (RequestKind Kind, string Argument)? MemoryAsk(string t)
    {
        if (MemoryPage.IsMatch(t)) return (RequestKind.Memory, "");
        if (ForgetAll.IsMatch(t)) return (RequestKind.Forget, ForgetEverything);
        if (ForgetLast.IsMatch(t)) return (RequestKind.Forget, ForgetNewest);
        if (ForgetRule.Match(t) is { Success: true } forget) return (RequestKind.Forget, forget.Groups["what"].Value.Trim());
        if (!Reminder.IsMatch(t) && RememberRule.Match(t) is { Success: true } remember)
        {
            string what = remember.Groups["what"].Value.Trim();
            bool today = ForToday.IsMatch(what);
            what = ForToday.Replace(what, "").Trim();
            return what.Length == 0 ? null : (RequestKind.Remember, (today ? TodayOnly : "") + what);
        }
        if (RecallRule.Match(t) is { Success: true } recall) return (RequestKind.Recall, recall.Groups["what"].Value.Trim());
        return null;
    }

    /// <summary>The timer command a line asks for (see <see cref="TimerCommand"/>), or null if it isn't about the timer.</summary>
    static string? TimerAsk(string t)
    {
        if (TimeLeft.IsMatch(t)) return TimerCommand.Left;
        if (!TimerWord.IsMatch(t)) return null;
        if (TimerCancel.IsMatch(t)) return TimerCommand.Cancel;
        if (TimerPause.IsMatch(t)) return TimerCommand.Pause;
        if (TimerResume.IsMatch(t)) return TimerCommand.Resume;
        if (Durations.Find(t) is { } d) return TimerCommand.Start(d);
        if (TimerLeft.IsMatch(t)) return TimerCommand.Left;
        if (TimerOpen.IsMatch(t)) return TimerCommand.Open;
        return null;
    }

    public static Request Parse(string text)
    {
        string said = text.Trim();
        string t = said.TrimEnd('.', '!', '?', ' ');

        // An agent addressed by name leads the line: "Operator, open Spotify", "Scout: find the invoice".
        Agent? named = null;
        foreach (var agent in Cast.All)
        {
            // By its name, or by its role ("Operator, …" still reaches Bolt).
            foreach (var name in agent.AlsoCalled.Prepend(agent.Name))
            {
                var m = Regex.Match(t, $@"^(?:hey\s+)?{Regex.Escape(name)}\b[,:]?\s*(?<rest>.*)$", RegexOptions.IgnoreCase);
                if (m.Success) { named = agent; t = m.Groups["rest"].Value; break; }
            }
            if (named != null) break;
        }
        t = Trailing.Replace(Polite.Replace(t, ""), "").Trim();

        if (Pause.IsMatch(t)) return new Request(RequestKind.PauseEverything, null, "", named, said);
        if (Resume.IsMatch(t)) return new Request(RequestKind.ResumeEverything, null, "", named, said);
        // The mirror is Forty's own, whoever is asked; the timer is Tempo's.
        if (MirrorClose.IsMatch(t)) return new Request(RequestKind.Mirror, null, "close", named, said);
        if (MirrorOpen.IsMatch(t)) return new Request(RequestKind.Mirror, null, "open", named, said);
        if (named is null or { Id: "planner" } && TimerAsk(t) is { } timer)
            return new Request(RequestKind.Timer, Cast.Tempo, timer, named, said);
        if (named is null or { Id: "operator" })
            foreach (var (pattern, action) in MediaRules)
                if (pattern.IsMatch(t)) return new Request(RequestKind.Media, Cast.Bolt, action, named, said);
        if (named is null or { Id: "scout" })
            foreach (var (pattern, fact) in TellRules)
                if (pattern.IsMatch(t)) return new Request(RequestKind.Tell, Cast.Iris, fact, named, said);
        if (named is null or { Id: "scout" } && MemoryAsk(t) is { } memory)
            return new Request(memory.Kind, Cast.Iris, memory.Argument, named, said);
        if (Open.Match(t) is { Success: true } open && named is null or { Id: "operator" })
            return new Request(RequestKind.OpenApp, Cast.Bolt, open.Groups["app"].Value.Trim(), named, said);
        return new Request(RequestKind.Unknown, named, t, named, said);
    }
}
