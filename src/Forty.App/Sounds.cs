using System.Runtime.InteropServices;
using Forty.Core;
using Forty.Motion;

namespace Forty.App;

/// <summary>
/// Plays Forty's own sounds (made by <see cref="SoundMaker"/>) through the system's sound player, from
/// memory that stays put for the life of the app. One sound plays at a time, as PlaySound does: a tick
/// never plays over the alarm, because ticks only come while you set a timer.
/// <para>The "sounds" flag turns the ruler's ticks and the start chime off; the alarm is the point of a
/// timer and always sounds. Demo and test runs never play anything.</para>
/// </summary>
sealed class Sounds
{
    [DllImport("winmm.dll", EntryPoint = "PlaySoundW")] static extern bool PlaySound(IntPtr sound, IntPtr module, uint flags);
    const uint Async = 0x0001, NoDefault = 0x0002, Memory = 0x0004, Loop = 0x0008, NoStop = 0x0010;

    public const string Flag = "sounds";

    static IntPtr _tick, _start, _alarm;
    readonly TickPacer _pacer = new();
    bool _alarming;

    /// <summary>True in demo and test runs: everything is silent.</summary>
    public static bool Silent { get; set; }

    /// <summary>Ticks and the start chime (the alarm ignores this).</summary>
    public bool On { get; set; } = true;

    static IntPtr Load(ref IntPtr slot, Func<byte[]> make)
    {
        if (slot != IntPtr.Zero) return slot;
        var bytes = make();
        slot = Marshal.AllocHGlobal(bytes.Length);   // kept for the life of the app: an async sound reads it while it plays
        Marshal.Copy(bytes, 0, slot, bytes.Length);
        return slot;
    }

    /// <summary>Makes the sounds ahead of the first tick, so the first one isn't late.</summary>
    public void Warm()
    {
        Load(ref _tick, SoundMaker.Tick);
        Load(ref _start, SoundMaker.Start);
        Load(ref _alarm, SoundMaker.Alarm);
    }

    public void Tick()
    {
        if (Silent || !On || _alarming || !_pacer.Allow(Clock.Now)) return;
        PlaySound(Load(ref _tick, SoundMaker.Tick), IntPtr.Zero, Async | NoDefault | Memory);
    }

    public void Start()
    {
        if (Silent || !On || _alarming) return;
        PlaySound(Load(ref _start, SoundMaker.Start), IntPtr.Zero, Async | NoDefault | Memory);
    }

    public void Alarm()
    {
        if (Silent) return;
        _alarming = true;
        PlaySound(Load(ref _alarm, SoundMaker.Alarm), IntPtr.Zero, Async | NoDefault | Memory | Loop);
    }

    public void StopAlarm()
    {
        if (!_alarming) return;
        _alarming = false;
        PlaySound(IntPtr.Zero, IntPtr.Zero, 0);
    }
}
