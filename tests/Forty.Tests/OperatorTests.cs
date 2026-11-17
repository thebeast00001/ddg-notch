using Forty.Core;

namespace Forty.Tests;

public static class OperatorTests
{
    // A made-up Start menu, shaped like a real one: shortcuts, packages, the same app twice, and junk.
    static readonly InstalledApp[] Apps =
    {
        new("Notepad", "Microsoft.WindowsNotepad_8wekyb3d8bbwe!App", AppKind.Packaged),
        new("Calculator", "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", AppKind.Packaged),
        new("Visual Studio Code", @"C:\Start\Visual Studio Code.lnk", AppKind.Shortcut),
        new("Google Chrome", @"C:\Start\Google Chrome.lnk", AppKind.Shortcut),
        new("Microsoft Edge", @"C:\Start\Microsoft Edge.lnk", AppKind.Shortcut),
        new("Spotify", @"C:\Start\Spotify.lnk", AppKind.Shortcut),
        new("Spotify", "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify", AppKind.Packaged),
        new("Uninstall Spotify", @"C:\Start\Uninstall Spotify.lnk", AppKind.Shortcut),
        new("Photos", "Microsoft.Windows.Photos_8wekyb3d8bbwe!App", AppKind.Packaged),
        new("Paint", "Microsoft.Paint_8wekyb3d8bbwe!App", AppKind.Packaged),
        new("Sticky Notes", "Microsoft.MicrosoftStickyNotes_8wekyb3d8bbwe!App", AppKind.Packaged),
        new("File Explorer", @"C:\Start\File Explorer.lnk", AppKind.Shortcut),
        new("Windows PowerShell", @"C:\Start\Windows PowerShell.lnk", AppKind.Shortcut),
        new("Terminal", "Microsoft.WindowsTerminal_8wekyb3d8bbwe!App", AppKind.Packaged),
        new("Tor Browser", @"C:\Start\Tor Browser.lnk", AppKind.Shortcut),
    };

    static string? Opens(string phrase, IReadOnlyDictionary<string, string>? aliases = null) => AppMatcher.Find(phrase, Apps, aliases).App?.Name;

    [Test]
    public static void Apps_are_found_the_way_people_say_them()
    {
        Check.Equal("Notepad", Opens("notepad"), "exactly");
        Check.Equal("Notepad", Opens("the Notepad app"), "with words around it");
        Check.Equal("Google Chrome", Opens("chrome"), "without the maker's name");
        Check.Equal("Microsoft Edge", Opens("edge"), "without the maker's name");
        Check.Equal("Visual Studio Code", Opens("vs code"), "by the name everyone uses");
        Check.Equal("File Explorer", Opens("explorer"), "by the name everyone uses");
        Check.Equal("Calculator", Opens("calc"), "a short name");
        Check.Equal("Spotify", Opens("spotfy"), "a slip of the tongue");
        Check.Equal("Visual Studio Code", Opens("visual studio"), "by the start of its name");
        Check.True(AppMatcher.Find("spotify", Apps).App?.Kind == AppKind.Packaged, "the same app twice counts once, and the package wins");
        Check.True(AppMatcher.Find("uninstall spotify", Apps).App == null, "uninstallers are never opened");
        Check.True(AppMatcher.Find("photoshop", Apps).App == null && AppMatcher.Find("photoshop", Apps).Choices.Count == 0, "something not installed is not found");
    }

    [Test]
    public static void Purposes_find_the_installed_app_for_the_job_or_ask_which()
    {
        Check.Equal("Sticky Notes", Opens("my notes app"), "the one notes app installed");
        var photos = AppMatcher.Find("something to edit photos", Apps);
        Check.True(photos.App == null && photos.Choices.Select(c => c.Name).SequenceEqual(new[] { "Photos", "Paint" }), "two photo apps: ask, best first");
        var browser = AppMatcher.Find("a browser", Apps);
        Check.True(browser.Choices.Select(c => c.Name).SequenceEqual(new[] { "Google Chrome", "Microsoft Edge" }), "two browsers: ask (not the app with \"browser\" in its name)");
        Check.Equal("purpose", browser.How, "found by purpose");
        Check.Equal("Tor Browser", Opens("tor browser"), "named, it is found by name");
    }

    [Test]
    public static void Your_aliases_come_first()
    {
        var aliases = new Dictionary<string, string> { ["notes"] = "Notepad", ["music"] = "Spotify" };
        Check.Equal("Notepad", Opens("notes", aliases), "your name for it, not Sticky Notes");
        Check.Equal("Spotify", Opens("open music".Replace("open ", ""), aliases), "an alias");
    }

    [Test]
    public static void Requests_go_to_the_right_agent()
    {
        var r = RequestRules.Parse("Can you open Spotify please?");
        Check.True(r.Kind == RequestKind.OpenApp && r.Agent == Cast.Bolt && r.Argument == "Spotify", "politeness is ignored");
        Check.Equal("Notion", RequestRules.Parse("Operator, launch Notion").Argument, "Operator named");
        Check.Equal("Spotify", RequestRules.Parse("Bolt, open Spotify").Argument, "Bolt by its own name");
        Check.True(RequestRules.Parse("Bolt, open Spotify").Named == Cast.Bolt && RequestRules.Parse("Operator, open Spotify").Named == Cast.Bolt, "the old role name still reaches Bolt");
        Check.Equal(RequestKind.OpenApp, RequestRules.Parse("hey forty open my notes app").Kind, "the wake words are dropped");
        var scout = RequestRules.Parse("Scout, open the invoice from March");
        Check.True(scout.Kind == RequestKind.Unknown && scout.Named == Cast.Iris, "a named agent wins, even when it can't do it yet");
        Check.Equal(RequestKind.PauseEverything, RequestRules.Parse("stop everything").Kind, "the spoken emergency pause");
        Check.Equal(RequestKind.ResumeEverything, RequestRules.Parse("resume").Kind, "and resume");
        Check.Equal(RequestKind.Unknown, RequestRules.Parse("what's the weather").Kind, "anything else is not guessed at");
    }

    [Test]
    public static void Approved_apps_are_kept_by_name()
    {
        string path = Path.Combine(Path.GetTempPath(), $"forty-settings-op-{Environment.ProcessId}.json");
        try
        {
            var settings = new Settings(path, watch: false);
            Check.True(!settings.IsApproved("Notepad"), "nothing approved at first");
            settings.Approve("Notepad");
            settings.Approve("the Notepad app");
            Check.True(settings.IsApproved("notepad") && settings.Approved.Count == 1, "approved once, by its normalised name");
            File.WriteAllText(path, "{ \"operator.aliases\": { \"notes\": \"Notion\" } }");
            Check.Equal("Notion", new Settings(path, watch: false).Aliases["notes"], "aliases are read from the file");
        }
        finally { File.Delete(path); }
    }

    [Test]
    public static void Runs_are_kept_with_their_steps_and_survive_a_restart()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"forty-runs-{Environment.ProcessId}");
        try
        {
            var log = new RunLog(folder);
            var run = new RunBuilder(Cast.Bolt, "open notepad");
            run.Step("Apps", "Notepad", Risk.Read, "found by name");
            run.Step("Apps", "Notepad", Risk.Local, "opened", "you said once");
            log.Save(run.Finish("Opened Notepad"));
            var again = new RunLog(folder).Recent(3);
            Check.Equal(1, again.Count, "one run, read back");
            Check.Equal("Opened Notepad", again[0].Outcome, "its outcome");
            Check.Equal(Risk.Local, again[0].Steps[1].Risk, "its steps, with their risk");
            Check.True(again[0].ToMarkdown().Contains("you said once"), "the Markdown export shows the approval");
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
    }

    [Test]
    public static void The_emergency_pause_stops_idle_agents_too()
    {
        var run = new AgentRun();
        run.Pause();
        Check.Equal(AgentState.Idle, run.StateAt(0), "an ordinary pause leaves an idle agent alone");
        run.Pause(evenIdle: true);
        Check.True(run.IsPaused, "the emergency pause stops it too");
        run.Work();
        Check.Equal(AgentState.Paused, run.StateAt(0), "and nothing starts until resumed");
        run.Resume();
        Check.Equal(AgentState.Idle, run.StateAt(0), "back to idle");
    }
}
