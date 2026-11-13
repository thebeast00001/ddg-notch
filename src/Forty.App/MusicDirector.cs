using Forty.Core;
using Forty.Platform;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;

namespace Forty.App;

/// <summary>
/// Everything about the song that is playing: its art and colours (loaded once per track), its synced
/// lyrics (only when you turn lyrics on), and which line is being sung. The line is tracked with one
/// timer set for the next line's moment, never by checking every frame; a seek or pause re-syncs it.
/// </summary>
sealed class MusicDirector
{
    public const string LyricsFlag = "lyrics", ColoursFlag = "music-colours";

    readonly FeatureFlags _flags;
    readonly LyricsClient _lyrics;
    readonly DispatcherQueue _ui;
    readonly DispatcherQueueTimer _lineTimer;
    string? _trackKey;
    List<LyricLine> _lines = new();
    CancellationTokenSource? _lookup;

    public NowPlaying? Playing { get; private set; }
    public ImageSource? ArtSmall { get; private set; }
    public ImageSource? ArtLarge { get; private set; }
    public TrackColours Colours { get; private set; } = TrackColours.Ink;
    public int LineIndex { get; private set; } = -1;
    public string LyricsNote { get; private set; } = "";

    /// <summary>The track, its art, its colours or its lyrics changed: re-render everything about it.</summary>
    public event Action? Changed;
    /// <summary>Only the current lyric line changed.</summary>
    public event Action? LineChanged;
    /// <summary>The position jumped (a seek); the panel's progress re-syncs.</summary>
    public event Action<PlaybackClock>? PositionChanged;

    public MusicDirector(FeatureFlags flags, LyricsClient lyrics)
    {
        _flags = flags;
        _lyrics = lyrics;
        _ui = DispatcherQueue.GetForCurrentThread();
        _lineTimer = _ui.CreateTimer();
        _lineTimer.IsRepeating = false;
        _lineTimer.Tick += (_, _) => Guard.Run(Resync, "lyric line");
    }

    public bool LyricsOn => _flags.IsOn(LyricsFlag);
    public bool ColoursOn => _flags.Get(ColoursFlag) != FlagState.Off || !_flags.Has(ColoursFlag);

    /// <summary>Lyrics take over the capsule when they are on, found, and the song is playing.</summary>
    public bool LyricsActive => LyricsOn && _lines.Count > 0 && Playing?.Playing == true;

    /// <summary>The line to show now: its text, or an instrumental mark for gaps and the intro.</summary>
    public (string Key, string Text, bool Instrumental) Line =>
        LineIndex >= 0 && LineIndex < _lines.Count && !_lines[LineIndex].IsInstrumental
            ? ($"{_trackKey}#{LineIndex}", _lines[LineIndex].Text, false)
            : ($"{_trackKey}#gap{LineIndex}", Glyphs.Music, true);

    public MusicView? View => Playing is not { } p ? null : new MusicView(
        p.TrackKey, p.Title, p.Artist, p.AppName, p.Playing, ArtSmall, ArtLarge, Colours, p.Clock,
        p.CanSeek, p.Shuffle, p.Repeat, LyricsOn, ColoursOn, LyricsNote);

    /// <summary>
    /// A new state from the media adapter (or from the demo). A new song is shown only when its cover
    /// is ready (or after 350 ms at most), so its title, cover and colours always change together.
    /// </summary>
    public void OnMedia(NowPlaying? playing, byte[]? artForDemo = null, string? lrcForDemo = null, Func<Task<byte[]?>>? readArt = null)
    {
        if (playing == null)
        {
            Playing = null;
            _trackKey = null;
            _lineTimer.Stop();
            Changed?.Invoke();
            return;
        }
        if (playing.TrackKey != _trackKey)
        {
            _trackKey = playing.TrackKey;
            _latest = playing;
            _ = LoadTrackAsync(playing, artForDemo, readArt, lrcForDemo);
            return;
        }
        _latest = playing;
        if (Playing?.TrackKey != playing.TrackKey) return;   // still waiting for this song's cover
        Playing = playing;
        Resync();
        Changed?.Invoke();
    }

    NowPlaying? _latest;

    public void OnPosition(PlaybackClock clock)
    {
        if (Playing == null) return;
        Playing = Playing with { Clock = clock };
        Resync();
        PositionChanged?.Invoke(clock);
    }

    /// <summary>The lyrics toggle changed: look the song up now, or let lyrics go.</summary>
    public void OnFlagsChanged()
    {
        if (LyricsOn && _lines.Count == 0 && Playing is { } p && LyricsNote != "No synced lyrics for this song") _ = LoadLyricsAsync(p);
        if (!LyricsOn) LyricsNote = "";
        Resync();
        Changed?.Invoke();
    }

    async Task LoadTrackAsync(NowPlaying song, byte[]? art, Func<Task<byte[]?>>? readArt, string? lrcForDemo)
    {
        string key = song.TrackKey;
        var load = Task.Run(async () => art ?? (readArt != null ? await readArt() : null));
        var ready = Task.Delay(350);
        TrackColours colours = TrackColours.Ink;
        ImageSource? small = null, large = null;
        if (await Task.WhenAny(load, ready) == load && await load is { } bytes)
        {
            colours = await MediaAdapter.ColoursOfAsync(bytes);
            small = await Art.DecodeAsync(bytes, 48);    // 22–26 DIP on screen, at up to 2× scale
            large = await Art.DecodeAsync(bytes, 160);   // 76 DIP in the panel
        }
        if (key != _trackKey) return;                    // the song changed while this was loading

        // Commit: the new song, its cover, its colours and its lyrics, all at once.
        Playing = _latest?.TrackKey == key ? _latest : song;
        (Colours, ArtSmall, ArtLarge) = (colours, small, large);
        _lines = lrcForDemo != null ? Lrc.Parse(lrcForDemo) : new List<LyricLine>();
        LineIndex = -1;
        LyricsNote = "";
        Resync();
        Changed?.Invoke();

        if (small == null && !load.IsCompleted)
        {
            // A slow cover: the song is shown without it, and the cover fades in when it arrives.
            if (await load is { } late && key == _trackKey)
            {
                Colours = await MediaAdapter.ColoursOfAsync(late);
                ArtSmall = await Art.DecodeAsync(late, 48);
                ArtLarge = await Art.DecodeAsync(late, 160);
                if (key == _trackKey) Changed?.Invoke();
            }
        }
        if (lrcForDemo == null && LyricsOn && key == _trackKey && Playing is { } p) await LoadLyricsAsync(p);
    }

    async Task LoadLyricsAsync(NowPlaying p)
    {
        _lookup?.Cancel();
        var cancel = _lookup = new CancellationTokenSource();
        LyricsNote = "Looking for lyrics…";
        Changed?.Invoke();
        string? lrc = await _lyrics.FindSyncedAsync(p.Title, p.Artist, p.Album, p.Clock.Duration, cancel.Token);
        if (cancel.IsCancellationRequested || p.TrackKey != _trackKey) return;
        _lines = Lrc.Parse(lrc);
        LyricsNote = _lines.Count > 0 ? "" : "No synced lyrics for this song";
        Resync();
        Changed?.Invoke();
    }

    /// <summary>Works out the current line from the clock and sets one timer for the next line.</summary>
    void Resync()
    {
        _lineTimer.Stop();
        if (Playing is not { } p || _lines.Count == 0) return;
        var position = p.Clock.At(DateTimeOffset.Now);
        int index = Lrc.IndexAt(_lines, position);
        if (index != LineIndex)
        {
            LineIndex = index;
            LineChanged?.Invoke();
        }
        if (!p.Playing || index + 1 >= _lines.Count) return;
        var wait = (_lines[index + 1].At - position) / Math.Max(0.1, p.Clock.Rate);
        _lineTimer.Interval = wait < TimeSpan.FromMilliseconds(15) ? TimeSpan.FromMilliseconds(15) : wait;
        _lineTimer.Start();
    }
}
