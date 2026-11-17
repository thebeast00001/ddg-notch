using System.Numerics;
using Forty.Core;
using Forty.Motion;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Forty.App;

/// <summary>
/// An agent's face (F14, F15, F38): its eyes in its hue, with an expression for each of the ten states,
/// and a small badge where the expression alone isn't enough. The pose comes only from the run's
/// state; the working manner runs only while work runs, at about one step a second, and stops the
/// moment work stops.
///
/// | State | Face |
/// | --- | --- |
/// | Idle | Neutral, blinking now and then |
/// | Listening | Wide, opening with your voice |
/// | Working | Its own manner: Echo reads left to right, Iris looks around, Bolt narrows and nods, Tempo thinks |
/// | Using a tool | The same, with the tool's glyph on a badge |
/// | Waiting for approval | Looks straight at you; amber badge |
/// | Needs an answer | Curious; amber "?" badge |
/// | Done | Happy arcs; green check badge |
/// | Paused | Asleep; grey pause badge |
/// | Failed | Drooping; red "!" badge |
/// | Speaking | A mouth line that follows the voice |
/// </summary>
sealed class AgentMark
{
    readonly Agent _agent;
    readonly Face _face;
    readonly Border _badge;
    readonly FontIcon _badgeGlyph;
    readonly Part _badgePart;
    readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _manner;
    readonly bool _badges;
    int _step;
    Panel? _holder;

    public Grid Element { get; }
    public AgentState State { get; private set; } = AgentState.Idle;
    public Agent Agent => _agent;
    public Face Face => _face;

    /// <param name="badges">False where the words beside the face already say it (the capsule).</param>
    public AgentMark(Agent agent, Compositor c, double size, bool badges = true)
    {
        _agent = agent;
        _badges = badges;
        Element = new Grid { Width = size, Height = size };
        AutomationProperties.SetName(Element, agent.Name);
        _face = new Face(FaceStyle.For(agent.Eyes), c, size, Colours.Of(agent.Hue));
        Element.Children.Add(_face.Element);

        double badge = Math.Round(size * 0.46);
        _badgeGlyph = new FontIcon { FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = badge * 0.58, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _badge = new Border
        {
            Width = badge, Height = badge, CornerRadius = new CornerRadius(badge / 2), Child = _badgeGlyph,
            BorderThickness = new Thickness(1.5), BorderBrush = new SolidColorBrush(Palette.Fill),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, -badge * 0.25, -badge * 0.2), IsHitTestVisible = false,
        };
        _badge.CenterPoint = new Vector3((float)badge / 2, (float)badge / 2, 0);
        Element.Children.Add(_badge);
        _badgePart = new Part(_badge, c);

        _manner = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        _manner.Interval = TimeSpan.FromMilliseconds(620);
        _manner.Tick += (_, _) => Guard.Run(Step, "agent manner");
    }

    /// <summary>
    /// Moves the mark into <paramref name="panel"/>, out of wherever it was. The old container is
    /// remembered rather than asked for: once a panel's content is cleared, WinUI no longer reports
    /// the element's parent, but the element still belongs to it.
    /// </summary>
    public FrameworkElement PlaceIn(Panel panel)
    {
        _holder?.Children.Remove(Element);
        if (Element.Parent is Panel other && other != panel) other.Children.Remove(Element);
        panel.Children.Add(Element);
        _holder = panel;
        return Element;
    }

    /// <summary>Takes the mark out of its container (it is about to show elsewhere).</summary>
    public void Remove()
    {
        _holder?.Children.Remove(Element);
        _holder = null;
        _manner.Stop();
        _face.AutoBlink(false);
    }

    /// <summary>Moves to the face for <paramref name="state"/>. <paramref name="toolGlyph"/> is the tool's Segoe Fluent glyph while using one.</summary>
    public void Show(AgentState state, string? toolGlyph = null)
    {
        State = state;
        _face.Speak(state == AgentState.Speaking);
        _face.Express(state switch
        {
            AgentState.Listening => Expression.Listening,
            AgentState.Working or AgentState.UsingTool => _agent.Eyes switch
            {
                Eyes.Square => Expression.Determined,
                Eyes.Wide => Expression.Thinking,
                _ => Expression.Focused,
            },
            AgentState.WaitingForApproval => Expression.Perk,
            AgentState.NeedsInput => Expression.Curious,
            AgentState.Done => Expression.Happy,
            AgentState.Paused => Expression.Asleep,
            AgentState.Failed => Expression.Sad,
            _ => Expression.Neutral,
        });
        if (state == AgentState.WaitingForApproval) _face.Look(0, 0.15);   // straight at you
        bool working = state is AgentState.Working or AgentState.UsingTool && !MotionPolicy.Reduced;
        if (working && !_manner.IsRunning) { _step = 0; Step(); _manner.Start(); }
        else if (!working && _manner.IsRunning) { _manner.Stop(); if (state != AgentState.WaitingForApproval) _face.Look(0, 0); }
        _face.AutoBlink(state is not (AgentState.Paused or AgentState.Done) && _holder != null);
        if (_badges) Badge(state, toolGlyph);
    }

    /// <summary>One step of the agent's working manner, about every 0.6 s while it works.</summary>
    void Step()
    {
        int n = _step++;
        switch (_agent.Eyes)
        {
            case Eyes.Round:   // Echo reads: left to right in four steps, then back to the start of the line
            {
                double[] xs = { -0.8, -0.25, 0.3, 0.85 };
                _face.Look(xs[n % xs.Length], 0.15);
                break;
            }
            case Eyes.Tall:    // Iris looks around
            {
                (double X, double Y)[] spots = { (-0.75, -0.35), (0.6, -0.55), (0.8, 0.3), (-0.4, 0.45), (0.1, -0.7) };
                var spot = spots[n % spots.Length];
                _face.Look(spot.X, spot.Y);
                break;
            }
            case Eyes.Square:  // Bolt nods into it: a quick dip every other step
                _face.Look(0, n % 2 == 0 ? 0.6 : -0.1);
                break;
            default:           // Tempo thinks ahead: eyes drift up, left and right
                _face.Look(n % 2 == 0 ? 0.7 : -0.5, -0.8);
                break;
        }
    }

    /// <summary>The live level (0 to 1) while listening (the eyes open with it) or speaking (the mouth follows it).</summary>
    public void SetLevel(double level) => _face.SetLevel(level);

    void Badge(AgentState state, string? toolGlyph)
    {
        var ink = Color.FromArgb(255, 20, 20, 20);
        (Color plate, Color glyphInk, string glyph)? look = state switch
        {
            AgentState.UsingTool => (Palette.Primary, Palette.OnPrimary, toolGlyph ?? ""),
            AgentState.WaitingForApproval => (Palette.NeedsYou, ink, ""),
            AgentState.NeedsInput => (Palette.NeedsYou, ink, ""),
            AgentState.Done => (Palette.Done, ink, ""),
            AgentState.Failed => (Palette.Error, Color.FromArgb(255, 255, 255, 255), ""),
            AgentState.Paused => (Color.FromArgb(255, 150, 150, 155), ink, ""),
            _ => null,
        };
        if (look is not { } l)
        {
            if (_badgePart.Shown) _badgePart.Leave(toScale: 0.5);
            return;
        }
        _badge.Background = new SolidColorBrush(l.plate);
        _badgeGlyph.Foreground = new SolidColorBrush(l.glyphInk);
        _badgeGlyph.Glyph = l.glyph;
        if (!_badgePart.Shown) _badgePart.Enter(fromScale: 0.5, spring: Feel.Pop);
    }

    /// <summary>For the layout check: the face for a state at a level, with every spring at its target.</summary>
    public void Pose(AgentState state, double level, string? toolGlyph = null)
    {
        Show(state, toolGlyph);
        _manner.Stop();
        _face.AutoBlink(false);
        if (state is AgentState.Working or AgentState.UsingTool) Step();
        _face.SetLevel(level);
        _face.Settle();
    }
}
