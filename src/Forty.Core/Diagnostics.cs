using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Forty.Core;

/// <summary>
/// Performance monitoring that never records content (F130, F150). It keeps timings (how long an
/// expand or a render took) and resource samples (CPU, memory), each under a name from a fixed list,
/// as numbers only. Nothing free-form can be written, so a title, a transcript or a file name cannot
/// end up here by accident. Files stay on this PC; a week of data is capped well under 5 MB.
/// </summary>
public sealed class Meter
{
    /// <summary>The only names a measurement may have.</summary>
    public static readonly IReadOnlySet<string> Names = new HashSet<string>
    {
        "render.ms", "expand.ms", "collapse.ms", "start.ms", "cpu.percent", "memory.mb", "frames.dropped",
        "service.ms", "dictation.ms",
    };

    const int MaxSamplesPerName = 512;
    const long MaxFileBytes = 700_000;   // per day; seven days stay under 5 MB

    readonly Dictionary<string, List<double>> _samples = new();
    readonly string? _folder;
    readonly object _gate = new();

    /// <param name="folder">Where daily files go, or null to keep everything in memory only.</param>
    public Meter(string? folder) => _folder = folder;

    public void Record(string name, double value)
    {
        if (!Names.Contains(name)) throw new ArgumentException($"\"{name}\" is not an allowed measurement name.", nameof(name));
        if (double.IsNaN(value) || double.IsInfinity(value)) return;
        lock (_gate)
        {
            if (!_samples.TryGetValue(name, out var list)) _samples[name] = list = new List<double>();
            list.Add(value);
            if (list.Count > MaxSamplesPerName) list.RemoveAt(0);
        }
        Append(name, value);
    }

    /// <summary>Times an action and records it under <paramref name="name"/>.</summary>
    public T Time<T>(string name, Func<T> action)
    {
        long start = Stopwatch.GetTimestamp();
        try { return action(); }
        finally { Record(name, Stopwatch.GetElapsedTime(start).TotalMilliseconds); }
    }

    /// <summary>The typical (middle) and slowest-in-twenty values recorded so far.</summary>
    public (double P50, double P95, int Count)? Summary(string name)
    {
        lock (_gate)
        {
            if (!_samples.TryGetValue(name, out var list) || list.Count == 0) return null;
            var sorted = list.OrderBy(v => v).ToList();
            return (sorted[sorted.Count / 2], sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * 0.95))], sorted.Count);
        }
    }

    void Append(string name, double value)
    {
        if (_folder == null) return;
        try
        {
            Directory.CreateDirectory(_folder);
            string file = Path.Combine(_folder, $"perf-{DateTime.UtcNow:yyyyMMdd}.jsonl");
            if (File.Exists(file) && new FileInfo(file).Length > MaxFileBytes) return;
            // Only a time, an allowed name and a number: the line is built here, not from caller text.
            File.AppendAllText(file, $"{{\"t\":{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()},\"n\":\"{name}\",\"v\":{value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}}}\n");
            foreach (var old in Directory.EnumerateFiles(_folder, "perf-*.jsonl"))
                if (File.GetLastWriteTimeUtc(old) < DateTime.UtcNow.AddDays(-7)) File.Delete(old);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>
/// A crash or error report that cannot carry personal data (F148). It keeps exception types and the
/// names of the methods on the stack, the app and Windows versions, adapter states and recent timings.
/// It never keeps exception messages (they often quote file paths, titles or text), never variable
/// values, never window titles or transcripts. You see the report before anything is sent, and
/// sending is opt-in; today nothing is sent at all, the report is only written to this PC.
/// </summary>
public static class CrashReport
{
    public static string Build(Exception error, string appVersion, IEnumerable<AdapterStatus> adapters, Meter? meter)
    {
        var report = new Dictionary<string, object?>
        {
            ["app"] = appVersion,
            ["windows"] = Environment.OSVersion.Version.ToString(),
            ["runtime"] = Environment.Version.ToString(),
            ["utc"] = DateTime.UtcNow.ToString("u"),
            ["errors"] = Chain(error).Select(e => new Dictionary<string, object?>
            {
                ["type"] = e.GetType().FullName,
                ["hresult"] = $"0x{e.HResult:X8}",
                ["stack"] = Frames(e),
            }).ToList(),
            // Adapter names and states come from a fixed set; their free-text reasons are left out.
            ["adapters"] = adapters.ToDictionary(a => a.Name, a => a.Availability.ToString()),
            ["timings"] = meter == null ? null : Meter.Names.Select(n => (n, meter.Summary(n)))
                .Where(x => x.Item2 != null).ToDictionary(x => x.n, x => new[] { x.Item2!.Value.P50, x.Item2.Value.P95 }),
        };
        return JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
    }

    static IEnumerable<Exception> Chain(Exception error)
    {
        for (var e = error; e != null; e = e.InnerException)
        {
            yield return e;
            if (e is AggregateException many)
                foreach (var inner in many.InnerExceptions.Skip(1)) yield return inner;
        }
    }

    /// <summary>"Namespace.Type.Method" for each frame, nothing else: no file paths, no line text, no arguments.</summary>
    static List<string> Frames(Exception error)
    {
        var frames = new List<string>();
        foreach (var frame in new StackTrace(error, fNeedFileInfo: false).GetFrames() ?? Array.Empty<StackFrame>())
        {
            var method = frame.GetMethod();
            if (method == null) continue;
            frames.Add($"{method.DeclaringType?.FullName}.{method.Name}");
            if (frames.Count >= 40) break;
        }
        return frames;
    }

    /// <summary>Writes a report to the folder and keeps only the ten most recent.</summary>
    public static string? Save(string folder, string report)
    {
        try
        {
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, $"crash-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
            File.WriteAllText(path, report, new UTF8Encoding(false));
            foreach (var old in Directory.EnumerateFiles(folder, "crash-*.json").OrderByDescending(f => f).Skip(10)) File.Delete(old);
            return path;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
