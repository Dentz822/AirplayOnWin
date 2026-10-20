using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AirPlay.Core.Audio;
using AirPlay.Core.Discovery;
using AirPlay.Core.FairPlay;
using AirPlay.Core.Identity;
using AirPlay.Core.Metadata;
using AirPlay.Core.Mirroring;
using AirPlay.Core.Pairing;
using AirPlay.Core.Rtsp;
using AirPlay.Core.Timing;
using Claunia.PropertyList;

namespace AirPlay.Core;

public sealed class ReceiverOptions
{
    public int DisplayWidth { get; init; } = 1920;
    public int DisplayHeight { get; init; } = 1080;
    public int RefreshRate { get; init; } = 60;
    public int MaxFps { get; init; } = 30;
    /// <summary>Log headers and decoded plist bodies of every request.</summary>
    public bool Verbose { get; init; }
    /// <summary>Creates a consumer for each mirroring session's video (null = discard).</summary>
    public Func<PlaybackClock, IMirrorSink>? MirrorSinkFactory { get; init; }
    /// <summary>Creates a player for each audio stream (null = receive and discard).</summary>
    public Func<AudioFormat, PlaybackClock, IAudioSink>? AudioSinkFactory { get; init; }
    /// <summary>Called when an iPhone starts a session: (device name, model).</summary>
    public Action<string, string>? ClientConnected { get; init; }
    /// <summary>Track info / album art / progress changed (null = session ended).</summary>
    public Action<NowPlaying?>? NowPlayingChanged { get; init; }
}

/// <summary>Receives the decoded-ready H.264 of one mirroring session.</summary>
public interface IMirrorSink : IDisposable
{
    void OnFormat(VideoFormat format);
    void OnFrame(VideoFrame frame);
}

/// <summary>
/// Handles the RTSP control connection of one iPhone: device info, pairing, FairPlay,
/// SETUP of time sync + mirroring video + audio, and TEARDOWN.
/// </summary>
public sealed class AirPlayConnection : IRtspHandler, IDisposable
{
    private readonly DeviceIdentity _id;
    private readonly ReceiverOptions _options;
    private readonly Action<string> _log;
    private readonly RtspConnectionInfo _connection;
    private readonly PairingSession _pairing;
    private readonly FairPlaySession _fairPlay = new();
    private readonly PlaybackClock _clock = new(); // shared by this session's video and audio

    private NtpClient? _ntp;
    private MirrorStream? _mirror;
    private IMirrorSink? _sink;
    private AudioStream? _audio;

    /// <summary>Session AES key: FairPlay-decrypted ekey, hashed with the pairing secret.</summary>
    public byte[]? AesKey { get; private set; }
    /// <summary>AES-CBC IV for audio (stage 7).</summary>
    public byte[]? AesIv { get; private set; }
    public double Volume { get; private set; }
    public NowPlaying? NowPlaying { get; private set; }

    public AirPlayConnection(DeviceIdentity id, ReceiverOptions options, RtspConnectionInfo connection, Action<string> log)
    {
        _id = id;
        _options = options;
        _connection = connection;
        _log = log;
        _pairing = new PairingSession(id);
    }

    public Task<RtspResponse> HandleAsync(RtspRequest req, CancellationToken ct)
    {
        var quiet = req.Uri == "/feedback"; // keep-alive every 2 s
        if (!quiet || _options.Verbose)
        {
            _log($"  >> {req.Method} {req.Uri}{(req.Body.Length > 0 ? $" ({req.Body.Length} bytes)" : "")}");
            if (_options.Verbose) LogDetails(req);
        }

        var path = req.Uri.Split('?')[0];
        var response = (req.Method, path) switch
        {
            ("GET", "/info") => Info(req),
            ("OPTIONS", _) => Options(),
            ("POST", "/pair-setup") => PairSetup(req),
            ("POST", "/pair-verify") => PairVerify(req),
            ("POST", "/fp-setup") => FpSetup(req),
            ("POST", "/feedback") => new RtspResponse(),
            ("SETUP", _) => Setup(req),
            ("RECORD", _) => Record(),
            ("GET_PARAMETER", _) => GetParameter(req),
            ("SET_PARAMETER", _) => SetParameter(req),
            ("FLUSH", _) => Flush(),
            ("TEARDOWN", _) => Teardown(req),
            _ => RtspResponse.Error(501, "Not Implemented"),
        };
        if (req.Method != "RECORD" && req.Header("CSeq") is not null)
            response.Headers.TryAdd("Audio-Jack-Status", "connected; type=digital");
        if (response.Status != 200) _log($"  << {response.Status} {response.Reason}");
        return Task.FromResult(response);
    }

    // ---- GET /info -------------------------------------------------------------------------

    private RtspResponse Info(RtspRequest req)
    {
        var dict = new NSDictionary();

        // The first /info carries a plist {"qualifier": ["txtAirPlay"]} and only wants the TXT record.
        if (req.IsBinaryPlist && req.Body.Length > 0)
        {
            var qualifier = (PropertyListParser.Parse(req.Body) as NSDictionary)?
                .ObjectForKey("qualifier") as NSArray;
            var first = qualifier is { Count: > 0 } ? qualifier[0].ToString() : null;
            if (first == "txtAirPlay")
                dict.Add("txtAirPlay", new NSData(AirPlayServices.EncodeTxt(AirPlayServices.AirPlayTxt(_id))));
            else if (first == "txtRAOP")
                dict.Add("txtRAOP", new NSData(AirPlayServices.EncodeTxt(AirPlayServices.RaopTxt(_id))));
            return Plist(dict);
        }

        dict.Add("deviceID", new NSString(_id.DeviceId));
        dict.Add("macAddress", new NSString(_id.DeviceId));
        dict.Add("pk", new NSData(_id.Ed25519PublicKey));
        dict.Add("features", new NSNumber((long)AirPlayServices.Features));
        dict.Add("name", new NSString(_id.Name));
        dict.Add("pi", new NSString(_id.PairingId.ToString()));
        dict.Add("vv", new NSNumber(2));
        dict.Add("statusFlags", new NSNumber(68));
        dict.Add("keepAliveLowPower", new NSNumber(true));
        dict.Add("sourceVersion", new NSString(AirPlayServices.SourceVersion));
        dict.Add("keepAliveSendStatsAsBody", new NSNumber(true));
        dict.Add("model", new NSString(AirPlayServices.Model));
        dict.Add("initialVolume", new NSNumber(0.0));

        var latencies = new NSArray();
        var formats = new NSArray();
        foreach (var type in new[] { 100, 101 })
        {
            var latency = new NSDictionary();
            latency.Add("type", new NSNumber(type));
            latency.Add("inputLatencyMicros", new NSNumber(0));
            latency.Add("audioType", new NSString("default"));
            latency.Add("outputLatencyMicros", new NSNumber(false));
            latencies.Add(latency);

            var format = new NSDictionary();
            format.Add("type", new NSNumber(type));
            format.Add("audioInputFormats", new NSNumber(0x3fffffc));
            format.Add("audioOutputFormats", new NSNumber(0x3fffffc));
            formats.Add(format);
        }
        dict.Add("audioLatencies", latencies);
        dict.Add("audioFormats", formats);

        var display = new NSDictionary();
        display.Add("uuid", new NSString("e0ff8a27-6738-3d56-8a16-cc53aacee925"));
        display.Add("widthPhysical", new NSNumber(0));
        display.Add("heightPhysical", new NSNumber(0));
        display.Add("width", new NSNumber(_options.DisplayWidth));
        display.Add("height", new NSNumber(_options.DisplayHeight));
        display.Add("widthPixels", new NSNumber(_options.DisplayWidth));
        display.Add("heightPixels", new NSNumber(_options.DisplayHeight));
        display.Add("rotation", new NSNumber(false));
        display.Add("refreshRate", new NSNumber(1.0 / _options.RefreshRate));
        display.Add("maxFPS", new NSNumber(_options.MaxFps));
        display.Add("overscanned", new NSNumber(false));
        display.Add("features", new NSNumber(14));
        dict.Add("displays", new NSArray(display));

        return Plist(dict);
    }

    private static RtspResponse Options()
    {
        var r = new RtspResponse();
        r.Headers["Public"] = "SETUP, RECORD, FLUSH, TEARDOWN, OPTIONS, GET_PARAMETER, SET_PARAMETER";
        return r;
    }

    // ---- pairing -------------------------------------------------------------------------

    private RtspResponse PairSetup(RtspRequest req)
    {
        var r = new RtspResponse();
        r.SetBody(_pairing.Setup(req.Body), "application/octet-stream");
        _log("     pair-setup 完成");
        return r;
    }

    private RtspResponse PairVerify(RtspRequest req)
    {
        if (req.Body.Length < 4) return RtspResponse.Error(400, "Bad Request");
        var r = new RtspResponse();
        if (req.Body[0] == 1)
        {
            r.SetBody(_pairing.VerifyStart(req.Body), "application/octet-stream");
        }
        else
        {
            try
            {
                _pairing.VerifyFinish(req.Body);
                _log("     pair-verify 完成，簽章驗證成功 ✓");
            }
            catch (PairingException ex)
            {
                _log($"     pair-verify 失敗: {ex.Message}");
                return new RtspResponse { Status = 470, Reason = "Connection Authorization Required", Disconnect = true };
            }
            r.Headers["Content-Type"] = "application/octet-stream";
        }
        return r;
    }

    // ---- FairPlay ------------------------------------------------------------------------

    private RtspResponse FpSetup(RtspRequest req)
    {
        try
        {
            var r = new RtspResponse();
            r.SetBody(_fairPlay.Setup(req.Body), "application/octet-stream");
            _log(req.Body.Length == 16
                ? $"     FairPlay 第 1 步 (mode {req.Body[14]})"
                : "     FairPlay 第 2 步完成 ✓");
            return r;
        }
        catch (FairPlayException ex)
        {
            _log($"     FairPlay 失敗: {ex.Message}");
            return RtspResponse.Error(501, "Not Implemented");
        }
    }

    // ---- SETUP ---------------------------------------------------------------------------
    // Round 1 carries ekey/eiv + timingPort: decrypt the key, start NTP.
    // Round 2 (and later) carries "streams": type 110 = mirroring video, type 96 = audio.

    private RtspResponse Setup(RtspRequest req)
    {
        if (!req.IsBinaryPlist || req.Body.Length == 0) return RtspResponse.Error(400, "Bad Request");
        if (PropertyListParser.Parse(req.Body) is not NSDictionary plist) return RtspResponse.Error(400, "Bad Request");
        var result = new NSDictionary();

        if (plist.ObjectForKey("ekey") is NSData ekey)
        {
            var key = _fairPlay.DecryptKey(ekey.Bytes);
            // With pairing, the effective key is SHA-512(aeskey | ecdh secret)[0..16].
            AesKey = _pairing.SharedSecret is { } secret ? SHA512.HashData([.. key, .. secret])[..16] : key;
            AesIv = (plist.ObjectForKey("eiv") as NSData)?.Bytes;
            _log($"     FairPlay 解出金鑰 ✓  來源: {plist.ObjectForKey("name")} ({plist.ObjectForKey("model")}, iOS {plist.ObjectForKey("osVersion")})");
            _options.ClientConnected?.Invoke(plist.ObjectForKey("name")?.ToString() ?? "iPhone", plist.ObjectForKey("model")?.ToString() ?? "");

            if (plist.ObjectForKey("timingPort") is NSNumber timingPort)
            {
                _ntp?.Dispose();
                _ntp = new NtpClient(_connection.Remote.Address, timingPort.ToInt(), _log);
                _ntp.Start();
                result.Add("timingPort", new NSNumber(_ntp.LocalPort));
            }
            result.Add("eventPort", new NSNumber(0)); // not used for mirroring
        }

        if (plist.ObjectForKey("streams") is NSArray streams)
        {
            var replies = new NSArray();
            foreach (var s in streams.OfType<NSDictionary>())
            {
                var type = (s.ObjectForKey("type") as NSNumber)?.ToInt() ?? -1;
                switch (type)
                {
                    case 110:
                        replies.Add(SetupMirror(s));
                        break;
                    case 96:
                        replies.Add(SetupAudio(s));
                        break;
                    default:
                        _log($"     ⚠ 不支援的串流類型 {type}");
                        break;
                }
            }
            result.Add("streams", replies);
        }

        return Plist(result);
    }

    private NSDictionary SetupMirror(NSDictionary stream)
    {
        if (AesKey is null) throw new InvalidOperationException("mirroring SETUP before key exchange");
        var connectionId = unchecked((ulong)((stream.ObjectForKey("streamConnectionID") as NSNumber)?.ToLong() ?? 0));

        StopMirror();
        _mirror = new MirrorStream(AesKey, connectionId, _log);
        if (_options.MirrorSinkFactory is { } factory)
        {
            _sink = factory(_clock);
            _mirror.FormatChanged += _sink.OnFormat;
            _mirror.FrameReceived += _sink.OnFrame;
        }
        _mirror.Start();
        _log($"     畫面串流準備好 (TCP {_mirror.Port})");

        return new NSDictionary
        {
            { "type", new NSNumber(110) },
            { "dataPort", new NSNumber(_mirror.Port) },
        };
    }

    private NSDictionary SetupAudio(NSDictionary stream)
    {
        if (AesKey is null || AesIv is null) throw new InvalidOperationException("audio SETUP before key exchange");
        var ct = (stream.ObjectForKey("ct") as NSNumber)?.ToInt() ?? 0;
        var spf = (stream.ObjectForKey("spf") as NSNumber)?.ToInt() ?? 0;
        var remoteControlPort = (stream.ObjectForKey("controlPort") as NSNumber)?.ToInt() ?? 0;
        var format = new AudioFormat(ct, spf);

        _audio?.Dispose();
        IAudioSink? sink = null;
        try { sink = _options.AudioSinkFactory?.Invoke(format, _clock); }
        catch (Exception ex) { _log($"     ⚠ 無法建立音訊播放: {ex.Message}"); }
        sink?.SetVolume(Volume);

        var remoteControl = remoteControlPort > 0
            ? new System.Net.IPEndPoint(_connection.Remote.Address, remoteControlPort)
            : null;
        _audio = new AudioStream(format, AesKey, AesIv, remoteControl, sink, _log, _clock);
        _audio.Start();
        _log($"     音訊串流準備好: {format.CodecName}, 每幀 {spf} 樣本 (UDP {_audio.DataPort}/{_audio.ControlPort}){(sink is null ? "，不播放" : "")}");

        return new NSDictionary
        {
            { "type", new NSNumber(96) },
            { "dataPort", new NSNumber(_audio.DataPort) },
            { "controlPort", new NSNumber(_audio.ControlPort) },
        };
    }

    private RtspResponse Flush()
    {
        _audio?.Flush();
        return new RtspResponse();
    }

    private static RtspResponse Record()
    {
        var r = new RtspResponse();
        r.Headers["Audio-Latency"] = "0";
        r.Headers["Audio-Jack-Status"] = "connected; type=analog";
        return r;
    }

    // ---- parameters ------------------------------------------------------------------------

    private RtspResponse GetParameter(RtspRequest req)
    {
        if (req.ContentType is null) return RtspResponse.Error(451, "Parameter not understood");
        var r = new RtspResponse();
        if (req.ContentType == "text/parameters" && Encoding.ASCII.GetString(req.Body).Contains("volume"))
            r.SetBody(Encoding.ASCII.GetBytes($"volume: {Volume.ToString("F6", CultureInfo.InvariantCulture)}\r\n"), "text/parameters");
        return r;
    }

    private RtspResponse SetParameter(RtspRequest req)
    {
        switch (req.ContentType)
        {
            case "image/jpeg" or "image/png":
                UpdateNowPlaying(np => np with { Artwork = req.Body });
                _log($"     收到封面圖片 ({req.Body.Length / 1024} KB)");
                break;
            case "image/none":
                UpdateNowPlaying(np => np with { Artwork = null });
                break;
            case "application/x-dmap-tagged":
                var tags = NowPlayingParser.ParseDmap(req.Body);
                tags.TryGetValue("minm", out var title);
                tags.TryGetValue("asar", out var artist);
                tags.TryGetValue("asal", out var album);
                UpdateNowPlaying(np => np with { Title = title, Artist = artist, Album = album });
                _log($"     正在播放: {title} — {artist}（{album}）");
                break;
        }
        if (req.ContentType == "text/parameters")
        {
            foreach (var line in Encoding.ASCII.GetString(req.Body).Split("\r\n"))
            {
                if (NowPlayingParser.ParseProgress(line) is { } progress)
                    UpdateNowPlaying(np => np with
                    {
                        Position = progress.Position,
                        Duration = progress.Duration,
                        PositionAtTicks = System.Diagnostics.Stopwatch.GetTimestamp(),
                    });
                // "volume: -15.000000" — 0 is full volume, -30 is quietest, -144 is mute
                if (line.StartsWith("volume:") &&
                    double.TryParse(line[7..].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                {
                    Volume = v;
                    _audio?.SetVolume(v);
                    _log($"     音量: {v:F1} dB");
                }
            }
        }
        return new RtspResponse();
    }

    private void UpdateNowPlaying(Func<NowPlaying, NowPlaying> change)
    {
        NowPlaying = change(NowPlaying ?? new NowPlaying());
        _options.NowPlayingChanged?.Invoke(NowPlaying);
    }

    // ---- TEARDOWN ------------------------------------------------------------------------

    private RtspResponse Teardown(RtspRequest req)
    {
        var types = new HashSet<int>();
        if (req.IsBinaryPlist && req.Body.Length > 0 &&
            (PropertyListParser.Parse(req.Body) as NSDictionary)?.ObjectForKey("streams") is NSArray streams)
        {
            foreach (var s in streams.OfType<NSDictionary>())
                if (s.ObjectForKey("type") is NSNumber n) types.Add(n.ToInt());
        }

        if (types.Contains(96)) { _audio?.Dispose(); _audio = null; _clock.ResetAudio(); }
        if (types.Contains(110)) StopMirror();
        if (types.Count == 0)
        {
            _log("     iPhone 結束投影");
            StopAll();
        }

        var r = new RtspResponse();
        r.Headers["Connection"] = "close";
        return r;
    }

    private void StopMirror()
    {
        _mirror?.Dispose();
        _mirror = null;
        _sink?.Dispose();
        _sink = null;
    }

    private void StopAll()
    {
        if (NowPlaying is not null)
        {
            NowPlaying = null;
            _options.NowPlayingChanged?.Invoke(null);
        }
        StopMirror();
        _audio?.Dispose();
        _audio = null;
        _ntp?.Dispose();
        _ntp = null;
    }

    public void Dispose() => StopAll();

    // ---- helpers ---------------------------------------------------------------------------

    private static RtspResponse Plist(NSObject root)
    {
        var r = new RtspResponse();
        r.SetBody(BinaryPropertyListWriter.WriteToArray(root), "application/x-apple-binary-plist");
        return r;
    }

    private void LogDetails(RtspRequest req)
    {
        foreach (var (k, v) in req.Headers) _log($"     {k}: {v}");
        if (req.Body.Length == 0) return;
        if (req.IsBinaryPlist)
        {
            try { _log(Indent(PropertyListParser.Parse(req.Body).ToXmlPropertyList())); }
            catch (Exception ex) { _log($"     (plist 解析失敗: {ex.Message})"); }
        }
        else
        {
            var n = Math.Min(64, req.Body.Length);
            _log($"     body: {Convert.ToHexString(req.Body, 0, n)}{(req.Body.Length > n ? "…" : "")}");
        }
    }

    private static string Indent(string text) =>
        string.Join("\n", text.Split('\n').Select(l => "        " + l.TrimEnd('\r')));
}
