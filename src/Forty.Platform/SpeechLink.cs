using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using Forty.Core;
using Microsoft.Win32.SafeHandles;

namespace Forty.Platform;

/// <summary>
/// The island's end of the link to the speech process (F116, F128): a named pipe that only your
/// Windows account can open, carrying one short line per event in each direction.
///
/// From the speech process: what it is doing ("listening", "level 0.42", "recording Teams", …).
/// To it: your answers ("call-accept", "call-stop", …), the power policy ("policy saver") and a
/// request for its current state ("state?") whenever it connects.
///
/// The link also watches the speech process itself: if the connection breaks, the island learns
/// whether the process is still running (it may still be capturing) or has exited (it cannot be).
/// </summary>
public sealed class SpeechLink : Adapter
{
    public const string PipeName = "forty-island";

    readonly string _pipeName;
    readonly CancellationTokenSource _stop = new();
    readonly object _gate = new();
    Channel<string>? _outbox;   // lines waiting to be written; null while nothing is connected
    Process? _peer;

    /// <param name="pipeName">The pipe's name; tests use their own so they never meet a running island.</param>
    public SpeechLink(string pipeName = PipeName) : base("speech") => _pipeName = pipeName;

    /// <summary>A line from the speech process. Raised on a background thread.</summary>
    public event Action<string>? Line;
    /// <summary>The speech process connected (raised on a background thread).</summary>
    public event Action? Connected;
    /// <summary>The connection broke; the bool says whether the speech process is still running.</summary>
    public event Action<bool>? Lost;
    /// <summary>The speech process exited after its connection broke: nothing can be capturing any more.</summary>
    public event Action? ProcessExited;

    public bool IsConnected { get { lock (_gate) return _outbox != null; } }

    protected override void OnStart()
    {
        Report(Availability.Available, "waiting for the speech process");
        _ = Task.Run(Serve);
    }

    /// <summary>
    /// Sends a line to the speech process, if one is connected. Never throws and never waits: lines go
    /// into a short queue that a background writer drains, so a speech process that is busy (or stuck)
    /// and not reading can never hold up the island. If 64 lines pile up, the oldest are dropped.
    /// </summary>
    public void Send(string line)
    {
        lock (_gate) _outbox?.Writer.TryWrite(line);
    }

    async Task Serve()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 4096, 4096);
                await server.WaitForConnectionAsync(_stop.Token);
                Watch(server.SafePipeHandle);
                var outbox = Channel.CreateBounded<string>(new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
                lock (_gate) _outbox = outbox;
                _ = Task.Run(() => Drain(server, outbox));
                Report(Availability.Available, "connected");
                Connected?.Invoke();
                using var reader = new StreamReader(server, Encoding.UTF8, false, 1024, leaveOpen: true);
                while (await reader.ReadLineAsync(_stop.Token) is { } line) Line?.Invoke(line);
                Broken();
            }
            catch (OperationCanceledException) { break; }
            catch (IOException)
            {
                Broken();
                // Another island (an old spike) may own the pipe name; try again in a moment.
                try { await Task.Delay(1000, _stop.Token); } catch (OperationCanceledException) { break; }
            }
        }
    }

    /// <summary>Writes queued lines as the pipe takes them. Ends when the connection does.</summary>
    async Task Drain(NamedPipeServerStream server, Channel<string> outbox)
    {
        try
        {
            var writer = new StreamWriter(server, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
            await foreach (var line in outbox.Reader.ReadAllAsync(_stop.Token)) await writer.WriteLineAsync(line.AsMemory(), _stop.Token);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException) { }
    }

    void Broken()
    {
        Channel<string>? outbox;
        lock (_gate) { outbox = _outbox; _outbox = null; }
        if (outbox == null) return;
        outbox.Writer.TryComplete();
        Report(Availability.Available, "waiting for the speech process");
        Process? peer;
        lock (_gate) peer = _peer;
        bool running = peer != null && !HasExited(peer);
        Lost?.Invoke(running);
        if (running) _ = WaitForExit(peer!);
    }

    async Task WaitForExit(Process peer)
    {
        try { await peer.WaitForExitAsync(_stop.Token); }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException) { return; }
        bool current;
        lock (_gate) current = ReferenceEquals(_peer, peer) && _outbox == null;   // not reconnected meanwhile
        if (current) ProcessExited?.Invoke();
    }

    /// <summary>Remembers which process is on the other end, to know later whether it is still running.</summary>
    void Watch(SafePipeHandle handle)
    {
        Process? peer = null;
        if (GetNamedPipeClientProcessId(handle, out uint pid))
        {
            try { peer = Process.GetProcessById((int)pid); }
            catch (ArgumentException) { }
        }
        lock (_gate)
        {
            _peer?.Dispose();
            _peer = peer;
        }
    }

    static bool HasExited(Process process)
    {
        try { return process.HasExited; }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }   // unknown: assume it may still capture
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    public override void Dispose()
    {
        _stop.Cancel();
        lock (_gate) { _peer?.Dispose(); _peer = null; }
        base.Dispose();
    }
}
