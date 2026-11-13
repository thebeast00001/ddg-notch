using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SpeechSpike;

// ─────────────── The parts of Windows core audio that call capture needs ───────────────
// Only the methods that are called are declared, each interface up to the last one used.

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
class MMDeviceEnumeratorClass { }

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(int dataFlow, uint stateMask, out IMMDeviceCollection devices);
    [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
}

[ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceCollection
{
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int Item(uint index, out IMMDevice device);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDevice
{
    [PreserveSig] int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object activated);
    [PreserveSig] int OpenPropertyStore(uint access, out IntPtr properties);
    [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
}

[ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioSessionManager2
{
    [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, uint flags, out IntPtr control);
    [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, uint flags, out IntPtr volume);
    [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator enumerator);
}

[ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioSessionEnumerator
{
    [PreserveSig] int GetCount(out int count);
    [PreserveSig] int GetSession(int index, out IAudioSessionControl2 session);
}

[ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioSessionControl2
{
    [PreserveSig] int GetState(out int state);
    [PreserveSig] int GetDisplayName(out IntPtr name);
    [PreserveSig] int SetDisplayName(IntPtr name, IntPtr context);
    [PreserveSig] int GetIconPath(out IntPtr path);
    [PreserveSig] int SetIconPath(IntPtr path, IntPtr context);
    [PreserveSig] int GetGroupingParam(out Guid group);
    [PreserveSig] int SetGroupingParam(IntPtr group, IntPtr context);
    [PreserveSig] int RegisterAudioSessionNotification(IntPtr events);
    [PreserveSig] int UnregisterAudioSessionNotification(IntPtr events);
    [PreserveSig] int GetSessionIdentifier(out IntPtr id);
    [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr id);
    [PreserveSig] int GetProcessId(out uint processId);
}

[ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioClient
{
    [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
    [PreserveSig] int GetBufferSize(out uint frames);
    [PreserveSig] int GetStreamLatency(out long latency);
    [PreserveSig] int GetCurrentPadding(out uint padding);
    [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
    [PreserveSig] int GetMixFormat(out IntPtr format);
    [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
    [PreserveSig] int Reset();
    [PreserveSig] int SetEventHandle(IntPtr handle);
    [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
}

[StructLayout(LayoutKind.Sequential)]
struct AudioClientProperties { public uint Size; public int IsOffload; public int Category; public int Options; }

[ComImport, Guid("726778CD-F60A-4EDA-82DE-E47610CD78AA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioClient2
{
    [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
    [PreserveSig] int GetBufferSize(out uint frames);
    [PreserveSig] int GetStreamLatency(out long latency);
    [PreserveSig] int GetCurrentPadding(out uint padding);
    [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
    [PreserveSig] int GetMixFormat(out IntPtr format);
    [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
    [PreserveSig] int Reset();
    [PreserveSig] int SetEventHandle(IntPtr handle);
    [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    [PreserveSig] int IsOffloadCapable(int category, out int capable);
    [PreserveSig] int SetClientProperties(ref AudioClientProperties properties);
}

[ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioCaptureClient
{
    [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
    [PreserveSig] int ReleaseBuffer(uint frames);
    [PreserveSig] int GetNextPacketSize(out uint frames);
}

[ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IActivateAudioInterfaceAsyncOperation
{
    [PreserveSig] int GetActivateResult(out int result, [MarshalAs(UnmanagedType.IUnknown)] out object activated);
}

[ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IActivateAudioInterfaceCompletionHandler
{
    [PreserveSig] int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation);
}

[ComImport, Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAgileObject { }

[ComVisible(true)]
sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
{
    public ManualResetEventSlim Done { get; } = new(false);
    public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation) { Done.Set(); return 0; }
}

/// <summary>One audio stream that some program has open: is it recording or playing, and is it live.</summary>
sealed record AudioSession(bool IsCapture, uint ProcessId, string ProcessName, string App, bool Active);

/// <summary>
/// Reads which programs are using the microphone and which are playing sound. Reading this list
/// does not open the microphone or capture any audio.
/// </summary>
static class AudioSessions
{
    public static List<AudioSession> Snapshot()
    {
        var sessions = new List<AudioSession>();
        var parents = ProcessTree.Read();
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorClass();
        Guid managerId = typeof(IAudioSessionManager2).GUID;

        for (int flow = 0; flow <= 1; flow++)   // 0: playing, 1: recording
        {
            if (enumerator.EnumAudioEndpoints(flow, 1 /* active devices */, out var devices) != 0) continue;
            devices.GetCount(out uint deviceCount);
            for (uint d = 0; d < deviceCount; d++)
            {
                if (devices.Item(d, out var device) != 0) continue;
                if (device.Activate(ref managerId, 23 /* all contexts */, IntPtr.Zero, out object activated) != 0) continue;
                var manager = (IAudioSessionManager2)activated;
                if (manager.GetSessionEnumerator(out var list) != 0) continue;
                list.GetCount(out int count);
                for (int i = 0; i < count; i++)
                {
                    if (list.GetSession(i, out var session) != 0 || session == null) continue;
                    session.GetState(out int state);
                    session.GetProcessId(out uint pid);
                    if (pid == 0) continue;   // the system sounds session
                    sessions.Add(new AudioSession(flow == 1, pid, parents.NameOf(pid), parents.AppOf(pid), state == 1));
                }
            }
        }
        return sessions;
    }

    /// <summary>
    /// A program that is recording from the microphone and playing sound at the same time is
    /// probably in a call. Returns the process doing the playing, which is the one to listen to.
    /// </summary>
    public static AudioSession? FindCall(List<AudioSession> sessions, string? appFilter = null)
    {
        bool Matches(AudioSession s) => appFilter == null ||
            s.App.Contains(appFilter, StringComparison.OrdinalIgnoreCase) || s.ProcessName.Contains(appFilter, StringComparison.OrdinalIgnoreCase);
        int self = Environment.ProcessId;
        var recording = sessions.Where(s => s.IsCapture && s.Active && s.ProcessId != self).ToList();
        var playing = sessions.Where(s => !s.IsCapture && s.Active && s.ProcessId != self && Matches(s)).ToList();
        return playing.FirstOrDefault(p => recording.Any(r => r.ProcessId == p.ProcessId))
            ?? playing.FirstOrDefault(p => recording.Any(r => r.App == p.App));
    }
}

/// <summary>Who started whom, so that a helper process can be named after the app it belongs to.</summary>
sealed class ProcessTree
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct Entry
    {
        public uint Size, Usage, ProcessId; public IntPtr DefaultHeap; public uint ModuleId, Threads, ParentId; public int Priority; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32FirstW(IntPtr snapshot, ref Entry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32NextW(IntPtr snapshot, ref Entry entry);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);

    readonly Dictionary<uint, (uint Parent, string Exe)> _map = new();
    static readonly HashSet<string> Shells = new(StringComparer.OrdinalIgnoreCase)
        { "explorer.exe", "svchost.exe", "sihost.exe", "services.exe", "wininit.exe", "winlogon.exe", "System", "[System Process]", "RuntimeBroker.exe", "cmd.exe", "powershell.exe", "pwsh.exe", "bash.exe", "conhost.exe", "WindowsTerminal.exe" };

    public static ProcessTree Read()
    {
        var tree = new ProcessTree();
        IntPtr snapshot = CreateToolhelp32Snapshot(2 /* processes */, 0);
        if (snapshot == new IntPtr(-1)) return tree;
        try
        {
            var entry = new Entry { Size = (uint)Marshal.SizeOf<Entry>() };
            for (bool more = Process32FirstW(snapshot, ref entry); more; more = Process32NextW(snapshot, ref entry))
                tree._map[entry.ProcessId] = (entry.ParentId, entry.ExeFile);
        }
        finally { CloseHandle(snapshot); }
        return tree;
    }

    public string NameOf(uint pid) => _map.TryGetValue(pid, out var p) ? p.Exe : $"pid {pid}";

    /// <summary>The topmost ancestor that is an app and not part of Windows' own shell.</summary>
    public string AppOf(uint pid)
    {
        uint current = pid;
        string app = NameOf(pid);
        for (int depth = 0; depth < 12 && _map.TryGetValue(current, out var p); depth++)
        {
            if (Shells.Contains(p.Exe)) break;
            app = p.Exe;
            if (p.Parent == 0 || p.Parent == current) break;
            current = p.Parent;
        }
        return app;
    }
}

/// <summary>
/// Records one audio stream into memory at 16 kHz mono: either everything one program is playing
/// (and nothing any other program plays), or the microphone with Windows' call processing applied.
/// </summary>
sealed class WasapiCapture : IDisposable
{
    [DllImport("Mmdevapi.dll", ExactSpelling = true)]
    static extern int ActivateAudioInterfaceAsync([MarshalAs(UnmanagedType.LPWStr)] string devicePath, ref Guid iid, IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler handler, out IActivateAudioInterfaceAsyncOperation operation);

    [DllImport("kernel32.dll")] static extern IntPtr CreateEventW(IntPtr attributes, bool manualReset, bool initialState, IntPtr name);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);

    const uint Loopback = 0x00020000, EventCallback = 0x00040000, AutoConvert = 0x80000000, DefaultQuality = 0x08000000;

    readonly Func<int> _start, _stop;
    readonly IAudioCaptureClient _capture;
    readonly IntPtr _event;
    readonly Thread _thread;
    readonly int _channels;
    readonly List<(long Time, short[] Mono)> _chunks = new();
    volatile bool _stopping;

    /// <summary>Sample rate actually captured. 16000 unless Windows would not convert for us.</summary>
    public int Rate { get; }
    public string Description { get; }

    WasapiCapture(Func<IntPtr, (int Hr, object? Client)> open, string what)
    {
        // Ask for 16 kHz mono first, which is what speech recognition wants. If Windows will not
        // convert, take CD format and convert here.
        (int rate, int channels)[] formats = { (16000, 1), (44100, 2), (48000, 2) };
        object? client = null;
        int hr = -1, rateUsed = 0, channelsUsed = 0;
        foreach (var (rate, channels) in formats)
        {
            IntPtr format = Marshal.AllocHGlobal(18);
            Marshal.WriteInt16(format, 0, 1);                          // PCM
            Marshal.WriteInt16(format, 2, (short)channels);
            Marshal.WriteInt32(format, 4, rate);
            Marshal.WriteInt32(format, 8, rate * channels * 2);
            Marshal.WriteInt16(format, 12, (short)(channels * 2));
            Marshal.WriteInt16(format, 14, 16);
            Marshal.WriteInt16(format, 16, 0);
            (hr, client) = open(format);
            Marshal.FreeHGlobal(format);
            if (hr == 0 && client != null) { rateUsed = rate; channelsUsed = channels; break; }
        }
        if (hr != 0 || client == null) throw new InvalidOperationException($"Could not open {what} (error 0x{hr:X8}).");

        Rate = rateUsed;
        _channels = channelsUsed;
        Description = $"{what}, {rateUsed} Hz, {channelsUsed} channel{(channelsUsed == 1 ? "" : "s")}";
        _event = CreateEventW(IntPtr.Zero, false, false, IntPtr.Zero);

        Guid captureId = typeof(IAudioCaptureClient).GUID;
        object service;
        if (client is IAudioClient2 two)
        {
            two.SetEventHandle(_event);
            if (two.GetService(ref captureId, out service) != 0) throw new InvalidOperationException("No capture service.");
            _start = two.Start; _stop = two.Stop;
        }
        else
        {
            var one = (IAudioClient)client;
            one.SetEventHandle(_event);
            if (one.GetService(ref captureId, out service) != 0) throw new InvalidOperationException("No capture service.");
            _start = one.Start; _stop = one.Stop;
        }
        _capture = (IAudioCaptureClient)service;
        _thread = new Thread(Pump) { IsBackground = true, Name = "capture " + what };
    }

    /// <summary>Everything the given process, and the processes it started, are playing. Nothing else.</summary>
    public static WasapiCapture ForProcess(uint processId, string label) => Task.Run(() =>
    {
        IntPtr parameters = Marshal.AllocHGlobal(12);
        Marshal.WriteInt32(parameters, 0, 1);                  // process loopback
        Marshal.WriteInt32(parameters, 4, (int)processId);
        Marshal.WriteInt32(parameters, 8, 0);                  // include the target's process tree
        IntPtr variant = Marshal.AllocHGlobal(24);
        for (int i = 0; i < 24; i++) Marshal.WriteByte(variant, i, 0);
        Marshal.WriteInt16(variant, 0, 65);                    // a blob
        Marshal.WriteInt32(variant, 8, 12);
        Marshal.WriteIntPtr(variant, 16, parameters);

        var handler = new ActivationHandler();
        Guid clientId = typeof(IAudioClient).GUID;
        int hr = ActivateAudioInterfaceAsync("VAD\\Process_Loopback", ref clientId, variant, handler, out var operation);
        if (hr != 0) throw new InvalidOperationException($"Windows would not start app audio capture (error 0x{hr:X8}).");
        if (!handler.Done.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("App audio capture did not start.");
        operation.GetActivateResult(out int result, out object activated);
        Marshal.FreeHGlobal(variant);
        Marshal.FreeHGlobal(parameters);
        if (result != 0) throw new InvalidOperationException($"App audio capture was refused (error 0x{result:X8}).");

        var client = (IAudioClient)activated;
        return new WasapiCapture(format => (client.Initialize(0, Loopback | EventCallback | AutoConvert | DefaultQuality, 2_000_000, 0, format, IntPtr.Zero), client),
                                 $"audio played by {label}");
    }).GetAwaiter().GetResult();

    /// <summary>
    /// The default microphone, opened the way call apps open it. That asks Windows for its
    /// communications processing, which on Windows 11 24H2 and later includes echo cancellation.
    /// </summary>
    public static WasapiCapture ForMicrophone() => ForEndpoint(microphone: true);

    /// <summary>
    /// The same code path pointed at the speakers instead: everything the PC is playing. Used only
    /// to check the plumbing without opening the microphone.
    /// </summary>
    public static WasapiCapture ForSpeakerOutput() => ForEndpoint(microphone: false);

    static WasapiCapture ForEndpoint(bool microphone) => Task.Run(() =>
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorClass();
        int hr = microphone ? enumerator.GetDefaultAudioEndpoint(1 /* recording */, 2 /* communications */, out var device)
                            : enumerator.GetDefaultAudioEndpoint(0 /* playing */, 0 /* console */, out device);
        if (hr != 0) throw new InvalidOperationException(microphone ? "No microphone was found." : "No speakers were found.");
        Guid clientId = typeof(IAudioClient2).GUID;
        if (device.Activate(ref clientId, 23, IntPtr.Zero, out object activated) != 0)
            throw new InvalidOperationException("The audio device could not be opened.");
        var client = (IAudioClient2)activated;
        var properties = new AudioClientProperties { Size = (uint)Marshal.SizeOf<AudioClientProperties>(), Category = microphone ? 3 /* communications */ : 0 };
        int set = client.SetClientProperties(ref properties);
        uint flags = EventCallback | AutoConvert | DefaultQuality | (microphone ? 0 : Loopback);
        var capture = new WasapiCapture(format => (client.Initialize(0, flags, 2_000_000, 0, format, IntPtr.Zero), client),
                                        microphone ? "the microphone" : "the speaker output");
        capture.PropertiesResult = set;
        return capture;
    }).GetAwaiter().GetResult();

    /// <summary>Result of asking Windows for a processing category on this stream; 0 means accepted.</summary>
    public int PropertiesResult { get; private set; }

    public void Start()
    {
        _thread.Start();
        int hr = _start();
        if (hr != 0) throw new InvalidOperationException($"Capture would not start (error 0x{hr:X8}).");
    }

    void Pump()
    {
        while (!_stopping)
        {
            WaitForSingleObject(_event, 100);
            while (_capture.GetNextPacketSize(out uint waiting) == 0 && waiting > 0)
            {
                if (_capture.GetBuffer(out IntPtr data, out uint frames, out uint flags, out _, out ulong time) != 0) break;
                var mono = new short[frames];
                if ((flags & 2) == 0 && frames > 0)   // 2: Windows says this packet is silence
                {
                    var raw = new short[frames * _channels];
                    Marshal.Copy(data, raw, 0, raw.Length);
                    for (int i = 0; i < frames; i++)
                    {
                        int sum = 0;
                        for (int c = 0; c < _channels; c++) sum += raw[i * _channels + c];
                        mono[i] = (short)(sum / _channels);
                    }
                }
                _capture.ReleaseBuffer(frames);
                lock (_chunks) _chunks.Add(((long)time, mono));
            }
        }
    }

    public void Stop()
    {
        if (_stopping) return;
        _stopping = true;
        _stop();
        if (_thread.IsAlive) _thread.Join(1000);
    }

    /// <summary>
    /// The recording laid out on a clock, from <paramref name="from"/> to <paramref name="to"/>
    /// (both in 100-nanosecond ticks of the system's performance counter), as 16 kHz mono. An app
    /// that plays nothing sends nothing, so gaps are filled with silence to keep the two streams of
    /// a call lined up.
    /// </summary>
    public Clip Assemble(long from, long to)
    {
        var timeline = new short[Math.Max(0, (to - from) * Rate / 10_000_000)];
        long write = -1;
        lock (_chunks)
            foreach (var (time, mono) in _chunks)
            {
                long at = (time - from) * Rate / 10_000_000;
                // Packets that follow on from each other are joined exactly; a real gap moves the pen.
                if (write < 0 || Math.Abs(at - write) > Rate / 50) write = at;
                for (int i = 0; i < mono.Length; i++)
                {
                    long index = write + i;
                    if (index >= 0 && index < timeline.Length) timeline[index] = mono[i];
                }
                write += mono.Length;
            }
        var samples = new float[timeline.Length];
        for (int i = 0; i < samples.Length; i++) samples[i] = timeline[i] / 32768f;
        var clip = new Clip(samples, Rate);
        return Rate == 16000 ? clip : Resampler.To16k(clip);
    }

    /// <summary>The system performance counter now, in 100-nanosecond ticks, the unit capture packets are stamped in.</summary>
    public static long Now => (long)(Stopwatch.GetTimestamp() * (10_000_000.0 / Stopwatch.Frequency));

    public int PacketCount { get { lock (_chunks) return _chunks.Count; } }

    public void Dispose()
    {
        Stop();
        CloseHandle(_event);
    }
}

static class Resampler
{
    /// <summary>Low-pass at 7.2 kHz, then pick samples at 16 kHz. Enough for speech recognition.</summary>
    public static Clip To16k(Clip clip)
    {
        const int taps = 63;
        double cutoff = 7200.0 / clip.SampleRate;
        var kernel = new double[taps];
        double sum = 0;
        for (int i = 0; i < taps; i++)
        {
            double x = i - (taps - 1) / 2.0;
            double sinc = x == 0 ? 2 * cutoff : Math.Sin(2 * Math.PI * cutoff * x) / (Math.PI * x);
            kernel[i] = sinc * (0.54 - 0.46 * Math.Cos(2 * Math.PI * i / (taps - 1)));
            sum += kernel[i];
        }
        for (int i = 0; i < taps; i++) kernel[i] /= sum;

        var source = clip.Samples;
        int length = (int)((long)source.Length * 16000 / clip.SampleRate);
        var result = new float[length];
        double step = clip.SampleRate / 16000.0;
        for (int n = 0; n < length; n++)
        {
            int centre = (int)(n * step);
            double value = 0;
            for (int k = 0; k < taps; k++)
            {
                int index = centre + k - taps / 2;
                if (index >= 0 && index < source.Length) value += source[index] * kernel[k];
            }
            result[n] = (float)value;
        }
        return new Clip(result, 16000);
    }
}

/// <summary>Finds the stretches of a recording where someone is speaking, by loudness.</summary>
static class Speech
{
    public static List<(double Start, double End)> Segments(Clip clip, double maxSeconds = 22)
    {
        const double frame = 0.03;
        int size = (int)(clip.SampleRate * frame), frames = clip.Samples.Length / size;
        if (frames == 0) return new();
        var level = new double[frames];
        for (int f = 0; f < frames; f++)
        {
            double sum = 0;
            for (int i = 0; i < size; i++) { float s = clip.Samples[f * size + i]; sum += s * s; }
            level[f] = Math.Sqrt(sum / size);
        }
        // The quiet tenth of the recording is taken as the room; speech has to stand clearly above it.
        var sorted = level.OrderBy(x => x).ToArray();
        double floor = sorted[frames / 10], threshold = Math.Max(floor * 3.5, 0.004);

        var result = new List<(double, double)>();
        int start = -1, quiet = 0;
        const int hang = 20;   // 0.6 s of quiet ends a stretch
        for (int f = 0; f < frames; f++)
        {
            bool speech = level[f] > threshold;
            if (speech) { if (start < 0) start = f; quiet = 0; }
            else if (start >= 0 && ++quiet >= hang) { Add(start, f - quiet + 1); start = -1; quiet = 0; }
            if (start >= 0 && (f - start) * frame >= maxSeconds)
            {
                // Too long for one pass: cut at the quietest moment in the last few seconds.
                int from = Math.Max(start + 1, f - 100), cut = from;
                for (int k = from; k <= f; k++) if (level[k] < level[cut]) cut = k;
                Add(start, cut);
                start = cut;
            }
        }
        if (start >= 0) Add(start, frames);
        return result;

        void Add(int a, int b)
        {
            double s = Math.Max(0, a * frame - 0.15), e = Math.Min(clip.Seconds, b * frame + 0.15);
            if (e - s >= 0.35) result.Add((s, e));
        }
    }

    /// <summary>
    /// Brings a recording to a normal speaking level, whatever volume the app or microphone was set
    /// to. Pause detection and recognition both assume that level. Silence is left alone.
    /// </summary>
    public static Clip Normalise(Clip clip)
    {
        if (clip.Samples.Length == 0) return clip;
        var magnitudes = clip.Samples.Select(Math.Abs).OrderBy(x => x).ToArray();
        float loud = magnitudes[(int)(magnitudes.Length * 0.999)];   // ignores the odd click
        if (loud < 0.0003f) return clip;
        float gain = Math.Min(150f, 0.5f / loud);
        return new Clip(clip.Samples.Select(s => Math.Clamp(s * gain, -1f, 1f)).ToArray(), clip.SampleRate);
    }

    public static Clip Slice(Clip clip, double start, double end)
    {
        int a = Math.Clamp((int)(start * clip.SampleRate), 0, clip.Samples.Length), b = Math.Clamp((int)(end * clip.SampleRate), a, clip.Samples.Length);
        return new Clip(clip.Samples.AsSpan(a, b - a).ToArray(), clip.SampleRate);
    }

    /// <summary>
    /// How much of one stream can be heard in the other: the strongest match between the two
    /// waveforms when one is slid up to 0.4 s later, from 0 (nothing in common) to 1 (identical).
    /// </summary>
    public static double Leak(Clip source, Clip into, double maxLagSeconds = 0.4)
    {
        const int step = 4;                         // work at 4 kHz
        int na = source.Samples.Length / step, nb = into.Samples.Length / step;
        if (na < 4000 || nb < 4000) return 0;
        var a = new float[na];
        var b = new float[nb];
        for (int i = 0; i < na; i++) a[i] = source.Samples[i * step];
        for (int i = 0; i < nb; i++) b[i] = into.Samples[i * step];
        double energyA = 0;
        for (int i = 0; i < na; i++) energyA += a[i] * a[i];
        if (energyA < 1e-9) return 0;

        // Running energy of the second stream, so each lag is judged against the stretch it overlaps.
        var prefix = new double[nb + 1];
        for (int i = 0; i < nb; i++) prefix[i + 1] = prefix[i] + b[i] * b[i];

        double best = 0;
        int maxLag = Math.Min((int)(maxLagSeconds * 16000 / step), nb - 1);
        for (int lag = 0; lag <= maxLag; lag += 2)
        {
            int overlap = Math.Min(na, nb - lag);
            if (overlap < 2000) break;
            double dot = 0;
            for (int i = 0; i < overlap; i += 3) dot += a[i] * b[i + lag];
            double energyB = prefix[lag + overlap] - prefix[lag];
            if (energyB < 1e-9) continue;
            best = Math.Max(best, Math.Abs(dot * 3) / Math.Sqrt(energyA * energyB));
        }
        return Math.Min(1, best);
    }
}

/// <summary>Plays a sound file through the default speakers at a chosen share of full volume. Test use only.</summary>
static class TestPlayer
{
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)] static extern bool PlaySoundW(string sound, IntPtr module, uint flags);
    [DllImport("winmm.dll")] static extern int waveOutSetVolume(IntPtr device, uint volume);

    public static void Play(string wavPath, double volume, int delayMs)
    {
        ushort level = (ushort)(Math.Clamp(volume, 0, 1) * 0xFFFF);
        try
        {
            waveOutSetVolume(IntPtr.Zero, (uint)(level | (level << 16)));   // this program's own volume, not the system's
            Thread.Sleep(delayMs);
            foreach (string path in wavPath.Split(';', StringSplitOptions.RemoveEmptyEntries))   // several files play one after another
                PlaySoundW(path, IntPtr.Zero, 0x00020000 /* a file */ | 0x0002 /* no default sound */);
            Thread.Sleep(300);
        }
        finally
        {
            waveOutSetVolume(IntPtr.Zero, 0xFFFFFFFF);   // always put this program's volume back to full
        }
    }
}
