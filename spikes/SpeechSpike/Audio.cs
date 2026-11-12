using System.Runtime.InteropServices;

namespace SpeechSpike;

/// <summary>Mono audio as floats in [-1, 1].</summary>
sealed record Clip(float[] Samples, int SampleRate)
{
    public double Seconds => Samples.Length / (double)SampleRate;

    public Clip Take(double seconds) =>
        new(Samples.AsSpan(0, Math.Min(Samples.Length, (int)(seconds * SampleRate))).ToArray(), SampleRate);

    public static Clip Concat(IEnumerable<Clip> clips)
    {
        var list = clips.ToList();
        return new Clip(list.SelectMany(c => c.Samples).ToArray(), list[0].SampleRate);
    }

    /// <summary>Bare 16-bit little-endian PCM, with no file header.</summary>
    public byte[] ToPcm16Bytes()
    {
        var bytes = new byte[Samples.Length * 2];
        for (int i = 0; i < Samples.Length; i++)
        {
            short value = (short)Math.Clamp((int)MathF.Round(Samples[i] * 32767f), short.MinValue, short.MaxValue);
            bytes[2 * i] = (byte)value;
            bytes[2 * i + 1] = (byte)(value >> 8);
        }
        return bytes;
    }

    /// <summary>16-bit PCM WAV bytes, for sending to a web API.</summary>
    public byte[] ToWavBytes()
    {
        using var memory = new MemoryStream();
        using var writer = new BinaryWriter(memory);
        int dataBytes = Samples.Length * 2;
        writer.Write("RIFF"u8); writer.Write(36 + dataBytes); writer.Write("WAVE"u8);
        writer.Write("fmt "u8); writer.Write(16); writer.Write((short)1); writer.Write((short)1);
        writer.Write(SampleRate); writer.Write(SampleRate * 2); writer.Write((short)2); writer.Write((short)16);
        writer.Write("data"u8); writer.Write(dataBytes);
        foreach (float s in Samples) writer.Write((short)Math.Clamp((int)MathF.Round(s * 32767f), short.MinValue, short.MaxValue));
        writer.Flush();
        return memory.ToArray();
    }
}

static class Wav
{
    /// <summary>Reads PCM 16-bit or 32-bit float WAV files; several channels are averaged to one.</summary>
    public static Clip Read(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        if (new string(reader.ReadChars(4)) != "RIFF") throw new InvalidDataException("Not a RIFF file: " + path);
        reader.ReadInt32();
        if (new string(reader.ReadChars(4)) != "WAVE") throw new InvalidDataException("Not a WAVE file: " + path);

        int format = 0, channels = 0, rate = 0, bits = 0;
        while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
        {
            string id = new(reader.ReadChars(4));
            int size = reader.ReadInt32();
            long next = reader.BaseStream.Position + size + (size & 1);
            if (id == "fmt ")
            {
                format = reader.ReadUInt16();
                channels = reader.ReadUInt16();
                rate = reader.ReadInt32();
                reader.ReadInt32(); reader.ReadUInt16();
                bits = reader.ReadUInt16();
                if (format == 0xFFFE && size >= 26) { reader.ReadBytes(8); format = reader.ReadUInt16(); }   // extensible
            }
            else if (id == "data")
            {
                if (channels == 0) throw new InvalidDataException("WAV data before format: " + path);
                int available = (int)Math.Min(size, reader.BaseStream.Length - reader.BaseStream.Position);
                byte[] raw = reader.ReadBytes(available);
                int bytesPerSample = bits / 8, frames = raw.Length / (bytesPerSample * channels);
                var samples = new float[frames];
                for (int i = 0; i < frames; i++)
                {
                    float sum = 0;
                    for (int c = 0; c < channels; c++)
                    {
                        int offset = (i * channels + c) * bytesPerSample;
                        sum += (format, bits) switch
                        {
                            (1, 16) => BitConverter.ToInt16(raw, offset) / 32768f,
                            (3, 32) => BitConverter.ToSingle(raw, offset),
                            _ => throw new NotSupportedException($"WAV format {format}/{bits} bits: {path}"),
                        };
                    }
                    samples[i] = sum / channels;
                }
                return new Clip(samples, rate);
            }
            reader.BaseStream.Position = next;
        }
        throw new InvalidDataException("No audio data in " + path);
    }
}

/// <summary>
/// Records from the default microphone at 16 kHz mono. Used only by the "record" mode, which a
/// person starts and stops by hand. Nothing here runs unless that mode is chosen.
/// </summary>
static class Microphone
{
    [StructLayout(LayoutKind.Sequential)]
    struct WaveFormat
    {
        public ushort FormatTag, Channels;
        public uint SamplesPerSec, AvgBytesPerSec;
        public ushort BlockAlign, BitsPerSample, Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WaveHeader
    {
        public IntPtr Data;
        public uint BufferLength, BytesRecorded;
        public IntPtr User;
        public uint Flags, Loops;
        public IntPtr Next, Reserved;
    }

    const uint WaveMapper = 0xFFFFFFFF, HeaderDone = 0x1;

    [DllImport("winmm.dll")] static extern int waveInOpen(out IntPtr handle, uint device, ref WaveFormat format, IntPtr callback, IntPtr instance, uint flags);
    [DllImport("winmm.dll")] static extern int waveInPrepareHeader(IntPtr handle, IntPtr header, int size);
    [DllImport("winmm.dll")] static extern int waveInUnprepareHeader(IntPtr handle, IntPtr header, int size);
    [DllImport("winmm.dll")] static extern int waveInAddBuffer(IntPtr handle, IntPtr header, int size);
    [DllImport("winmm.dll")] static extern int waveInStart(IntPtr handle);
    [DllImport("winmm.dll")] static extern int waveInReset(IntPtr handle);
    [DllImport("winmm.dll")] static extern int waveInClose(IntPtr handle);

    /// <summary>
    /// Records until <paramref name="stop"/> returns true. Each 50 ms of audio is also handed to
    /// <paramref name="onChunk"/> as it arrives, for engines that listen while you speak.
    /// </summary>
    public static Clip Record(Func<bool> stop, Action<short[]>? onChunk = null)
    {
        const int rate = 16000, bufferBytes = rate * 2 / 20, bufferCount = 16;   // 50 ms each, so the level meter keeps up with the voice
        var format = new WaveFormat
        {
            FormatTag = 1, Channels = 1, SamplesPerSec = rate, AvgBytesPerSec = rate * 2, BlockAlign = 2, BitsPerSample = 16,
        };
        int opened = waveInOpen(out IntPtr handle, WaveMapper, ref format, IntPtr.Zero, IntPtr.Zero, 0);
        if (opened != 0) throw new InvalidOperationException($"Could not open the microphone (error {opened}).");

        int headerSize = Marshal.SizeOf<WaveHeader>();
        var headers = new IntPtr[bufferCount];
        var collected = new List<short>(rate * 30);

        void Collect(IntPtr headerPtr, bool requeue)
        {
            var header = Marshal.PtrToStructure<WaveHeader>(headerPtr);
            if ((header.Flags & HeaderDone) == 0) return;
            int count = (int)header.BytesRecorded / 2;
            var chunk = new short[count];
            Marshal.Copy(header.Data, chunk, 0, count);
            collected.AddRange(chunk);
            if (count > 0) onChunk?.Invoke(chunk);
            header.BytesRecorded = 0;
            header.Flags &= ~HeaderDone;
            Marshal.StructureToPtr(header, headerPtr, false);
            if (requeue) waveInAddBuffer(handle, headerPtr, headerSize);
        }

        try
        {
            for (int i = 0; i < bufferCount; i++)
            {
                headers[i] = Marshal.AllocHGlobal(headerSize);
                var header = new WaveHeader { Data = Marshal.AllocHGlobal(bufferBytes), BufferLength = bufferBytes };
                Marshal.StructureToPtr(header, headers[i], false);
                waveInPrepareHeader(handle, headers[i], headerSize);
                waveInAddBuffer(handle, headers[i], headerSize);
            }
            waveInStart(handle);
            while (!stop())
            {
                Thread.Sleep(20);
                foreach (var h in headers) Collect(h, requeue: true);
            }
            waveInReset(handle);   // returns every buffer, including the partly filled one
            foreach (var h in headers) Collect(h, requeue: false);
        }
        finally
        {
            foreach (var h in headers)
            {
                if (h == IntPtr.Zero) continue;
                waveInUnprepareHeader(handle, h, headerSize);
                Marshal.FreeHGlobal(Marshal.PtrToStructure<WaveHeader>(h).Data);
                Marshal.FreeHGlobal(h);
            }
            waveInClose(handle);
        }
        return new Clip(collected.Select(s => s / 32768f).ToArray(), rate);
    }
}
