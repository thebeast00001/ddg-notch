namespace Forty.App;

/// <summary>
/// Every piece of work the island runs from Windows' message loop (a timer tick, a queued update, a
/// deferred build) goes through here. An exception that escapes such a callback is a fail-fast inside
/// WinUI: the process ends before the app's own crash handler sees it, so nothing is learned and the
/// island is gone. Here it is recorded instead (a crash report without personal data, F148), at most
/// once a minute for each kind, and the island carries on.
/// </summary>
static class Guard
{
    /// <summary>Set at start: saves a crash report and writes the log line.</summary>
    public static Action<Exception, string>? Report { get; set; }

    static readonly Dictionary<string, DateTime> LastReported = new();

    public static void Run(Action action, string where)
    {
        try { action(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            string key = where + "|" + ex.GetType().FullName;
            lock (LastReported)
            {
                if (LastReported.TryGetValue(key, out var last) && DateTime.UtcNow - last < TimeSpan.FromMinutes(1)) return;
                LastReported[key] = DateTime.UtcNow;
            }
            Report?.Invoke(ex, where);
        }
    }
}
