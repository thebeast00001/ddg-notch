using System.Numerics;
using Forty.Core;
using Forty.Motion;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Forty.App;

/// <summary>Everything the settings page shows. Switch states are drawn from here once; afterwards each switch keeps its own state.</summary>
sealed record SettingsModel(
    bool Lyrics, bool Colours, bool AnnounceDevices, bool OutputNotes, bool HideFromCapture, bool Glass,
    IReadOnlyList<int> Alerts, int? FullAlert, IReadOnlyList<string> HiddenApps, string? FrontProgram,
    IReadOnlyList<(BluetoothState Device, bool Quiet)> Devices, IReadOnlyList<string> Diagnostics, string Version)
{
    /// <summary>What changes the page's structure (lists); switches and chips update themselves in place.</summary>
    public string Structure => string.Join("|", HiddenApps) + "#" + FrontProgram + "#" + string.Join("|", Devices.Select(d => d.Device.Id));
}

/// <summary>
/// The settings page: the panel growing into the full page (MOTION.md 2). Every switch answers on the
/// press itself: its knob slides on a spring and its track changes colour at once, and only then is the
/// choice saved. The page is not rebuilt under your pointer when a switch changes.
/// </summary>
sealed partial class IslandSurface
{
    public const double SettingsWidth = 480;
    string? _settingsStructure;

    /// <summary>Fills and shows the settings page; returns the height it needs, at most <paramref name="maxHeight"/> (the rest scrolls).</summary>
    public double ShowSettings(SettingsModel model, bool opening, double maxHeight)
    {
        if (!opening && _settingsStructure == model.Structure && _panel.Width == SettingsWidth) return _lastSettingsHeight;
        _settingsStructure = model.Structure;
        foreach (var row in _rows) if (row.Visible) row.Leave(toScale: 0.96);
        HideLyrics();
        _panelContent.Children.Clear();
        _panelArt = null;
        _progressFill = null;
        _clockTimer.Stop();
        _panel.Width = SettingsWidth;
        _panelContent.CenterPoint = new Vector3((float)SettingsWidth / 2, 0, 0);
        // The list runs to the island's rounded bottom edge, so rows slide under the edge as you
        // scroll instead of stopping at a hard line above it; the last row keeps its room.
        _panelContent.Padding = PanelPadding with { Bottom = 0 };

        // Header: back, title.
        var header = new Grid { ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var back = Round("", "Back", 30, false, true, "back", Palette.Primary);
        header.Children.Add(back);
        var title = Text("Settings", 17, FontWeights.SemiBold, Palette.Primary, display: true);
        title.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(title, 1);
        header.Children.Add(title);
        _panelContent.Children.Add(header);

        var sections = new StackPanel { Spacing = 18, Padding = new Thickness(0, 0, 10, PanelPadding.Bottom + 4) };

        sections.Children.Add(Section("Music",
            SwitchRow("Lyrics", "Shows only the line being sung. Song names are sent to lrclib.net to find lyrics.", model.Lyrics, "toggle:" + MusicDirector.LyricsFlag),
            SwitchRow("Album colours", "Tints the island and fills it with the cover's colours while music plays.", model.Colours, "toggle:" + MusicDirector.ColoursFlag)));

        var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (int level in Settings.AlertChoices) chips.Children.Add(Chip($"{level}%", model.Alerts.Contains(level), $"alert:{level}"));
        sections.Children.Add(Section("Battery",
            Labelled("Warn me when the battery drops to", chips),
            SwitchRow("Tell me at 80% while charging", "For people who keep the battery between 20 and 80 percent.", model.FullAlert == 80, "full:80")));

        var devices = new List<UIElement>
        {
            SwitchRow("Announce headsets and keyboards", "A short note when one connects or disconnects, with its battery.", model.AnnounceDevices, "toggle:bluetooth"),
            SwitchRow("Say when sound moves", "\"Now using …\" when the output device changes.", model.OutputNotes, "toggle:audio-output"),
        };
        foreach (var (device, quiet) in model.Devices.Where(d => d.Device.Kind is DeviceKind.Audio or DeviceKind.Input).Take(6))
            devices.Add(SwitchRow(device.Name, device.Connected ? device.Battery is { } b ? $"Connected · battery {b}%" : "Connected" : "Not connected", !quiet, $"quiet:{device.Id}", indent: true));
        sections.Children.Add(Section("Devices", devices.ToArray()));

        var apps = new StackPanel { Spacing = 6 };
        foreach (string app in model.HiddenApps)
        {
            var line = new Grid();
            line.Children.Add(Text(app, 13, FontWeights.Normal, Palette.Primary));
            var remove = Round("", $"Stop hiding for {app}", 24, false, true, $"hide:{app}", Palette.Secondary);
            remove.HorizontalAlignment = HorizontalAlignment.Right;
            line.Children.Add(remove);
            apps.Children.Add(line);
        }
        if (model.FrontProgram is { Length: > 0 } front && !model.HiddenApps.Contains(front, StringComparer.OrdinalIgnoreCase) && front != "Forty")
            apps.Children.Add(Chip($"Hide in front of {front}", false, $"hide:{front}"));
        if (apps.Children.Count == 0) apps.Children.Add(Text("Bring an app to the front, then add it here.", 12, FontWeights.Normal, Palette.Tertiary));
        sections.Children.Add(Section("Screen",
            SwitchRow("Hide from screen sharing", "You still see the island; people watching your screen don't.", model.HideFromCapture, "toggle:hide-from-capture"),
            Labelled("Hide the island in front of", apps)));

        sections.Children.Add(Section("Look",
            SwitchRow("Glass (experiment)", "Windows' acrylic inside the island. It does not blur yet; Ink is used meanwhile.", model.Glass, "toggle:glass")));

        var diagnostics = new StackPanel { Spacing = 3 };
        foreach (string line in model.Diagnostics) diagnostics.Children.Add(Text(line, 12, FontWeights.Normal, Palette.Secondary));
        diagnostics.Children.Add(Chip("Open the diagnostics folder", false, "open-diagnostics"));
        sections.Children.Add(Section("Diagnostics", diagnostics));

        var footer = new Grid();
        footer.Children.Add(Text($"Forty {model.Version} · phase 1", 11, FontWeights.Normal, Palette.Tertiary));
        var quit = new HyperlinkButton { Content = "Quit", FontSize = 11, Padding = new Thickness(6, 1, 6, 1), HorizontalAlignment = HorizontalAlignment.Right, Foreground = Brush(Palette.Secondary) };
        quit.Click += (_, _) => Command?.Invoke("quit");
        footer.Children.Add(quit);
        sections.Children.Add(footer);

        var scroller = new ScrollViewer
        {
            Content = sections, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Disabled,
            MaxHeight = Math.Max(160, maxHeight - PanelPadding.Top - 30 - 14),   // header 30, spacing 14
        };
        _panelContent.Children.Add(scroller);
        _settingsScroller = scroller;

        _panel.IsHitTestVisible = true;
        _panelContent.Measure(new Windows.Foundation.Size(SettingsWidth, double.PositiveInfinity));
        _lastSettingsHeight = Math.Clamp(Math.Ceiling(_panelContent.DesiredSize.Height), 200, maxHeight);
        _panelPart.Enter(opening ? 70 : 0, fromScale: opening ? 0.97 : 1, fromY: opening ? -4 : 0);
        return _lastSettingsHeight;
    }

    double _lastSettingsHeight;
    ScrollViewer? _settingsScroller;

    /// <summary>For the scripted demo: the settings page scrolled to its end.</summary>
    public void ScrollSettingsToEnd() => _settingsScroller?.ChangeView(null, _settingsScroller.ScrollableHeight, null, disableAnimation: true);

    /// <summary>Back from settings: the panel's width returns before its content is rebuilt.</summary>
    void ResetPanelWidth()
    {
        _settingsStructure = null;
        _panel.Width = BodyShape.Panel.Width;
        _panelContent.CenterPoint = new Vector3((float)BodyShape.Panel.Width / 2, 0, 0);
        _panelContent.Padding = PanelPadding;
    }

    static readonly Thickness PanelPadding = new(22, 20, 22, 16);

    StackPanel Section(string title, params UIElement[] rows)
    {
        var section = new StackPanel { Spacing = 10 };
        var heading = Text(title.ToUpperInvariant(), 11, FontWeights.SemiBold, Palette.Tertiary);
        heading.CharacterSpacing = 60;
        section.Children.Add(heading);
        foreach (var row in rows) section.Children.Add(row);
        return section;
    }

    StackPanel Labelled(string label, UIElement content)
    {
        var block = new StackPanel { Spacing = 8 };
        block.Children.Add(Text(label, 13, FontWeights.Normal, Palette.Primary));
        block.Children.Add(content);
        return block;
    }

    /// <summary>A setting with a sentence under it and a switch beside it.</summary>
    Grid SwitchRow(string label, string description, bool on, string command, bool indent = false)
    {
        var row = new Grid { ColumnSpacing = 14, Margin = new Thickness(indent ? 14 : 0, 0, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var words = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(Text(label, 13, FontWeights.Normal, Palette.Primary, trim: true));
        var note = Text(description, 11, FontWeights.Normal, Palette.Tertiary);
        note.TextWrapping = TextWrapping.Wrap;
        words.Children.Add(note);
        row.Children.Add(words);
        var toggle = Switch(on, command, label);
        Grid.SetColumn(toggle, 1);
        row.Children.Add(toggle);
        return row;
    }

    /// <summary>
    /// An on/off switch. The knob slides on the press spring and the track changes colour immediately;
    /// the choice is saved after. Its state lives in the switch, so the page never has to be rebuilt.
    /// </summary>
    Button Switch(bool on, string command, string name)
    {
        var knob = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Background = Brush(Windows.UI.Color.FromArgb(255, 255, 255, 255)), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(2, 0, 0, 0) };
        var track = new Grid { Width = 42, Height = 24, CornerRadius = new CornerRadius(12) };
        track.Children.Add(knob);
        var slide = new Part(knob, _win.Compositor, 1);
        bool state = on;
        void Paint(bool animate)
        {
            track.Background = Brush(state ? Palette.Done : Windows.UI.Color.FromArgb(56, 255, 255, 255));
            if (animate) slide.Nudge(state ? 18 : 0, 0, Feel.Pop);
            else slide.Place(state ? 18 : 0, 0, 1);
        }
        Paint(false);
        var button = new Button { Content = track, Padding = new Thickness(0), BorderThickness = new Thickness(0), Background = Brush(Windows.UI.Color.FromArgb(0, 0, 0, 0)), CornerRadius = new CornerRadius(12) };
        AutomationProperties.SetName(button, name);
        AutomationProperties.SetItemStatus(button, state ? "On" : "Off");
        button.Click += (_, _) =>
        {
            state = !state;
            Paint(true);
            AutomationProperties.SetItemStatus(button, state ? "On" : "Off");
            Command?.Invoke(command);
        };
        return button;
    }

    /// <summary>A pill that toggles between outlined and filled on press, for choices such as alert levels.</summary>
    Button Chip(string label, bool on, string command)
    {
        var text = new TextBlock { Text = label, FontFamily = TextFace, FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        var button = new Button { Content = text, Height = 28, CornerRadius = new CornerRadius(14), Padding = new Thickness(12, 0, 12, 0), BorderThickness = new Thickness(0) };
        bool state = on;
        void Paint()
        {
            button.Background = Brush(state ? Palette.Primary : Windows.UI.Color.FromArgb(34, 255, 255, 255));
            text.Foreground = Brush(state ? Windows.UI.Color.FromArgb(255, 0, 0, 0) : Palette.Primary);
            AutomationProperties.SetItemStatus(button, state ? "Selected" : "Not selected");
        }
        Paint();
        AutomationProperties.SetName(button, label);
        button.Click += (_, _) =>
        {
            if (!command.StartsWith("hide:", StringComparison.Ordinal) && command != "open-diagnostics") { state = !state; Paint(); }
            Command?.Invoke(command);
        };
        return button;
    }
}
