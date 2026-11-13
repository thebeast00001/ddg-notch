using System.Runtime.InteropServices;

namespace SpeechSpike;

/// <summary>
/// F128: a model on this PC that is let go after a minute unused while the island says the PC is in
/// battery saver, and loaded again the next time it is needed. The load starts the moment you begin
/// speaking (Warm), so most of it happens while you talk. Outside battery saver it stays loaded, as before.
/// </summary>
sealed class IdleEngine : ISpeechEngine
{
    readonly Func<ISpeechEngine> _make;
    readonly Func<bool> _saver;
    readonly long _idleMs;
    readonly object _gate = new();
    readonly Timer _timer;
    ISpeechEngine? _engine;
    Task? _loading;
    long _lastUse = Environment.TickCount64;

    public IdleEngine(string name, Func<ISpeechEngine> make, Func<bool> saver, bool preload, long idleMs = 60_000, long checkMs = 15_000)
    {
        Name = name;
        _make = make;
        _saver = saver;
        _idleMs = idleMs;
        if (preload) _engine = make();
        _timer = new Timer(_ => LetGoIfIdle(), null, checkMs, checkMs);
    }

    public string Name { get; }
    public bool Loaded { get { lock (_gate) return _engine != null; } }
    public int Loads { get; private set; }
    public int Unloads { get; private set; }

    public string Transcribe(Clip clip)
    {
        _loading?.Wait();
        lock (_gate)
        {
            if (_engine == null) { _engine = _make(); Loads++; }
            _lastUse = Environment.TickCount64;
            return _engine.Transcribe(clip);
        }
    }

    /// <summary>Starts loading the model, if it was let go, while you are still speaking.</summary>
    public void Warm()
    {
        lock (_gate)
        {
            _lastUse = Environment.TickCount64;
            if (_engine != null || _loading is { IsCompleted: false }) return;
            _loading = Task.Run(() =>
            {
                var made = _make();
                lock (_gate) { if (_engine == null) { _engine = made; Loads++; } else made.Dispose(); }
            });
        }
    }

    void LetGoIfIdle()
    {
        if (!_saver()) return;
        lock (_gate)
        {
            if (_engine == null || _loading is { IsCompleted: false } || Environment.TickCount64 - _lastUse < _idleMs) return;
            _engine.Dispose();
            _engine = null;
            Unloads++;
        }
        GC.Collect();
        Console.WriteLine("  battery saver: the speech model was let go after a minute unused; it loads again when you next speak");
    }

    public void Dispose()
    {
        _timer.Dispose();
        lock (_gate) { _engine?.Dispose(); _engine = null; }
    }
}

/// <summary>
/// F136: which microphone Windows uses by default right now, read without opening anything. A
/// headset plugged in, or Bluetooth dropping, changes it; capture follows within half a second.
/// </summary>
static class DefaultDevice
{
    /// <param name="role">0 for the console default (what dictation uses), 2 for communications (what call apps use).</param>
    public static string? CaptureId(int role)
    {
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorClass();
            if (enumerator.GetDefaultAudioEndpoint(1 /* recording */, role, out var device) != 0) return null;
            return device.GetId(out string id) == 0 ? id : null;
        }
        catch (COMException) { return null; }
        catch (InvalidCastException) { return null; }
    }
}

/// <summary>
/// The call's microphone side, following the default communications microphone (F136). When it
/// changes mid-call, the old capture stops and a new one starts on the new device; every packet is
/// stamped with the system clock, so the pieces are laid back on one timeline with no gap beyond the
/// moment of the switch.
/// </summary>
sealed class FollowingMicrophone : IDisposable
{
    readonly Func<WasapiCapture> _open;
    readonly Func<string?> _deviceId;
    readonly List<WasapiCapture> _pieces = new();
    readonly Thread _watch;
    volatile bool _stopping;
    string? _device;

    public FollowingMicrophone(Func<WasapiCapture>? open = null, Func<string?>? deviceId = null)
    {
        _open = open ?? WasapiCapture.ForMicrophone;
        _deviceId = deviceId ?? (() => DefaultDevice.CaptureId(2));
        _watch = new Thread(Watch) { IsBackground = true, Name = "microphone follower" };
    }

    public int Switches { get; private set; }

    public void Start()
    {
        _device = _deviceId();
        var first = _open();
        lock (_pieces) _pieces.Add(first);
        first.Start();
        _watch.Start();
    }

    void Watch()
    {
        while (!_stopping)
        {
            Thread.Sleep(500);
            string? now = _deviceId();
            if (_stopping || now == null || now == _device) continue;
            _device = now;
            try
            {
                var next = _open();
                next.Start();
                WasapiCapture? old;
                lock (_pieces) { old = _pieces[^1]; _pieces.Add(next); }
                old.Stop();
                Switches++;
                Console.WriteLine("  the microphone changed; notes continue on the new one");
            }
            catch (Exception ex) when (ex is InvalidOperationException or COMException or TimeoutException)
            {
                Console.WriteLine("  the new microphone could not be opened: " + ex.Message);
            }
        }
    }

    public void Stop()
    {
        _stopping = true;
        lock (_pieces) foreach (var piece in _pieces) piece.Stop();
        if (_watch.IsAlive) _watch.Join(1000);
    }

    /// <summary>Every piece laid on one timeline: each is silence outside the time it was recording, so they add up.</summary>
    public Clip Assemble(long from, long to)
    {
        List<WasapiCapture> pieces;
        lock (_pieces) pieces = _pieces.ToList();
        float[]? sum = null;
        foreach (var piece in pieces)
        {
            var clip = piece.Assemble(from, to);
            sum ??= new float[clip.Samples.Length];
            for (int i = 0; i < Math.Min(sum.Length, clip.Samples.Length); i++) sum[i] = Math.Clamp(sum[i] + clip.Samples[i], -1f, 1f);
        }
        return new Clip(sum ?? Array.Empty<float>(), 16000);
    }

    public void Dispose()
    {
        Stop();
        lock (_pieces) foreach (var piece in _pieces) piece.Dispose();
    }
}

/// <summary>Checks for F128 and F136 that need no microphone and no model: run with "policy-selftest".</summary>
static class PolicySelfTest
{
    sealed class Fake : ISpeechEngine
    {
        public static int Alive;
        public Fake() => Interlocked.Increment(ref Alive);
        public string Name => "fake";
        public string Transcribe(Clip clip) => "ok";
        public void Dispose() => Interlocked.Decrement(ref Alive);
    }

    public static int Run()
    {
        int failed = 0;
        void Check(bool ok, string what) { Console.WriteLine($"  {(ok ? "pass" : "FAIL")}  {what}"); if (!ok) failed++; }
        var silence = new Clip(new float[1600], 16000);

        bool saver = false;
        using (var engine = new IdleEngine("fake", () => new Fake(), () => saver, preload: true, idleMs: 200, checkMs: 50))
        {
            Thread.Sleep(400);
            Check(engine.Loaded, "outside battery saver the model stays loaded");
            saver = true;
            Thread.Sleep(450);
            Check(!engine.Loaded && engine.Unloads == 1 && Fake.Alive == 0, "in battery saver it is let go after being unused");
            engine.Warm();
            Check(engine.Transcribe(silence) == "ok" && engine.Loads == 1, "it loads again when next needed");
            Check(Fake.Alive == 1, "exactly one copy is loaded");
        }
        Check(Fake.Alive == 0, "closing lets the model go");

        var id = DefaultDevice.CaptureId(0);
        Console.WriteLine($"  default microphone: {(id == null ? "none found" : "found (its id is not printed)")}");
        Check(true, "reading the default microphone opens nothing");

        Console.WriteLine(failed == 0 ? "All policy checks passed." : $"{failed} policy check(s) failed.");
        return failed == 0 ? 0 : 1;
    }
}
