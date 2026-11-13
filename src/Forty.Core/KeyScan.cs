using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Forty.Core;

/// <summary>
/// F121's check, made automatic: does any file Forty wrote contain key text? Every file in Forty's
/// folder (flags, settings, diagnostics, crash reports; never the encrypted key file itself) is searched
/// for each stored key, whole or in part, and any key text found is cut out of the file. What comes out
/// is counts and Forty's own file names only, never a value and never where in a file it was.
/// </summary>
public static class KeyScan
{
    /// <summary>The shortest run of a key's characters that counts as key text.</summary>
    public const int Piece = 12;

    /// <summary>Keys shorter than this are not searched for: too short to tell apart from ordinary text.</summary>
    public const int Shortest = 8;

    const long LargestFile = 16 * 1024 * 1024;
    public const string Removed = "[key removed]";

    /// <summary>How many files were read, which of them held key text (now cut out), and how many could not be read.</summary>
    public sealed record Result(int Files, IReadOnlyList<string> Cleaned, int Unreadable);

    /// <summary>A piece of one key: its text, the key it came from and where in the key it starts (-1: an escaped form).</summary>
    readonly record struct Probe(string Text, string Key, int Offset);

    /// <summary>
    /// The pieces to look for: each whole key, and 12-character pieces starting every 6 characters (plus
    /// the last 12), so its start, its end and any run of 17 or more of its characters are found. Each
    /// piece is also looked for as JSON writes it, because JSON escapes characters such as "+".
    /// </summary>
    static List<Probe> Probes(IEnumerable<string> keys)
    {
        var probes = new List<Probe>();
        foreach (var key in keys.Where(k => k != null && k.Length >= Shortest).Distinct())
        {
            probes.Add(new Probe(key, key, 0));
            if (key.Length > Piece)
            {
                for (int at = 0; at + Piece <= key.Length; at += Piece / 2) probes.Add(new Probe(key.Substring(at, Piece), key, at));
                probes.Add(new Probe(key[^Piece..], key, key.Length - Piece));
            }
        }
        foreach (var probe in probes.ToList())
        {
            string escaped = JsonEncodedText.Encode(probe.Text, JavaScriptEncoder.Default).Value;
            if (escaped != probe.Text) probes.Add(new Probe(escaped, probe.Key, -1));
        }
        return probes;
    }

    /// <summary>Cuts every piece of every key out of <paramref name="text"/>. Returns the text and whether anything was found.</summary>
    public static (string Text, bool Found) Scrub(string text, IEnumerable<string> keys) => Scrub(text, Probes(keys));

    static (string Text, bool Found) Scrub(string text, List<Probe> probes)
    {
        bool[]? cut = null;
        foreach (var probe in probes)
        {
            for (int at = text.IndexOf(probe.Text, StringComparison.Ordinal); at >= 0; at = text.IndexOf(probe.Text, at + 1, StringComparison.Ordinal))
            {
                cut ??= new bool[text.Length];
                int start = at, end = at + probe.Text.Length;
                if (probe.Offset >= 0)
                {
                    // Widen to everything around the piece that still lines up with the key, so no part of it stays.
                    int k = probe.Offset;
                    while (start > 0 && k > 0 && text[start - 1] == probe.Key[k - 1]) { start--; k--; }
                    k = probe.Offset + probe.Text.Length;
                    while (end < text.Length && k < probe.Key.Length && text[end] == probe.Key[k]) { end++; k++; }
                }
                for (int i = start; i < end; i++) cut[i] = true;
            }
        }
        if (cut == null) return (text, false);
        var clean = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            if (!cut[i]) { clean.Append(text[i]); continue; }
            clean.Append(Removed);
            while (i + 1 < text.Length && cut[i + 1]) i++;
        }
        return (clean.ToString(), true);
    }

    /// <summary>
    /// Searches every file under <paramref name="folder"/> except <paramref name="skip"/> (file names),
    /// and rewrites any file that held key text without it. Files that are too large or not text are passed over.
    /// </summary>
    public static Result Run(string folder, IEnumerable<string> keys, IEnumerable<string> skip)
    {
        var probes = Probes(keys);
        var skipped = new HashSet<string>(skip, StringComparer.OrdinalIgnoreCase);
        var cleaned = new List<string>();
        int files = 0, unreadable = 0;
        if (!Directory.Exists(folder) || probes.Count == 0) return new Result(0, cleaned, 0);
        foreach (var path in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            if (skipped.Contains(Path.GetFileName(path))) continue;
            try
            {
                if (new FileInfo(path).Length > LargestFile) continue;
                byte[] bytes = File.ReadAllBytes(path);
                if (Array.IndexOf(bytes, (byte)0) >= 0) continue;   // not text
                files++;
                var (text, found) = Scrub(Encoding.UTF8.GetString(bytes), probes);
                if (!found) continue;
                File.WriteAllText(path, text, new UTF8Encoding(false));
                cleaned.Add(Path.GetRelativePath(folder, path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { unreadable++; }
        }
        return new Result(files, cleaned, unreadable);
    }
}
