using System.Diagnostics;
using System.Net;

namespace SpeechSpike;

/// <summary>
/// One connection pool for every web service, so a connection opened early is still open when the
/// audio is ready to go. Opening a secure connection costs a few hundred milliseconds; that cost is
/// paid while you are still speaking, not after you let go of the key.
/// </summary>
static class Net
{
    public static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(4),
        PooledConnectionLifetime = TimeSpan.FromMinutes(30),
        ConnectTimeout = TimeSpan.FromSeconds(4),          // offline is noticed in seconds, not minutes
        AutomaticDecompression = DecompressionMethods.All,
    })
    { Timeout = Timeout.InfiniteTimeSpan };                 // every request sets its own limit

    /// <summary>Opens (or keeps open) a connection to a service. Returns at once; failure is silent.</summary>
    public static void Warm(string url) => _ = Task.Run(async () =>
    {
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            using var response = await Http.SendAsync(new HttpRequestMessage(HttpMethod.Head, url), HttpCompletionOption.ResponseHeadersRead, limit.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException) { }
    });

    /// <summary>Sends a request with a time limit and returns status and body. Network trouble becomes one plain exception.</summary>
    public static (int Status, string Body) Send(HttpRequestMessage request, double timeoutSeconds, string service)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            using var response = Http.SendAsync(request, limit.Token).GetAwaiter().GetResult();
            return ((int)response.StatusCode, response.Content.ReadAsStringAsync(limit.Token).GetAwaiter().GetResult());
        }
        catch (OperationCanceledException) { throw new ServiceException(service, $"{service} did not answer within {timeoutSeconds:0.#} seconds", retryElsewhere: true); }
        catch (HttpRequestException ex) { throw new ServiceException(service, $"{service} could not be reached: {ex.Message}", retryElsewhere: true); }
    }
}

/// <summary>A web service failed. <see cref="Message"/> is one sentence a person can read.</summary>
sealed class ServiceException(string service, string message, bool retryElsewhere, int status = 0) : InvalidOperationException(message)
{
    public string Service { get; } = service;
    public int Status { get; } = status;
    /// <summary>False when trying again cannot help, such as a rejected key.</summary>
    public bool RetryElsewhere { get; } = retryElsewhere;
}

/// <summary>
/// Lets a test break a service on purpose: FORTY_FAULT=sarvam-down makes Sarvam fail at once,
/// sarvam-hang makes it not answer, and the same for "eleven". Several can be given, separated by commas.
/// </summary>
static class Fault
{
    static readonly HashSet<string> Active = new((Environment.GetEnvironmentVariable("FORTY_FAULT") ?? "")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.OrdinalIgnoreCase);

    public static void Apply(string service, string label)
    {
        if (Active.Count == 0) return;
        if (Active.Contains(service + "-down")) throw new ServiceException(label, $"{label} could not be reached (test fault)", retryElsewhere: true);
        if (Active.Contains(service + "-hang"))
        {
            Thread.Sleep(20_000);
            throw new ServiceException(label, $"{label} did not answer (test fault)", retryElsewhere: true);
        }
    }
}

/// <summary>An engine that is only created, and its model only loaded, the first time it is needed.</summary>
sealed class LazyEngine(string name, Func<ISpeechEngine> make) : ISpeechEngine
{
    readonly Lazy<ISpeechEngine> _engine = new(make);
    public string Name => name;
    public string Transcribe(Clip clip) => _engine.Value.Transcribe(clip);
    public void Dispose() { if (_engine.IsValueCreated) _engine.Value.Dispose(); }
}

/// <summary>
/// One speech engine made of several, so that a slow or broken service is never what you wait on.
/// The first choice is asked. If it has not answered after a short head start, the second choice is
/// asked as well and whichever answers first wins. If every service fails, a model on this PC
/// answers. A service that just failed is not given its head start again for a minute.
/// </summary>
sealed class RoutedEngine : ISpeechEngine
{
    readonly (ISpeechEngine Engine, string Short)[] _services;
    readonly Func<ISpeechEngine?>? _lastResort;
    readonly int _headStartMs;
    readonly long[] _troubleUntil;      // per service: ticks until which it gets no head start
    readonly object _gate = new();

    public string Name { get; }

    /// <summary>Who answered the last utterance, and what went wrong on the way, if anything.</summary>
    public string LastAnsweredBy { get; private set; } = "";
    public string? LastTrouble { get; private set; }

    /// <param name="services">In order of preference.</param>
    /// <param name="lastResort">A model on this PC, loaded only if it is ever needed.</param>
    public RoutedEngine((ISpeechEngine Engine, string Short)[] services, Func<ISpeechEngine?>? lastResort, string lastResortName, int headStartMs = 1200)
    {
        _services = services;
        _lastResort = lastResort;
        _headStartMs = headStartMs;
        _troubleUntil = new long[services.Length];
        var names = services.Select(s => s.Short).ToList();
        if (lastResort != null) names.Add(lastResortName + " on this PC");
        Name = string.Join(", then ", names);
    }

    public void Warm() { foreach (var (engine, _) in _services) engine.Warm(); }

    bool InTrouble(int index) => Stopwatch.GetTimestamp() < Interlocked.Read(ref _troubleUntil[index]);
    void MarkTrouble(int index) => Interlocked.Exchange(ref _troubleUntil[index], Stopwatch.GetTimestamp() + 60 * Stopwatch.Frequency);
    void MarkHealthy(int index) => Interlocked.Exchange(ref _troubleUntil[index], 0);

    public string Transcribe(Clip clip)
    {
        var running = new List<(Task<string> Task, int Index)>();
        var troubles = new List<string>();
        int next = 0;

        void StartNext()
        {
            int index = next++;
            var engine = _services[index].Engine;
            running.Add((Task.Run(() => engine.Transcribe(clip)), index));
        }

        while (true)
        {
            if (running.Count == 0)
            {
                if (next >= _services.Length) break;      // nobody left to ask
                StartNext();
            }

            // The newest one asked gets a head start, unless it failed recently or nobody is left to race it.
            int newest = running[^1].Index;
            bool canRace = next < _services.Length;
            // Longer speech takes a service longer, so the head start grows with it: 1.2 s for a
            // sentence, about 3 s for half a minute. Measured: Sarvam's middle answer time is 0.5 s.
            int headStart = Math.Max(_headStartMs, (int)(800 + 80 * clip.Seconds));
            int wait = !canRace ? Timeout.Infinite : InTrouble(newest) ? 0 : headStart;
            int finished = Task.WaitAny(running.Select(r => (Task)r.Task).ToArray(), wait);

            if (finished < 0)
            {
                // No answer within the head start: ask the next service too, and keep waiting on both.
                // A service already marked as in trouble got no head start, so this says nothing new about it.
                if (wait > 0)
                {
                    MarkTrouble(newest);
                    troubles.Add($"{_services[newest].Short} was slow");
                }
                StartNext();
                continue;
            }

            var (task, who) = running[finished];
            running.RemoveAt(finished);
            if (task.IsCompletedSuccessfully)
            {
                // Slow counts as trouble too, so "healthy" is only for an answer that needed no help.
                if (!troubles.Any(t => t.StartsWith(_services[who].Short, StringComparison.Ordinal))) MarkHealthy(who);
                foreach (var (other, _) in running) _ = other.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                Report(_services[who].Short, troubles);
                return task.Result;
            }
            MarkTrouble(who);
            troubles.Add($"{_services[who].Short}: {Keys.Plain(task.Exception!)}");
        }

        if (_lastResort?.Invoke() is { } local)
        {
            string text = local.Transcribe(clip);
            Report(local.Name, troubles);
            return text;
        }
        Report("", troubles);
        throw new InvalidOperationException("no speech service answered (" + string.Join("; ", troubles) + ")");
    }

    void Report(string answeredBy, List<string> troubles)
    {
        lock (_gate)
        {
            LastAnsweredBy = answeredBy;
            LastTrouble = troubles.Count == 0 ? null : string.Join("; ", troubles);
        }
    }

    public void Dispose()
    {
        foreach (var (engine, _) in _services) engine.Dispose();
    }
}
