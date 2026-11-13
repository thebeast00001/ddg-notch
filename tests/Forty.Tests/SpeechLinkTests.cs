using System.IO.Pipes;
using System.Text;
using Forty.Platform;

namespace Forty.Tests;

/// <summary>F116 over a real pipe: the island hears the speech process, and notices when it goes away.</summary>
public static class SpeechLinkTests
{
    static string Name() => $"forty-test-{Environment.ProcessId}-{Guid.NewGuid():N}";

    static bool Wait(Func<bool> condition, int ms = 3000)
    {
        var until = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < until) { if (condition()) return true; Thread.Sleep(20); }
        return condition();
    }

    [Test]
    public static void Lines_arrive_and_a_closed_connection_is_noticed_while_the_process_still_runs()
    {
        string name = Name();
        using var link = new SpeechLink(name);
        var lines = new List<string>();
        bool? lostWhileRunning = null;
        link.Line += l => { lock (lines) lines.Add(l); };
        link.Lost += running => lostWhileRunning = running;
        link.Start();

        // A client that, like a busy speech process, never reads what the island sends it.
        var client = new NamedPipeClientStream(".", name, PipeDirection.InOut);
        client.Connect(2000);
        var writer = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true };
        Check.True(Wait(() => link.IsConnected), "connected");
        link.Send("policy full");   // the island speaks first, as it does on every connection
        link.Send("state?");
        writer.WriteLine("recording Test");
        Check.True(Wait(() => { lock (lines) return lines.Contains("recording Test"); }), "the line arrived");

        client.Dispose();
        Check.True(Wait(() => lostWhileRunning != null), "the broken connection was noticed");
        Check.True(lostWhileRunning == true, "and this process (the speech process's stand-in) is still running");
        Check.True(!link.IsConnected, "no longer connected");
    }

    [Test]
    public static void Sending_never_blocks_on_a_speech_process_that_does_not_read()
    {
        string name = Name();
        using var link = new SpeechLink(name);
        link.Start();
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut);
        client.Connect(2000);
        Check.True(Wait(() => link.IsConnected), "connected");
        var started = Environment.TickCount64;
        var send = Task.Run(() => { for (int i = 0; i < 200; i++) link.Send($"policy full {i} " + new string('x', 64)); });
        Check.True(send.Wait(2000), $"200 lines sent without waiting for the reader ({Environment.TickCount64 - started} ms)");
    }

    [Test]
    public static void A_second_connection_is_heard_after_the_first_goes()
    {
        string name = Name();
        using var link = new SpeechLink(name);
        var lines = new List<string>();
        link.Line += l => { lock (lines) lines.Add(l); };
        link.Start();
        for (int round = 0; round < 2; round++)
        {
            using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut);
            client.Connect(2000);
            var writer = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true };
            writer.WriteLine($"hello {round}");
            Check.True(Wait(() => { lock (lines) return lines.Contains($"hello {round}"); }), $"round {round} heard");
        }
    }
}
