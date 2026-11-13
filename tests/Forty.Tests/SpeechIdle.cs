using System.Diagnostics;
using Forty.Platform;

namespace Forty.Tests;

/// <summary>
/// F128's check: "10 minutes idle in battery saver averages 0.0% CPU" for the speech process.
/// Run as <c>Forty.Tests.exe speech-idle 10 path\to\SpeechSpike.exe</c>. This process plays the
/// island's part (it hosts the pipe and says "policy saver"), starts the speech process's call
/// watcher with no microphone, and measures its CPU minute by minute. Nothing is recorded and no
/// audio device is opened: a call offer, if one came, is never answered.
/// </summary>
static class SpeechIdle
{
    public static int Run(double minutes, string exe)
    {
        using var link = new SpeechLink();
        int lines = 0;
        bool told = false;
        link.Line += _ => Interlocked.Increment(ref lines);
        link.Connected += () => { link.Send("policy saver"); told = true; };
        link.Start();
        if (link.Status.Availability != Forty.Core.Availability.Available) { Console.WriteLine("Could not host the pipe (is an island running?)."); return 1; }

        var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        // As it really runs: no app filter. If a call did start, the watcher would only ask; this
        // stand-in never answers Yes, so nothing can be recorded.
        foreach (var arg in new[] { "watch-calls", "--no-mic" }) start.ArgumentList.Add(arg);
        using var speech = Process.Start(start);
        if (speech == null) { Console.WriteLine("Could not start the speech process."); return 1; }
        speech.OutputDataReceived += (_, _) => { };
        speech.ErrorDataReceived += (_, _) => { };
        speech.BeginOutputReadLine();
        speech.BeginErrorReadLine();

        Thread.Sleep(TimeSpan.FromSeconds(30));   // start-up costs are not idle
        Console.WriteLine($"told battery saver: {told}; lines from the speech process so far: {lines}");
        var samples = new List<double>();
        for (int minute = 1; minute <= Math.Ceiling(minutes); minute++)
        {
            speech.Refresh();
            if (speech.HasExited) { Console.WriteLine($"the speech process exited (code {speech.ExitCode})"); return 1; }
            var before = speech.TotalProcessorTime;
            var watch = Stopwatch.StartNew();
            Thread.Sleep(TimeSpan.FromMinutes(1));
            speech.Refresh();
            double percent = (speech.TotalProcessorTime - before).TotalMilliseconds / watch.Elapsed.TotalMilliseconds * 100;
            samples.Add(percent);
            Console.WriteLine($"minute {minute}: {percent:0.000}% of a core, {speech.PrivateMemorySize64 / 1048576.0:0} MB");
        }
        Console.WriteLine($"average over {samples.Count} minutes: {samples.Average():0.000}% of a core");
        try { speech.Kill(); } catch (InvalidOperationException) { }
        return 0;
    }
}
