using System.IO.Pipes;
using System.Text;
using Microsoft.UI.Dispatching;

namespace IslandSpike;

/// <summary>
/// The island's side of the link to the speech process: a named pipe that only the signed-in user
/// can open, carrying one short line per event in each direction.
///
/// From the speech process: "listening", "level 0.42", "working", "done", "failed", "idle" for
/// dictation; "call-offer Chrome", "call-gone", "recording Chrome", "call-working", "call-done",
/// "call-failed" for call notes. To the speech process: "accept", "decline", "stop".
/// </summary>
sealed class IslandPipe
{
    public const string Name = "forty-island";

    readonly DispatcherQueue _ui;
    readonly CancellationTokenSource _stop = new();
    readonly object _gate = new();
    StreamWriter? _writer;

    public event Action<string>? Line;

    public IslandPipe(DispatcherQueue ui)
    {
        _ui = ui;
        _ = Task.Run(Serve);
    }

    /// <summary>Sends a line to the speech process, if one is connected.</summary>
    public void Send(string line)
    {
        lock (_gate)
        {
            try { _writer?.WriteLine(line); }
            catch (IOException) { _writer = null; }
            catch (ObjectDisposedException) { _writer = null; }
        }
    }

    async Task Serve()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(Name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_stop.Token);
                lock (_gate) _writer = new StreamWriter(server, new UTF8Encoding(false)) { AutoFlush = true };
                using var reader = new StreamReader(server, Encoding.UTF8);
                while (await reader.ReadLineAsync(_stop.Token) is { } line)
                {
                    string copy = line;
                    _ui.TryEnqueue(() => Line?.Invoke(copy));
                }
                lock (_gate) _writer = null;
                _ui.TryEnqueue(() => Line?.Invoke("gone"));   // the other side went away: show nothing
            }
            catch (OperationCanceledException) { break; }
            catch (IOException) { lock (_gate) _writer = null; await Task.Delay(300); }
        }
    }

    public void Stop() => _stop.Cancel();
}
