namespace Forty.Core;

/// <summary>
/// Forty's own sounds, made from numbers when they are first needed: no sound files ship and nothing is
/// borrowed from anyone's sound set. Each is a 16-bit mono WAV at 44.1 kHz.
/// <list type="bullet">
/// <item><b>Tick</b>: the ruler's detent, a 12 ms click with a small wooden resonance; quiet, like a
/// watch crown, so a fast throw becomes a soft ratchet.</item>
/// <item><b>Start</b>: two soft bell notes a fifth apart, rising (the timer is off and running).</item>
/// <item><b>Alarm</b>: a gentle four-note marimba figure, played twice, then a breath of silence. It
/// loops until you stop it, or for a minute.</item>
/// </list>
/// </summary>
public static class SoundMaker
{
    public const int Rate = 44100;

    public static byte[] Tick()
    {
        int n = (int)(Rate * 0.012);
        var s = new float[n];
        var noise = new Random(40);
        for (int i = 0; i < n; i++)
        {
            double t = i / (double)Rate;
            double body = 0.62 * Math.Sin(2 * Math.PI * 2350 * t) + 0.28 * Math.Sin(2 * Math.PI * 4170 * t);
            double click = (noise.NextDouble() * 2 - 1) * Math.Exp(-t / 0.00045);
            s[i] = (float)(body * Math.Exp(-t / 0.0021) + 0.35 * click);
        }
        return Wav(Normalise(s, 0.20, attack: 4));   // a click keeps its edge: a 0.1 ms ramp, not 2 ms
    }

    public static byte[] Start()
    {
        var s = new float[(int)(Rate * 0.62)];
        Bell(s, 0.00, 1046.50, 0.42, 0.55);   // C6
        Bell(s, 0.11, 1567.98, 0.50, 0.60);   // G6
        return Wav(Normalise(s, 0.30));
    }

    public static byte[] Alarm()
    {
        var s = new float[(int)(Rate * 2.4)];
        double[] figure = { 1046.50, 1318.51, 1567.98, 1318.51 };   // C6 E6 G6 E6
        for (int round = 0; round < 2; round++)
            for (int k = 0; k < figure.Length; k++)
                Marimba(s, round * 0.82 + k * 0.135, figure[k], 0.65);
        return Wav(Normalise(s, 0.42));
    }

    /// <summary>A soft bell: a sine with a quiet inharmonic partial, a 4 ms attack and an exponential fade.</summary>
    static void Bell(float[] s, double at, double hz, double decay, double gain)
    {
        int start = (int)(at * Rate);
        for (int i = start; i < s.Length; i++)
        {
            double t = (i - start) / (double)Rate;
            double env = Math.Min(1, t / 0.004) * Math.Exp(-t / (decay / 3));
            s[i] += (float)(gain * env * (Math.Sin(2 * Math.PI * hz * t) + 0.18 * Math.Sin(2 * Math.PI * hz * 2.76 * t) * Math.Exp(-t / 0.05)));
        }
    }

    /// <summary>A struck bar: the fundamental and its fourth harmonic, which dies away quickly, as wood does.</summary>
    static void Marimba(float[] s, double at, double hz, double gain)
    {
        int start = (int)(at * Rate);
        int end = Math.Min(s.Length, start + (int)(Rate * 0.7));
        for (int i = start; i < end; i++)
        {
            double t = (i - start) / (double)Rate;
            double env = Math.Min(1, t / 0.002) * Math.Exp(-t / 0.16);
            double tail = Math.Min(1, (end - i) / (Rate * 0.02));   // never cut off with a click
            s[i] += (float)(gain * env * tail * (Math.Sin(2 * Math.PI * hz * t) + 0.32 * Math.Sin(2 * Math.PI * hz * 4 * t) * Math.Exp(-t / 0.025)));
        }
    }

    static float[] Normalise(float[] s, double peak, int attack = Rate / 500)
    {
        float max = 0;
        foreach (float v in s) max = Math.Max(max, Math.Abs(v));
        if (max > 0) for (int i = 0; i < s.Length; i++) s[i] = (float)(s[i] / max * peak);
        // A fade at both ends (2 ms unless asked), so no sound starts or stops with a pop.
        int edge = Math.Min(s.Length / 2, Rate / 500), rise = Math.Min(s.Length / 2, attack);
        for (int i = 0; i < rise; i++) s[i] *= i / (float)rise;
        for (int i = 0; i < edge; i++) s[s.Length - 1 - i] *= i / (float)edge;
        return s;
    }

    /// <summary>The samples as a WAV file: RIFF header, 16-bit little-endian PCM.</summary>
    public static byte[] Wav(float[] samples)
    {
        int data = samples.Length * 2;
        var wav = new byte[44 + data];
        void Text(int at, string t) { for (int i = 0; i < 4; i++) wav[at + i] = (byte)t[i]; }
        void U32(int at, int v) => BitConverter.TryWriteBytes(wav.AsSpan(at, 4), v);
        void U16(int at, short v) => BitConverter.TryWriteBytes(wav.AsSpan(at, 2), v);
        Text(0, "RIFF"); U32(4, 36 + data); Text(8, "WAVE");
        Text(12, "fmt "); U32(16, 16); U16(20, 1); U16(22, 1); U32(24, Rate); U32(28, Rate * 2); U16(32, 2); U16(34, 16);
        Text(36, "data"); U32(40, data);
        for (int i = 0; i < samples.Length; i++)
            BitConverter.TryWriteBytes(wav.AsSpan(44 + i * 2, 2), (short)Math.Round(Math.Clamp(samples[i], -1f, 1f) * short.MaxValue));
        return wav;
    }

    /// <summary>The loudest sample of a WAV made here, 0 to 1 (for the tests).</summary>
    public static double Peak(byte[] wav)
    {
        int max = 0;
        for (int i = 44; i + 1 < wav.Length; i += 2) max = Math.Max(max, Math.Abs((int)BitConverter.ToInt16(wav, i)));
        return max / (double)short.MaxValue;
    }

    public static double Seconds(byte[] wav) => (wav.Length - 44) / 2.0 / Rate;
}
