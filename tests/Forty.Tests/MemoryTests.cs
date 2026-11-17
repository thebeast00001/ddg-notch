using System.Text;
using Forty.Core;

namespace Forty.Tests;

public static class MemoryTests
{
    static readonly DateTimeOffset T0 = new(2026, 11, 17, 10, 0, 0, TimeSpan.FromHours(5.5));

    /// <summary>A store in a folder of its own, "encrypted" by reversing the bytes, so a test can see what the file holds.</summary>
    static (MemoryStore Store, string Path) Fresh()
    {
        string folder = Path.Combine(Path.GetTempPath(), "forty-memory-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "memory.bin");
        return (Open(path), path);
    }

    static MemoryStore Open(string path) => new(path, b => b.Reverse().ToArray(), b => b.Reverse().ToArray());

    static MemorySource Told(string line) => new(SourceKind.YouToldMe, line, T0);

    [Test]
    public static void What_you_say_becomes_a_sentence_about_you()
    {
        Check.Equal("Your wife's name is Priya.", MemoryText.FromUser("my wife's name is Priya"), "my → your");
        Check.Equal("You prefer bullet points.", MemoryText.FromUser("I prefer bullet points"), "I → you");
        Check.Equal("You're vegetarian.", MemoryText.FromUser("I'm vegetarian."), "I'm → you're");
        Check.Equal("You have two cats.", MemoryText.FromUser("i have two cats"), "said in lower case");
        Check.Equal(MemoryTopics.People, MemoryTopics.Guess("Your wife's name is Priya."), "family is People");
        Check.Equal(MemoryTopics.Preferences, MemoryTopics.Guess("You prefer bullet points."), "a preference");
        Check.Equal(MemoryTopics.Work, MemoryTopics.Guess("You work at a bank."), "work");
        Check.Equal(MemoryTopics.AboutYou, MemoryTopics.Guess("Your birthday is 4 April."), "about you");
    }

    [Test]
    public static void Searches_look_only_in_the_scopes_they_were_given()
    {
        var (store, _) = Fresh();
        store.Add("Your wife's name is Priya.", MemoryTopics.People, MemoryScopes.Personal, Told("my wife's name is Priya"), "you asked", T0);
        store.Add("Your browser is Microsoft Edge.", MemoryTopics.Apps, MemoryScopes.Of(Cast.Bolt), Told(""), "you said yes", T0, key: "app.for:browser", value: "Microsoft Edge");

        Check.Equal("Your wife's name is Priya.", store.Search("what's my wife's name", new[] { MemoryScopes.Personal }, T0).FirstOrDefault()?.Text, "found in your memory");
        Check.True(store.Search("wife name", new[] { MemoryScopes.Of(Cast.Iris) }, T0).Count == 0, "Iris's own scope doesn't hold your memory (F17)");
        Check.True(store.Search("browser", new[] { MemoryScopes.Personal }, T0).Count == 0, "Bolt's memory isn't yours either");
        Check.Equal("Microsoft Edge", store.Find("app.for:browser", MemoryScopes.Of(Cast.Bolt), T0)?.Value, "Bolt finds its own by key");
        Check.True(store.Search("the", new[] { MemoryScopes.Personal }, T0).Count == 0, "small words alone find nothing");
    }

    [Test]
    public static void Task_context_is_gone_after_a_day_from_every_search_and_from_the_file()
    {
        var (store, path) = Fresh();
        store.Add("You're working from home today.", MemoryTopics.Work, MemoryScopes.Personal, Told("I'm working from home"), "you asked", T0, forThisTask: true);
        store.Add("You prefer short answers.", MemoryTopics.Preferences, MemoryScopes.Personal, Told("I prefer short answers"), "you asked", T0);
        Check.True(store.All(T0)[0].ForThisTask || store.All(T0)[1].ForThisTask, "labelled for this task");
        var later = T0 + MemoryStore.TaskLife + TimeSpan.FromMinutes(1);
        Check.Equal(1, store.All(later).Count, "the next day only the lasting one is left");
        Check.True(store.Search("home", new[] { MemoryScopes.Personal }, later).Count == 0, "and search can't find it");
        Check.True(!Contents(path).Contains("working from home"), "nor is it in the file");
    }

    /// <summary>The file as text, undoing the test's stand-in encryption.</summary>
    static string Contents(string path) => Encoding.UTF8.GetString(File.ReadAllBytes(path).Reverse().ToArray());

    [Test]
    public static void Forgetting_removes_it_from_the_file_and_says_what_went()
    {
        var (store, path) = Fresh();
        var item = store.Add("Your wife's name is Priya.", MemoryTopics.People, MemoryScopes.Personal, Told("my wife's name is Priya"), "you asked", T0);
        store.Add("You prefer short answers.", MemoryTopics.Preferences, MemoryScopes.Personal, Told("I prefer short answers"), "you asked", T0);
        Check.True(Contents(path).Contains("Priya"), "kept in the file");
        var gone = store.Forget(i => i.Id == item.Id, T0);
        Check.Equal(1, gone.Count, "one thing went");
        Check.Equal("Your wife's name is Priya.", gone[0].Text, "and it says which");
        Check.True(!Contents(path).Contains("Priya"), "not a trace in the file, the words it came from included (F101)");
        Check.Equal(1, Open(path).All(T0).Count, "a fresh read agrees");
        store.ForgetScope(MemoryScopes.Personal, T0);
        Check.Equal(0, Open(path).All(T0).Count, "a whole memory space can go");
        var bolt = MemoryScopes.Of(Cast.Bolt);
        var p = store.Propose("Your notes app is Notion.", MemoryTopics.Apps, bolt, Told(""), T0, "app.for:notes", "Notion")!;
        store.Decline(p.Id, T0);
        store.Clear(T0);
        Check.True(!Contents(path).Contains("Notion"), "forget everything leaves not even a remembered no");
    }

    [Test]
    public static void Nothing_is_learned_without_a_yes_and_at_most_one_card_an_hour()
    {
        var (store, _) = Fresh();
        var bolt = MemoryScopes.Of(Cast.Bolt);
        var choice = new MemorySource(SourceKind.YourChoice, "you chose Microsoft Edge when you asked for a browser", T0);
        var p = store.Propose("Your browser is Microsoft Edge.", MemoryTopics.Apps, bolt, choice, T0, "app.for:browser", "Microsoft Edge")!;
        store.Propose("Your notes app is Notion.", MemoryTopics.Apps, bolt, choice, T0, "app.for:notes", "Notion");
        Check.Equal(0, store.All(T0).Count, "a suggestion isn't a memory (F95)");
        Check.Equal(p.Id, store.TakeCard(T0)?.Id, "the first suggestion is asked about");
        Check.True(store.TakeCard(T0 + TimeSpan.FromMinutes(30)) == null, "but not another within the hour");
        Check.True(store.TakeCard(T0 + TimeSpan.FromMinutes(61)) != null, "after an hour, the next");

        var kept = store.Accept(p.Id, null, T0)!;
        Check.True(kept.Approval == "you said yes" && kept.Source.Kind == SourceKind.YourChoice, "remembered, with how it was approved and where it came from");
        var notion = store.Suggested(T0).Single();
        store.Decline(notion.Id, T0);
        Check.True(store.Propose("Your notes app is Notion.", MemoryTopics.Apps, bolt, choice, T0, "app.for:notes", "Notion") == null, "a no is remembered: it isn't asked again");
        Check.True(store.Propose("Your browser is Microsoft Edge.", MemoryTopics.Apps, bolt, choice, T0, "app.for:browser", "Microsoft Edge") == null, "nor is what's already remembered");
        var changed = store.Propose("Your browser is Firefox.", MemoryTopics.Apps, bolt, choice, T0, "app.for:browser", "Firefox")!;
        var edited = store.Accept(changed.Id, "my browser is Firefox at work", T0)!;
        Check.True(edited.Approval == "you edited it" && store.InScope(bolt, T0).Count(i => i.Key == "app.for:browser") == 1, "your words win, and a new choice replaces the old");
    }

    [Test]
    public static void Editing_keeps_where_it_came_from_and_a_damaged_file_means_nothing_remembered()
    {
        var (store, path) = Fresh();
        var item = store.Add("Your manager is Ravi.", MemoryTopics.People, MemoryScopes.Personal, Told("my manager is Ravi"), "you asked", T0);
        var edited = store.Edit(item.Id, "your manager is Ravi Kumar", T0 + TimeSpan.FromHours(1))!;
        Check.True(edited.Text == "Your manager is Ravi Kumar." && edited.Source == item.Source && edited.Approval == "you asked" && edited.Edited != null, "the words change; the source and approval stay");
        File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
        var damaged = new MemoryStore(path, b => b, _ => throw new InvalidOperationException("can't decrypt"));
        Check.Equal(0, damaged.All(T0).Count, "unreadable: treated as empty, never a crash");
    }

    [Test]
    public static void Memory_requests_go_to_Iris()
    {
        Request R(string s) => RequestRules.Parse(s);
        var remember = R("Remember that my wife's name is Priya");
        Check.True(remember.Kind == RequestKind.Remember && remember.Agent == Cast.Iris && remember.Argument == "my wife's name is Priya", "remember that …");
        Check.Equal(RequestRules.TodayOnly + "I'm working from home", R("remember I'm working from home for today").Argument, "just for today (F93)");
        Check.Equal(RequestKind.Unknown, R("remember to call mom").Kind, "a reminder isn't a memory");
        var recall = R("what's my wife's name?");
        Check.True(recall.Kind == RequestKind.Recall && recall.Argument == "my wife's name", "what's my …");
        Check.Equal(RequestKind.Recall, R("who is my manager").Kind, "who is my …");
        Check.Equal(RequestKind.Tell, R("what's my battery").Kind, "the battery is still a fact this PC tells");
        Check.Equal(RequestKind.Memory, R("what do you know about me").Kind, "the memory page");
        Check.Equal(RequestKind.Memory, R("show my memories").Kind, "the memory page, another way");
        Check.True(R("forget my wife's name") is { Kind: RequestKind.Forget, Argument: "my wife's name" }, "forget …");
        Check.Equal(RequestRules.ForgetNewest, R("forget that").Argument, "forget that: the newest");
        Check.Equal(RequestRules.ForgetEverything, R("forget everything").Argument, "forget everything");
        Check.Equal(RequestKind.Unknown, R("Bolt, remember that I like jazz").Kind, "another agent named: the brain routes it to Iris");
    }

    [Test]
    public static void The_brain_can_remember_recall_and_forget_and_never_answers_about_you()
    {
        var keep = DecisionParser.Parse("{\"agent\":\"iris\",\"skill\":\"remember\",\"args\":{\"text\":\"You prefer short answers.\",\"topic\":\"preferences\",\"today_only\":false}}");
        Check.True(keep is { Argument: "You prefer short answers.", Topic: MemoryTopics.Preferences }, "remember, with its topic");
        var today = DecisionParser.Parse("{\"agent\":\"iris\",\"skill\":\"remember\",\"args\":{\"text\":\"You're at home today.\",\"topic\":\"nonsense\",\"today_only\":true}}");
        Check.True(today is { Topic: MemoryTopics.Other } && today.Argument.StartsWith(RequestRules.TodayOnly), "for today; an unknown topic is Other");
        Check.Equal("wife name", DecisionParser.Parse("{\"agent\":\"iris\",\"skill\":\"recall\",\"args\":{\"query\":\"wife name\"}}")?.Argument, "recall");
        Check.True(DecisionParser.Parse("{\"agent\":\"iris\",\"skill\":\"forget\",\"args\":{}}") == null, "forget needs to know what");
        Check.True(DecisionParser.Parse("{\"agent\":\"bolt\",\"skill\":\"remember\",\"args\":{\"text\":\"x\"}}") == null, "memory is Iris's, not Bolt's");
        Check.True(BrainPrompt.System().Contains("never \"answer\""), "the model is told it knows nothing about you");
    }

    [Test]
    public static void Every_skill_has_a_switch_and_your_memory_is_off_until_you_allow_it()
    {
        foreach (var skill in Skills.All.Where(s => s.Agent != null))
            Check.True(SkillBook.ById(SkillBook.For(skill)) is { } info && info.Agent == skill.Agent, $"{skill.Id} has a switch on {skill.Agent!.Name}'s page");
        string path = Path.Combine(Path.GetTempPath(), "forty-settings-test-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        using var settings = new Settings(path, watch: false);
        Check.True(settings.SkillOn("open_app"), "skills start on");
        settings.SetSkill("open_app", false);
        Check.True(!settings.SkillOn("open_app") && new Settings(path, watch: false).SkillOn("open_app") == false, "switched off, and saved");
        var irisMemory = DataBook.Find(Cast.Iris, DataBook.PersonalMemory)!;
        Check.True(!settings.DataAllowed(irisMemory), "your memory is off for every agent until you allow it (F17, F92)");
        Check.True(settings.DataAllowed(DataBook.Find(Cast.Bolt, "apps")!), "what a job needs starts on");
        settings.SetData(irisMemory, true);
        Check.True(settings.DataAllowed(irisMemory) && !settings.DataAllowed(DataBook.Find(Cast.Bolt, DataBook.PersonalMemory)!), "allowed for Iris, still not for Bolt");
        Check.Equal(Interruption.InterruptMe, settings.InterruptionFor(Cast.Iris), "agents may interrupt unless you say");
        settings.SetInterruption(Cast.Iris, Interruption.BriefOnly);
        Check.Equal(Interruption.BriefOnly, new Settings(path, watch: false).InterruptionFor(Cast.Iris), "brief only, saved (F23)");
    }

    [Test]
    public static void A_kind_of_app_has_a_memory_key()
    {
        Check.Equal<(string, string)?>(("app.for:browser", "browser"), AppMatcher.PurposeOf("a browser"), "a browser");
        Check.Equal<(string, string)?>(("app.for:photo", "photo app"), AppMatcher.PurposeOf("something to edit photos"), "photos");
        Check.True(AppMatcher.PurposeOf("Spotify") == null, "a name isn't a kind");
    }
}
