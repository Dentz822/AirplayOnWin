using System.Diagnostics;
using AirPlay.Core.Timing;
using AirPlay.Core.Video;
using AirPlay.Media;

namespace AirPlay.App;

/// <summary>
/// AirPlay video on this PC: runs a <see cref="UrlPlayer"/>, sends pictures to the window's
/// <see cref="FrameQueue"/> and sound to the speakers, and answers the iPhone's status polls.
/// Pictures are shown when the audio reaches them (or by a wall clock if there is no audio).
/// </summary>
public sealed class VideoPlaybackHost : IVideoPlayer
{
    private readonly FrameQueue _frames;
    private readonly Action<string> _log;
    private readonly Action<PlaybackClock?, bool> _onActiveChanged; // (clock, active)
    private readonly object _lock = new();

    private UrlPlayer? _player;
    private PcmOutput? _audio;
    private PlaybackClock? _clock;
    private double _volumeDb;
    private double _rate = 1;
    private bool _ready;
    private bool _ended;
    private long _lastPictureNs;
    // Wall clock for video without sound: media time = _wallBaseNs + elapsed (frozen while paused).
    private long _wallBaseNs;
    private long _wallStartTicks;
    private Timer? _wallTimer;

    public VideoPlaybackHost(FrameQueue frames, Action<string> log, Action<PlaybackClock?, bool> onActiveChanged)
    {
        _frames = frames;
        _log = log;
        _onActiveChanged = onActiveChanged;
    }

    public void Play(string url, double startSeconds)
    {
        Stop();
        lock (_lock)
        {
            _clock = new PlaybackClock(neverStale: true);
            _rate = 1;
            _ready = false;
            _ended = false;
            _lastPictureNs = (long)(startSeconds * 1e9);
            ResetWallClock(_lastPictureNs);
            _audio = new PcmOutput(_clock, startAfter: TimeSpan.FromMilliseconds(200), maxBuffered: null,
                capacity: TimeSpan.FromSeconds(5), _log);
            _audio.SetVolume(_volumeDb);

            var player = new UrlPlayer();
            player.Info += _log;
            player.Failed += message => _log($"     ⚠ 影片播放失敗：{message}");
            player.Ended += () => { lock (_lock) _ended = true; _log("     影片讀取完畢"); };
            player.Picture += OnPicture;
            player.Sound += (pcm, ptsNs) => _audio?.Add(pcm.Span, ptsNs);
            player.SeekDone += OnSeekDone;
            // Read ahead at most ~1 s of video and 2 s of sound.
            player.ShouldWait = () => _frames.Count > 30 || (_audio?.Buffered ?? TimeSpan.Zero) > TimeSpan.FromSeconds(2);
            _player = player;
            player.Start(url, startSeconds);

            // Video without sound: advance the clock from the wall clock (10×/s).
            _wallTimer = new Timer(_ =>
            {
                lock (_lock)
                    if (_player is { IsOpen: true, HasAudio: false } && _ready)
                        _clock?.ReportAudible(WallClockNs());
            }, null, 100, 100);
        }
        _log($"     影片播放開始{(startSeconds > 0 ? $"（從 {startSeconds:F1} 秒）" : "")}");
    }

    private void OnPicture(BgraFrame picture, long ptsNs)
    {
        PlaybackClock? clock;
        bool first;
        lock (_lock)
        {
            clock = _clock;
            first = !_ready;
            _ready = true;
            _lastPictureNs = ptsNs;
        }
        _frames.Enqueue(picture, ptsNs);
        if (first)
        {
            _log($"     影片第一個畫面 {picture.Width}x{picture.Height}");
            lock (_lock) ResetWallClock(ptsNs); // without sound, time starts at the first picture
            _onActiveChanged(clock, true);
        }
    }

    private void OnSeekDone()
    {
        _frames.Clear();
        _audio?.Flush();
        lock (_lock)
        {
            _clock?.ResetAudio();
            if (_rate == 0) _clock?.SetPaused(true);
        }
    }

    public void SetRate(double rate)
    {
        lock (_lock)
        {
            if (_player is null) return;
            _rate = rate;
            var paused = rate == 0;
            _player.SetPaused(paused);
            if (paused) { _audio?.Pause(); _wallBaseNs = WallClockNs(); _wallStartTicks = 0; }
            else { _audio?.Resume(); ResetWallClock(_wallBaseNs); }
            _clock?.SetPaused(paused);
        }
    }

    public void Seek(double seconds)
    {
        lock (_lock)
        {
            if (_player is null) return;
            _ended = false;
            _lastPictureNs = (long)(seconds * 1e9);
            ResetWallClock(_lastPictureNs);
            if (_rate == 0) _wallStartTicks = 0;
            _player.Seek(seconds);
        }
    }

    public void SetVolume(double db)
    {
        _volumeDb = db;
        _audio?.SetVolume(db);
    }

    public VideoPlaybackStatus Status
    {
        get
        {
            lock (_lock)
            {
                if (_player is null) return new VideoPlaybackStatus(0, 0, 0, false, false);
                var position = (_clock?.AudibleRemoteNanosNow() ?? _lastPictureNs) / 1e9;
                var duration = _player.DurationSeconds;
                // Finished when the reader is at the end and everything read has been shown.
                var finished = _ended && _frames.Count == 0 && (_audio?.Buffered ?? TimeSpan.Zero) < TimeSpan.FromMilliseconds(100);
                return new VideoPlaybackStatus(duration, Math.Clamp(position, 0, duration > 0 ? duration : double.MaxValue),
                    _rate, _ready, finished);
            }
        }
    }

    public void Stop()
    {
        UrlPlayer? player;
        PcmOutput? audio;
        bool wasActive;
        lock (_lock)
        {
            player = _player;
            audio = _audio;
            wasActive = _ready;
            _player = null;
            _audio = null;
            _clock = null;
            _ready = false;
            _wallTimer?.Dispose();
            _wallTimer = null;
        }
        if (player is null) return;
        player.Dispose();
        audio?.Dispose();
        _frames.Clear();
        _log("     影片播放結束");
        if (wasActive) _onActiveChanged(null, false);
    }

    private void ResetWallClock(long baseNs)
    {
        _wallBaseNs = baseNs;
        _wallStartTicks = Stopwatch.GetTimestamp();
    }

    private long WallClockNs() => _wallStartTicks == 0
        ? _wallBaseNs
        : _wallBaseNs + (long)(Stopwatch.GetElapsedTime(_wallStartTicks).TotalMilliseconds * 1_000_000);
}
