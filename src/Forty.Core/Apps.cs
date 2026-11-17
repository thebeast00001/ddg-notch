using System.Text;

namespace Forty.Core;

public enum AppKind { Shortcut, Packaged }

/// <summary>An app as the Start menu lists it. <see cref="Launch"/> is the shortcut's path, or the package's app id.</summary>
public sealed record InstalledApp(string Name, string Launch, AppKind Kind);

/// <summary>What a phrase found: one app, several to choose from, or nothing; and how it was found.</summary>
public sealed record AppMatch(InstalledApp? App, IReadOnlyList<InstalledApp> Choices, string How)
{
    public static readonly AppMatch None = new(null, Array.Empty<InstalledApp>(), "none");
}

/// <summary>Names as people say them: "the Spotify app" and "Spotify®" are both "spotify".</summary>
public static class AppNames
{
    public static string Normalize(string text)
    {
        var s = new StringBuilder(text.Length);
        foreach (char c in text.Normalize(NormalizationForm.FormKC).ToLowerInvariant())
            s.Append(char.IsLetterOrDigit(c) ? c : ' ');
        var words = s.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (words.Count > 1 && words[0] is "the" or "my" or "a" or "an") words.RemoveAt(0);
        while (words.Count > 1 && words[^1] is "app" or "application" or "program") words.RemoveAt(words.Count - 1);
        return string.Join(' ', words);
    }

    /// <summary>Start-menu entries that aren't apps you'd open: uninstallers, read-me files, help, websites.</summary>
    public static bool IsJunk(InstalledApp app)
    {
        var words = Normalize(app.Name).Split(' ');
        string[] junk = { "uninstall", "uninstaller", "readme", "license", "changelog", "documentation", "manual", "website", "faq" };
        if (words.Any(w => junk.Contains(w))) return true;
        string n = Normalize(app.Name);
        return app.Kind == AppKind.Shortcut && (n.Contains("read me") || n.Contains("release notes") || n.Contains("web site") || n.EndsWith(" help", StringComparison.Ordinal));
    }
}

/// <summary>
/// Finds the app a phrase means (F39): by your own aliases, by name (exactly, without the maker's name,
/// by its words, by the start of its name, or with a slip of the tongue), and then by purpose ("something
/// to edit photos", "a browser"). Purposes come from a fixed table today; the language model takes them
/// over when the agent runtime has one, and this stays as the fast path that needs no network.
/// </summary>
public static class AppMatcher
{
    /// <summary>Short names people use for apps whose full names they never say.</summary>
    static readonly Dictionary<string, string> Common = new()
    {
        ["vs code"] = "visual studio code", ["vscode"] = "visual studio code", ["code"] = "visual studio code",
        ["cmd"] = "command prompt", ["command line"] = "command prompt",
        ["explorer"] = "file explorer", ["files"] = "file explorer", ["file manager"] = "file explorer",
        ["store"] = "microsoft store", ["powershell"] = "windows powershell", ["snip"] = "snipping tool",
        ["calc"] = "calculator", ["task manager"] = "task manager", ["control panel"] = "control panel",
    };

    static readonly string[] Makers = { "microsoft", "google", "adobe", "mozilla", "apple", "windows" };

    /// <summary>Kinds of app, the words that ask for each, and the apps that do it, best first.</summary>
    static readonly (string[] Words, string[] Apps)[] Purposes =
    {
        (new[] { "photo", "photos", "picture", "pictures", "image", "images" }, new[] { "photos", "paint", "photoshop", "lightroom", "gimp", "affinity photo", "paint net", "canva" }),
        (new[] { "browser", "web", "internet", "browse", "website" }, new[] { "chrome", "edge", "firefox", "brave", "opera", "vivaldi", "arc" }),
        (new[] { "notes", "note", "notebook" }, new[] { "onenote", "notion", "obsidian", "sticky notes", "evernote", "notepad" }),
        (new[] { "music", "songs", "song", "playlist" }, new[] { "spotify", "apple music", "media player", "amazon music", "itunes" }),
        (new[] { "mail", "email", "emails", "inbox" }, new[] { "outlook", "mail", "thunderbird" }),
        (new[] { "chat", "message", "messages", "messaging", "texts" }, new[] { "whatsapp", "telegram", "discord", "slack", "teams", "signal", "messenger" }),
        (new[] { "code", "coding", "programming", "editor" }, new[] { "visual studio code", "cursor", "visual studio", "intellij idea", "pycharm", "android studio", "sublime text" }),
        (new[] { "terminal", "shell", "console" }, new[] { "terminal", "windows powershell", "powershell", "command prompt", "git bash" }),
        (new[] { "folder", "folders", "file", "files" }, new[] { "file explorer" }),
        (new[] { "calculate", "calculator", "maths", "math", "sums" }, new[] { "calculator" }),
        (new[] { "calendar", "schedule", "agenda" }, new[] { "calendar", "outlook" }),
        (new[] { "meeting", "meetings", "call", "calls" }, new[] { "teams", "zoom", "google meet", "webex", "skype" }),
        (new[] { "document", "documents", "write", "writing", "essay", "letter" }, new[] { "word", "notion", "libreoffice writer", "wordpad", "notepad" }),
        (new[] { "spreadsheet", "spreadsheets", "sheet", "sheets" }, new[] { "excel", "libreoffice calc" }),
        (new[] { "presentation", "presentations", "slides", "deck" }, new[] { "powerpoint", "libreoffice impress" }),
        (new[] { "pdf", "pdfs" }, new[] { "acrobat", "adobe acrobat", "acrobat reader", "sumatrapdf", "edge" }),
        (new[] { "game", "games", "gaming" }, new[] { "steam", "epic games launcher", "xbox", "battle net" }),
        (new[] { "screenshot", "screenshots", "snip" }, new[] { "snipping tool" }),
        (new[] { "video", "videos", "movie", "movies" }, new[] { "vlc media player", "media player", "clipchamp", "davinci resolve", "premiere pro" }),
    };

    public static AppMatch Find(string phrase, IReadOnlyList<InstalledApp> installed, IReadOnlyDictionary<string, string>? aliases = null)
    {
        string p = AppNames.Normalize(phrase);
        if (p.Length == 0) return AppMatch.None;
        var apps = installed.Where(a => !AppNames.IsJunk(a)).ToList();
        InstalledApp? Named(string name) => apps.FirstOrDefault(a => AppNames.Normalize(a.Name) == AppNames.Normalize(name));

        // Your own aliases first, then the short names everyone uses.
        if (aliases != null)
            foreach (var (alias, target) in aliases)
                if (AppNames.Normalize(alias) == p && Named(target) is { } yours) return new AppMatch(yours, new[] { yours }, "alias");
        if (Common.TryGetValue(p, out var common) && Named(common) is { } known) return new AppMatch(known, new[] { known }, "alias");

        // "A browser", "something to edit photos": a kind of app, not a name, so purpose comes first
        // (otherwise "a browser" would open whatever app has "browser" in its name).
        bool kind = System.Text.RegularExpressions.Regex.IsMatch(phrase, @"^\s*(a|an|some|something|anything)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (kind && ByPurpose(p, apps) is { } wanted) return wanted;

        // By name.
        var scored = apps.Select(a => (App: a, Score: Score(p, AppNames.Normalize(a.Name)))).Where(x => x.Score > 0).ToList();
        if (scored.Count > 0)
        {
            double top = scored.Max(x => x.Score);
            var best = Distinct(scored.Where(x => x.Score == top).Select(x => x.App));
            if (best.Count == 1 || top >= 95) return new AppMatch(best[0], best, "name");
            return new AppMatch(null, best.Take(4).ToList(), "name");
        }
        return ByPurpose(p, apps) ?? AppMatch.None;
    }

    static readonly Dictionary<string, string> PurposeLabels = new()
    {
        ["photo"] = "photo app", ["browser"] = "browser", ["notes"] = "notes app", ["music"] = "music app", ["mail"] = "email app",
        ["chat"] = "chat app", ["code"] = "code editor", ["terminal"] = "terminal", ["folder"] = "file manager", ["calculate"] = "calculator",
        ["calendar"] = "calendar", ["meeting"] = "meeting app", ["document"] = "writing app", ["spreadsheet"] = "spreadsheet app",
        ["presentation"] = "slides app", ["pdf"] = "PDF reader", ["game"] = "game launcher", ["screenshot"] = "screenshot tool", ["video"] = "video player",
    };

    /// <summary>
    /// The kind of app a phrase asks for, as a memory key and words ("a browser" → "app.for:browser",
    /// "browser"), so the app you choose for it can be remembered and opened next time without asking.
    /// </summary>
    public static (string Key, string Label)? PurposeOf(string phrase)
    {
        var words = AppNames.Normalize(phrase).Split(' ');
        foreach (var (purposeWords, _) in Purposes)
            if (words.Any(w => purposeWords.Contains(w))) return ("app.for:" + purposeWords[0], PurposeLabels.GetValueOrDefault(purposeWords[0], purposeWords[0] + " app"));
        return null;
    }

    /// <summary>The words of the phrase name a kind of app; the installed ones of that kind, best first.</summary>
    static AppMatch? ByPurpose(string p, List<InstalledApp> apps)
    {
        var words = p.Split(' ');
        foreach (var (purposeWords, kinds) in Purposes)
        {
            if (!words.Any(w => purposeWords.Contains(w))) continue;
            var found = new List<InstalledApp>();
            foreach (var kind in kinds)
                found.AddRange(apps.Where(a => ContainsWords(AppNames.Normalize(a.Name), kind) && !found.Contains(a)));
            found = Distinct(found);
            if (found.Count == 1) return new AppMatch(found[0], found, "purpose");
            if (found.Count > 1) return new AppMatch(null, found.Take(4).ToList(), "purpose");
        }
        return null;
    }

    /// <summary>How well a phrase names an app, 0 (not at all) to 100 (exactly).</summary>
    static double Score(string p, string n)
    {
        if (n == p) return 100;
        string bare = StripMaker(n);
        if (bare == p) return 95;
        var pw = p.Split(' ');
        var nw = n.Split(' ');
        if (pw.All(w => nw.Contains(w))) return 80 - 3 * (nw.Length - pw.Length);
        if (p.Length >= 3 && (n.StartsWith(p, StringComparison.Ordinal) || bare.StartsWith(p, StringComparison.Ordinal))) return 70 - 0.5 * (n.Length - p.Length);
        if (pw.All(w => w.Length >= 3 && nw.Any(x => x.StartsWith(w, StringComparison.Ordinal)))) return 60;
        // A slip: one letter off in a word of five or more, two in a word of eight or more.
        if (pw.Length == 1 && p.Length >= 5)
        {
            int allowed = p.Length >= 8 ? 2 : 1;
            if (nw.Any(x => Math.Abs(x.Length - p.Length) <= allowed && Distance(p, x) <= allowed) || Distance(p, bare.Replace(" ", "")) <= allowed) return 50;
        }
        return 0;
    }

    static string StripMaker(string n)
    {
        foreach (var maker in Makers)
            if (n.StartsWith(maker + " ", StringComparison.Ordinal)) return n[(maker.Length + 1)..];
        return n;
    }

    static bool ContainsWords(string name, string words) => (" " + name + " ").Contains(" " + words + " ", StringComparison.Ordinal);

    /// <summary>The same app listed twice (a shortcut and a package) counts once; the package wins.</summary>
    static List<InstalledApp> Distinct(IEnumerable<InstalledApp> apps) =>
        apps.GroupBy(a => AppNames.Normalize(a.Name)).Select(g => g.OrderBy(a => a.Kind == AppKind.Packaged ? 0 : 1).First()).ToList();

    static int Distance(string a, string b)
    {
        var row = Enumerable.Range(0, b.Length + 1).ToArray();
        for (int i = 1; i <= a.Length; i++)
        {
            int diagonal = row[0];
            row[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int above = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), diagonal + (a[i - 1] == b[j - 1] ? 0 : 1));
                diagonal = above;
            }
        }
        return row[b.Length];
    }
}
