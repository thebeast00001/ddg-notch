using Forty.Core;
using Forty.Motion;
using Forty.Platform;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Forty.App;

public static class Program
{
    [STAThread]
    static void Main()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(parameters =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App();
        });
    }
}

sealed partial class App : Application
{
    public const double WindowWidth = 560, WindowHeight = 640;   // room for the full settings page
    IslandController? _controller;

    /// <summary>
    /// Forty's folder for settings, flags, keys and diagnostics: %LOCALAPPDATA%\Forty, or FORTY_HOME for
    /// tests. A scripted demo uses a folder of its own, so it never changes your flags, settings or keys.
    /// </summary>
    public static string Home => Environment.GetEnvironmentVariable("FORTY_HOME") is { Length: > 0 } home ? home
        : Environment.GetEnvironmentVariable("FORTY_DEMO") is { Length: > 0 } ? Path.Combine(Path.GetTempPath(), "forty-demo-home")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Forty");

    /// <summary>
    /// The flags a fresh install starts with (F149). An existing file is never overwritten. Sub-features
    /// that should be on by default (album colours) are on unless the file says otherwise.
    /// </summary>
    const string DefaultFlags = """
        {
          "audio-output": "on",
          "bluetooth": "on",
          "diagnostics": "you",
          "every-display": "off",
          "follow-display": "off",
          "glass": "off",
          "hide-from-capture": "off",
          "lyrics": "off",
          "music-colours": "on"
        }
        """;

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        string? log = Environment.GetEnvironmentVariable("FORTY_OUT");
        string diagnostics = Path.Combine(Home, "diagnostics");
        bool hold = Environment.GetEnvironmentVariable("FORTY_DEMO") == "hold";
        bool transitions = Environment.GetEnvironmentVariable("FORTY_DEMO") == "transitions";
        bool layout = Environment.GetEnvironmentVariable("FORTY_DEMO") == "layout";
        bool demo = Environment.GetEnvironmentVariable("FORTY_DEMO") == "1" || hold || transitions || layout;

        // Crash reports carry no personal data (F148) and stay on this PC.
        void Crash(Exception error, string where)
        {
            Log(log, $"{where}: {error.GetType().FullName}");
            var adapters = _controller?.Adapters.Select(a => a.Status) ?? Enumerable.Empty<AdapterStatus>();
            CrashReport.Save(diagnostics, CrashReport.Build(error, typeof(App).Assembly.GetName().Version?.ToString() ?? "?", adapters, _controller?.Meter));
        }
        UnhandledException += (_, e) => Crash(e.Exception, "unhandled");
        Guard.Report = (error, where) => Crash(error, "recovered in " + where);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => { if (e.ExceptionObject is Exception ex) Crash(ex, "unhandled (domain)"); };
        TaskScheduler.UnobservedTaskException += (_, e) => { Crash(e.Exception, "unobserved task"); e.SetObserved(); };

        try
        {
            Directory.CreateDirectory(Home);
            string flagsFile = Path.Combine(Home, "flags.json");
            if (demo && Environment.GetEnvironmentVariable("FORTY_HOME") is not { Length: > 0 })
                foreach (var file in new[] { flagsFile, Path.Combine(Home, "settings.json") }) File.Delete(file);   // each demo starts from the defaults
            if (!File.Exists(flagsFile)) File.WriteAllText(flagsFile, DefaultFlags);
            var flags = new FeatureFlags(flagsFile, developer: Environment.GetEnvironmentVariable("FORTY_CHANNEL") != "public");

            // FORTY_WINDOW_HEIGHT is for measuring only (what the window's size costs per frame).
            double height = double.TryParse(Environment.GetEnvironmentVariable("FORTY_WINDOW_HEIGHT"), out double h) ? h : WindowHeight;
            var window = new IslandWindow(WindowWidth, height);
            // The layout check runs off every screen: nothing shows on the display and nothing of yours is captured.
            // FORTY_OFFSCREEN=1 does the same for measuring a live island (adapters on) without it on your display.
            if (layout || Environment.GetEnvironmentVariable("FORTY_OFFSCREEN") == "1")
                window.Window.AppWindow.Move(new Windows.Graphics.PointInt32(-12000, -12000));
            var meter = new Meter(flags.IsOn("diagnostics") && !demo ? diagnostics : null);
            var settings = new Settings(Path.Combine(Home, "settings.json"));
            _controller = new IslandController(window, flags, settings, meter);
            _controller.Start(adapters: !demo && Environment.GetEnvironmentVariable("FORTY_ADAPTERS") != "0");

            var started = System.Diagnostics.Process.GetCurrentProcess().StartTime;
            double startMs = (DateTime.Now - started).TotalMilliseconds;
            meter.Record("start.ms", startMs);
            Log(log, $"visible {startMs:0} ms after the process started; adapters: " +
                     string.Join(", ", _controller.Adapters.Select(a => $"{a.Name} {a.Status.Availability}")));
            if (hold) await Demo.HoldAsync(_controller);
            else if (layout) await LayoutCheck.RunAsync(window, _controller, log ?? Path.Combine(Path.GetTempPath(), "forty-layout"));
            else if (transitions) await Demo.TransitionsAsync(window, _controller, log ?? Path.Combine(Path.GetTempPath(), "forty-transitions"));
            else if (demo) await Demo.RunAsync(window, _controller, log ?? Path.Combine(Path.GetTempPath(), "forty-demo"));
        }
        catch (Exception ex)
        {
            Crash(ex, "failed to start");
            Log(log, "failed: " + ex);
            Exit();
        }
    }

    public static void Log(string? folder, string line)
    {
        if (folder == null) return;
        Directory.CreateDirectory(folder);
        File.AppendAllText(Path.Combine(folder, "forty.log"), $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}");
    }
}
