using System.Globalization;
using System.Text;
using AirPlay.Core.Rtsp;
using Claunia.PropertyList;

namespace AirPlay.Core.Video;

/// <summary>Playback state the iPhone polls for with GET /playback-info.</summary>
public sealed record VideoPlaybackStatus(double Duration, double Position, double Rate, bool ReadyToPlay, bool Finished);

/// <summary>Plays a video URL for AirPlay video (implemented with FFmpeg in the app).</summary>
public interface IVideoPlayer
{
    void Play(string url, double startSeconds);
    /// <summary>0 = pause, 1 = play.</summary>
    void SetRate(double rate);
    void Seek(double seconds);
    void Stop();
    /// <summary>iPhone volume in dB (0 = full, -144 = mute).</summary>
    void SetVolume(double db);
    VideoPlaybackStatus Status { get; }
}

/// <summary>
/// AirPlay video ("AirPlay button inside an app", as opposed to screen mirroring), shared by all
/// connections of the receiver. The iPhone sends POST /play with the video's address:
///   • https://… (e.g. Safari): played directly.
///   • mlhls://localhost/master.m3u8 (YouTube): the playlists exist only on the iPhone. We ask for
///     them one by one with FCUP requests over the reverse channel; the iPhone answers each with
///     POST /action. We keep one video + one audio stream, expand YouTube's condensed playlists,
///     and serve them at http://127.0.0.1:7000/… to the local player. Media segments themselves
///     come straight from YouTube's servers.
/// Then it polls GET /playback-info and sends /rate (pause/play), /scrub (seek) and /stop.
/// DRM-protected apps (Netflix, Apple TV+, …) ask for "fp-setup2", which cannot be supported.
/// </summary>
public sealed class AirPlayVideoService
{
    private const string UserAgent = "AppleCoreMedia/1.0.0.11B554a (Apple TV; U; CPU OS 7_0_4 like Mac OS X; en_us)";

    private readonly IVideoPlayer _player;
    private readonly int _localPort;
    private readonly Action<string> _log;
    private readonly object _lock = new();

    private ReverseChannel? _reverse;
    private string? _sessionId;
    private int _nextRequestId = 1;
    private string? _remotePrefix;           // "mlhls://localhost"
    private string? _localMaster;            // rewritten master playlist
    private readonly Dictionary<string, string> _mediaPlaylists = new(); // local path -> playlist
    private readonly Queue<string> _pendingFetches = new();
    private double _startSeconds;
    private bool _playing;

    public AirPlayVideoService(IVideoPlayer player, int localPort, Action<string> log)
    {
        _player = player;
        _localPort = localPort;
        _log = log;
    }

    /// <summary>Feature bits to add when AirPlay video is supported: bit 0 = video, bit 4 = HLS.</summary>
    public const ulong FeatureBits = 0x11;

    private string LocalPrefix => $"http://127.0.0.1:{_localPort}";

    /// <summary>Routes AirPlay-video HTTP requests; returns null for requests it doesn't handle.</summary>
    public RtspResponse? Handle(RtspRequest req, RtspConnectionInfo connection)
    {
        var path = req.Uri.Split('?')[0];
        var query = req.Uri.Contains('?') ? req.Uri[(req.Uri.IndexOf('?') + 1)..] : "";
        return (req.Method, path) switch
        {
            ("GET", "/server-info") => ServerInfo(),
            ("POST", "/reverse") => Reverse(req),
            ("POST", "/play") => Play(req),
            ("POST", "/action") => Action(req),
            ("GET", "/playback-info") => PlaybackInfo(),
            ("POST", "/rate") => Rate(query),
            ("POST", "/scrub") => Scrub(query),
            ("GET", "/scrub") => ScrubStatus(),
            ("POST", "/stop") => Stop(),
            ("PUT", "/setProperty") => PropertyAccepted(),
            ("GET", "/getProperty") => new RtspResponse(),
            ("POST", "/fp-setup2") => DrmUnsupported(),
            ("GET", _) when path.EndsWith(".m3u8") => ServeLocalPlaylist(path),
            _ => null,
        };
    }

    public void SetVolume(double db)
    {
        if (_playing) _player.SetVolume(db);
    }

    // ---- requests ---------------------------------------------------------------------------

    private RtspResponse ServerInfo()
    {
        var dict = new NSDictionary
        {
            { "features", new NSNumber(0x27F) }, // video, photo, FairPlay video, volume, HLS, slideshow, audio
            { "model", new NSString(Discovery.AirPlayServices.Model) },
            { "osBuildVersion", new NSString("12B435") },
            { "protovers", new NSString("1.0") },
            { "srcvers", new NSString(Discovery.AirPlayServices.SourceVersion) },
            { "vv", new NSNumber(2) },
        };
        return XmlPlist(dict);
    }

    private RtspResponse Reverse(RtspRequest req)
    {
        _log($"     影片: 建立反向連線（{req.Header("X-Apple-Purpose") ?? "event"}）");
        var r = new RtspResponse { Status = 101, Reason = "Switching Protocols" };
        r.Headers["Connection"] = "Upgrade";
        r.Headers["Upgrade"] = "PTTH/1.0";
        r.TakeOver = async (stream, ct) =>
        {
            var channel = new ReverseChannel(stream);
            lock (_lock) _reverse = channel;
            await channel.RunAsync(ct);
            lock (_lock) if (_reverse == channel) _reverse = null;
            _log("     影片: 反向連線結束");
        };
        return r;
    }

    private RtspResponse Play(RtspRequest req)
    {
        if (!req.IsBinaryPlist || PropertyListParser.Parse(req.Body) is not NSDictionary plist)
            return RtspResponse.Error(400, "Bad Request");
        var location = plist.ObjectForKey("Content-Location")?.ToString();
        if (string.IsNullOrEmpty(location)) return RtspResponse.Error(400, "Bad Request");

        var start = plist.ObjectForKey("Start-Position-Seconds") is NSNumber s ? s.ToDouble() : 0;
        var app = plist.ObjectForKey("clientProcName")?.ToString() ?? "?";
        _log($"     影片: 播放要求 ({app}) {Shorten(location)}，從 {start:F1} 秒開始");

        lock (_lock)
        {
            _sessionId = req.Header("X-Apple-Session-ID") ?? Guid.NewGuid().ToString();
            _startSeconds = start;
            _mediaPlaylists.Clear();
            _pendingFetches.Clear();
            _localMaster = null;
        }

        if (location.StartsWith("http://") || location.StartsWith("https://"))
        {
            StartPlayer(location, start);
            return new RtspResponse();
        }

        var master = location.IndexOf("/master.m3u8", StringComparison.Ordinal);
        if (master < 0)
        {
            _log($"     ⚠ 影片: 不支援的位址格式 {location}");
            return RtspResponse.Error(400, "Bad Request");
        }
        lock (_lock) _remotePrefix = location[..master];
        _ = RequestPlaylistAsync(location); // after this response is sent
        return new RtspResponse();
    }

    private RtspResponse Action(RtspRequest req)
    {
        if (!req.IsBinaryPlist || PropertyListParser.Parse(req.Body) is not NSDictionary plist)
            return RtspResponse.Error(400, "Bad Request");
        var type = plist.ObjectForKey("type")?.ToString();
        var parameters = plist.ObjectForKey("params") as NSDictionary;

        switch (type)
        {
            case "unhandledURLResponse" when parameters is not null:
                var url = parameters.ObjectForKey("FCUP_Response_URL")?.ToString();
                var data = (parameters.ObjectForKey("FCUP_Response_Data") as NSData)?.Bytes;
                if (url is null || data is null) return RtspResponse.Error(400, "Bad Request");
                try
                {
                    OnPlaylistReceived(url, Encoding.UTF8.GetString(data));
                }
                catch (Exception ex)
                {
                    _log($"     ⚠ 影片: 播放清單處理失敗 {ex.Message}");
                    return RtspResponse.Error(400, "Bad Request");
                }
                break;
            case "playlistRemove":
                _log("     影片: iPhone 移除播放項目");
                StopPlayer();
                break;
            default:
                _log($"     影片: 動作 {type}（略過）");
                break;
        }
        return new RtspResponse();
    }

    private void OnPlaylistReceived(string url, string playlist)
    {
        if (!playlist.StartsWith("#EXTM3U")) throw new FormatException("not an M3U8 playlist");
        string? next = null;
        string? playUrl = null;
        lock (_lock)
        {
            if (_remotePrefix is null) throw new InvalidOperationException("no play request in progress");
            if (url.EndsWith("/master.m3u8"))
            {
                var (variants, audio) = HlsPlaylist.ParseMaster(playlist);
                var variant = HlsPlaylist.ChooseVariant(variants) ?? throw new FormatException("master playlist has no variants");
                var track = HlsPlaylist.ChooseAudio(audio, variant.AudioGroup, CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);
                _localMaster = HlsPlaylist.BuildMaster(variant, track, _remotePrefix, LocalPrefix);
                _pendingFetches.Enqueue(variant.Uri);
                if (track is not null) _pendingFetches.Enqueue(track.Uri);
                _log($"     影片: 主清單 {variants.Count} 種畫質，選 {variant.Width}x{variant.Height} {variant.Codecs}" +
                     (track is null ? "" : $"，音軌 {track.Language ?? track.GroupId}"));
            }
            else
            {
                var expanded = HlsPlaylist.ExpandYouTubeCondensed(playlist);
                var localPath = url.StartsWith(_remotePrefix) ? url[_remotePrefix.Length..] : new Uri(url).AbsolutePath;
                _mediaPlaylists[localPath] = expanded;
                var (duration, segments, endList) = HlsPlaylist.Analyze(expanded);
                _log($"     影片: 媒體清單 {localPath}：{segments} 段，{duration:F0} 秒{(endList ? "" : "（直播）")}");
            }

            if (_pendingFetches.Count > 0) next = _pendingFetches.Dequeue();
            else playUrl = LocalPrefix + "/master.m3u8";
        }

        if (next is not null) _ = RequestPlaylistAsync(next);
        else if (playUrl is not null) StartPlayer(playUrl, _startSeconds);
    }

    /// <summary>FCUP request: asks the iPhone to fetch a URL for us (sent over the reverse channel).</summary>
    private async Task RequestPlaylistAsync(string url)
    {
        ReverseChannel? channel;
        string session;
        int requestId;
        lock (_lock)
        {
            channel = _reverse;
            session = _sessionId ?? "";
            requestId = _nextRequestId++;
        }
        // The reverse channel is usually set up just before /play; give it a moment if not yet.
        for (var i = 0; channel is null && i < 20; i++)
        {
            await Task.Delay(100);
            lock (_lock) channel = _reverse;
        }
        if (channel is null)
        {
            _log("     ⚠ 影片: 沒有反向連線，無法向 iPhone 要播放清單");
            return;
        }

        var request = new NSDictionary
        {
            { "FCUP_Response_ClientInfo", new NSNumber(1) },
            { "FCUP_Response_ClientRef", new NSNumber(40030004) },
            { "FCUP_Response_RequestID", new NSNumber(requestId) },
            { "FCUP_Response_URL", new NSString(url) },
            { "sessionID", new NSNumber(1) },
            { "FCUP_Response_Headers", new NSDictionary
                {
                    { "X-Playback-Session-Id", new NSString(session) },
                    { "User-Agent", new NSString(UserAgent) },
                }
            },
        };
        var root = new NSDictionary
        {
            { "sessionID", new NSNumber(1) },
            { "type", new NSString("unhandledURLRequest") },
            { "request", request },
        };
        try
        {
            await channel.SendEventAsync(session, root.ToXmlPropertyList());
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            _log($"     ⚠ 影片: 送出 FCUP 失敗 {ex.Message}");
        }
    }

    private RtspResponse ServeLocalPlaylist(string path)
    {
        string? content;
        lock (_lock) content = path == "/master.m3u8" ? _localMaster : _mediaPlaylists.GetValueOrDefault(path);
        if (content is null) return RtspResponse.Error(404, "Not Found");
        var r = new RtspResponse();
        r.SetBody(Encoding.UTF8.GetBytes(content), "application/x-mpegURL; charset=utf-8");
        return r;
    }

    private RtspResponse PlaybackInfo()
    {
        if (!_playing) return new RtspResponse();
        var s = _player.Status;
        if (s.Finished)
        {
            _log("     影片: 播放完畢");
            StopPlayer();
            return new RtspResponse { Disconnect = true };
        }
        if (!s.ReadyToPlay) return new RtspResponse();

        static NSArray Range(double start, double duration) =>
            new(new NSDictionary { { "start", new NSNumber(start) }, { "duration", new NSNumber(duration) } });
        var dict = new NSDictionary
        {
            { "duration", new NSNumber(s.Duration) },
            { "position", new NSNumber(s.Position) },
            { "rate", new NSNumber(s.Rate) },
            { "readyToPlay", new NSNumber(1) },
            { "playbackBufferEmpty", new NSNumber(0) },
            { "playbackBufferFull", new NSNumber(1) },
            { "playbackLikelyToKeepUp", new NSNumber(1) },
            { "loadedTimeRanges", Range(s.Position, Math.Max(0, s.Duration - s.Position)) },
            { "seekableTimeRanges", Range(0, s.Duration) },
        };
        return XmlPlist(dict);
    }

    private RtspResponse Rate(string query)
    {
        var rate = QueryValue(query) ?? 1;
        _log($"     影片: {(rate == 0 ? "暫停" : "播放")}");
        if (_playing) _player.SetRate(rate);
        return new RtspResponse();
    }

    private RtspResponse Scrub(string query)
    {
        if (QueryValue(query) is { } position && _playing)
        {
            _log($"     影片: 跳到 {position:F1} 秒");
            _player.Seek(position);
        }
        return new RtspResponse();
    }

    private RtspResponse ScrubStatus()
    {
        var s = _player.Status;
        var r = new RtspResponse();
        r.SetBody(Encoding.ASCII.GetBytes(
            string.Create(CultureInfo.InvariantCulture, $"duration: {s.Duration:F6}\nposition: {s.Position:F6}\n")), "text/parameters");
        return r;
    }

    private RtspResponse Stop()
    {
        _log("     影片: 停止");
        StopPlayer();
        return new RtspResponse();
    }

    private static RtspResponse PropertyAccepted() =>
        XmlPlist(new NSDictionary { { "errorCode", new NSNumber(0) } });

    private RtspResponse DrmUnsupported()
    {
        _log("     ⚠ 影片: 這個 App 的影片有 DRM 保護（FairPlay），無法在非 Apple 裝置播放，請改用「螢幕鏡像輸出」");
        return new RtspResponse { Status = 421, Reason = "Misdirected Request" };
    }

    // ---- helpers ----------------------------------------------------------------------------

    private void StartPlayer(string url, double start)
    {
        _playing = true;
        _player.Play(url, start);
    }

    private void StopPlayer()
    {
        if (!_playing) return;
        _playing = false;
        _player.Stop();
    }

    /// <summary>"value=0.000000" / "position=12.5" → number.</summary>
    private static double? QueryValue(string query)
    {
        var eq = query.IndexOf('=');
        return eq >= 0 && double.TryParse(query[(eq + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private static RtspResponse XmlPlist(NSObject root)
    {
        var r = new RtspResponse();
        r.SetBody(Encoding.UTF8.GetBytes(root.ToXmlPropertyList()), "text/x-apple-plist+xml");
        return r;
    }

    private static string Shorten(string url) => url.Length > 100 ? url[..100] + "…" : url;
}
