using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SpeechSpike;

/// <summary>One line of a call transcript.</summary>
sealed record TranscriptLine(int Number, double At, string Who, string Text);

/// <summary>A decision, open question or to-do, with the transcript lines it rests on.</summary>
sealed record NoteItem(string Text, int[] Lines, string Owner = "", bool OwnerCertain = true, string? Due = null);

sealed record CallSummary(string Summary, List<NoteItem> Decisions, List<NoteItem> OpenQuestions, List<NoteItem> Todos, List<string> Removed);

/// <summary>
/// A language model reached over the chat-completions shape that Ollama, LM Studio, Foundry Local,
/// Sarvam and most hosted services all speak. Which one is used is configuration, not code.
/// </summary>
sealed class ChatModel
{
    static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };   // each request sets its own limit

    readonly string _baseUrl, _model;
    readonly string? _key;
    readonly bool _isSarvam;

    public string Name { get; }
    /// <summary>True when the transcript leaves this PC to reach the model.</summary>
    public bool IsRemote { get; }

    ChatModel(string name, string baseUrl, string? key, string model, bool remote, bool sarvam = false)
    {
        Name = name; _baseUrl = baseUrl.TrimEnd('/'); _key = key; _model = model; IsRemote = remote; _isSarvam = sarvam;
    }

    public const string HowToEnable =
        "No language model is set up, so only the transcript is written. Three ways to get a summary:\n" +
        "  1. Run a model on this PC with Ollama or LM Studio. It is found by itself and the call never leaves the PC.\n" +
        "  2. Add --cloud-notes to use Sarvam's chat model with SARVAM_API_KEY. The transcript text is sent to Sarvam.\n" +
        "  3. Set FORTY_LLM_URL, FORTY_LLM_KEY and FORTY_LLM_MODEL for any service that speaks the chat-completions format.";

    /// <summary>
    /// Picks the model to use: one named explicitly in FORTY_LLM_URL / FORTY_LLM_KEY / FORTY_LLM_MODEL,
    /// else a local runtime that is already running, else, only when a hosted model has been allowed,
    /// Sarvam if its key is set. A call transcript is never sent off the PC just because a speech
    /// key happens to be there; that takes a deliberate yes.
    /// </summary>
    public static ChatModel? Find(bool allowHosted)
    {
        string? Env(string name)
        {
            string? v = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(v)) v = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);
            return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        }

        if (Env("FORTY_LLM_URL") is { } url && Uri.TryCreate(url, UriKind.Absolute, out var address))
        {
            string model = Env("FORTY_LLM_MODEL") ?? "default";
            bool local = address.IsLoopback;
            return new ChatModel($"{model} at {address.Host}{(local ? " (on this PC)" : " (cloud)")}", url, Env("FORTY_LLM_KEY"), model, remote: !local);
        }
        if (FirstLocalModel("http://localhost:11434/v1") is { } ollama) return new ChatModel($"{ollama} (Ollama, on this PC)", "http://localhost:11434/v1", null, ollama, remote: false);
        if (FirstLocalModel("http://localhost:1234/v1") is { } studio) return new ChatModel($"{studio} (LM Studio, on this PC)", "http://localhost:1234/v1", null, studio, remote: false);
        // Not sarvam-105b: measured on 2026-11-12, it thought for 2,500, 3,000 and 8,000 tokens on a
        // 20-line transcript and wrote nothing. The conversations model answered in 1.6 s.
        if (allowHosted && SarvamEngine.FindKey() is { } sarvam) return new ChatModel("Sarvam sarvam-105b-conversations (cloud)", "https://api.sarvam.ai/v1", sarvam, "sarvam-105b-conversations", remote: true, sarvam: true);
        return null;
    }

    static string? FirstLocalModel(string baseUrl)
    {
        try
        {
            using var quick = new CancellationTokenSource(TimeSpan.FromMilliseconds(600));
            using var response = Http.GetAsync(baseUrl + "/models", quick.Token).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode) return null;
            using var json = JsonDocument.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            foreach (var model in json.RootElement.GetProperty("data").EnumerateArray())
                if (model.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } name && !name.Contains("embed", StringComparison.OrdinalIgnoreCase))
                    return name;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or KeyNotFoundException) { }
        return null;
    }

    public string Complete(string system, string user)
    {
        var (status, text) = Post(system, user, jsonMode: true);
        // Some servers refuse the JSON-mode switch. The instructions ask for JSON anyway, so ask again without it.
        if (status is 400 or 422) (status, text) = Post(system, user, jsonMode: false);
        if (status is < 200 or >= 300)
            throw new InvalidOperationException($"{Name} returned {status}: {(text.Length > 240 ? text[..240] : text)}");
        string answer = "", finish = "";
        try
        {
            using var json = JsonDocument.Parse(text);
            var choice = json.RootElement.GetProperty("choices")[0];
            if (choice.GetProperty("message").TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String) answer = content.GetString()!;
            if (choice.TryGetProperty("finish_reason", out var reason) && reason.ValueKind == JsonValueKind.String) finish = reason.GetString()!;
            if (json.RootElement.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                LastUsage = (usage.TryGetProperty("prompt_tokens", out var p) && p.TryGetInt32(out int sent) ? sent : 0,
                             usage.TryGetProperty("completion_tokens", out var c) && c.TryGetInt32(out int written) ? written : 0);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
        {
            throw new InvalidOperationException($"{Name} answered in a shape that could not be read.");
        }
        // Seen with Sarvam's sarvam-105b: a model that thinks before answering can spend its whole
        // allowance thinking and hand back nothing.
        if (answer.Trim().Length == 0)
            throw new InvalidOperationException(finish == "length"
                ? $"{Name} used its whole allowance without writing an answer. Models that think first can do this; use one that answers directly."
                : $"{Name} wrote an empty answer.");
        return answer;
    }

    /// <summary>Tokens sent and tokens written by the last call, when the service reports them.</summary>
    public (int Sent, int Written)? LastUsage { get; private set; }

    (int Status, string Text) Post(string system, string user, bool jsonMode)
    {
        using var body = new MemoryStream();
        using (var writer = new Utf8JsonWriter(body))
        {
            writer.WriteStartObject();
            writer.WriteString("model", _model);
            writer.WriteNumber("temperature", 0);   // the same call should give the same notes
            writer.WriteNumber("max_tokens", 2500);
            if (jsonMode) { writer.WriteStartObject("response_format"); writer.WriteString("type", "json_object"); writer.WriteEndObject(); }
            writer.WriteStartArray("messages");
            writer.WriteStartObject(); writer.WriteString("role", "system"); writer.WriteString("content", system); writer.WriteEndObject();
            writer.WriteStartObject(); writer.WriteString("role", "user"); writer.WriteString("content", user); writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/chat/completions")
        {
            Content = new ByteArrayContent(body.ToArray()),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        if (_key != null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
            if (_isSarvam) request.Headers.Add("api-subscription-key", _key);
        }
        int seconds = IsRemote ? 60 : 300;   // a model running on this PC's processor can be slow on a long call
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        try
        {
            using var response = Http.SendAsync(request, limit.Token).GetAwaiter().GetResult();
            return ((int)response.StatusCode, response.Content.ReadAsStringAsync(limit.Token).GetAwaiter().GetResult());
        }
        catch (OperationCanceledException) { throw new InvalidOperationException($"{Name} did not answer within {seconds} seconds."); }
        catch (HttpRequestException ex) { throw new InvalidOperationException($"{Name} could not be reached: {ex.Message}"); }
    }
}

/// <summary>
/// Writes the notes for a call from its transcript: a summary, decisions, open questions and to-dos.
/// The model's answer is not trusted as it stands. Every item has to point at transcript lines that
/// exist, and an owner is called certain only when that person actually speaks in those lines.
/// Anything that fails is removed or marked unsure, and the removals are listed.
/// </summary>
static class Summariser
{
    const string Instructions = """
        You write the notes for a call from its transcript.

        Each transcript line begins with its number, its time and who spoke. "You" is the person who
        recorded the call. Every other label is someone else on the call: either "Others" for all
        of them together, or "Speaker 1", "Speaker 2" and so on when their voices were told apart.
        These are labels, not names. If a name is spoken in the call, do not guess which label it belongs to.

        Work in this order:
        1. Go through the transcript line by line. Each time a speaker says they will do something
           ("I'll send it", "I can do the test tomorrow", "मैं भेज दूँगा"), that is a to-do for that
           speaker. List every one, each as its own to-do. Do not skip one because it seems small,
           and do not merge two into one.
        2. A task that someone says should be done, without anyone taking it, is also a to-do, with owner "unsure".
        3. Then list what the call settled as decisions, and questions that were asked and left unanswered as open questions.
        4. Write the summary last.

        Use only what the transcript says. Never invent a decision, an owner, a number or a date.
        If something is unclear in the transcript, leave it out or mark it unsure.

        Answer with JSON only, in exactly this shape:
        {
          "summary": "two to four sentences on what the call was about and where it ended",
          "decisions": [ { "text": "what was decided", "lines": [3, 4] } ],
          "open_questions": [ { "text": "a question left unanswered", "lines": [9] } ],
          "todos": [ { "text": "the task", "owner": "You", "due": "Friday", "lines": [12, 13] } ]
        }

        Rules:
        - "lines" lists the numbers of the transcript lines the item comes from. Every item needs at least one.
        - A to-do's "owner" is the label of the speaker who agreed to do it, exactly as it appears in the transcript ("You", "Others", "Speaker 2"), or "unsure" if the transcript does not make it clear.
        - "due" is the date or time copied word for word from the transcript line, in the transcript's own language and script (for example "by Friday" or "बुधवार तक"). Do not translate or reword it. Use null if no time was spoken.
        - A decision is something the call settled. A proposal nobody agreed to is not a decision.
        - If there are no decisions, open questions or to-dos, give an empty list.
        """;

    public static string Numbered(IEnumerable<TranscriptLine> lines) =>
        string.Join("\n", lines.Select(l => string.Create(CultureInfo.InvariantCulture, $"[{l.Number}] ({(int)(l.At / 60)}:{(int)(l.At % 60):00}) {l.Who}: {l.Text}")));

    /// <summary>
    /// Which language the notes are to be written in. Decided here by counting letters, not left to
    /// the model: asked to "use the call's language", it wrote English notes for a Hindi call.
    /// </summary>
    public static string LanguageLine(IEnumerable<TranscriptLine> lines)
    {
        int letters = 0, devanagari = 0;
        foreach (var line in lines)
            foreach (char c in line.Text)
            {
                if (!char.IsLetter(c) && (c < 'ऀ' || c > 'ॿ')) continue;
                letters++;
                if (c >= 'ऀ' && c <= 'ॿ') devanagari++;
            }
        return letters > 0 && devanagari * 2 > letters
            ? "This call is in Hindi. Write the summary and every item in Hindi, in Devanagari script; keep English words that were spoken in English as they are."
            : "Write the summary and every item in English.";
    }

    /// <summary>Asks the model, and asks once more if the first answer fails or cannot be read.</summary>
    public static CallSummary Summarise(ChatModel model, IReadOnlyList<TranscriptLine> lines)
    {
        string system = Instructions + "\n" + LanguageLine(lines), user = "Transcript:\n" + Numbered(lines);
        try
        {
            var notes = Check(model.Complete(system, user), lines);
            if (notes.Summary.Length > 0 || notes.Decisions.Count + notes.OpenQuestions.Count + notes.Todos.Count > 0) return notes;
        }
        catch (InvalidOperationException) { }
        Thread.Sleep(700);
        return Check(model.Complete(system, user), lines);
    }

    /// <summary>Turns the model's answer into notes, keeping only what the transcript supports.</summary>
    public static CallSummary Check(string answer, IReadOnlyList<TranscriptLine> lines)
    {
        var removed = new List<string>();
        var byNumber = lines.ToDictionary(l => l.Number);

        // Models sometimes wrap JSON in a code fence or add a sentence around it.
        int open = answer.IndexOf('{'), close = answer.LastIndexOf('}');
        if (open < 0 || close <= open) return new CallSummary("", new(), new(), new(), new() { "the model's answer was not JSON, so no notes were taken from it" });

        JsonDocument json;
        try { json = JsonDocument.Parse(answer[open..(close + 1)]); }
        catch (JsonException) { return new CallSummary("", new(), new(), new(), new() { "the model's answer was not valid JSON, so no notes were taken from it" }); }

        using (json)
        {
            var root = json.RootElement;
            string summary = root.TryGetProperty("summary", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString()!.Trim() : "";

            List<NoteItem> Items(string name, bool withOwner)
            {
                var items = new List<NoteItem>();
                if (!root.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array) return items;
                foreach (var element in array.EnumerateArray())
                {
                    if (element.ValueKind != JsonValueKind.Object) continue;
                    string text = element.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()!.Trim() : "";
                    if (text.Length == 0) continue;

                    var cited = new List<int>();
                    if (element.TryGetProperty("lines", out var l) && l.ValueKind == JsonValueKind.Array)
                        foreach (var n in l.EnumerateArray())
                            if (n.ValueKind == JsonValueKind.Number && n.TryGetInt32(out int number) && byNumber.ContainsKey(number)) cited.Add(number);
                    if (cited.Count == 0)
                    {
                        removed.Add($"\"{text}\": it pointed at no line of the transcript");
                        continue;
                    }

                    string owner = "";
                    bool certain = true;
                    string? due = null;
                    if (withOwner)
                    {
                        string said = element.TryGetProperty("owner", out var o) && o.ValueKind == JsonValueKind.String ? o.GetString()!.Trim() : "";
                        // Only a label that is actually in this transcript can own a to-do.
                        owner = lines.Select(l => l.Who).FirstOrDefault(who => who.Equals(said, StringComparison.OrdinalIgnoreCase)) ?? "Unsure who";
                        // An owner is certain only if that person speaks in the lines the to-do rests on.
                        // "You'll send it, right?" said by someone else is not You agreeing.
                        certain = owner != "Unsure who" && cited.Any(n => byNumber[n].Who == owner);
                        if (element.TryGetProperty("due", out var d) && d.ValueKind == JsonValueKind.String && d.GetString() is { Length: > 0 } when)
                        {
                            // A date has to appear in the lines it is said to come from.
                            bool spoken = cited.Any(n => byNumber[n].Text.Contains(when, StringComparison.OrdinalIgnoreCase));
                            due = spoken ? when : null;
                            if (!spoken) removed.Add($"the due date \"{when}\" on \"{text}\": those words are not in the lines it cites");
                        }
                    }
                    items.Add(new NoteItem(text, cited.Distinct().OrderBy(n => n).ToArray(), owner, certain, due));
                }
                return items;
            }

            return new CallSummary(summary, Items("decisions", false), Items("open_questions", false), Items("todos", true), removed);
        }
    }

    /// <summary>The notes as Markdown, each item followed by the time of the line it rests on.</summary>
    public static string Render(CallSummary notes, IReadOnlyList<TranscriptLine> lines, string modelName)
    {
        var byNumber = lines.ToDictionary(l => l.Number);
        string Times(NoteItem item) => string.Join(", ", item.Lines.Select(n => string.Create(CultureInfo.InvariantCulture, $"{(int)(byNumber[n].At / 60)}:{(int)(byNumber[n].At % 60):00}")).Distinct());
        var text = new StringBuilder();

        text.AppendLine("## Summary");
        text.AppendLine();
        text.AppendLine(notes.Summary.Length > 0 ? notes.Summary : "No summary was written.");
        text.AppendLine();

        void Section(string title, List<NoteItem> items, Func<NoteItem, string> line)
        {
            text.AppendLine("## " + title);
            text.AppendLine();
            if (items.Count == 0) text.AppendLine("None.");
            foreach (var item in items) text.AppendLine(line(item));
            text.AppendLine();
        }
        Section("Decisions", notes.Decisions, i => $"- {i.Text} ({Times(i)})");
        Section("Open questions", notes.OpenQuestions, i => $"- {i.Text} ({Times(i)})");
        Section("To-dos", notes.Todos, i =>
            $"- [ ] **{i.Owner}{(i.OwnerCertain || i.Owner == "Unsure who" ? "" : " (unsure)")}:** {i.Text.TrimEnd('.', '।', ' ')}{(i.Due != null ? $", due {i.Due}" : "")} ({Times(i)})");

        text.AppendLine($"Notes written by {modelName} from the transcript below. Times in brackets point at the lines each item rests on; check them before relying on it.");
        if (notes.Removed.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("Left out because the transcript did not support them:");
            foreach (string reason in notes.Removed) text.AppendLine("- " + reason);
        }
        text.AppendLine();
        return text.ToString();
    }

    /// <summary>Reads lines of the form "**You** (1:05) text" back out of a notes file.</summary>
    public static List<TranscriptLine> ParseTranscript(string markdown)
    {
        var lines = new List<TranscriptLine>();
        foreach (string raw in markdown.Split('\n'))
        {
            string line = raw.Trim();
            if (!line.StartsWith("**", StringComparison.Ordinal)) continue;
            int nameEnd = line.IndexOf("**", 2, StringComparison.Ordinal), open = line.IndexOf('(', Math.Max(nameEnd, 0)), close = line.IndexOf(')', Math.Max(open, 0));
            if (nameEnd < 0 || open < 0 || close < 0) continue;
            string[] time = line[(open + 1)..close].Split(':');
            if (time.Length != 2 || !int.TryParse(time[0], out int minutes) || !int.TryParse(time[1], out int seconds)) continue;
            string text = line[(close + 1)..].Trim();
            if (text.Length > 0) lines.Add(new TranscriptLine(lines.Count + 1, minutes * 60 + seconds, line[2..nameEnd], text));
        }
        return lines;
    }
}
