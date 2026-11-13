using Forty.Core;

namespace Forty.Tests;

public static class KeyScanTests
{
    // Made-up keys, shaped like real ones.
    const string Key = "demo_key_4f9a1c27e0b84d6c9a1f3e5b7d2c8a6";
    const string Plus = "AbC+dEf/GhI+jKl=mNo+pQr";

    [Test]
    public static void A_whole_key_is_cut_out_and_nothing_else_changes()
    {
        var (text, found) = Scan($"{{\"note\":\"x\",\"auth\":\"Bearer {Key}\"}}");
        Check.True(found, "found");
        Check.Equal($"{{\"note\":\"x\",\"auth\":\"Bearer {KeyScan.Removed}\"}}", text, "only the key is replaced");
    }

    [Test]
    public static void Part_of_a_key_is_found_and_all_of_that_part_is_removed()
    {
        // A key cut short in a log line, and a run from the middle of one.
        var (start, a) = Scan("using " + Key[..14] + "… for speech");
        Check.True(a && !start.Contains(Key[..6]), "its start, all of it");
        var (middle, b) = Scan("x" + Key[5..27] + "y");
        Check.Equal("x" + KeyScan.Removed + "y", middle, "a run from the middle, all of it");
        Check.True(b, "found");
    }

    [Test]
    public static void Short_coincidences_are_not_key_text()
    {
        var (text, found) = Scan("demo_key_ is a prefix many keys share; " + Key[10..20]);
        Check.True(!found, "fewer than 12 characters in a row is not a key");
        Check.Equal("demo_key_ is a prefix many keys share; " + Key[10..20], text, "unchanged");
    }

    [Test]
    public static void A_key_is_found_as_json_writes_it()
    {
        string json = System.Text.Json.JsonSerializer.Serialize(new { key = Plus });   // "+" is written as +
        Check.True(!json.Contains(Plus), "the test needs JSON to have escaped it");
        Check.True(KeyScan.Scrub(json, new[] { Plus }).Found, "found in its escaped form");
    }

    [Test]
    public static void A_folder_scan_cleans_files_with_key_text_and_leaves_the_rest_alone()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"forty-keyscan-{Environment.ProcessId}");
        Directory.CreateDirectory(Path.Combine(folder, "diagnostics"));
        try
        {
            File.WriteAllText(Path.Combine(folder, "flags.json"), "{ \"lyrics\": \"on\" }");
            File.WriteAllText(Path.Combine(folder, "diagnostics", "crash-1.json"), $"{{ \"error\": \"failed with {Key}\" }}");
            File.WriteAllText(Path.Combine(folder, "keys.bin"), Key);   // the store itself is never touched
            var cleanBefore = File.GetLastWriteTimeUtc(Path.Combine(folder, "flags.json"));

            var result = KeyScan.Run(folder, new[] { Key }, new[] { "keys.bin" });
            Check.Equal(2, result.Files, "two files read; the key store passed over");
            Check.True(result.Cleaned.SequenceEqual(new[] { Path.Combine("diagnostics", "crash-1.json") }), "the crash report was cleaned");
            Check.True(!File.ReadAllText(Path.Combine(folder, "diagnostics", "crash-1.json")).Contains(Key[..12]), "no key text left in it");
            Check.Equal(cleanBefore, File.GetLastWriteTimeUtc(Path.Combine(folder, "flags.json")), "a clean file is not rewritten");
            Check.Equal(Key, File.ReadAllText(Path.Combine(folder, "keys.bin")), "the key store is left alone");
            Check.True(KeyScan.Run(folder, new[] { Key }, new[] { "keys.bin" }).Cleaned.Count == 0, "a second scan finds nothing");
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    static (string Text, bool Found) Scan(string text) => KeyScan.Scrub(text, new[] { Key });
}
