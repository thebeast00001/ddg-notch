using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Forty.Core;

/// <summary>
/// How much a tool can do (ROADMAP 2.4): reading runs without asking; local and reversible shows an
/// undo; external asks every time with a preview; destructive asks every time and never offers "always".
/// </summary>
public enum Risk { Read, Local, External, Destructive }

/// <summary>One thing a run did (F124): when, which tool, on what (by name, never content), how risky, whether you approved it, and how it went.</summary>
public sealed record RunStep(DateTimeOffset At, string Tool, string Target, Risk Risk, string? Approval, string Outcome);

/// <summary>
/// One run, kept as history (F22): who did it, what you asked, every step, and how it ended. Runs are
/// history and are kept apart from memory; deleting one never deletes the other.
/// </summary>
public sealed record RunRecord(string Id, string Agent, string Request, DateTimeOffset Started, DateTimeOffset Ended, string Outcome, IReadOnlyList<RunStep> Steps)
{
    /// <summary>The run as one line of Markdown, for the export (F22).</summary>
    public string ToMarkdown()
    {
        var md = new StringBuilder($"### {Started.ToLocalTime():yyyy-MM-dd HH:mm} · {Agent}\n\n> {Request}\n\n");
        foreach (var step in Steps)
            md.Append($"- {step.At.ToLocalTime():HH:mm:ss} {step.Tool} · {step.Target} ({step.Risk.ToString().ToLowerInvariant()}{(step.Approval is { } a ? ", " + a : "")}): {step.Outcome}\n");
        md.Append($"\n**{Outcome}**\n");
        return md.ToString();
    }
}

/// <summary>
/// Run history and the audit trail (F22, F124): every run is written as one JSON line to a file per day
/// in Forty's folder (so the key check covers it), and kept 30 days. The most recent runs are kept in
/// memory for the panel's "last results". Nothing here is sent anywhere.
/// </summary>
public sealed class RunLog
{
    public const int KeepDays = 30;
    readonly string? _folder;
    readonly object _gate = new();
    readonly List<RunRecord> _recent = new();

    static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    /// <param name="folder">Where the daily files go, or null to keep runs in memory only.</param>
    public RunLog(string? folder)
    {
        _folder = folder;
        if (folder == null || !Directory.Exists(folder)) return;
        // The last few runs from earlier sessions, so "last results" survives a restart.
        foreach (var file in Directory.EnumerateFiles(folder, "runs-*.jsonl").OrderByDescending(f => f).Take(3))
        {
            try
            {
                foreach (var line in File.ReadLines(file).Reverse().Take(10))
                    if (JsonSerializer.Deserialize<RunRecord>(line, Json) is { } run) _recent.Add(run);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        }
        _recent.Sort((a, b) => b.Ended.CompareTo(a.Ended));
        if (_recent.Count > 20) _recent.RemoveRange(20, _recent.Count - 20);
    }

    public event Action? Changed;

    public void Save(RunRecord run)
    {
        lock (_gate)
        {
            _recent.Insert(0, run);
            if (_recent.Count > 20) _recent.RemoveAt(_recent.Count - 1);
        }
        if (_folder != null)
        {
            try
            {
                Directory.CreateDirectory(_folder);
                File.AppendAllText(Path.Combine(_folder, $"runs-{run.Ended.UtcDateTime:yyyyMMdd}.jsonl"), JsonSerializer.Serialize(run, Json) + "\n", new UTF8Encoding(false));
                foreach (var old in Directory.EnumerateFiles(_folder, "runs-*.jsonl"))
                    if (File.GetLastWriteTimeUtc(old) < DateTime.UtcNow.AddDays(-KeepDays)) File.Delete(old);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        Changed?.Invoke();
    }

    /// <summary>The latest runs, newest first.</summary>
    public IReadOnlyList<RunRecord> Recent(int count) { lock (_gate) return _recent.Take(count).ToList(); }
}

/// <summary>Builds a run as it happens, then hands the finished record to the log.</summary>
public sealed class RunBuilder
{
    readonly List<RunStep> _steps = new();
    public string Id { get; } = Guid.NewGuid().ToString("N")[..12];
    public Agent Agent { get; }
    public string Request { get; }
    public DateTimeOffset Started { get; } = DateTimeOffset.Now;

    public RunBuilder(Agent agent, string request) { Agent = agent; Request = request; }

    public void Step(string tool, string target, Risk risk, string outcome, string? approval = null) =>
        _steps.Add(new RunStep(DateTimeOffset.Now, tool, target, risk, approval, outcome));

    public RunRecord Finish(string outcome) => new(Id, Agent.Name, Request, Started, DateTimeOffset.Now, outcome, _steps.ToList());
}
