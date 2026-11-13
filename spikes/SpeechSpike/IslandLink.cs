using System.IO.Pipes;
using System.Text;

namespace SpeechSpike;

/// <summary>
/// The speech process's side of the link to the island: one short line per event, in each
/// direction, over a named pipe that only the signed-in user can open. If the island is not
/// running, lines are quietly dropped and everything else works the same without it.
/// </summary>
sealed class IslandLink : IDisposable
{
    const string PipeName = "forty-island";

    readonly object _gate = new();
    readonly Timer _finder;
    NamedPipeClientStream? _pipe;
    StreamWriter? _writer;
    long _nextAttempt;

    /// <summary>
    /// A line from the island: "accept", "decline", "stop", "open-notes", "state?" or "policy saver|full".
    /// Raised on a background thread.
    /// </summary>
    public event Action<string>? Received;

    /// <summary>True while the island says the PC is in battery saver (F128): do less.</summary>
    public bool Saver { get; private set; }

    /// <summary>Looks for the island every few seconds, so one started later still hears what is going on.</summary>
    public IslandLink() => _finder = new Timer(_ => { if (!Connected) Send("hello"); }, null, 3000, 3000);

    public bool Connected { get { lock (_gate) return _writer != null; } }

    public void Send(string line)
    {
        lock (_gate)
        {
            try
            {
                if (_writer == null)
                {
                    if (Environment.TickCount64 < _nextAttempt) return;
                    var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    pipe.Connect(150);
                    _pipe = pipe;
                    _writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
                    _ = Task.Run(() => Listen(pipe));
                }
                _writer.WriteLine(line);
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or ObjectDisposedException)
            {
                Drop();
            }
        }
    }

    void Listen(NamedPipeClientStream pipe)
    {
        try
        {
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
            while (reader.ReadLine() is { } line)
            {
                if (line.StartsWith("policy ", StringComparison.Ordinal)) Saver = line == "policy saver";
                Received?.Invoke(line);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        lock (_gate) { if (_pipe == pipe) Drop(); }
    }

    void Drop()
    {
        _writer = null;
        try { _pipe?.Dispose(); } catch (IOException) { }
        _pipe = null;
        _nextAttempt = Environment.TickCount64 + 2000;   // look for the island again in a moment
    }

    /// <summary>How loud a piece of audio is, from 0 (silence) to 1 (speaking up), for the island's meter.</summary>
    public static double Level(ReadOnlySpan<short> pcm)
    {
        if (pcm.Length == 0) return 0;
        double sum = 0;
        foreach (short s in pcm) sum += (double)s * s;
        double rms = Math.Sqrt(sum / pcm.Length) / 32768.0;
        double decibels = 20 * Math.Log10(rms + 1e-6);
        return Math.Clamp((decibels + 52) / 40, 0, 1);   // -52 dB reads as silence, -12 dB as full
    }

    public void Dispose()
    {
        _finder.Dispose();
        lock (_gate) Drop();
    }
}
