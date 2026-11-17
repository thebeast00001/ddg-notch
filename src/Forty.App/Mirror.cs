using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.Devices.Enumeration;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace Forty.App;

enum MirrorState { Off, Starting, On, Denied, NoCamera, Busy, Failed }

/// <summary>
/// The mirror: your camera, flipped like a mirror, in the island, to check how you look before a call
/// without opening the Camera app. Nothing is recorded, saved or sent; the frames go straight from the
/// camera to the screen.
/// <para>The camera runs only while the mirror is on screen: <see cref="Stop"/> is called when the page
/// goes (closed, the island collapsed, another page, two minutes passed), and it releases the camera at
/// once, so Windows' camera light goes off with it. The camera is shared when another app (the call
/// you're about to join) is already using it, and never taken from that app.</para>
/// </summary>
sealed class CameraMirror
{
    public const double Width = 376, Height = 236, Radius = 18;

    /// <summary>Demo and test runs never open the camera: the mirror shows a stand-in picture.</summary>
    public static bool StandIn { get; set; }

    readonly Compositor _c;
    readonly Grid _frame;
    MediaPlayerElement? _video;
    MediaCapture? _capture;
    MediaSource? _source;
    MediaPlayer? _player;
    int _version;

    public MirrorState State { get; private set; } = MirrorState.Off;
    public event Action<MirrorState>? Changed;

    /// <summary>The rounded frame the picture shows in.</summary>
    public FrameworkElement Frame => _frame;

    public CameraMirror(Compositor c)
    {
        _c = c;
        _frame = new Grid { Width = Width, Height = Height, Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 18, 18, 20)) };
        // Rounded with a composition clip: the video draws in its own swap chain, which a XAML corner radius doesn't reach.
        var visual = ElementCompositionPreview.GetElementVisual(_frame);
        var shape = c.CreateRoundedRectangleGeometry();
        shape.Size = new Vector2((float)Width, (float)Height);
        shape.CornerRadius = new Vector2((float)Radius, (float)Radius);
        visual.Clip = c.CreateGeometricClip(shape);
    }

    void Set(MirrorState state)
    {
        if (State == state) return;
        State = state;
        Changed?.Invoke(state);
    }

    public async Task StartAsync()
    {
        int version = ++_version;
        if (StandIn) { ShowStandIn(); Set(MirrorState.On); return; }
        Set(MirrorState.Starting);
        try
        {
            var cameras = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
            if (version != _version) return;
            // The camera facing you: the one in the screen's bezel if Windows knows, else the first.
            var camera = cameras.FirstOrDefault(d => d.EnclosureLocation?.Panel == Windows.Devices.Enumeration.Panel.Front) ?? cameras.FirstOrDefault();
            if (camera == null) { Set(MirrorState.NoCamera); return; }

            var capture = new MediaCapture();
            var settings = new MediaCaptureInitializationSettings
            {
                VideoDeviceId = camera.Id, StreamingCaptureMode = StreamingCaptureMode.Video,
                MemoryPreference = MediaCaptureMemoryPreference.Auto, SharingMode = MediaCaptureSharingMode.ExclusiveControl,
            };
            // A free camera is opened normally. If the call app already has it, the mirror watches
            // alongside it (shared, read-only) and never takes it away.
            try { await capture.InitializeAsync(settings); }
            catch (Exception ex) when (ex is not UnauthorizedAccessException)
            {
                capture.Dispose();
                if (version != _version) return;
                capture = new MediaCapture();
                settings.SharingMode = MediaCaptureSharingMode.SharedReadOnly;
                await capture.InitializeAsync(settings);
            }
            if (version != _version) { Release(capture, null, null); return; }

            var frames = capture.FrameSources.Values.Where(s => s.Info.SourceKind == MediaFrameSourceKind.Color).ToList();
            var source = frames.FirstOrDefault(s => s.Info.MediaStreamType == MediaStreamType.VideoPreview)
                         ?? frames.FirstOrDefault(s => s.Info.MediaStreamType == MediaStreamType.VideoRecord) ?? frames.FirstOrDefault();
            if (source == null) { Release(capture, null, null); Set(MirrorState.Failed); return; }

            var media = MediaSource.CreateFromMediaFrameSource(source);
            var player = new MediaPlayer { RealTimePlayback = true, IsMuted = true, AutoPlay = true };
            player.CommandManager.IsEnabled = false;   // not a song: it must never appear in Windows' media controls, or in Forty's own music
            player.Source = media;
            _capture = capture; _source = media; _player = player;

            _video ??= new MediaPlayerElement
            {
                Stretch = Stretch.UniformToFill, AreTransportControlsEnabled = false, IsHitTestVisible = false,
                RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5), RenderTransform = new ScaleTransform { ScaleX = -1 },   // a mirror, not a camera
            };
            if (!_frame.Children.Contains(_video)) { _frame.Children.Clear(); _frame.Children.Add(_video); }
            _video.SetMediaPlayer(player);
            Set(MirrorState.On);
        }
        catch (UnauthorizedAccessException) { if (version == _version) Set(MirrorState.Denied); }
        catch (Exception ex)
        {
            if (version != _version) return;
            Guard.Report?.Invoke(ex, "mirror");
            // 0xC00D3704 and 0xC00D36B2: another app holds the camera alone.
            Set(ex.HResult is unchecked((int)0xC00D3704) or unchecked((int)0xC00D36B2) or unchecked((int)0xC00D3E85) ? MirrorState.Busy : MirrorState.Failed);
            Release(_capture, _source, _player);
            _capture = null; _source = null; _player = null;
        }
    }

    /// <summary>Turns the camera off now. Safe to call at any time, as often as it happens.</summary>
    public void Stop()
    {
        _version++;
        if (_video != null) _video.SetMediaPlayer(null);
        Release(_capture, _source, _player);
        _capture = null; _source = null; _player = null;
        Set(MirrorState.Off);
    }

    static void Release(MediaCapture? capture, MediaSource? source, MediaPlayer? player)
    {
        try { player?.Pause(); } catch (Exception) { }
        if (player != null) player.Source = null;
        player?.Dispose();
        source?.Dispose();
        // Disposing a capture can take a moment while the driver lets go: off the UI thread.
        if (capture != null) _ = Task.Run(() => { try { capture.Dispose(); } catch (Exception) { } });
    }

    /// <summary>For the layout check: a soft stand-in where the picture would be (no camera is opened).</summary>
    void ShowStandIn()
    {
        _frame.Children.Clear();
        var light = new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Width = 120, Height = 150, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, -40),
            Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(46, 255, 255, 255)),
        };
        var head = new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Width = 64, Height = 78, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 30),
            Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(56, 255, 255, 255)),
        };
        _frame.Background = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(1, 1),
            GradientStops = { new GradientStop { Color = Windows.UI.Color.FromArgb(255, 58, 52, 70), Offset = 0 }, new GradientStop { Color = Windows.UI.Color.FromArgb(255, 24, 26, 34), Offset = 1 } },
        };
        _frame.Children.Add(light);
        _frame.Children.Add(head);
    }
}
