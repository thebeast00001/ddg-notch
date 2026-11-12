using Forty.Platform;

namespace Forty.Tests;

public static class SecretStoreTests
{
    [Test]
    public static void A_secret_survives_a_restart_and_is_unreadable_in_the_file()
    {
        string path = Path.Combine(Path.GetTempPath(), $"forty-secrets-{Environment.ProcessId}.bin");
        const string value = "made-up-value-0123456789";
        try
        {
            new SecretStore(path).Set("TEST_KEY", value);
            byte[] bytes = File.ReadAllBytes(path);
            Check.True(!System.Text.Encoding.UTF8.GetString(bytes).Contains(value) && !System.Text.Encoding.Unicode.GetString(bytes).Contains(value), "not readable in the file");
            var again = new SecretStore(path);
            Check.Equal(value, again.Get("TEST_KEY"), "same value after a restart");
            Check.True(again.Names.SequenceEqual(new[] { "TEST_KEY" }), "names can be listed, values are never returned in bulk");
            again.Set("TEST_KEY", "your-key-here");
            Check.True(again.Get("TEST_KEY") == null, "placeholder text is not a key");
            again.Set("TEST_KEY", null);
            Check.True(!File.Exists(path), "removing the last secret removes the file");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Test]
    public static void A_damaged_file_reads_as_empty_without_an_error()
    {
        string path = Path.Combine(Path.GetTempPath(), $"forty-secrets-bad-{Environment.ProcessId}.bin");
        try
        {
            File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4, 5, 6 });
            Check.True(new SecretStore(path).Get("ANY") == null, "nothing, and no exception");
        }
        finally { File.Delete(path); }
    }
}
