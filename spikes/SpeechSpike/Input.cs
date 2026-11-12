using System.Runtime.InteropServices;

namespace SpeechSpike;

/// <summary>
/// Watches the keyboard system-wide through raw input. Raw input only listens: nothing here sits in
/// the path of a keystroke, so no key is ever delayed or swallowed, whatever app is in front.
/// </summary>
sealed class KeyWatcher
{
    public const int VkControl = 0x11, VkMenu = 0x12, VkPacket = 0xE7;

    /// <summary>Virtual key, whether it is the right-hand (extended) variant, and whether it went down.</summary>
    public event Action<int, bool, bool>? Key;

    delegate IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WindowClass
    {
        public int Size; public uint Style; public IntPtr Procedure; public int ClassExtra, WindowExtra;
        public IntPtr Instance, Icon, Cursor, Background;
        public string? MenuName; public string ClassName; public IntPtr SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Message { public IntPtr Hwnd; public uint Id; public IntPtr WParam, LParam; public uint Time; public int X, Y; public uint Private; }

    [StructLayout(LayoutKind.Sequential)]
    struct RawInputDevice { public ushort UsagePage, Usage; public uint Flags; public IntPtr Target; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern ushort RegisterClassExW(ref WindowClass windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateWindowExW(uint exStyle, string className, string title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] static extern IntPtr DefWindowProcW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern int GetMessageW(out Message message, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] public static extern bool PeekMessageW(out Message message, IntPtr hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] public static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] public static extern IntPtr DispatchMessageW(ref Message message);
    [DllImport("user32.dll")] static extern bool PostMessageW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern void PostQuitMessage(int code);
    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterRawInputDevices(RawInputDevice[] devices, uint count, uint size);
    [DllImport("user32.dll")] static extern uint GetRawInputData(IntPtr rawInput, uint command, IntPtr data, ref uint size, uint headerSize);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandleW(string? name);

    const uint WmInput = 0x00FF, WmClose = 0x0010, WmDestroy = 0x0002;
    const uint RidInput = 0x10000003, RidevInputSink = 0x00000100;
    const int HeaderSize = 24;   // RAWINPUTHEADER on 64-bit Windows

    readonly WndProc _procedure;   // kept alive for the life of the window
    readonly IntPtr _hwnd;
    readonly IntPtr _buffer = Marshal.AllocHGlobal(128);

    public KeyWatcher()
    {
        _procedure = Handle;
        var windowClass = new WindowClass
        {
            Size = Marshal.SizeOf<WindowClass>(),
            Procedure = Marshal.GetFunctionPointerForDelegate(_procedure),
            Instance = GetModuleHandleW(null),
            ClassName = "FortyKeyWatcher",
        };
        RegisterClassExW(ref windowClass);
        _hwnd = CreateWindowExW(0, windowClass.ClassName, "", 0, 0, 0, 0, 0, new IntPtr(-3) /* message-only */, IntPtr.Zero, windowClass.Instance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) throw new InvalidOperationException("Could not create the key watcher window.");

        var keyboard = new[] { new RawInputDevice { UsagePage = 1, Usage = 6, Flags = RidevInputSink, Target = _hwnd } };
        if (!RegisterRawInputDevices(keyboard, 1, (uint)Marshal.SizeOf<RawInputDevice>()))
            throw new InvalidOperationException("Could not register for keyboard input.");
    }

    IntPtr Handle(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == WmInput)
        {
            uint size = 128;
            uint read = GetRawInputData(lParam, RidInput, _buffer, ref size, HeaderSize);
            if (read != uint.MaxValue && read >= HeaderSize + 8 && Marshal.ReadInt32(_buffer, 0) == 1 /* keyboard */)
            {
                ushort flags = (ushort)Marshal.ReadInt16(_buffer, HeaderSize + 2);
                ushort vkey = (ushort)Marshal.ReadInt16(_buffer, HeaderSize + 6);
                Key?.Invoke(vkey, (flags & 2) != 0, (flags & 1) == 0);
            }
        }
        else if (message == WmDestroy)
        {
            PostQuitMessage(0);
        }
        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    /// <summary>Runs until <see cref="Stop"/> is called. Must run on the thread that created the watcher.</summary>
    public void Run()
    {
        while (GetMessageW(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref message);
            DispatchMessageW(ref message);
        }
    }

    public void Stop() => PostMessageW(_hwnd, WmClose, IntPtr.Zero, IntPtr.Zero);
}

/// <summary>Types text into whatever app has the keyboard, as Unicode input, so any script works in any app.</summary>
static class TextTyper
{
    [StructLayout(LayoutKind.Sequential)]
    struct MouseInput { public int X, Y; public uint Data, Flags, Time; public IntPtr Extra; }

    [StructLayout(LayoutKind.Sequential)]
    struct KeyboardInput { public ushort Vk, Scan; public uint Flags, Time; public IntPtr Extra; }

    [StructLayout(LayoutKind.Explicit)]
    struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Input { public uint Type; public InputUnion Data; }

    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint count, Input[] inputs, int size);

    const uint KeyUp = 0x0002, Unicode = 0x0004;

    /// <summary>
    /// Types the text and returns how many characters Windows accepted. Zero usually means the app in
    /// front runs as administrator, which Windows protects from input sent by ordinary apps.
    /// <paramref name="stillSafe"/> is checked before each batch; typing stops if it says no.
    /// </summary>
    public static int Type(string text, Func<bool>? stillSafe = null)
    {
        text = text.Replace("\r", "").Replace('\n', ' ');
        int accepted = 0;
        const int batch = 40;
        for (int start = 0; start < text.Length; start += batch)
        {
            if (stillSafe != null && !stillSafe()) break;
            int count = Math.Min(batch, text.Length - start);
            var inputs = new Input[count * 2];
            for (int i = 0; i < count; i++)
            {
                char unit = text[start + i];
                inputs[2 * i] = new Input { Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput { Scan = unit, Flags = Unicode } } };
                inputs[2 * i + 1] = new Input { Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput { Scan = unit, Flags = Unicode | KeyUp } } };
            }
            accepted += (int)SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) / 2;
            if (start + batch < text.Length) Thread.Sleep(2);
        }
        return accepted;
    }
}

/// <summary>Two very short, quiet sounds: one when the microphone opens, one when it closes.</summary>
static class Cues
{
    [DllImport("winmm.dll")] static extern bool PlaySound(IntPtr sound, IntPtr module, uint flags);
    const uint Async = 0x0001, NoDefault = 0x0002, Memory = 0x0004;

    static readonly IntPtr StartSound = Make(880, 0.05), StopSound = Make(587, 0.05);

    static IntPtr Make(double hertz, double seconds)
    {
        const int rate = 44100;
        int samples = (int)(rate * seconds);
        using var memory = new MemoryStream();
        using var writer = new BinaryWriter(memory);
        writer.Write("RIFF"u8); writer.Write(36 + samples * 2); writer.Write("WAVE"u8);
        writer.Write("fmt "u8); writer.Write(16); writer.Write((short)1); writer.Write((short)1);
        writer.Write(rate); writer.Write(rate * 2); writer.Write((short)2); writer.Write((short)16);
        writer.Write("data"u8); writer.Write(samples * 2);
        for (int i = 0; i < samples; i++)
        {
            double envelope = Math.Sin(Math.PI * i / samples);   // fades in and out, so no click
            writer.Write((short)(Math.Sin(2 * Math.PI * hertz * i / rate) * envelope * 0.12 * short.MaxValue));
        }
        writer.Flush();
        byte[] bytes = memory.ToArray();
        IntPtr unmanaged = Marshal.AllocHGlobal(bytes.Length);   // must outlive playback; kept for the life of the process
        Marshal.Copy(bytes, 0, unmanaged, bytes.Length);
        return unmanaged;
    }

    public static void Start() => PlaySound(StartSound, IntPtr.Zero, Memory | Async | NoDefault);
    public static void Stop() => PlaySound(StopSound, IntPtr.Zero, Memory | Async | NoDefault);
}

/// <summary>
/// Checks the typing path end to end without touching any of your apps: it opens its own small text
/// window, types into it, reads the text back and compares. It types only while that window is
/// confirmed to be in front; if Windows will not bring it forward, nothing is typed at all.
/// </summary>
static class TypingSelfTest
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateWindowExW(uint exStyle, string className, string title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern IntPtr SetFocus(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextLengthW(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr hwnd, [Out] char[] text, int max);

    static void Pump(int milliseconds)
    {
        long until = Environment.TickCount64 + milliseconds;
        while (Environment.TickCount64 < until)
        {
            while (KeyWatcher.PeekMessageW(out var message, IntPtr.Zero, 0, 0, 1))
            {
                KeyWatcher.TranslateMessage(ref message);
                KeyWatcher.DispatchMessageW(ref message);
            }
            Thread.Sleep(5);
        }
    }

    public static int Run()
    {
        const uint overlappedVisible = 0x10CF0000, multiline = 0x0004 | 0x0040;
        string[] samples =
        {
            "The quick brown fox jumps over the lazy dog, twice. ",
            "मेरा phone number है 9840950950, कल सुबह call करना। ",
        };
        string expected = string.Concat(samples);

        // An edit control's title is also its text, so the title is left empty.
        IntPtr hwnd = CreateWindowExW(0x00000008 /* topmost */, "EDIT", "",
            overlappedVisible | multiline, 120, 120, 760, 220, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (hwnd == IntPtr.Zero) { Console.WriteLine("Could not create the test window."); return 2; }

        try
        {
            SetForegroundWindow(hwnd);
            SetFocus(hwnd);
            Pump(600);
            if (GetForegroundWindow() != hwnd)
            {
                Console.WriteLine("Windows would not bring the test window to the front, so nothing was typed. Typing is unverified.");
                return 3;
            }

            int accepted = 0;
            foreach (string sample in samples)
            {
                accepted += TextTyper.Type(sample, () => GetForegroundWindow() == hwnd);
                Pump(250);
            }
            Pump(300);

            var buffer = new char[GetWindowTextLengthW(hwnd) + 1];
            string got = new(buffer, 0, GetWindowTextW(hwnd, buffer, buffer.Length));
            bool match = got == expected;
            Console.WriteLine($"Typed {expected.Length} characters; Windows accepted {accepted}; the window received {got.Length}.");
            Console.WriteLine(match ? "PASS: the text arrived exactly, English and Hindi with digits and punctuation."
                                    : $"FAIL: expected [{expected}] but the window holds [{got}]");
            return match ? 0 : 1;
        }
        finally
        {
            DestroyWindow(hwnd);
        }
    }
}
