using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace SpeechSpike;

/// <summary>A test clip with the text that was actually said.</summary>
sealed record TestClip(string Name, Clip Audio, string Reference);

static class TestClips
{
    public static List<TestClip> Load(string clipsRoot, string language)
    {
        string folder = Path.Combine(clipsRoot, language), refs = Path.Combine(folder, "refs.tsv");
        var clips = new List<TestClip>();
        if (!File.Exists(refs)) return clips;
        foreach (string line in File.ReadAllLines(refs, Encoding.UTF8))
        {
            string[] parts = line.Split('\t', 2);
            if (parts.Length == 2 && File.Exists(Path.Combine(folder, parts[0])))
                clips.Add(new TestClip(parts[0], Wav.Read(Path.Combine(folder, parts[0])), parts[1]));
        }
        return clips;
    }

    /// <summary>
    /// Downloads a few short public clips with reference text: Hindi from IndicTTS (two studio
    /// speakers) and English from a small LibriSpeech sample. Each is converted to 16 kHz mono.
    /// </summary>
    public static async Task Fetch(string clipsRoot)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("speech-test-clips");
        await FetchSet(http, Path.Combine(clipsRoot, "hi"), "SPRINGLab/IndicTTS-Hindi", "default", "train", perWindow: 5, windows: 2);
        await FetchSet(http, Path.Combine(clipsRoot, "en"), "hf-internal-testing/librispeech_asr_dummy", "clean", "validation", perWindow: 10, windows: 1);
    }

    static async Task FetchSet(HttpClient http, string folder, string dataset, string config, string split, int perWindow, int windows)
    {
        Directory.CreateDirectory(folder);
        string rawFolder = Path.Combine(folder, "raw");
        Directory.CreateDirectory(rawFolder);
        var refs = new List<string>();
        int kept = 0, total = -1;

        for (int window = 0; window < windows; window++)
        {
            // The first window starts at the beginning; a second one starts late in the set, where
            // the other speaker's recordings are.
            int offset = window == 0 ? 0 : Math.Max(0, total - 400);
            int keptHere = 0;

            // Small pages: the server prepares each clip on request and times out on large ones.
            for (int page = 0; page < 5 && keptHere < perWindow; page++)
            {
            string url = $"https://datasets-server.huggingface.co/rows?dataset={Uri.EscapeDataString(dataset)}&config={config}&split={split}&offset={offset + page * 8}&length=8";
            using var rows = JsonDocument.Parse(await GetWithRetry(http, url));
            total = rows.RootElement.GetProperty("num_rows_total").GetInt32();

            foreach (var item in rows.RootElement.GetProperty("rows").EnumerateArray())
            {
                if (keptHere >= perWindow) break;
                var row = item.GetProperty("row");
                string text = row.GetProperty("text").GetString() ?? "";
                string? source = row.GetProperty("audio")[0].GetProperty("src").GetString();
                if (source == null || text.Length == 0) continue;

                string raw = Path.Combine(rawFolder, $"{kept:00}.bin"), name = $"{kept:00}.wav", wav = Path.Combine(folder, name);
                await File.WriteAllBytesAsync(raw, await http.GetByteArrayAsync(source));
                if (!ConvertTo16kMono(raw, wav)) continue;

                double seconds = Wav.Read(wav).Seconds;
                if (seconds < 3.5 || seconds > 12) { File.Delete(wav); continue; }   // dictation-length sentences only
                refs.Add($"{name}\t{text.Replace('\t', ' ').Replace('\n', ' ').Trim()}");
                kept++;
                keptHere++;
            }
            }
        }
        File.WriteAllLines(Path.Combine(folder, "refs.tsv"), refs, new UTF8Encoding(false));
        Console.WriteLine($"{dataset}: kept {kept} clips in {folder}");
    }

    static async Task<string> GetWithRetry(HttpClient http, string url)
    {
        for (int attempt = 1; ; attempt++)
        {
            try { return await http.GetStringAsync(url); }
            catch (HttpRequestException) when (attempt < 4) { await Task.Delay(2000 * attempt); }
        }
    }

    static bool ConvertTo16kMono(string input, string output)
    {
        var info = new ProcessStartInfo("ffmpeg")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
        };
        foreach (string arg in new[] { "-v", "error", "-y", "-i", input, "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", output })
            info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0 && File.Exists(output);
    }
}
