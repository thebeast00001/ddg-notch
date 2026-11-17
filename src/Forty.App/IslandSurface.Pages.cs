using System.Numerics;
using Forty.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Forty.App;

/// <summary>What an agent's page shows (F18, F21, F23, F17).</summary>
sealed record AgentPageModel(Agent Agent, AgentState State, IReadOnlyList<(SkillInfo Skill, bool On)> Skills, IReadOnlyList<(DataSource Data, bool On)> Data,
                             Interruption Interruption, IReadOnlyList<MemoryItem> Memories, int PersonalCount, IReadOnlyList<RunRecord> Runs, string? Confirm, DateTimeOffset Now)
{
    /// <summary>What rebuilds the page: its lists and choices. Switches change in place.</summary>
    public string Structure => $"agent:{Agent.Id}#{Interruption}#{Confirm}#{PersonalCount}#" + string.Join("|", Memories.Select(m => m.Id + m.Edited)) + "#" + string.Join("|", Runs.Select(r => r.Id));
}

/// <summary>What the memory page shows (F91, F94, F95, F101).</summary>
sealed record MemoryPageModel(IReadOnlyList<MemoryItem> Items, IReadOnlyList<MemoryProposal> Suggested, string Filter, string? Editing, string? Confirm,
                              string? Forgot, IReadOnlyList<Agent> Allowed, DateTimeOffset Now)
{
    public string Structure => $"memory#{Filter}#{Editing}#{Confirm}#{Forgot}#{string.Join(",", Allowed.Select(a => a.Id))}#" +
                               string.Join("|", Items.Select(i => i.Id + i.Edited)) + "#" + string.Join("|", Suggested.Select(s => s.Id));
}

/// <summary>
/// The pages under Settings: one for each agent (what it can do, what it can use, how much it may
/// interrupt, what it remembers, what it did lately) and one for memory. They share Settings' frame:
/// a back button, a title and a list that scrolls under the island's rounded edge. Going to a page,
/// its content slides in from the right; coming back, from the left, so the motion follows the way you went.
/// </summary>
sealed partial class IslandSurface
{
    readonly List<AgentMark> _pageFaces = new();
    TextBox? _pageEdit;

    /// <summary>Faces on a page that is going away stop blinking and let go of their places.</summary>
    void ClearPageFaces()
    {
        foreach (var face in _pageFaces) face.Remove();
        _pageFaces.Clear();
        _pageEdit = null;
    }

    /// <summary>A face for a page, in a holder of its own (returned directly: WinUI's parent link can't be trusted here).</summary>
    (AgentMark Mark, Grid Holder) PageFace(Agent agent, double size, bool alive)
    {
        var mark = new AgentMark(agent, _win.Compositor, size, badges: false);
        _pageFaces.Add(mark);
        var holder = new Grid { VerticalAlignment = VerticalAlignment.Center };
        mark.PlaceIn(holder);
        mark.Show(AgentState.Idle);
        if (!alive) mark.Face.AutoBlink(false);   // a list of faces holds still; only the page's own face blinks
        return (mark, holder);
    }

    /// <summary>The frame every page under Settings shares; <paramref name="direction"/> is +1 going in, -1 coming back.</summary>
    double ShowSubPage(string title, Func<FrameworkElement?> makeLead, string structure, int direction, bool opening, double maxHeight, Action<StackPanel> fill, string? subtitle = null)
    {
        string key = "page:" + structure;
        if (!opening && direction == 0 && _settingsStructure == key && _panel.Width == SettingsWidth) return _lastSettingsHeight;
        _settingsStructure = key;
        LeaveTool();   // and the last page's faces
        var lead = makeLead();
        foreach (var row in _rows) if (row.Visible) row.Leave(toScale: 0.96);
        HideLyrics();
        _panelContent.Children.Clear();
        _switches.Clear();
        _panelArt = null;
        _progressFill = null;
        _clockTimer.Stop();
        _panel.Width = SettingsWidth;
        _panelContent.CenterPoint = new Vector3((float)SettingsWidth / 2, 0, 0);
        _panelContent.Padding = PanelPadding with { Bottom = 0 };

        var header = new Grid { ColumnSpacing = 10 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.Children.Add(Round(IconArt.Glyph("", 14, Palette.Primary), "Back", 32, ButtonLook.Quiet, "back"));
        if (lead != null) { Grid.SetColumn(lead, 1); header.Children.Add(lead); }
        var name = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        name.Children.Add(Text(title, 17, FontWeights.SemiBold, Palette.Primary, display: true));
        if (subtitle != null) name.Children.Add(Text(subtitle, 11.5, FontWeights.Normal, Palette.Tertiary, trim: true));
        Grid.SetColumn(name, lead != null ? 2 : 1);
        if (lead == null) Grid.SetColumnSpan(name, 2);
        header.Children.Add(name);
        _panelContent.Children.Add(header);

        var sections = new StackPanel { Spacing = 18, Padding = new Thickness(0, 0, 10, PanelPadding.Bottom + 4) };
        fill(sections);
        var scroller = new ScrollViewer
        {
            Content = sections, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Disabled,
            MaxHeight = Math.Max(160, maxHeight - PanelPadding.Top - 40 - 14),
        };
        _panelContent.Children.Add(scroller);
        _settingsScroller = scroller;

        _panel.IsHitTestVisible = true;
        _panelContent.Measure(new Windows.Foundation.Size(SettingsWidth, double.PositiveInfinity));
        _lastSettingsHeight = Math.Clamp(Math.Ceiling(_panelContent.DesiredSize.Height), 200, maxHeight);
        EnterPage(opening, direction);
        _pageEdit?.Focus(FocusState.Programmatic);
        return _lastSettingsHeight;
    }

    /// <summary>One entrance for the page: from the capsule when the island opens, sideways between pages, in place otherwise.</summary>
    void EnterPage(bool opening, int direction)
    {
        if (direction != 0 && !opening && !MotionPolicy.Reduced)
        {
            _panelPart.Vanish();
            _panelPart.Enter(0, fromScale: 1, fromX: direction * 28);
        }
        else _panelPart.Enter(opening ? 70 : 0, fromScale: opening ? 0.97 : 1, fromY: opening ? -4 : 0);
    }

    static (string Glyph, string Words) RiskLook(Risk risk) => risk switch
    {
        Risk.Read => ("", "Reads only"),
        Risk.Local => ("", "On this PC"),
        Risk.External => ("", "Uses the cloud"),
        _ => ("", "Can delete"),
    };

    static string InterruptWords(Interruption level, Agent agent) => level switch
    {
        Interruption.Quietly => $"{agent.Name}'s results show in the capsule, but never open the island or speak up.",
        Interruption.BriefOnly => $"{agent.Name}'s results wait under the ask field and here. The island stays as it was. Questions still ask you.",
        _ => $"{agent.Name}'s results show in the capsule, and an answer opens the island to be read.",
    };

    // ── An agent's page ──

    public double ShowAgentPage(AgentPageModel m, int direction, bool opening, double maxHeight)
    {
        var a = m.Agent;
        FrameworkElement Lead()
        {
            var (face, holder) = PageFace(a, 34, alive: true);
            face.Show(m.State);
            return holder;
        }
        return ShowSubPage(a.Name, Lead, m.Structure, direction, opening, maxHeight, sections =>
        {
            // What it can do (F21): each skill with its switch, what it risks, and how to ask.
            var skills = new List<UIElement>();
            foreach (var (skill, on) in m.Skills)
            {
                var (glyph, words) = RiskLook(skill.Risk);
                var row = SwitchRow(skill.Name, $"{skill.What} Try “{skill.Examples[0]}”.", on, "skill:" + skill.Id);
                var risk = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Margin = new Thickness(0, 4, 0, 0) };
                risk.Children.Add(IconArt.Glyph(glyph, 10, Palette.Tertiary));
                risk.Children.Add(Text(words, 10.5, FontWeights.Normal, Palette.Tertiary));
                if (row.Children[0] is StackPanel text) text.Children.Add(risk);
                skills.Add(row);
            }
            sections.Children.Add(Section("What it can do", skills.ToArray()));

            // What it can use (F18): checked every time it is used.
            sections.Children.Add(Section("What it can use",
                m.Data.Select(d => (UIElement)SwitchRow(d.Data.Name, d.Data.What, d.On, "data:" + d.Data.Key)).ToArray()));

            // How much it may interrupt you (F23).
            var levels = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            foreach (var (level, label) in new[] { (Interruption.InterruptMe, "Interrupt me"), (Interruption.Quietly, "Quietly"), (Interruption.BriefOnly, "Brief only") })
                levels.Children.Add(Chip(label, m.Interruption == level, $"interrupt:{a.Id}:{level}"));
            var why = Text(InterruptWords(m.Interruption, a), 11, FontWeights.Normal, Palette.Tertiary);
            why.TextWrapping = TextWrapping.Wrap;
            sections.Children.Add(Section($"When {a.Name} has something for you", levels, why));

            // What it remembers (F17): its own task memory; your memory is on the memory page.
            var remembers = new List<UIElement>();
            foreach (var item in m.Memories) remembers.Add(MemoryRow(item, m.Now, m.Confirm == item.Id, editing: false, showScope: false));
            if (m.Memories.Count == 0)
                remembers.Add(Note(a.Id == Cast.Iris.Id ? "Iris keeps what you ask it to remember in your memory, not here." : $"Nothing yet. {a.Name} asks before it remembers anything."));
            if (a.Id == Cast.Iris.Id)
                remembers.Add(Chip(m.PersonalCount == 0 ? "Open your memory" : $"Your memory · {m.PersonalCount}", false, "page:memory"));
            sections.Children.Add(Section($"What {a.Name} remembers", remembers.ToArray()));

            // What it did lately (F22).
            var recent = new List<UIElement>();
            foreach (var run in m.Runs) recent.Add(Note($"{run.Outcome} · {MemorySource.Ago(run.Ended, m.Now)}"));
            if (recent.Count == 0) recent.Add(Note("Nothing yet."));
            sections.Children.Add(Section("Lately", recent.ToArray()));
        }, subtitle: a.Job + " · " + AgentWords.Label(m.State));
    }

    TextBlock Note(string words)
    {
        var note = Text(words, 12, FontWeights.Normal, Palette.Tertiary);
        note.TextWrapping = TextWrapping.Wrap;
        return note;
    }

    // ── The memory page ──

    public double ShowMemoryPage(MemoryPageModel m, int direction, bool opening, double maxHeight) =>
        ShowSubPage("Memory", () => null, m.Structure, direction, opening, maxHeight, sections =>
        {
            // Whose: everything, yours, or one agent's (only scopes that hold something are offered).
            var filters = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, -6, 0, 0) };
            filters.Children.Add(Chip("All", m.Filter == "all", "mem-filter:all"));
            filters.Children.Add(Chip("You", m.Filter == MemoryScopes.Personal, "mem-filter:" + MemoryScopes.Personal));
            foreach (var agent in Cast.All.Where(c => m.Items.Any(i => i.Scope == MemoryScopes.Of(c))))
                filters.Children.Add(Chip(agent.Name, m.Filter == MemoryScopes.Of(agent), "mem-filter:" + MemoryScopes.Of(agent)));
            sections.Children.Add(filters);

            if (m.Forgot is { } forgot)
            {
                var done = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                done.Children.Add(IconArt.Glyph("", 12, Palette.Done));
                var words = Text(forgot, 12.5, FontWeights.Normal, Palette.Secondary);
                words.TextWrapping = TextWrapping.Wrap;
                done.Children.Add(words);
                sections.Children.Add(done);
            }

            // Suggestions waiting for a yes (F95): nothing here is remembered yet.
            if (m.Suggested.Count > 0)
            {
                var cards = new List<UIElement>();
                foreach (var p in m.Suggested) cards.Add(SuggestionRow(p, m.Now, m.Editing == "p:" + p.Id));
                sections.Children.Add(Section("Waiting for your yes", cards.ToArray()));
            }

            var shown = m.Items.Where(i => m.Filter == "all" || i.Scope == m.Filter).ToList();
            foreach (string topic in MemoryTopics.All)
            {
                var items = shown.Where(i => i.Topic == topic).ToList();
                if (items.Count == 0) continue;
                sections.Children.Add(Section(topic, items.Select(i => (UIElement)MemoryRow(i, m.Now, m.Confirm == i.Id, m.Editing == i.Id, showScope: m.Filter == "all")).ToArray()));
            }
            if (shown.Count == 0)
                sections.Children.Add(Note(m.Items.Count == 0
                    ? "Nothing remembered yet. Type or say “remember that …”, and it stays on this PC."
                    : "Nothing here for this filter."));

            // Who may use it, and what happens to it.
            var who = m.Allowed.Count == 0 ? "No agent can use your memory yet; each asks first, on its page."
                    : $"{string.Join(" and ", m.Allowed.Select(a => a.Name))} can use it. Change this on each agent's page.";
            var about = new List<UIElement>
            {
                Note(who),
                Note("Kept only on this PC, encrypted for your Windows account. When you ask about something, this PC looks it up: no model sees your memories."),
            };
            if (m.Items.Count > 0)
            {
                if (m.Confirm == "*")
                {
                    about.Add(Note($"Forget all {m.Items.Count} things? They can't be brought back."));
                    var sure = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                    sure.Children.Add(ActionButton("Forget everything", true, "mem-forget-all-yes", height: 30));
                    sure.Children.Add(ActionButton("Keep", false, "mem-keep", height: 30));
                    about.Add(sure);
                }
                else about.Add(Chip("Forget everything", false, "mem-forget-all"));
            }
            sections.Children.Add(Section("About your memory", about.ToArray()));
        });

    /// <summary>
    /// One memory: the sentence, then where it came from (F94) with the words it was learned from,
    /// then Edit and Forget. Forget asks once, in place.
    /// </summary>
    StackPanel MemoryRow(MemoryItem item, DateTimeOffset now, bool confirming, bool editing, bool showScope)
    {
        var row = new StackPanel { Spacing = 3 };
        if (editing) row.Children.Add(EditBox(item.Text, $"mem-save:{item.Id}:"));
        else
        {
            var text = Text(item.Text, 13, FontWeights.Normal, Palette.Primary);
            text.TextWrapping = TextWrapping.Wrap;
            row.Children.Add(text);
        }
        string meta = item.Source.Describe(now)
            + (item.ForThisTask ? " · for today" : "")
            + (item.Edited != null ? " · edited" : "")
            + (showScope && item.Scope != MemoryScopes.Personal ? $" · {MemoryScopes.Name(item.Scope)}'s" : "");
        row.Children.Add(Text(meta, 11, FontWeights.Normal, Palette.Tertiary, trim: true));
        if (!item.Source.Deleted && item.Source.Line.Length > 0)
        {
            var quote = Text($"“{item.Source.Line}”", 11, FontWeights.Normal, Palette.Tertiary, trim: true);
            quote.FontStyle = Windows.UI.Text.FontStyle.Italic;
            row.Children.Add(quote);
        }
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(-9, 0, 0, 0) };
        if (confirming)
        {
            actions.Margin = new Thickness(0, 4, 0, 0);
            actions.Spacing = 8;
            actions.Children.Add(ActionButton("Forget", true, $"mem-forget-yes:{item.Id}", height: 28));
            actions.Children.Add(ActionButton("Keep", false, "mem-keep", height: 28));
        }
        else if (!editing)
        {
            actions.Children.Add(TextLink("Edit", "Edit this memory", $"mem-edit:{item.Id}"));
            actions.Children.Add(TextLink("Forget", "Forget this memory", $"mem-forget:{item.Id}"));
        }
        if (actions.Children.Count > 0) row.Children.Add(actions);
        AutomationProperties.SetName(row, $"{item.Text} {meta}");
        return row;
    }

    StackPanel SuggestionRow(MemoryProposal p, DateTimeOffset now, bool editing)
    {
        var row = new StackPanel { Spacing = 4 };
        if (editing) row.Children.Add(EditBox(p.Text, $"mem-yes:{p.Id}:"));
        else
        {
            var text = Text(p.Text, 13, FontWeights.Normal, Palette.Primary);
            text.TextWrapping = TextWrapping.Wrap;
            row.Children.Add(text);
        }
        string who = MemoryScopes.AgentOf(p.Scope)?.Name ?? "Forty";
        row.Children.Add(Text($"{who} would like to remember this · {p.Source.Describe(now)}", 11, FontWeights.Normal, Palette.Tertiary, trim: true));
        if (!editing)
        {
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };
            buttons.Children.Add(ActionButton("Remember", true, $"mem-yes:{p.Id}", height: 28));
            buttons.Children.Add(ActionButton("Edit", false, $"mem-edit-p:{p.Id}", height: 28));
            buttons.Children.Add(ActionButton("No", false, $"mem-no:{p.Id}", height: 28));
            row.Children.Add(buttons);
        }
        return row;
    }

    /// <summary>
    /// Your own words for a memory, in a field like the ask field: Enter saves, Esc puts it back. The
    /// command gets what you typed appended to <paramref name="commandPrefix"/>.
    /// </summary>
    Border EditBox(string text, string commandPrefix)
    {
        var plate = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 6, 10, 6), Background = Brush(Windows.UI.Color.FromArgb(36, 255, 255, 255)) };
        var box = new TextBox
        {
            Text = text, FontFamily = TextFace, FontSize = 13, BorderThickness = new Thickness(0), Padding = new Thickness(0), MinHeight = 0,
            Background = Brush(Microsoft.UI.Colors.Transparent), Foreground = Brush(Palette.Primary), TextWrapping = TextWrapping.Wrap, AcceptsReturn = false,
            IsSpellCheckEnabled = true,
        };
        var clear = Brush(Microsoft.UI.Colors.Transparent);
        foreach (string key in new[] { "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused", "TextControlBorderBrush",
                                       "TextControlBorderBrushPointerOver", "TextControlBorderBrushFocused", "TextControlElevationBorderBrush", "TextControlElevationBorderFocusedBrush" })
            box.Resources[key] = clear;
        foreach (string key in new[] { "TextControlForeground", "TextControlForegroundPointerOver", "TextControlForegroundFocused" })
            box.Resources[key] = Brush(Palette.Primary);
        AutomationProperties.SetName(box, "Your words for this memory. Enter saves, Escape cancels.");
        box.AddHandler(UIElement.PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => WantsFocus?.Invoke()), true);
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter && box.Text.Trim().Length > 0) { e.Handled = true; Send(commandPrefix + box.Text.Trim()); }
            else if (e.Key == Windows.System.VirtualKey.Escape) { e.Handled = true; Send("mem-cancel"); }
        };
        box.SelectAll();
        plate.Child = box;
        _pageEdit = box;
        return plate;
    }

    // ── Settings' own rows for the pages ──

    /// <summary>The agents, each a row that opens its page, and the memory row. In Settings, after Asking.</summary>
    StackPanel[] AgentSections(int memories, int suggestions)
    {
        var rows = new List<UIElement>();
        foreach (var agent in Cast.All)
        {
            var (_, holder) = PageFace(agent, 24, alive: false);
            rows.Add(PageRow(holder, agent.Name, agent.Job, "page:agent:" + agent.Id));
        }
        var memory = PageRow(IconArt.Glyph("", 16, Palette.Secondary), "Memory",
            memories == 0 ? "Nothing remembered yet" : $"{memories} thing{(memories == 1 ? "" : "s")} remembered" + (suggestions > 0 ? $" · {suggestions} waiting for your yes" : ""),
            "page:memory");
        return new[] { Section("Agents", rows.ToArray()), Section("Memory", memory) };
    }

    /// <summary>A row that opens a page: what it is, a line about it, and a chevron. The whole row is the button.</summary>
    Button PageRow(FrameworkElement icon, string title, string line, string command)
    {
        var grid = new Grid { ColumnSpacing = 12, Width = SettingsWidth - PanelPadding.Left - PanelPadding.Right - 10 - 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(icon);
        var words = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(Text(title, 13, FontWeights.Normal, Palette.Primary));
        words.Children.Add(Text(line, 11, FontWeights.Normal, Palette.Tertiary, trim: true));
        Grid.SetColumn(words, 1);
        grid.Children.Add(words);
        var chevron = IconArt.Glyph("", 11, Palette.Tertiary);
        chevron.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(chevron, 2);
        grid.Children.Add(chevron);
        var button = new IslandButton(grid, $"{title}: {line}", ButtonLook.Quiet, double.NaN, 46, 12, _win.Compositor, new Thickness(6, 0, 6, 0));
        button.Button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.Button.Margin = new Thickness(-6, 0, 0, 0);
        button.Button.Click += (_, _) => Send(command);
        return button;
    }

    /// <summary>For the layout check: the memory page's edit field, as if typed into.</summary>
    public void DemoTypeEdit(string text) { if (_pageEdit != null) _pageEdit.Text = text; }
}
