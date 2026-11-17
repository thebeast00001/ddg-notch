using System.Diagnostics;
using Forty.Core;
using Forty.Platform;

namespace Forty.Tests;

/// <summary>
/// "apps": indexes this PC's Start menu (read-only) and shows how long that takes, how many apps it
/// finds, and what a few everyday phrases would open. Nothing is opened.
/// </summary>
static class AppsProbe
{
    public static int Run()
    {
        var clock = Stopwatch.StartNew();
        var apps = AppIndex.Load();
        Console.WriteLine($"indexed in {clock.ElapsedMilliseconds} ms: {apps.Count(a => a.Kind == AppKind.Shortcut)} shortcuts, {apps.Count(a => a.Kind == AppKind.Packaged)} packaged apps");
        foreach (var phrase in new[] { "notepad", "calculator", "settings", "terminal", "explorer", "edge", "a browser", "something to edit photos", "my notes app", "notpad" })
        {
            var match = AppMatcher.Find(phrase, apps);
            string answer = match.App is { } app ? $"{app.Name} ({app.Kind.ToString().ToLowerInvariant()}, by {match.How})"
                : match.Choices.Count > 0 ? "asks: " + string.Join(" or ", match.Choices.Select(c => c.Name))
                : "nothing";
            Console.WriteLine($"  \"{phrase}\" -> {answer}");
        }
        return 0;
    }
}
