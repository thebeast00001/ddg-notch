using Forty.Core;
using Forty.Motion;
using Microsoft.UI.Dispatching;

namespace Forty.App;

/// <summary>
/// The timer (Tempo's job) and the mirror. The timer is one countdown at a time: set it on the ruler,
/// by typing or by saying it ("set a timer for 10 minutes"), and it lives in the capsule with Tempo's
/// face beside the digits until it rings. The mirror shows your camera in the island for a minute or two
/// before a call; the camera is on only while the mirror is on screen.
/// </summary>
sealed partial class IslandController
{
    const string TimerKey = IslandSurface.TimerKey;
    const int MirrorSeconds = 120;

    readonly CountdownTimer _timer = new();
    DispatcherQueueTimer? _timerClock, _mirrorLimit;
    string? _tool;
    int _pickMinutes = 15;
    readonly Dictionary<string, double> _toolHeights = new();

    /// <summary>The ruler's ticks and the start chime are on unless the flags file turns them off.</summary>
    bool SoundsOn => !_flags.Has(Sounds.Flag) || _flags.IsOn(Sounds.Flag);

    TimerView TimerViewNow()
    {
        double now = TimerClock.Now;
        return new TimerView(_timer.Phase, _timer.Duration, _timer.Remaining(now), now);
    }

    // ── Tool pages ──

    void OpenTool(string tool, bool takeFocus)
    {
        _settingsOpen = false;
        _tool = tool;
        _lastPanel = null;
        if (tool == "mirror") StartMirrorLimit();
        Expand(takeFocus);
    }

    /// <summary>The tool page goes (the mirror's camera with it, as the page is cleared).</summary>
    void CloseTool()
    {
        _tool = null;
        _mirrorLimit?.Stop();
    }

    void ShowToolPage(bool takeFocus)
    {
        string tool = _tool!;
        bool opening = !_expanded || _surface.Tool != tool || _body.Shape.Name == "full";
        _expanded = true;
        _surface.ShowHost(false, blink: false);
        MusicLook(false);
        _surface.Sounds.On = SoundsOn;
        double Build(bool open) => tool == "mirror" ? _surface.ShowMirror(open) : _surface.ShowTimer(TimerViewNow(), _pickMinutes, open);
        if (opening)
        {
            double guess = _toolHeights.TryGetValue(tool, out double known) ? known : tool == "mirror" ? 362 : 300;
            _body.GoTo(BodyShape.Panel with { Height = guess }, Springs.Move);
            Defer(() =>
            {
                if (!_expanded || _tool != tool) return;
                double height = Build(true);
                _toolHeights[tool] = height;
                if (Math.Abs(height - guess) > 0.5) _body.GoTo(BodyShape.Panel with { Height = height }, Springs.Move);
                if (takeFocus || _focusOwner != IntPtr.Zero) _surface.FocusTool();
            });
        }
        else
        {
            double height = Build(false);
            _toolHeights[tool] = height;
            _body.GoTo(BodyShape.Panel with { Height = height }, Springs.Quick);
        }
        _body.ShowSatellite(false);
        _body.ShowDot(null);
        if (takeFocus && _focusOwner == IntPtr.Zero) _focusOwner = _win.TakeFocus();
        if (opening) Shown?.Invoke("panel");
    }

    /// <summary>The tool pages' buttons. Returns false for commands that aren't theirs.</summary>
    bool ToolCommand(string command)
    {
        switch (command)
        {
            case "tool:timer": OpenTool("timer", takeFocus: _focusOwner != IntPtr.Zero); return true;
            case "tool:mirror": OpenTool("mirror", takeFocus: false); return true;
            case "tool:mirror-retry": StartMirrorLimit(); _surface.RetryMirror(); return true;
            case "tool-close":
                CloseTool();
                _lastPanel = null;
                Expand(takeFocus: false);   // back to the panel; moving away closes the island as usual
                return true;
            case "mirror-settings":
                _ = Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:privacy-webcam"));
                return true;
            case "timer-pause": TimerAsk(TimerCommand.Pause, "", null); return true;
            case "timer-resume": TimerAsk(TimerCommand.Resume, "", null); return true;
            case "timer-cancel": TimerAsk(TimerCommand.Cancel, "", null); return true;
            case "timer-stop": StopAlarm(); return true;
            case "timer-more": AddMinute(); return true;
            case var start when start.StartsWith("timer-start:", StringComparison.Ordinal):
                if (int.TryParse(start.AsSpan(12), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int seconds) && seconds > 0)
                    StartTimer(TimeSpan.FromSeconds(seconds), "");
                return true;
            default: return false;
        }
    }

    // ── The timer ──

    /// <summary>Tempo's timer skill, from the rules or the brain: start, open, pause, resume, cancel, or say what's left.</summary>
    void TimerAsk(string argument, string text, Via? via)
    {
        if (text.Length > 0 && !Gate("timer", "clock", () => TimerAsk(argument, text, via))) return;   // asked of Tempo; the timer's own buttons are yours
        if (TimerCommand.Duration(argument) is { } duration) { StartTimer(duration, text, via); return; }
        double now = TimerClock.Now;
        switch (argument)
        {
            case TimerCommand.Open:
                OpenTool("timer", takeFocus: _focusOwner != IntPtr.Zero);
                return;
            case TimerCommand.Pause when _timer.Phase == TimerPhase.Running:
                _timer.Pause(now);
                Log(text, "pause", "paused", via);
                break;
            case TimerCommand.Resume when _timer.Phase == TimerPhase.Paused:
                _timer.Resume(now);
                Log(text, "resume", "resumed", via);
                break;
            case TimerCommand.Cancel when _timer.Phase is TimerPhase.Running or TimerPhase.Paused:
                _timer.Cancel();
                Log(text, "cancel", "cancelled", via);
                break;
            case TimerCommand.Cancel when _timer.Phase == TimerPhase.Ringing:
                StopAlarm();
                return;
            case TimerCommand.Left when _timer.Phase is TimerPhase.Running or TimerPhase.Paused:
            {
                var left = _timer.Remaining(now);
                string said = $"{CountdownTimer.Format(left)} left";
                Reply(new Activity(ResultKey, "planner", Priority.Timely, said, null, "",
                    $"{CountdownTimer.Words(TimeSpan.FromSeconds(Math.Ceiling(left.TotalSeconds)))} left on your timer{(_timer.Phase == TimerPhase.Paused ? ", paused" : "")}",
                    TimeSpan.FromSeconds(4), AgentId: Cast.Tempo.Id));
                return;
            }
            default:
                Note("No timer is running", _timer.Phase == TimerPhase.Ringing ? "Your timer is ringing. Press Stop." : "There's no timer to do that to. Say “set a timer for 10 minutes”.", Accent.None, Cast.Tempo);
                return;
        }
        TimerChanged();
    }

    void StartTimer(TimeSpan duration, string text, Via? via = null)
    {
        _surface.Sounds.StopAlarm();
        _timer.Start(duration, TimerClock.Now);
        if (duration.TotalMinutes >= 1) _pickMinutes = Math.Clamp((int)Math.Round(duration.TotalMinutes), TimerRuler.Min, TimerRuler.Max);
        _surface.Sounds.On = SoundsOn;
        _surface.Sounds.Start();
        Log(text, CountdownTimer.Words(_timer.Duration), "started", via, $"Timer set for {CountdownTimer.Words(_timer.Duration)}");
        TimerChanged();
        // Typed in the panel: the answer shows under the question. Otherwise the capsule is the answer.
        if (_expanded && _tool == null)
            Reply(new Activity(ResultKey, "planner", Priority.Ambient, $"Timer set for {CountdownTimer.Words(_timer.Duration)}", null, "",
                $"Timer set for {CountdownTimer.Words(_timer.Duration)}", TimeSpan.FromSeconds(3), Accent.Done, AgentId: Cast.Tempo.Id));
    }

    /// <summary>
    /// A request to Tempo goes in the run log like any agent's. Presses on the timer's own buttons
    /// (no request text) don't: they are you using a control, not asking an agent.
    /// </summary>
    void Log(string text, string what, string outcome, Via? via, string? result = null)
    {
        if (text.Length == 0) return;
        var run = new RunBuilder(Cast.Tempo, text);
        ModelStep(run, via);
        run.Step("Timer", what, Risk.Local, outcome);
        _runs.Save(run.Finish(result ?? $"Timer {outcome}"));
    }

    /// <summary>+1 min: on a ringing timer it starts a new minute; on a running one it adds a minute.</summary>
    void AddMinute()
    {
        if (_timer.Phase == TimerPhase.Ringing) { StartTimer(TimeSpan.FromMinutes(1), ""); return; }
        if (_timer.Phase == TimerPhase.Off) return;
        _timer.Add(TimeSpan.FromMinutes(1), TimerClock.Now);
        TimerChanged();
    }

    void StopAlarm()
    {
        _surface.Sounds.StopAlarm();
        if (_timer.Phase == TimerPhase.Ringing) _timer.Cancel();
        TimerChanged();
    }

    /// <summary>The timer changed state: the capsule, the page and the clock that drives them follow.</summary>
    void TimerChanged()
    {
        PostTimer();
        ScheduleTimerClock();
        _surface.TimerTime(TimerViewNow());
        if (_expanded && (_tool == "timer" || _tool == null))
        {
            _lastPanel = null;
            Expand(takeFocus: false);
        }
    }

    void PostTimer()
    {
        var t = _timer;
        double now = TimerClock.Now;
        switch (t.Phase)
        {
            case TimerPhase.Running:
            case TimerPhase.Paused:
            {
                bool paused = t.Phase == TimerPhase.Paused;
                var left = t.Remaining(now);
                string narration = paused
                    ? $"Timer paused, {CountdownTimer.Words(TimeSpan.FromSeconds(Math.Ceiling(left.TotalSeconds)))} left"
                    : $"Timer, {CountdownTimer.Words(t.Duration)}, ends at {DateTime.Now + left:t}";
                Queue.Post(new Activity(TimerKey, "planner", Priority.Timely, paused ? "Paused" : "Timer", CountdownTimer.Format(t.Duration), "",
                    narration, null, AgentId: Cast.Tempo.Id), Clock.Now);
                break;
            }
            case TimerPhase.Ringing:
                Queue.Post(new Activity(TimerKey, "planner", Priority.ActNow, "Time's up", null, "",
                    $"Time's up. Your {CountdownTimer.Words(t.Duration)} timer has finished.", null,
                    Actions: new ActivityActions("Stop", "timer-stop", "+1 min", "timer-more"), AgentId: Cast.Tempo.Id), Clock.Now);
                break;
            default:
                Queue.Remove(TimerKey, Clock.Now);
                break;
        }
    }

    /// <summary>
    /// One clock for the timer, woken on the second (when the digits change) and at the end; nothing
    /// runs between. A paused or finished timer leaves it asleep. Wall-clock time, so a timer set before
    /// the PC sleeps rings as soon as it wakes if its time has passed.
    /// </summary>
    void ScheduleTimerClock()
    {
        _timerClock ??= Timer(1000, TimerTick);
        _timerClock.Stop();
        double now = TimerClock.Now;
        switch (_timer.Phase)
        {
            case TimerPhase.Running:
            {
                double left = _timer.Remaining(now).TotalSeconds;
                double next = left - Math.Floor(left);
                if (next < 0.01) next += 1;
                _timerClock.Interval = TimeSpan.FromSeconds(Math.Clamp(next + 0.004, 0.01, 1.0));
                _timerClock.Start();
                break;
            }
            case TimerPhase.Ringing:
                _timerClock.Interval = TimeSpan.FromSeconds(1);
                _timerClock.Start();
                break;
        }
    }

    void TimerTick()
    {
        double now = TimerClock.Now;
        var before = _timer.Phase;
        if (_timer.Tick(now)) { Ring(); return; }
        if (before == TimerPhase.Ringing && _timer.Phase == TimerPhase.Off)
        {
            // It rang for a minute with nobody there: it stops by itself.
            _surface.Sounds.StopAlarm();
            TimerChanged();
            return;
        }
        _surface.TimerTime(TimerViewNow());
        ScheduleTimerClock();
    }

    /// <summary>Time's up: the alarm, Tempo wide-eyed, Stop and +1 min in the capsule (or the page, if the island is open).</summary>
    void Ring()
    {
        _surface.Sounds.Alarm();
        PostTimer();
        ScheduleTimerClock();
        if (_expanded && !_settingsOpen) { _tool = "timer"; _lastPanel = null; Expand(takeFocus: false); }
    }

    // ── The mirror ──

    void MirrorAsk(string argument)
    {
        if (argument == "close") { if (_tool == "mirror" && _expanded) Collapse(); return; }
        OpenTool("mirror", takeFocus: false);
    }

    /// <summary>Two minutes, then the mirror closes and the camera with it, wherever your attention went.</summary>
    void StartMirrorLimit()
    {
        _mirrorLimit ??= Timer(MirrorSeconds * 1000, () => { if (_tool == "mirror" && _expanded) Collapse(); });
        _mirrorLimit.Stop();
        _mirrorLimit.Start();
    }

    // ── For the layout check ──

    /// <summary>Opens the timer page with the ruler at <paramref name="minutes"/>.</summary>
    public void DemoTimerPage(int minutes) { _pickMinutes = minutes; OpenTool("timer", takeFocus: false); }

    /// <summary>Starts, pauses or rings a timer without waiting for it.</summary>
    public void DemoTimer(string what, TimeSpan duration)
    {
        switch (what)
        {
            case "start": StartTimer(duration, ""); break;
            case "pause": TimerAsk(TimerCommand.Pause, "", null); break;
            case "ring": _timer.Start(TimeSpan.FromSeconds(1), TimerClock.Now - 2); TimerTick(); break;
            case "stop": StopAlarm(); TimerAsk(TimerCommand.Cancel, "", null); break;
        }
    }

    public void DemoMirror() => OpenTool("mirror", takeFocus: false);
    public string? ToolNow => _tool;
    public TimerPhase TimerPhaseNow => _timer.Phase;
}
