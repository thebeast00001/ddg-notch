using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace IslandSpike;

public static class Program
{
    [STAThread]
    static void Main()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(parameters =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            var app = new App();
        });
    }
}

sealed class App : Application
{
    static Timer? s_watchdog;

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        string mode = Environment.GetEnvironmentVariable("FORTY_SPIKE") ?? "run";
        string engine = Environment.GetEnvironmentVariable("FORTY_ENGINE") ?? "c";   // additive springs won the S2 comparison
        bool aligned = Environment.GetEnvironmentVariable("FORTY_ALIGNED") != "0";

        Modes.OutDir = Environment.GetEnvironmentVariable("FORTY_OUT") ?? Path.Combine(Path.GetTempPath(), "forty-spike");
        Directory.CreateDirectory(Modes.OutDir);
        UnhandledException += (_, e) => Modes.Log("unhandled: " + e.Exception);

        try
        {
            if (mode == "run")
            {
                Modes.Interactive(engine);
                return;
            }

            // Scripted modes always end by themselves, even if something hangs.
            s_watchdog = new Timer(_ => Environment.Exit(3), null, 240_000, Timeout.Infinite);
            switch (mode)
            {
                case "selfcheck": await Modes.SelfCheck(); break;
                case "topmost-probe": Modes.TopmostProbe(); break;
                case "dictation-demo": await Modes.DictationDemo(); break;
                case "call-demo": await Modes.CallDemo(); break;
                case "torture": await Modes.Torture(engine, aligned); break;
                case "calibrate": await Modes.Calibrate(); break;
                default: Modes.Log("unknown mode " + mode); break;
            }
        }
        catch (Exception ex)
        {
            Modes.Log("failed: " + ex);
        }
        Exit();
    }
}
