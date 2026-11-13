using System.Reflection;
using System.Runtime.CompilerServices;

namespace Forty.Tests;

/// <summary>Marks a method as a test. Tests are public static void methods in this assembly.</summary>
[AttributeUsage(AttributeTargets.Method)]
sealed class TestAttribute : Attribute;

/// <summary>Raised by a failed check; carries the line it failed on.</summary>
sealed class CheckFailed(string message) : Exception(message);

static class Check
{
    public static void True(bool condition, string what, [CallerLineNumber] int line = 0)
    {
        if (!condition) throw new CheckFailed($"{what} (line {line})");
    }

    public static void Equal<T>(T expected, T actual, string what, [CallerLineNumber] int line = 0)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new CheckFailed($"{what}: expected {expected}, got {actual} (line {line})");
    }

    public static void Near(double expected, double actual, double tolerance, string what, [CallerLineNumber] int line = 0)
    {
        if (double.IsNaN(actual) || Math.Abs(expected - actual) > tolerance)
            throw new CheckFailed($"{what}: expected {expected:0.####} ± {tolerance}, got {actual:0.####} (line {line})");
    }
}

static class Runner
{
    static int Main(string[] args)
    {
        if (args.FirstOrDefault() == "lyrics-check") return LyricsCheck.Run();
        if (args.FirstOrDefault() == "bt-events") return BluetoothEvents.Run(args.Length > 1 ? double.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) : 20, args.Length > 2 ? args[2] : "both");
        if (args.FirstOrDefault() == "speech-idle" && args.Length >= 3) return SpeechIdle.Run(double.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture), args[2]);
        if (args.FirstOrDefault() == "bt-cost") return BluetoothCost.Run(args.Length > 1 && double.TryParse(args[1], out double w) ? w : 120);
        if (args.FirstOrDefault() == "probe") return Probe.Run(args.Length > 1 && double.TryParse(args[1], out double s) ? s : 3);
        string? filter = args.FirstOrDefault();
        var tests = typeof(Runner).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Where(m => m.GetCustomAttribute<TestAttribute>() != null)
            .Where(m => filter == null || $"{m.DeclaringType!.Name}.{m.Name}".Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(m => m.DeclaringType!.Name).ThenBy(m => m.MetadataToken)
            .ToList();

        int failed = 0;
        string? group = null;
        foreach (var test in tests)
        {
            if (test.DeclaringType!.Name != group) { group = test.DeclaringType.Name; Console.WriteLine(group); }
            string name = Words(test.Name);
            try
            {
                test.Invoke(null, null);
                Console.WriteLine($"  pass  {name}");
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                failed++;
                Console.WriteLine($"  FAIL  {name}: {ex.InnerException.Message}");
            }
        }
        Console.WriteLine(failed == 0 ? $"All {tests.Count} tests passed." : $"{failed} of {tests.Count} tests FAILED.");
        return failed;
    }

    /// <summary>"Ambient_leaves_four_seconds_after_shown" reads as a sentence.</summary>
    static string Words(string name) => name.Replace('_', ' ');
}
