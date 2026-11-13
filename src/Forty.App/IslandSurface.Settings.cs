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
    IReadOnlyList<(BluetoothState Device, bool Quiet)> Devices, IReadOnlyList<string> Diagnostics, string Version,
    bool FollowDisplay = false, int Displays = 1, IReadOnlyList<string>? Keys = null, bool HelloReady = false, bool EveryDisplay = false)
{
    /// <summary>What changes the page's structure (lists); switches and chips update themselves in place.</summary>
    public string Structure => string.Join("|", HiddenApps) + "#" + FrontProgram + "#" + string.Join("|", Devices.Select(d => d.Device.Id)) +
                               "#" + Displays + "#" + string.Join("|", Keys ?? Array.Empty<string>()) + "#" + HelloReady;

    /// <summary>A stored key's name in words: which service it is for. The value itself is never shown.</summary>
    public static string KeyLabel(string name) => name switch
    {
        "SARVAM_API_KEY" => "Sarvam",
        "ELEVENLABS_API_KEY" => "ElevenLabs",
        "FORTY_LLM_KEY" => "Notes model",
        _ => name,
    };
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
        _switches.Clear();
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
        var back = Round(IconArt.Glyph("\uE72B", 14, Palette.Primary), "Back", 32, ButtonLook.Quiet, "back");
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
            var remove = Round(IconArt.Glyph("\uE711", 10, Palette.Secondary), $"Stop hiding for {app}", 24, ButtonLook.Quiet, $"hide:{app}");
            remove.HorizontalAlignment = HorizontalAlignment.Right;
            line.Children.Add(remove);
            apps.Children.Add(line);
        }
        if (model.FrontProgram is { Length: > 0 } front && !model.HiddenApps.Contains(front, StringComparer.OrdinalIgnoreCase) && front != "Forty")
            apps.Children.Add(Chip($"Hide in front of {front}", false, $"hide:{front}"));
        if (apps.Children.Count == 0) apps.Children.Add(Text("Bring an app to the front, then add it here.", 12, FontWeights.Normal, Palette.Tertiary));
        sections.Children.Add(Section("Screen",
            SwitchRow("Hide from screen sharing", "You still see the island; people watching your screen don't.", model.HideFromCapture, "toggle:hide-from-capture"),
            SwitchRow("Show on every display",
                model.Displays > 1 ? "A live copy of the island on each display. Press one to bring the island there."
                                   : "One display is connected now; this applies when you add another.",
                model.EveryDisplay, "toggle:every-display"),
            SwitchRow("Follow the display you're using",
                model.Displays > 1 ? "The island moves to the screen with the window you're working in."
                                   : "One display is connected now; this applies when you add another.",
                model.FollowDisplay, "toggle:follow-display"),
            Labelled("Hide the island in front of", apps)));

        // Service keys (F121): names only, never values; removing one asks for Windows Hello.
        var keys = new StackPanel { Spacing = 6 };
        foreach (string name in model.Keys ?? Array.Empty<string>())
        {
            var line = new Grid();
            line.Children.Add(Text(SettingsModel.KeyLabel(name), 13, FontWeights.Normal, Palette.Primary));
            var remove = Chip("Remove", false, $"remove-key:{name}");
            remove.HorizontalAlignment = HorizontalAlignment.Right;
            AutomationProperties.SetName(remove, $"Remove the {SettingsModel.KeyLabel(name)} key from this PC");
            line.Children.Add(remove);
            keys.Children.Add(line);
        }
        if (keys.Children.Count == 0) keys.Children.Add(Text("No keys are stored. spikes\\setup-keys.cmd adds them.", 12, FontWeights.Normal, Palette.Tertiary));
        var note = Text(model.HelloReady ? "Encrypted for your Windows account on this PC. Removing one asks for Windows Hello."
                                         : "Encrypted for your Windows account on this PC. Windows Hello isn't set up, so removing one doesn't ask.",
                        12, FontWeights.Normal, Palette.Tertiary);
        note.TextWrapping = TextWrapping.Wrap;
        keys.Children.Add(note);
        sections.Children.Add(Section("Service keys", keys));

        sections.Children.Add(Section("Look",
            SwitchRow("Glass (experiment)", "Windows' acrylic inside the island. It does not blur yet; Ink is used meanwhile.", model.Glass, "toggle:glass")));

        var diagnostics = new StackPanel { Spacing = 3 };
        foreach (string line in model.Diagnostics)
        {
            var text = Text(line, 12, FontWeights.Normal, Palette.Secondary);
            text.TextWrapping = TextWrapping.Wrap;
            diagnostics.Children.Add(text);
        }
        diagnostics.Children.Add(Chip("Open the diagnostics folder", false, "open-diagnostics"));
        sections.Children.Add(Section("Diagnostics", diagnostics));

        var footer = new Grid();
        footer.Children.Add(Text($"Forty {model.Version} · phase 1", 11, FontWeights.Normal, Palette.Tertiary));
        var quit = TextLink("Quit", "Quit Forty", "quit");
        quit.HorizontalAlignment = HorizontalAlignment.Right;
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
    /// An on/off switch (see <see cref="IslandSwitch"/>): pressed, dragged or thrown like a real one. The
    /// choice is saved after it flips. Its state lives in the switch, so the page never has to be rebuilt.
    /// </summary>
    Button Switch(bool on, string command, string name)
    {
        var toggle = new IslandSwitch(on, name, _win.Compositor);
        toggle.Toggled += _ => Command?.Invoke(command);
        _switches.Add(toggle);
        return toggle.Button;
    }

    /// <summary>The switches on the settings page as built last (for the layout check's poses).</summary>
    readonly List<IslandSwitch> _switches = new();
    public IReadOnlyList<IslandSwitch> Switches => _switches;

    /// <summary>A pill that toggles between outlined and filled on press, for choices such as alert levels.</summary>
    Button Chip(string label, bool on, string command)
    {
        var text = IconArt.Label(label, 12, on ? Palette.OnPrimary : Palette.Primary, TextFace);
        var ink = (SolidColorBrush)text.Foreground;
        var island = new IslandButton(text, label, on ? ButtonLook.Filled : ButtonLook.Soft, double.NaN, 28, 14, _win.Compositor, new Thickness(13, 0, 13, 0));
        Button button = island;
        bool state = on;
        void Paint()
        {
            island.Look = state ? ButtonLook.Filled : ButtonLook.Soft;
            Ease.Colour(ink, state ? Palette.OnPrimary : Palette.Primary);   // the letters change with the plate, not a frame before it
            AutomationProperties.SetItemStatus(button, state ? "Selected" : "Not selected");
        }
        Paint();
        button.Click += (_, _) =>
        {
            if (!command.StartsWith("hide:", StringComparison.Ordinal) && command != "open-diagnostics") { state = !state; Paint(); }
            Command?.Invoke(command);
        };
        return button;
    }
}
