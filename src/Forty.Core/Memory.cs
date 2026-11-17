using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Forty.Core;

/// <summary>
/// Whose a memory is (F17, F92). "personal" is about you; "agent:operator" is Bolt's own task memory
/// (how you like things done). An agent always reads its own scope; your personal memory only if you
/// allow it on the agent's page.
/// </summary>
public static class MemoryScopes
{
    public const string Personal = "personal";
    public static string Of(Agent agent) => "agent:" + agent.Id;
    public static Agent? AgentOf(string scope) => scope.StartsWith("agent:", StringComparison.Ordinal) ? Cast.ById(scope[6..]) : null;
    public static string Name(string scope) => scope == Personal ? "You" : AgentOf(scope)?.Name ?? scope;
}

public enum SourceKind { YouToldMe, YourChoice, Call, File }

/// <summary>Where a memory came from (F94): the words or the choice it was learned from, and when.</summary>
public sealed record MemorySource(SourceKind Kind, string Line, DateTimeOffset At, bool Deleted = false)
{
    public string Describe(DateTimeOffset now) =>
        (Kind switch { SourceKind.YouToldMe => "You told me", SourceKind.YourChoice => "From your choice", SourceKind.Call => "From a call", _ => "From a file" })
        + " · " + Ago(At, now) + (Deleted ? " · source deleted" : "");

    public static string Ago(DateTimeOffset at, DateTimeOffset now)
    {
        var span = now - at;
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes} min ago";
        if (at.Date == now.Date) return $"today, {at.LocalDateTime:t}";
        if (at.Date == now.Date.AddDays(-1)) return $"yesterday, {at.LocalDateTime:t}";
        return at.LocalDateTime.ToString(at.Year == now.Year ? "d MMM" : "d MMM yyyy", System.Globalization.CultureInfo.CurrentCulture);
    }
}

/// <summary>
/// One thing remembered, as a plain sentence about you ("You prefer bullet-point summaries."), with its
/// topic, whose it is, where it came from, how it was approved (F95), and, for short-lived task
/// context (F93), when it expires. <see cref="Key"/> and <see cref="Value"/> let an agent use it
/// ("app.for:browser" = "Microsoft Edge").
/// </summary>
public sealed record MemoryItem(string Id, string Text, string Topic, string Scope, MemorySource Source, DateTimeOffset Learned,
                                string Approval, DateTimeOffset? Expires = null, string? Key = null, string? Value = null, DateTimeOffset? Edited = null)
{
    [JsonIgnore] public bool ForThisTask => Expires != null;
}

/// <summary>Something an agent would like to remember. Nothing is learned until you say yes (F95).</summary>
public sealed record MemoryProposal(string Id, string Text, string Topic, string Scope, MemorySource Source, DateTimeOffset Proposed,
                                    string? Key = null, string? Value = null);

/// <summary>The topics the memory page groups by, in order.</summary>
public static class MemoryTopics
{
    public const string AboutYou = "About you", People = "People", Preferences = "Preferences", Work = "Work", Apps = "Apps", Other = "Other";
    public static readonly IReadOnlyList<string> All = new[] { AboutYou, People, Preferences, Work, Apps, Other };

    static readonly (Regex Words, string Topic)[] Guesses =
    {
        (new(@"\b(wife|husband|partner|mom|mum|mother|dad|father|son|daughter|brother|sister|friend|boss|manager|colleague|girlfriend|boyfriend|kids?|children|family|grandma|grandpa|uncle|aunt|cousin)\b", RegexOptions.IgnoreCase), People),
        (new(@"\b(work|job|office|company|employer|team|project|client|deadline|salary|career)\b", RegexOptions.IgnoreCase), Work),
        (new(@"\b(prefer|prefers|like|likes|love|loves|hate|hates|dislike|favou?rite|rather|always|never)\b", RegexOptions.IgnoreCase), Preferences),
        (new(@"\b(app|apps|browser|editor|spotify|chrome|edge|firefox|notion|word|excel|teams|zoom|outlook)\b", RegexOptions.IgnoreCase), Apps),
        (new(@"\b(name|birthday|born|live|lives|from|age|allergic|vegetarian|vegan|you are|you're|your height)\b", RegexOptions.IgnoreCase), AboutYou),
    };

    /// <summary>A topic for a sentence nobody gave one, from the words in it.</summary>
    public static string Guess(string text)
    {
        foreach (var (words, topic) in Guesses) if (words.IsMatch(text)) return topic;
        return Other;
    }

    public static string Normalize(string? topic) => All.FirstOrDefault(t => string.Equals(t, topic?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? Other;
}

/// <summary>Turns what you said about yourself into a sentence about you: "my wife's name is Priya" → "Your wife's name is Priya."</summary>
public static class MemoryText
{
    static readonly Regex Person = new(@"\b(I'm|I am|I was|I've|I have|I'd|I'll|I|me|my|mine|myself)\b", RegexOptions.IgnoreCase);

    public static string FromUser(string said)
    {
        string t = said.Trim().TrimEnd('.', '!', ' ');
        t = Person.Replace(t, m => m.Value.ToLowerInvariant() switch
        {
            "i'm" or "i am" => "you're",
            "i was" => "you were",
            "i've" or "i have" => "you have",
            "i'd" => "you'd",
            "i'll" => "you'll",
            "i" or "me" => "you",
            "my" => "your",
            "mine" => "yours",
            "myself" => "yourself",
            _ => m.Value,
        });
        return Sentence(t);
    }

    /// <summary>Capital first letter, one full stop.</summary>
    public static string Sentence(string text)
    {
        string t = Regex.Replace(text.Trim(), @"\s+", " ").TrimEnd('.', ' ');
        if (t.Length == 0) return t;
        return char.ToUpper(t[0], System.Globalization.CultureInfo.CurrentCulture) + t[1..] + (t.EndsWith('?') || t.EndsWith('!') ? "" : ".");
    }
}

/// <summary>
/// Everything Forty remembers, on this PC only, encrypted for your Windows account (the protect and
/// unprotect functions come from outside, so the rules are tested without Windows).
/// <list type="bullet">
/// <item>Scopes are applied before anything is ranked (F17): a search never even looks at a scope it wasn't given.</item>
/// <item>Short-lived task context expires after a day by default and is then gone from every search (F93).</item>
/// <item>Nothing is learned silently: proposals wait for a yes, at most one card an hour; a no is remembered so the same thing isn't asked again (F95).</item>
/// <item>Forgetting removes the item from the file itself, not just from view, and says what went (F101).</item>
/// </list>
/// </summary>
public sealed class MemoryStore
{
    public static readonly TimeSpan TaskLife = TimeSpan.FromHours(24), CardGap = TimeSpan.FromHours(1);
    public const int MaxText = 300;

    readonly string _path;
    readonly Func<byte[], byte[]> _protect, _unprotect;
    readonly object _gate = new();
    Data _data = new();
    bool _loaded;

    public event Action? Changed;

    sealed class Data
    {
        public List<MemoryItem> Items { get; set; } = new();
        public List<MemoryProposal> Suggested { get; set; } = new();
        public DateTimeOffset? CardShown { get; set; }
        public List<string> Declined { get; set; } = new();
    }

    static readonly JsonSerializerOptions Json = new() { WriteIndented = false, Converters = { new JsonStringEnumConverter() } };

    public MemoryStore(string path, Func<byte[], byte[]> protect, Func<byte[], byte[]> unprotect)
    {
        _path = path;
        _protect = protect;
        _unprotect = unprotect;
    }

    void Ensure(DateTimeOffset now)
    {
        if (!_loaded)
        {
            _loaded = true;
            try
            {
                if (File.Exists(_path)) _data = JsonSerializer.Deserialize<Data>(_unprotect(File.ReadAllBytes(_path)), Json) ?? new();
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
            {
                _data = new();   // from another account or PC, or damaged: it can't be read, so nothing is remembered
            }
        }
        // Task context past its day is gone, from the file too, before anything reads it.
        if (_data.Items.RemoveAll(i => i.Expires is { } e && e <= now) > 0) Save();
    }

    void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            string temp = _path + ".tmp";
            File.WriteAllBytes(temp, _protect(JsonSerializer.SerializeToUtf8Bytes(_data, Json)));
            File.Move(temp, _path, overwrite: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    void Done() { Save(); Changed?.Invoke(); }

    // ── Reading ──

    /// <summary>Everything remembered now, newest first.</summary>
    public IReadOnlyList<MemoryItem> All(DateTimeOffset now)
    {
        lock (_gate) { Ensure(now); return _data.Items.OrderByDescending(i => i.Learned).ToList(); }
    }

    public IReadOnlyList<MemoryItem> InScope(string scope, DateTimeOffset now) => All(now).Where(i => i.Scope == scope).ToList();

    public MemoryItem? Get(string id, DateTimeOffset now) => All(now).FirstOrDefault(i => i.Id == id);

    /// <summary>The item an agent stored under a key in its own scope ("app.for:browser"), if any.</summary>
    public MemoryItem? Find(string key, string scope, DateTimeOffset now) => All(now).FirstOrDefault(i => i.Key == key && i.Scope == scope);

    /// <summary>
    /// The best matches for <paramref name="query"/>, looking only in <paramref name="scopes"/> (F17:
    /// filtered first, ranked after). Words are matched whole or by their start, so "wife's" finds "wife".
    /// </summary>
    public IReadOnlyList<MemoryItem> Search(string query, IReadOnlyCollection<string> scopes, DateTimeOffset now, int max = 3)
    {
        var want = Words(query);
        if (want.Count == 0) return Array.Empty<MemoryItem>();
        return All(now).Where(i => scopes.Contains(i.Scope))
            .Select(i => (Item: i, Score: Score(want, Words(i.Text + " " + i.Value))))
            .Where(x => x.Score > 0).OrderByDescending(x => x.Score).ThenByDescending(x => x.Item.Learned)
            .Take(max).Select(x => x.Item).ToList();
    }

    static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "is", "are", "was", "were", "be", "my", "your", "you", "i", "me", "what", "whats", "who", "whos", "where", "when", "which", "how",
        "do", "does", "did", "of", "to", "in", "on", "at", "for", "and", "or", "that", "this", "it", "its", "about", "know", "remember", "tell", "please",
        "s", "re", "ve", "d", "ll", "m",
    };

    public static List<string> Words(string text)
    {
        var words = new List<string>();
        foreach (Match m in Regex.Matches(text.ToLowerInvariant().Replace("’", "'"), @"[\p{L}\p{N}]+(?:'[\p{L}]+)?"))
        {
            string w = m.Value;
            if (w.EndsWith("'s", StringComparison.Ordinal)) w = w[..^2];
            int apostrophe = w.IndexOf('\'');
            if (apostrophe > 0) w = w[..apostrophe];
            if (w.Length > 3 && w.EndsWith('s') && !w.EndsWith("ss", StringComparison.Ordinal)) w = w[..^1];
            if (w.Length < 2 || Stop.Contains(w)) continue;
            words.Add(w);
        }
        return words;
    }

    static double Score(List<string> want, List<string> have)
    {
        if (have.Count == 0) return 0;
        int hits = want.Count(w => have.Any(h => h == w || (w.Length >= 4 && h.StartsWith(w, StringComparison.Ordinal)) || (h.Length >= 4 && w.StartsWith(h, StringComparison.Ordinal))));
        return hits == 0 ? 0 : hits / (1 + 0.04 * have.Count);
    }

    // ── Learning ──

    /// <summary>
    /// Remembers something you asked to be remembered, or a proposal you said yes to. The same sentence
    /// isn't stored twice; the same key in the same scope is replaced (your browser changed).
    /// </summary>
    public MemoryItem Add(string text, string topic, string scope, MemorySource source, string approval, DateTimeOffset now,
                          bool forThisTask = false, string? key = null, string? value = null)
    {
        lock (_gate)
        {
            Ensure(now);
            string clean = Clip(text);
            var same = _data.Items.FirstOrDefault(i => i.Scope == scope && (key != null ? i.Key == key : string.Equals(i.Text, clean, StringComparison.OrdinalIgnoreCase)));
            if (same != null) _data.Items.Remove(same);
            var item = new MemoryItem(Guid.NewGuid().ToString("N")[..12], clean, MemoryTopics.Normalize(topic), scope, source, now, approval,
                                      forThisTask ? now + TaskLife : null, key, value);
            _data.Items.Add(item);
            Done();
            return item;
        }
    }

    static string Clip(string text)
    {
        string one = Regex.Replace(text, @"[\u0000-\u001F\u007F]+", " ").Trim();
        return one.Length <= MaxText ? one : one[..(MaxText - 1)].TrimEnd() + "…";
    }

    /// <summary>Your words replace the sentence; where it came from and how it was approved stay.</summary>
    public MemoryItem? Edit(string id, string text, DateTimeOffset now)
    {
        lock (_gate)
        {
            Ensure(now);
            int at = _data.Items.FindIndex(i => i.Id == id);
            string clean = Clip(text);
            if (at < 0 || clean.Length == 0) return null;
            var edited = _data.Items[at] with { Text = MemoryText.FromUser(clean), Edited = now };
            _data.Items[at] = edited;
            Done();
            return edited;
        }
    }

    /// <summary>Forgets every item that matches; returns what went, so you can be shown (F101).</summary>
    public IReadOnlyList<MemoryItem> Forget(Func<MemoryItem, bool> which, DateTimeOffset now)
    {
        lock (_gate)
        {
            Ensure(now);
            var gone = _data.Items.Where(which).ToList();
            if (gone.Count == 0) return gone;
            _data.Items.RemoveAll(i => gone.Contains(i));
            // A proposal about the same thing goes too, so it can't come back as a card.
            _data.Suggested.RemoveAll(p => gone.Any(g => g.Key != null && g.Key == p.Key && g.Scope == p.Scope));
            Done();
            return gone;
        }
    }

    public IReadOnlyList<MemoryItem> ForgetScope(string scope, DateTimeOffset now) => Forget(i => i.Scope == scope, now);

    /// <summary>
    /// Forgets everything (F101): every memory, every suggestion waiting, and every "no" kept so a
    /// suggestion isn't asked again. Nothing about any of it is left in the file.
    /// </summary>
    public IReadOnlyList<MemoryItem> Clear(DateTimeOffset now)
    {
        lock (_gate)
        {
            Ensure(now);
            var gone = _data.Items.ToList();
            _data = new Data();
            Done();
            return gone;
        }
    }

    /// <summary>The thing the memory came from was deleted (a call's notes, a file): the memory stays and says so (F94).</summary>
    public void SourceDeleted(Func<MemorySource, bool> which, DateTimeOffset now)
    {
        lock (_gate)
        {
            Ensure(now);
            bool any = false;
            for (int i = 0; i < _data.Items.Count; i++)
                if (!_data.Items[i].Source.Deleted && which(_data.Items[i].Source)) { _data.Items[i] = _data.Items[i] with { Source = _data.Items[i].Source with { Deleted = true } }; any = true; }
            if (any) Done();
        }
    }

    // ── Proposals (F95) ──

    /// <summary>
    /// An agent would like to remember something. It waits as a suggestion; nothing is learned yet. Not
    /// proposed again if you already said no to it, or if it is already remembered.
    /// </summary>
    public MemoryProposal? Propose(string text, string topic, string scope, MemorySource source, DateTimeOffset now, string? key = null, string? value = null)
    {
        lock (_gate)
        {
            Ensure(now);
            string sig = Signature(scope, key, value, text);
            if (_data.Declined.Contains(sig)) return null;
            if (_data.Items.Any(i => i.Scope == scope && (key != null ? i.Key == key && i.Value == value : string.Equals(i.Text, text, StringComparison.OrdinalIgnoreCase)))) return null;
            _data.Suggested.RemoveAll(p => p.Scope == scope && (key != null ? p.Key == key : p.Text == text));
            var proposal = new MemoryProposal(Guid.NewGuid().ToString("N")[..12], Clip(text), MemoryTopics.Normalize(topic), scope, source, now, key, value);
            _data.Suggested.Add(proposal);
            Done();
            return proposal;
        }
    }

    static string Signature(string scope, string? key, string? value, string text) => key != null ? $"{scope}|{key}|{value}" : $"{scope}|{text.ToLowerInvariant()}";

    public IReadOnlyList<MemoryProposal> Suggested(DateTimeOffset now) { lock (_gate) { Ensure(now); return _data.Suggested.ToList(); } }

    /// <summary>
    /// The suggestion to ask about now as a card, or null: at most one card an hour, so the rest wait on
    /// the memory page (and, from phase 3, in the brief). Showing it starts the hour.
    /// </summary>
    public MemoryProposal? TakeCard(DateTimeOffset now)
    {
        lock (_gate)
        {
            Ensure(now);
            if (_data.Suggested.Count == 0 || _data.CardShown is { } shown && now - shown < CardGap) return null;
            _data.CardShown = now;
            Save();
            return _data.Suggested[0];
        }
    }

    /// <summary>Yes (or yes with your own words): the suggestion becomes a memory, with how you approved it.</summary>
    public MemoryItem? Accept(string proposalId, string? yourWords, DateTimeOffset now)
    {
        MemoryProposal? p;
        lock (_gate)
        {
            Ensure(now);
            p = _data.Suggested.FirstOrDefault(s => s.Id == proposalId);
            if (p == null) return null;
            _data.Suggested.Remove(p);
        }
        bool edited = yourWords is { Length: > 0 } w && w.Trim() != p.Text;
        return Add(edited ? MemoryText.FromUser(yourWords!) : p.Text, p.Topic, p.Scope, p.Source, edited ? "you edited it" : "you said yes", now, key: p.Key, value: p.Value);
    }

    /// <summary>No: it isn't learned, and it won't be asked again.</summary>
    public void Decline(string proposalId, DateTimeOffset now)
    {
        lock (_gate)
        {
            Ensure(now);
            var p = _data.Suggested.FirstOrDefault(s => s.Id == proposalId);
            if (p == null) return;
            _data.Suggested.Remove(p);
            _data.Declined.Add(Signature(p.Scope, p.Key, p.Value, p.Text));
            Done();
        }
    }
}
