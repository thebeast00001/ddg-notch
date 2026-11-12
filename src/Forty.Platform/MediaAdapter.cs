using System.Runtime.InteropServices;
using Forty.Core;
using Windows.Graphics.Imaging;
using Windows.Media;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace Forty.Platform;

public enum RepeatMode { Off, Track, List }

/// <summary>What is playing, as Windows' media session manager reports it.</summary>
public sealed record NowPlaying(
    string AppId, string AppName, string Title, string Artist, string Album,
    bool Playing, bool CanSkip, bool CanSeek, bool? Shuffle, RepeatMode Repeat, PlaybackClock Clock)
{
    public bool IsEmpty => Title.Length == 0 && Artist.Length == 0;

    /// <summary>Identifies the track, so a new track can be told from a pause or a seek.</summary>
    public string TrackKey => $"{AppId}\u001f{Title}\u001f{Artist}\u001f{Album}";

    /// <summary>The same track in the same state, ignoring where in the song it is.</summary>
    public bool SameApartFromPosition(NowPlaying other) => this with { Clock = default } == other with { Clock = default };
}

/// <summary>
/// Now playing, its art, and the transport controls for any app that registers with Windows' media
/// session manager (F43): Spotify, browsers, Windows Media Player and others. Event-driven.
/// Reading is all this adapter does on its own; the controls act only when you press them.
/// </summary>
public sealed class MediaAdapter() : Adapter("media")
{
    GlobalSystemMediaTransportControlsSessionManager? _manager;
    GlobalSystemMediaTransportControlsSession? _session;
    IRandomAccessStreamReference? _thumbnail;
    readonly Dictionary<string, string> _appNames = new();

    public NowPlaying? Current { get; private set; }

    /// <summary>The track, its state or its controls changed.</summary>
    public event Action<NowPlaying?>? Changed;

    /// <summary>Only the position changed (a seek, or a periodic report); lyrics re-sync on this.</summary>
    public event Action<PlaybackClock>? PositionChanged;

    protected override void OnStart()
    {
        _manager = GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask().GetAwaiter().GetResult();
        _manager.CurrentSessionChanged += (_, _) => Follow(_manager.GetCurrentSession());
        Follow(_manager.GetCurrentSession());
    }

    void Follow(GlobalSystemMediaTransportControlsSession? session)
    {
        if (_session != null)
        {
            _session.MediaPropertiesChanged -= OnMediaChanged;
            _session.PlaybackInfoChanged -= OnPlaybackChanged;
            _session.TimelinePropertiesChanged -= OnTimelineChanged;
        }
        _session = session;
        if (_session != null)
        {
            _session.MediaPropertiesChanged += OnMediaChanged;
            _session.PlaybackInfoChanged += OnPlaybackChanged;
            _session.TimelinePropertiesChanged += OnTimelineChanged;
        }
        _ = ReadAsync();
    }

    void OnMediaChanged(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs e) => _ = ReadAsync();
    void OnPlaybackChanged(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs e) => _ = ReadAsync();
    void OnTimelineChanged(GlobalSystemMediaTransportControlsSession s, TimelinePropertiesChangedEventArgs e) => _ = ReadAsync();

    async Task ReadAsync()
    {
        var session = _session;
        NowPlaying? next = null;
        IRandomAccessStreamReference? thumbnail = null;
        try
        {
            if (session != null)
            {
                var media = await session.TryGetMediaPropertiesAsync();
                var playback = session.GetPlaybackInfo();
                var timeline = session.GetTimelineProperties();
                bool playing = playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                var clock = new PlaybackClock(
                    timeline?.Position ?? TimeSpan.Zero, timeline?.LastUpdatedTime ?? DateTimeOffset.Now, playing,
                    timeline?.EndTime ?? TimeSpan.Zero, playback?.PlaybackRate ?? 1);
                next = new NowPlaying(
                    session.SourceAppUserModelId ?? "", AppName(session.SourceAppUserModelId),
                    media?.Title ?? "", media?.Artist ?? "", media?.AlbumTitle ?? "",
                    playing, playback?.Controls.IsNextEnabled == true, playback?.Controls.IsPlaybackPositionEnabled == true,
                    playback?.IsShuffleActive,
                    playback?.AutoRepeatMode switch { MediaPlaybackAutoRepeatMode.Track => RepeatMode.Track, MediaPlaybackAutoRepeatMode.List => RepeatMode.List, _ => RepeatMode.Off },
                    clock);
                if (next.IsEmpty) next = null;
                thumbnail = media?.Thumbnail;
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            return;   // a player that is closing can fail mid-read; the next event brings a consistent picture
        }
        if (session != _session) return;   // a newer session took over while this read was running

        var before = Current;
        Current = next;
        _thumbnail = thumbnail;
        if (before != null && next != null && before.SameApartFromPosition(next))
        {
            if (before.Clock != next.Clock) PositionChanged?.Invoke(next.Clock);
            return;
        }
        Changed?.Invoke(next);
    }

    /// <summary>"Spotify" rather than "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify". Falls back to the id's last part.</summary>
    string AppName(string? appId)
    {
        if (string.IsNullOrEmpty(appId)) return "";
        if (_appNames.TryGetValue(appId, out var known)) return known;
        string name;
        try { name = Windows.ApplicationModel.AppInfo.GetFromAppUserModelId(appId).DisplayInfo.DisplayName; }
        catch (Exception ex) when (ex is COMException or ArgumentException) { name = ""; }
        if (string.IsNullOrWhiteSpace(name))
        {
            string tail = appId.Split('!').Last().Split('\\').Last();
            name = tail.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? tail[..^4] : tail;
            if (name.Length > 0) name = char.ToUpperInvariant(name[0]) + name[1..];
        }
        return _appNames[appId] = name;
    }

    /// <summary>The current track's art as encoded bytes (usually JPEG or PNG), or null.</summary>
    public async Task<byte[]?> ReadArtAsync()
    {
        var reference = _thumbnail;
        if (reference == null) return null;
        try
        {
            using var stream = await reference.OpenReadAsync();
            if (stream.Size == 0 || stream.Size > 4_000_000) return null;
            var buffer = new Windows.Storage.Streams.Buffer((uint)stream.Size);
            var read = await stream.ReadAsync(buffer, (uint)stream.Size, InputStreamOptions.None);
            var bytes = new byte[read.Length];
            using (var reader = DataReader.FromBuffer(read)) reader.ReadBytes(bytes);
            return bytes;
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The colours of a cover: decodes it to 24 × 24 and lets <see cref="TrackColours"/> choose.</summary>
    public static async Task<TrackColours> ColoursOfAsync(byte[] art)
    {
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(art);
                await writer.StoreAsync();
                await writer.FlushAsync();
            }
            var decoder = await BitmapDecoder.CreateAsync(stream);
            var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight,
                new BitmapTransform { ScaledWidth = 24, ScaledHeight = 24, InterpolationMode = BitmapInterpolationMode.Fant },
                ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
            return TrackColours.FromPixels(pixels.DetachPixelData());
        }
        catch (Exception ex) when (ex is COMException or ArgumentException) { return TrackColours.Ink; }
    }

    public Task<bool> PlayPauseAsync() => Do(s => s.TryTogglePlayPauseAsync().AsTask());
    public Task<bool> NextAsync() => Do(s => s.TrySkipNextAsync().AsTask());
    public Task<bool> PreviousAsync() => Do(s => s.TrySkipPreviousAsync().AsTask());
    public Task<bool> SeekAsync(TimeSpan position) => Do(s => s.TryChangePlaybackPositionAsync(position.Ticks).AsTask());
    public Task<bool> SetShuffleAsync(bool on) => Do(s => s.TryChangeShuffleActiveAsync(on).AsTask());

    public Task<bool> SetRepeatAsync(RepeatMode mode) => Do(s => s.TryChangeAutoRepeatModeAsync(mode switch
    {
        RepeatMode.Track => MediaPlaybackAutoRepeatMode.Track,
        RepeatMode.List => MediaPlaybackAutoRepeatMode.List,
        _ => MediaPlaybackAutoRepeatMode.None,
    }).AsTask());

    async Task<bool> Do(Func<GlobalSystemMediaTransportControlsSession, Task<bool>> action)
    {
        if (_session == null) return false;
        try { return await action(_session); }
        catch (COMException) { return false; }
    }

    public const string ActivityKey = "media.now-playing";

    /// <summary>The now-playing activity for the island, or null when nothing is playing.</summary>
    public static Activity? ToActivity(NowPlaying? playing) =>
        playing is not { Playing: true } p ? null
        : Activity.Live(ActivityKey, "media", Priority.Ambient,
                        p.Title.Length > 0 ? p.Title : p.Artist,
                        p.Artist.Length > 0 && p.Title.Length > 0 ? p.Artist : null, Glyphs.Music,
                        p.Artist.Length > 0 ? $"Playing {p.Title} by {p.Artist}" : $"Playing {p.Title}");
}

/// <summary>
/// Synced lyrics from LRCLIB (lrclib.net), a free public lyrics database. Used only when you turn
/// lyrics on, because it sends the song's title, artist, album and length to that site. Answers are
/// kept on this PC, so each song is looked up once; songs without synced lyrics are remembered too,
/// for a week, so they are not asked about again and again.
/// </summary>
public sealed class LyricsClient
{
    static readonly HttpClient Http = CreateClient();
    readonly string _cache;

    public LyricsClient(string cacheFolder) => _cache = cacheFolder;

    static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Forty/0.1 (desktop lyrics display)");
        return http;
    }

    /// <summary>LRC text with timings, or null when none exists or the site cannot be reached.</summary>
    public async Task<string?> FindSyncedAsync(string title, string artist, string album, TimeSpan duration, CancellationToken cancel = default)
    {
        if (title.Length == 0 || artist.Length == 0) return null;
        string key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{title}\u001f{artist}\u001f{album}\u001f{(int)Math.Round(duration.TotalSeconds)}")))[..24];
        string hit = Path.Combine(_cache, key + ".lrc"), miss = Path.Combine(_cache, key + ".none");
        if (File.Exists(hit)) return await File.ReadAllTextAsync(hit, cancel);
        if (File.Exists(miss) && File.GetLastWriteTimeUtc(miss) > DateTime.UtcNow.AddDays(-7)) return null;

        string? lrc = null;
        try
        {
            string query = $"track_name={Uri.EscapeDataString(title)}&artist_name={Uri.EscapeDataString(artist)}" +
                           (album.Length > 0 ? $"&album_name={Uri.EscapeDataString(album)}" : "") +
                           (duration > TimeSpan.Zero ? $"&duration={(int)Math.Round(duration.TotalSeconds)}" : "");
            using (var response = await Http.GetAsync("https://lrclib.net/api/get?" + query, cancel))
                if (response.IsSuccessStatusCode) lrc = Synced(await response.Content.ReadAsStringAsync(cancel));
            if (lrc == null)
            {
                // No exact match: search, and accept a result only if its length is within three seconds.
                using var response = await Http.GetAsync($"https://lrclib.net/api/search?track_name={Uri.EscapeDataString(title)}&artist_name={Uri.EscapeDataString(artist)}", cancel);
                if (response.IsSuccessStatusCode)
                {
                    using var results = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancel));
                    foreach (var item in results.RootElement.EnumerateArray())
                    {
                        double seconds = item.TryGetProperty("duration", out var d) && d.ValueKind == System.Text.Json.JsonValueKind.Number ? d.GetDouble() : -1;
                        if (duration > TimeSpan.Zero && Math.Abs(seconds - duration.TotalSeconds) > 3) continue;
                        if (item.TryGetProperty("syncedLyrics", out var s) && s.ValueKind == System.Text.Json.JsonValueKind.String) { lrc = s.GetString(); break; }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return null;   // offline or slow: try again next time, nothing cached
        }

        try
        {
            Directory.CreateDirectory(_cache);
            if (lrc != null) await File.WriteAllTextAsync(hit, lrc, cancel);
            else await File.WriteAllTextAsync(miss, "", cancel);
        }
        catch (IOException) { }
        return lrc;
    }

    static string? Synced(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("syncedLyrics", out var s) && s.ValueKind == System.Text.Json.JsonValueKind.String ? s.GetString() : null;
    }
}
