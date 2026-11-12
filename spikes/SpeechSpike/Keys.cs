using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace SpeechSpike;

/// <summary>
/// Where the service keys are kept. You type each key once into this program; it is checked with
/// the service and then stored encrypted by Windows for your user account (the same protection
/// browsers use for saved passwords), in a file only this account can decrypt. A key is never
/// printed, logged, or put in the project folder.
/// </summary>
static class Keys
{
    public const string Sarvam = "SARVAM_API_KEY", ElevenLabs = "ELEVENLABS_API_KEY";

    [StructLayout(LayoutKind.Sequential)]
    struct Blob { public int Size; public IntPtr Data; }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr memory);
    const int NoPrompt = 0x1;

    static readonly object Gate = new();
    static Dictionary<string, string>? _stored;

    public static string StorePath =>
        Environment.GetEnvironmentVariable("FORTY_KEYS_FILE") is { Length: > 0 } given ? given
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Forty", "keys.bin");

    static bool Real(string? value) => !string.IsNullOrWhiteSpace(value) && value.Trim() != "your-key-here";

    /// <summary>
    /// The key to use: one set for this process only (tests), else the stored one, else a
    /// Windows user variable of the same name. Placeholder text counts as no key.
    /// </summary>
    public static string? Find(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (Real(value)) return value!.Trim();
        if (Load().TryGetValue(name, out string? stored) && Real(stored)) return stored;
        value = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);
        return Real(value) ? value!.Trim() : null;
    }

    public static bool IsStored(string name) => Load().ContainsKey(name);

    /// <summary>Stores a key, or removes it when <paramref name="value"/> is null.</summary>
    public static void Save(string name, string? value)
    {
        lock (Gate)
        {
            var all = new Dictionary<string, string>(Load());
            if (value == null) all.Remove(name); else all[name] = value.Trim();
            string path = StorePath;
            if (all.Count == 0) { if (File.Exists(path)) File.Delete(path); }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, Protect(JsonSerializer.SerializeToUtf8Bytes(all)));
            }
            _stored = all;
        }
    }

    static Dictionary<string, string> Load()
    {
        lock (Gate)
        {
            if (_stored != null) return _stored;
            _stored = new();
            try
            {
                if (File.Exists(StorePath))
                    _stored = JsonSerializer.Deserialize<Dictionary<string, string>>(Unprotect(File.ReadAllBytes(StorePath))) ?? new();
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or UnauthorizedAccessException)
            {
                // A file from another account or another PC cannot be decrypted; it is treated as empty.
            }
            return _stored;
        }
    }

    static byte[] Protect(byte[] plain) => Transform(plain, protect: true);
    static byte[] Unprotect(byte[] sealedBytes) => Transform(sealedBytes, protect: false);

    static byte[] Transform(byte[] data, bool protect)
    {
        var pinned = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            var input = new Blob { Size = data.Length, Data = pinned.AddrOfPinnedObject() };
            bool ok = protect ? CryptProtectData(ref input, "Forty service keys", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, NoPrompt, out var output)
                              : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, NoPrompt, out output);
            if (!ok) throw new InvalidOperationException("Windows could not " + (protect ? "encrypt" : "decrypt") + " the keys file.");
            try
            {
                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, output.Size);
                return result;
            }
            finally { LocalFree(output.Data); }
        }
        finally { pinned.Free(); }
    }

    // ───────────────────────────── Checking a key with its service ─────────────────────────────

    public sealed record Verdict(bool Works, string Detail, double Ms);

    /// <summary>
    /// Sends half a second of a test sentence to Sarvam's speech service and one word to its chat
    /// model. Both are used: speech for Hindi dictation, chat for call summaries.
    /// </summary>
    public static Verdict CheckSarvam(string key, Clip sample)
    {
        long start = Stopwatch.GetTimestamp();
        try
        {
            using var speech = new SarvamEngine(key, "en");
            speech.Transcribe(sample);
            double speechMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.sarvam.ai/v1/chat/completions")
            {
                Content = new StringContent("{\"model\":\"sarvam-105b-conversations\",\"max_tokens\":1,\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}", Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            request.Headers.Add("api-subscription-key", key);
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var response = Net.Http.SendAsync(request, limit.Token).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
                return new Verdict(false, $"speech works, but the chat model answered {(int)response.StatusCode}", speechMs);
            return new Verdict(true, "speech and chat both answer", speechMs);
        }
        catch (Exception ex) { return new Verdict(false, Plain(ex), Stopwatch.GetElapsedTime(start).TotalMilliseconds); }
    }

    public static Verdict CheckElevenLabs(string key, Clip sample)
    {
        long start = Stopwatch.GetTimestamp();
        try
        {
            using var speech = new ElevenLabsEngine(key, "en");
            speech.Transcribe(sample);
            return new Verdict(true, "speech answers", Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }
        catch (Exception ex) { return new Verdict(false, Plain(ex), Stopwatch.GetElapsedTime(start).TotalMilliseconds); }
    }

    /// <summary>An error as one short sentence a person can act on.</summary>
    public static string Plain(Exception ex)
    {
        if (ex is AggregateException many) ex = many.Flatten().InnerExceptions.FirstOrDefault() ?? ex;
        string text = ex.Message;
        if (text.Contains("401") || text.Contains("403") || text.Contains("invalid_api_key", StringComparison.OrdinalIgnoreCase) || text.Contains("Invalid API", StringComparison.OrdinalIgnoreCase))
            return "the service rejected this key";
        if (ex is HttpRequestException || text.Contains("No such host", StringComparison.OrdinalIgnoreCase))
            return "the service could not be reached (no internet?)";
        if (ex is OperationCanceledException || text.Contains("did not answer")) return "the service did not answer in time";
        return text.Length > 160 ? text[..160] : text;
    }

    // ───────────────────────────── The one-time setup ─────────────────────────────

    /// <summary>Asks for each key, checks it, stores it. Typing is hidden. Enter alone keeps what is there.</summary>
    public static int Setup(Clip sample)
    {
        Console.WriteLine("Service keys for Forty. Each key is checked with its service, then stored encrypted");
        Console.WriteLine("for your Windows account in " + StorePath);
        Console.WriteLine("Paste a key and press Enter. Nothing you type is shown. Enter alone keeps the current key.");
        Console.WriteLine();
        int problems = 0;
        problems += Ask("Sarvam", Sarvam, "dashboard.sarvam.ai, API keys", key => CheckSarvam(key, sample));
        problems += Ask("ElevenLabs", ElevenLabs, "elevenlabs.io, Developers, API keys (it needs Speech to Text access)", key => CheckElevenLabs(key, sample));
        Console.WriteLine();
        Console.WriteLine(problems == 0 ? "Done. Start Forty with start-forty.cmd." : "Not everything is set. Run this again when you have the keys.");
        return problems == 0 ? 0 : 1;

        static int Ask(string service, string name, string where, Func<string, Verdict> check)
        {
            bool have = Find(name) != null;
            Console.Write($"{service} key ({where}){(have ? " [one is already set]" : "")}: ");
            string typed = ReadHidden();
            if (typed.Length == 0)
            {
                if (!have) { Console.WriteLine("  skipped; nothing stored."); return 1; }
                var current = check(Find(name)!);
                Console.WriteLine(current.Works ? $"  kept. It works: {current.Detail}." : $"  kept, but it does not work: {current.Detail}.");
                return current.Works ? 0 : 1;
            }
            var verdict = check(typed);
            if (!verdict.Works) { Console.WriteLine($"  not stored: {verdict.Detail}."); return 1; }
            Save(name, typed);
            Console.WriteLine($"  works ({verdict.Detail}) and is stored.");
            return 0;
        }
    }

    /// <summary>
    /// Keys are plain letters, digits and a few signs. A paste can carry things nobody sees (an
    /// invisible mark at the start, a quote, a space or line break), and they would make the key
    /// fail in a confusing way, so everything else is dropped.
    /// </summary>
    static string Clean(string typed) => new(typed.Where(c => c > ' ' && c < (char)127 && c != '"' && c != '\'').ToArray());

    static string ReadHidden()
    {
        if (Console.IsInputRedirected) return Clean(Console.ReadLine() ?? "");
        var text = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace) { if (text.Length > 0) text.Length--; continue; }
            if (!char.IsControl(key.KeyChar)) text.Append(key.KeyChar);
        }
        Console.WriteLine();
        return Clean(text.ToString());
    }

    /// <summary>
    /// Checks the store itself with a made-up value in a throwaway file: it comes back the same,
    /// the file does not contain it in readable form, and removing it removes the file.
    /// </summary>
    public static int SelfTest()
    {
        string file = Path.Combine(Path.GetTempPath(), $"forty-keys-test-{Environment.ProcessId}.bin");
        string? before = Environment.GetEnvironmentVariable("FORTY_KEYS_FILE");
        Environment.SetEnvironmentVariable("FORTY_KEYS_FILE", file);
        int failed = 0;
        void Expect(bool ok, string what) { Console.WriteLine((ok ? "  pass  " : "  FAIL  ") + what); if (!ok) failed++; }
        try
        {
            const string name = "FORTY_TEST_KEY", value = "made-up-value-1234567890";
            lock (Gate) _stored = null;
            Save(name, value);
            Expect(File.Exists(file), "saving creates the keys file");
            byte[] bytes = File.ReadAllBytes(file);
            Expect(!Encoding.UTF8.GetString(bytes).Contains(value) && !Encoding.Unicode.GetString(bytes).Contains(value), "the value is not readable in the file");
            lock (Gate) _stored = null;   // as a fresh start of the program would
            Expect(Find(name) == value, "the value comes back unchanged after a restart");
            Save(name, "your-key-here");
            Expect(Find(name) == null, "placeholder text is not treated as a key");
            File.WriteAllBytes(file, new byte[] { 1, 2, 3, 4, 5 });
            lock (Gate) _stored = null;
            Expect(Find(name) == null, "a damaged keys file is treated as empty, without an error");
            Save(name, value);
            Save(name, null);
            Expect(!File.Exists(file), "removing the last key removes the file");
        }
        finally
        {
            if (File.Exists(file)) File.Delete(file);
            Environment.SetEnvironmentVariable("FORTY_KEYS_FILE", before);
            lock (Gate) _stored = null;
        }
        Console.WriteLine(failed == 0 ? "All checks passed." : $"{failed} checks FAILED.");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>Which keys are there and where from. Never the keys themselves.</summary>
    public static void PrintStatus()
    {
        foreach (var (service, name) in new[] { ("Sarvam", Sarvam), ("ElevenLabs", ElevenLabs) })
        {
            string where = Real(Environment.GetEnvironmentVariable(name)) ? "set for this window only"
                         : IsStored(name) ? "stored (encrypted for your Windows account)"
                         : Real(Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User)) ? "set as a Windows user variable"
                         : "not set";
            Console.WriteLine($"  {service,-11} {where}");
        }
    }
}
