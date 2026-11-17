using Forty.Core;
using Forty.Motion;
using Forty.Platform;

namespace Forty.App;

/// <summary>
/// Memory and the agents' pages (F17, F18, F21, F23, F91–F95, F101).
/// <list type="bullet">
/// <item>Iris keeps what you ask it to remember, finds it again on this PC (no model ever sees a memory), and forgets it when you say.</item>
/// <item>Bolt suggests remembering the app you chose for a kind of app, and opens it next time without asking which.</item>
/// <item>Each skill and each kind of data has a switch on its agent's page, checked when it's used, not when the page is drawn.</item>
/// <item>Each agent may interrupt you, show its results quietly, or keep them for later.</item>
/// </list>
/// </summary>
sealed partial class IslandController
{
    const string MemoryKey = "agents.memory";

    readonly MemoryStore _memory = new(Path.Combine(App.Home, "memory.bin"), Dpapi.Protect, Dpapi.Unprotect);
    string? _settingsPage;            // a page under Settings: "agent:<id>" or "memory"
    int _pageDirection;               // +1 going to a page, -1 coming back
    string _memoryFilter = "all";
    string? _memoryEditing, _memoryConfirm, _memoryForgot;
    Action? _redo;                    // what was asked, to do once you turn the skill or the data back on

    void HookMemory()
    {
        _memory.Changed += () => Ui(() => { if (_settingsOpen) Expand(takeFocus: false); });
    }

    // ── Gates: checked at the moment something is used (F18, F21) ──

    /// <summary>
    /// The skill is on and its agent may use the data it needs; otherwise one line says which is off,
    /// with a button to turn it on that then does what you asked.
    /// </summary>
    bool Gate(string skillId, string? dataId, Action redo)
    {
        var skill = SkillBook.ById(skillId)!;
        if (!_settings.SkillOn(skillId))
        {
            _redo = redo;
            Refuse(skill.Agent, $"{skill.Name} is off", $"{skill.Agent.Name}'s {skill.Name} skill is switched off on its page.", "skill-on:" + skillId);
            return false;
        }
        if (dataId != null && DataBook.Find(skill.Agent, dataId) is { } data && !_settings.DataAllowed(data))
        {
            _redo = redo;
            Refuse(skill.Agent, $"{data.Name} is off", $"{skill.Agent.Name} isn't allowed to use {data.Name.ToLowerInvariant()}. It's switched off on its page.", "data-on:" + data.Key);
            return false;
        }
        return true;
    }

    void Refuse(Agent agent, string title, string words, string turnOn)
    {
        Reply(new Activity(ResultKey, agent.Id, Priority.Timely, title, null, "", words, TimeSpan.FromSeconds(10), Accent.None,
                           Actions: new ActivityActions("Turn on", turnOn, "Not now", "dismiss"), AgentId: agent.Id));
        ShowAnswerPanel(10, agent);
    }

    DataSource EchoMic => DataBook.Find(Cast.Echo, "microphone")!;
    DataSource EchoCalls => DataBook.Find(Cast.Echo, "call-audio")!;
    bool MicAllowed => _settings.SkillOn("dictation") && _settings.DataAllowed(EchoMic);
    bool CallNotesAllowed => _settings.SkillOn("call_notes") && _settings.DataAllowed(EchoCalls);

    /// <summary>The speech process is told whether Echo may use the microphone; with it off, the dictation key does nothing.</summary>
    void EchoGrants()
    {
        if (Speech.IsConnected) Speech.Send(MicAllowed ? "mic on" : "mic off");
    }

    bool _callsOffSaid;

    /// <summary>Lines from the speech process that Echo's page decides about. Returns true when the line is handled here.</summary>
    bool EchoLine(string line)
    {
        if (line == "mic-off")
        {
            Refuse(Cast.Echo, "Dictation is off", "Echo's dictation or its microphone is switched off on its page, so the key did nothing.",
                   _settings.SkillOn("dictation") ? "data-on:" + EchoMic.Key : "skill-on:dictation");
            return true;
        }
        if (line.StartsWith("call-offer", StringComparison.Ordinal) && !CallNotesAllowed)
        {
            // Call notes are off: the offer is declined at once, and nothing is ever recorded.
            Speech.Send("decline");
            if (!_callsOffSaid) Note("Call notes are off", "A call started. Echo's call notes are switched off on its page, so nothing is recorded.", Accent.None, Cast.Echo);
            _callsOffSaid = true;
            return true;
        }
        if (line == "call-gone") _callsOffSaid = false;
        return false;
    }

    /// <summary>F23: an agent set to keep its results for later doesn't put them in the capsule.</summary>
    bool HeldForLater(Activity activity) =>
        activity.Agent is { } agent && activity.Key == ResultKey && activity.Actions == null && activity.Priority != Priority.ActNow &&
        _settings.InterruptionFor(agent) == Interruption.BriefOnly;

    // ── Memory: Iris's skill ──

    /// <summary>Keeps something you asked to be remembered, in your words turned to a sentence about you, with where it came from.</summary>
    void Remember(string argument, string? topic, string text, Via? via)
    {
        if (_paused) { Note("Everything is paused", PausedNote, Accent.NeedsYou); return; }
        if (!Gate("memory", null, () => Remember(argument, topic, text, via))) return;
        bool today = argument.StartsWith(RequestRules.TodayOnly, StringComparison.Ordinal);
        string fact = today ? argument[RequestRules.TodayOnly.Length..] : argument;
        // The model already writes it to you; your own words are turned around here.
        string sentence = via == null ? MemoryText.FromUser(fact) : MemoryText.Sentence(fact);
        var now = DateTimeOffset.Now;
        var item = _memory.Add(sentence, topic ?? MemoryTopics.Guess(sentence), MemoryScopes.Personal,
                               new MemorySource(SourceKind.YouToldMe, text.Trim(), now), "you asked", now, forThisTask: today);
        var iris = _roster[Cast.Iris];
        iris.Work();
        var run = new RunBuilder(Cast.Iris, "Remember something");   // the run log never holds what you asked to keep
        ModelStep(run, via);
        run.Step("Memory", "your memory", Risk.Local, today ? "kept for today" : "remembered", "you asked");
        Finish(run, iris, today ? "Kept something for today" : "Remembered something", failed: false);
        Reply(new Activity(ResultKey, "scout", Priority.Timely, today ? "Kept for today" : "I'll remember that", null, "", item.Text,
                           TimeSpan.FromSeconds(6), Accent.Done, Actions: new ActivityActions("Undo", "mem-undo:" + item.Id), AgentId: Cast.Iris.Id));
    }

    /// <summary>
    /// A question about you: this PC looks in your memory and Iris answers with what it found and where
    /// it came from. Your memory is used only if you allowed Iris to use it.
    /// </summary>
    void Recall(Request request, string query, Via? via, bool fromRules)
    {
        if (_paused) { Note("Everything is paused", PausedNote, Accent.NeedsYou); return; }
        if (!Gate("memory", null, () => Recall(request, query, via, fromRules))) return;
        var personal = DataBook.Find(Cast.Iris, DataBook.PersonalMemory)!;
        if (!_settings.DataAllowed(personal))
        {
            _redo = () => Recall(request, query, via, fromRules);
            Reply(new Activity(ResultKey, "scout", Priority.Timely, "Iris can't use your memory yet", null, "",
                               "Your memory stays private from every agent until you allow it. Allow Iris to use it?", TimeSpan.FromSeconds(12), Accent.None,
                               Actions: new ActivityActions("Allow", "data-on:" + personal.Key, "Not now", "dismiss"), AgentId: Cast.Iris.Id));
            ShowAnswerPanel(12, Cast.Iris);
            return;
        }
        var now = DateTimeOffset.Now;
        var found = _memory.Search(query, new[] { MemoryScopes.Personal, MemoryScopes.Of(Cast.Iris) }, now, 1);
        var iris = _roster[Cast.Iris];
        var run = new RunBuilder(Cast.Iris, "A question about you");
        ModelStep(run, via);
        iris.Work();
        if (found.Count == 0)
        {
            // "What's my IP address" isn't about your memory: the brain looks at it instead.
            if (fromRules && BrainOn && BrainNow != null) { iris.End(); Think(request); return; }
            run.Step("Memory", "your memory", Risk.Read, "nothing found");
            Finish(run, iris, "Didn't know", failed: false);
            Reply(new Activity(ResultKey, "scout", Priority.Timely, "I don't know that yet", null, "",
                               "You haven't told me. Say “remember that …” and I'll keep it on this PC.", TimeSpan.FromSeconds(7), AgentId: Cast.Iris.Id));
            ShowAnswerPanel(7, Cast.Iris);
            return;
        }
        var item = found[0];
        run.Step("Memory", "your memory", Risk.Read, "found");
        Finish(run, iris, "Answered from your memory", failed: false);
        Reply(new Activity(ResultKey, "scout", Priority.Timely, Cast.Iris.Name, item.Source.Describe(now) + " · this PC", "", item.Text,
                           TimeSpan.FromSeconds(9), AgentId: Cast.Iris.Id));
        ShowAnswerPanel(9, Cast.Iris);
    }

    /// <summary>"Forget …": what would go is shown first, with Forget and Keep. "Forget everything" says how much.</summary>
    void ForgetAsk(string query, Via? via)
    {
        if (_paused) { Note("Everything is paused", PausedNote, Accent.NeedsYou); return; }
        if (!Gate("memory", null, () => ForgetAsk(query, via))) return;
        var now = DateTimeOffset.Now;
        var all = _memory.All(now);
        if (all.Count == 0) { Note("Nothing to forget", "I don't remember anything yet.", Accent.None, Cast.Iris); return; }
        if (query == RequestRules.ForgetEverything)
        {
            Reply(new Activity(MemoryKey, "scout", Priority.ActNow, $"Forget all {all.Count}?", null, "",
                               $"Everything I remember, {all.Count} thing{(all.Count == 1 ? "" : "s")}, goes from this PC and can't be brought back.", null, Accent.NeedsYou,
                               Actions: new ActivityActions("Forget all", "mem-forget-all-yes", "Keep", "mem-keep"), AgentId: Cast.Iris.Id));
            return;
        }
        var item = query == RequestRules.ForgetNewest ? all[0] : _memory.Search(query, all.Select(i => i.Scope).Distinct().ToList(), now, 1).FirstOrDefault();
        if (item == null) { Note("Nothing to forget", $"I don't remember anything about “{query}”.", Accent.None, Cast.Iris); return; }
        Reply(new Activity(MemoryKey, "scout", Priority.ActNow, "Forget this?", null, "", item.Text, null, Accent.NeedsYou,
                           Actions: new ActivityActions("Forget", "mem-forget-yes:" + item.Id, "Keep", "mem-keep"), AgentId: Cast.Iris.Id));
    }

    /// <summary>Forgets for good (F101), and says what went. The last request's context is dropped too.</summary>
    void ForgetNow(Func<MemoryItem, bool> which, bool undo = false, bool everything = false)
    {
        var gone = everything ? _memory.Clear(DateTimeOffset.Now) : _memory.Forget(which, DateTimeOffset.Now);
        _lastExchange = null;
        _memoryConfirm = null;
        ClearReply(MemoryKey);
        if (gone.Count == 0) return;
        var run = new RunBuilder(Cast.Iris, "Forget something");
        run.Step("Memory", "your memory", Risk.Destructive, $"forgot {gone.Count}", undo ? "you pressed Undo" : "you said yes");
        _runs.Save(run.Finish(gone.Count == 1 ? "Forgot something" : $"Forgot {gone.Count} things"));
        _memoryForgot = gone.Count == 1 ? $"Forgot “{gone[0].Text}” It's gone from this PC." : $"Forgot {gone.Count} things. They're gone from this PC.";
        if (_settingsOpen) { Expand(takeFocus: false); return; }
        Reply(new Activity(ResultKey, "scout", Priority.Ambient, undo ? "Undone" : "Forgotten", null, "", _memoryForgot, TimeSpan.FromSeconds(3), Accent.Done, AgentId: Cast.Iris.Id));
    }

    // ── Learning by suggestion (F95) ──

    /// <summary>You picked an app for a kind of app ("a browser"): Bolt would like to remember it. Nothing is learned until you say yes.</summary>
    void LearnChoice(InstalledApp app, string? asked)
    {
        if (asked == null || AppMatcher.PurposeOf(asked) is not { } purpose) return;
        var now = DateTimeOffset.Now;
        _memory.Propose($"Your {purpose.Label} is {app.Name}.", MemoryTopics.Apps, MemoryScopes.Of(Cast.Bolt),
                        new MemorySource(SourceKind.YourChoice, $"you chose {app.Name} when you asked for {asked}", now), now, purpose.Key, app.Name);
    }

    /// <summary>The app Bolt remembered for a kind of app, if you said yes to it and it's still installed.</summary>
    InstalledApp? RememberedApp(string asked)
    {
        if (AppMatcher.PurposeOf(asked) is not { } purpose) return null;
        if (_memory.Find(purpose.Key, MemoryScopes.Of(Cast.Bolt), DateTimeOffset.Now) is not { Value: { } name }) return null;
        return _apps.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// One suggestion as a card, when nothing else is asking you something: at most one an hour; the
    /// rest wait on the memory page.
    /// </summary>
    void OfferMemoryCard()
    {
        if (_paused || _pendingOpen != null || _pendingChoice != null || CurrentReply is { Actions: not null }) return;
        if (_memory.TakeCard(DateTimeOffset.Now) is not { } p) return;
        var agent = MemoryScopes.AgentOf(p.Scope);
        Reply(new Activity(MemoryKey, agent?.Id ?? "forty", Priority.Timely, "Remember this?", null, "", p.Text, TimeSpan.FromSeconds(20), Accent.None,
                           Actions: new ActivityActions("Yes", "mem-yes:" + p.Id, "Edit", "mem-edit-p:" + p.Id, "No", "mem-no:" + p.Id), AgentId: agent?.Id));
        // The capsule can't hold the sentence and three answers: the panel opens to show what it would keep,
        // without taking the keyboard, and closes again unless you move into it.
        ShowAnswerPanel(12, agent);
    }

    Microsoft.UI.Dispatching.DispatcherQueueTimer? _cardTimer;

    /// <summary>A moment after the work is done, so the card never lands on top of the result. The timer is kept, so it can't be collected before it fires.</summary>
    void OfferMemoryCardSoon()
    {
        _cardTimer ??= Timer(2600, OfferMemoryCard);
        _cardTimer.Stop();
        _cardTimer.Start();
    }

    // ── Pages ──

    AgentPageModel AgentPageNow(Agent agent)
    {
        var now = DateTimeOffset.Now;
        return new AgentPageModel(agent, _roster[agent].StateAt(Clock.Now),
            SkillBook.Of(agent).Select(s => (s, _settings.SkillOn(s.Id))).ToList(),
            DataBook.Of(agent).Select(d => (d, _settings.DataAllowed(d))).ToList(),
            _settings.InterruptionFor(agent), _memory.InScope(MemoryScopes.Of(agent), now), _memory.InScope(MemoryScopes.Personal, now).Count,
            _runs.Recent(40).Where(r => r.Agent == agent.Name).Take(4).ToList(), _memoryConfirm, now);
    }

    MemoryPageModel MemoryPageNow()
    {
        var now = DateTimeOffset.Now;
        return new MemoryPageModel(_memory.All(now), _memory.Suggested(now), _memoryFilter, _memoryEditing, _memoryConfirm, _memoryForgot,
            Cast.All.Where(a => DataBook.Find(a, DataBook.PersonalMemory) is { } d && _settings.DataAllowed(d)).ToList(), now);
    }

    void OpenPage(string page)
    {
        bool sideways = _expanded && _settingsOpen;
        _settingsOpen = true;
        CloseTool();
        if (_settingsPage != page) { _memoryEditing = null; _memoryConfirm = null; _memoryForgot = null; }
        _settingsPage = page;
        _pageDirection = sideways ? 1 : 0;
        _lastPanel = null;
        Expand(takeFocus: false);
    }

    /// <summary>The pages' buttons and the memory cards. Returns false for commands that aren't theirs.</summary>
    bool PageCommand(string command)
    {
        var parts = command.Split(':', 3);
        string verb = parts[0], arg = parts.Length > 1 ? parts[1] : "", rest = parts.Length > 2 ? parts[2] : "";
        var now = DateTimeOffset.Now;
        switch (verb)
        {
            case "page" when arg == "memory": OpenPage("memory"); return true;
            case "page" when arg == "agent" && Cast.ById(rest) != null: OpenPage("agent:" + rest); return true;
            case "back" when _settingsPage != null:
                _settingsPage = null;
                _memoryEditing = _memoryConfirm = _memoryForgot = null;
                _pageDirection = -1;
                Expand(takeFocus: false);
                return true;
            case "skill":
                _settings.SetSkill(arg, !_settings.SkillOn(arg));
                EchoGrants();
                return true;
            case "data" when DataBook.ByKey(arg + ":" + rest) is { } data:
                _settings.SetData(data, !_settings.DataAllowed(data));
                EchoGrants();
                return true;
            case "interrupt" when Cast.ById(arg) is { } agent && Enum.TryParse<Interruption>(rest, out var level):
                _settings.SetInterruption(agent, level);
                return true;
            case "skill-on":
                _settings.SetSkill(arg, true);
                EchoGrants();
                AfterTurnOn();
                return true;
            case "data-on" when DataBook.ByKey(arg + ":" + rest) is { } data:
                _settings.SetData(data, true);
                EchoGrants();
                AfterTurnOn();
                return true;
            case "dismiss":
                _redo = null;
                ClearReply(ResultKey);
                ClearReply(MemoryKey);
                return true;
            case "mem-filter": _memoryFilter = parts.Length > 2 ? arg + ":" + rest : arg; Expand(takeFocus: false); return true;
            case "mem-edit":
                _memoryEditing = arg;
                _memoryConfirm = null;
                if (_focusOwner == IntPtr.Zero) _focusOwner = _win.TakeFocus();
                Expand(takeFocus: false);
                return true;
            case "mem-save":
                _memory.Edit(arg, rest, now);
                _memoryEditing = null;
                Expand(takeFocus: false);
                return true;
            case "mem-cancel": _memoryEditing = null; Expand(takeFocus: false); return true;
            case "mem-forget": _memoryConfirm = arg; _memoryEditing = null; Expand(takeFocus: false); return true;
            case "mem-forget-yes": ForgetNow(i => i.Id == arg); return true;
            case "mem-forget-all": _memoryConfirm = "*"; Expand(takeFocus: false); return true;
            case "mem-forget-all-yes": ForgetNow(_ => true, everything: true); return true;
            case "mem-keep": _memoryConfirm = null; ClearReply(MemoryKey); if (_settingsOpen) Expand(takeFocus: false); return true;
            case "mem-undo": ForgetNow(i => i.Id == arg, undo: true); return true;
            case "mem-yes":
            {
                var item = _memory.Accept(arg, rest.Length > 0 ? rest : null, now);
                _memoryEditing = null;
                ClearReply(MemoryKey);
                if (item != null && !_settingsOpen)
                    Reply(new Activity(ResultKey, item.Scope == MemoryScopes.Personal ? "scout" : MemoryScopes.AgentOf(item.Scope)?.Id ?? "forty", Priority.Ambient,
                                       "Remembered", null, "", item.Text, TimeSpan.FromSeconds(3), Accent.Done, AgentId: MemoryScopes.AgentOf(item.Scope)?.Id));
                return true;
            }
            case "mem-no":
                _memory.Decline(arg, now);
                ClearReply(MemoryKey);
                return true;
            case "mem-edit-p":
                ClearReply(MemoryKey);
                OpenPage("memory");
                _memoryEditing = "p:" + arg;
                if (_focusOwner == IntPtr.Zero) _focusOwner = _win.TakeFocus();
                Expand(takeFocus: false);
                return true;
            default: return false;
        }
    }

    /// <summary>A skill or data switch turned on from a refusal: what you asked is done now.</summary>
    void AfterTurnOn()
    {
        ClearReply(ResultKey);
        var redo = _redo;
        _redo = null;
        if (redo != null) Guard.Run(redo, "redo");
    }

    // ── For the layout check ──

    public MemoryStore Memory => _memory;

    /// <summary>Every skill on, every data source at its default, every agent allowed to interrupt, and nothing remembered.</summary>
    public void DemoResetAgents()
    {
        foreach (var skill in SkillBook.All) _settings.SetSkill(skill.Id, true);
        foreach (var data in DataBook.All) _settings.SetData(data, data.OnByDefault);
        foreach (var agent in Cast.All) _settings.SetInterruption(agent, Interruption.InterruptMe);
        _memory.Clear(DateTimeOffset.Now);
    }
    public void DemoPage(string page) => OpenPage(page);
}
