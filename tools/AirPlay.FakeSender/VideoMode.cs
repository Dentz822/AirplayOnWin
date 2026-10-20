using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AirPlay.Media;
using Claunia.PropertyList;
using FFmpeg.AutoGen;

namespace AirPlay.FakeSender;

/// <summary>
/// AirPlay video test (the "AirPlay button inside an app" path):
///   --video-url : like Safari, POST /play with a plain http:// HLS address.
///   --youtube   : like YouTube, POST /play with mlhls://localhost/master.m3u8; playlists are
///                 handed over through FCUP requests on the reverse connection, the media
///                 playlist in YouTube's condensed form.
/// Test footage (moving pattern + tone, 2 s TS segments) is generated and served locally.
/// Then it polls /playback-info and exercises pause, resume, seek and stop.
/// </summary>
internal static class VideoMode
{
    private const int SegmentSeconds = 2;

    public static async Task<int> RunAsync(string[] args)
    {
        var youtube = args.Contains("--youtube");
        var host = ArgValue(args, "--host") ?? "127.0.0.1";
        var seconds = int.Parse(ArgValue(args, "--seconds") ?? "10", CultureInfo.InvariantCulture);
        void Log(string s) => Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [假 iPhone] {s}");

        // ---- test footage + web server ----
        var dir = Path.Combine(Path.GetTempPath(), "airplay-fake-hls");
        Directory.CreateDirectory(dir);
        var segments = TestFootage.Write(dir, seconds, SegmentSeconds);
        var webPort = FreePort();
        var web = $"http://127.0.0.1:{webPort}";
        using var server = new TinyWebServer(webPort, dir);
        Log($"測試影片 {seconds} 秒（{segments} 段），網址 {web}");

        var mediaPlaylist = new StringBuilder("#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:2\n#EXT-X-PLAYLIST-TYPE:VOD\n");
        var condensed = new StringBuilder(mediaPlaylist.ToString())
            .Append($"#YT-EXT-CONDENSED-URL:BASE-URI=\"{web}/videoplayback\",PARAMS=\"id,sq\",PREFIX=\"s/\"\n");
        for (var i = 0; i < segments; i++)
        {
            mediaPlaylist.Append($"#EXTINF:{SegmentSeconds}.000,\nseg{i}.ts\n");
            condensed.Append($"#EXTINF:{SegmentSeconds}.000,\ns/test/{i}\n");
        }
        mediaPlaylist.Append("#EXT-X-ENDLIST\n");
        condensed.Append("#EXT-X-ENDLIST\n");
        File.WriteAllText(Path.Combine(dir, "media.m3u8"), mediaPlaylist.ToString());

        const string master = """
            #EXTM3U
            #EXT-X-STREAM-INF:BANDWIDTH=9000000,CODECS="vp09.00.51.08,mp4a.40.2",RESOLUTION=3840x2160
            mlhls://localhost/itag/313/mediadata.m3u8
            #EXT-X-STREAM-INF:BANDWIDTH=800000,CODECS="avc1.42c01e,mp4a.40.2",RESOLUTION=640x360
            mlhls://localhost/itag/18/mediadata.m3u8
            """;

        // ---- connections: main (requests) + reverse (FCUP events from the receiver) ----
        using var main = new RtspClient(host, 7000);
        var info = await main.Send("GET", "/info");
        Log($"/info → {info.Status}");
        var serverInfo = await main.Send("GET", "/server-info");
        Log($"/server-info → {serverInfo.Status}");

        var sessionId = Guid.NewGuid().ToString().ToUpperInvariant();
        using var reverse = new TcpClient();
        await reverse.ConnectAsync(host, 7000);
        var reverseStream = reverse.GetStream();
        await reverseStream.WriteAsync(Encoding.ASCII.GetBytes(
            $"POST /reverse HTTP/1.1\r\nUpgrade: PTTH/1.0\r\nConnection: Upgrade\r\nX-Apple-Purpose: event\r\n" +
            $"X-Apple-Session-ID: {sessionId}\r\nContent-Length: 0\r\n\r\n"));
        var upgrade = await ReadHttpMessage(reverseStream);
        Log($"/reverse → {upgrade.FirstLine}");

        var fcupRequests = 0;
        var fetchedUrls = new List<string>();
        var reverseTask = Task.Run(async () =>
        {
            // Answer FCUP requests: "fetch this URL for me" → POST /action with the playlist.
            while (true)
            {
                var msg = await ReadHttpMessage(reverseStream);
                if (msg.FirstLine is null) return;
                await reverseStream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n"));
                var plist = (NSDictionary)PropertyListParser.Parse(msg.Body);
                var request = (NSDictionary)plist.ObjectForKey("request");
                var url = request.ObjectForKey("FCUP_Response_URL").ToString()!;
                fcupRequests++;
                fetchedUrls.Add(url);
                var playlist = url.EndsWith("/master.m3u8") ? master
                    : url.Contains("/itag/18/") ? condensed.ToString()
                    : throw new InvalidOperationException($"receiver asked for a stream it should have skipped: {url}");
                var action = new NSDictionary
                {
                    { "type", new NSString("unhandledURLResponse") },
                    { "params", new NSDictionary
                        {
                            { "FCUP_Response_URL", new NSString(url) },
                            { "FCUP_Response_Data", new NSData(Encoding.UTF8.GetBytes(playlist)) },
                            { "FCUP_Response_StatusCode", new NSNumber(200) },
                            { "FCUP_Response_RequestID", request.ObjectForKey("FCUP_Response_RequestID") },
                        }
                    },
                };
                lock (main)
                {
                    var r = main.SendWithSession("POST", "/action", BinaryPropertyListWriter.WriteToArray(action),
                        "application/x-apple-binary-plist", sessionId).GetAwaiter().GetResult();
                    Log($"FCUP {url} → /action {r.Status}");
                }
            }
        });

        // ---- play ----
        var location = youtube ? "mlhls://localhost/master.m3u8" : $"{web}/media.m3u8";
        var play = new NSDictionary
        {
            { "Content-Location", new NSString(location) },
            { "Start-Position-Seconds", new NSNumber(0.0) },
            { "uuid", new NSString(Guid.NewGuid().ToString()) },
            { "clientProcName", new NSString(youtube ? "YouTube" : "MobileSafari") },
        };
        (int Status, byte[] Body) playResponse;
        lock (main)
            playResponse = main.SendWithSession("POST", "/play", BinaryPropertyListWriter.WriteToArray(play),
                "application/x-apple-binary-plist", sessionId).GetAwaiter().GetResult();
        Log($"/play {location} → {playResponse.Status}");

        async Task<(double Position, double Duration, double Rate)?> PlaybackInfo()
        {
            (int Status, byte[] Body) r;
            lock (main) r = main.SendWithSession("GET", "/playback-info", null, null, sessionId).GetAwaiter().GetResult();
            await Task.Yield();
            if (r.Body.Length == 0) return null;
            var d = (NSDictionary)PropertyListParser.Parse(r.Body);
            return (((NSNumber)d.ObjectForKey("position")).ToDouble(), ((NSNumber)d.ObjectForKey("duration")).ToDouble(),
                    ((NSNumber)d.ObjectForKey("rate")).ToDouble());
        }

        async Task Poll(string label, int count)
        {
            for (var i = 0; i < count; i++)
            {
                await Task.Delay(1000);
                var s = await PlaybackInfo();
                Log(s is { } v ? $"{label}: 位置 {v.Position:F1} / {v.Duration:F1} 秒，rate {v.Rate}" : $"{label}: 尚未就緒");
            }
        }

        void Command(string method, string uri)
        {
            lock (main)
            {
                var r = main.SendWithSession(method, uri, null, null, sessionId).GetAwaiter().GetResult();
                Log($"{method} {uri} → {r.Status}");
            }
        }

        await Poll("播放中", 4);
        Command("POST", "/rate?value=0.000000");
        await Poll("暫停中", 2);
        Command("POST", "/rate?value=1.000000");
        await Poll("繼續播放", 2);
        Command("POST", "/scrub?position=6.000000");
        await Poll("跳轉後", 5);
        Command("POST", "/stop");

        Log($"FCUP 次數 {fcupRequests}：{string.Join(", ", fetchedUrls)}");
        Log($"網頁伺服器送出: {string.Join(", ", server.Served.Distinct().OrderBy(s => s))}");
        reverse.Close();
        return 0;
    }

    private static string? ArgValue(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static async Task<(string? FirstLine, byte[] Body)> ReadHttpMessage(Stream stream)
    {
        var head = new List<byte>();
        while (!(head.Count >= 4 && head[^4] == '\r' && head[^3] == '\n' && head[^2] == '\r' && head[^1] == '\n'))
        {
            var buffer = new byte[1];
            if (await stream.ReadAsync(buffer) == 0) return (null, []);
            head.Add(buffer[0]);
        }
        var lines = Encoding.ASCII.GetString(head.ToArray()).Split("\r\n");
        var length = lines.Where(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            .Select(l => int.Parse(l[15..].Trim())).FirstOrDefault();
        var body = new byte[length];
        await stream.ReadExactlyAsync(body);
        return (lines[0], body);
    }
}

/// <summary>Serves files from a folder; maps YouTube-style /videoplayback/id/x/sq/N to segN.ts.</summary>
internal sealed class TinyWebServer : IDisposable
{
    private readonly HttpListener _listener = new();
    public List<string> Served { get; } = new();

    public TinyWebServer(int port, string dir)
    {
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        _ = Task.Run(async () =>
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); } catch { return; }
                var path = ctx.Request.Url!.AbsolutePath;
                var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                var file = parts is ["videoplayback", "id", _, "sq", var n] ? $"seg{n}.ts" : parts.LastOrDefault() ?? "";
                var full = Path.Combine(dir, file);
                lock (Served) Served.Add(path.StartsWith("/videoplayback") ? "/videoplayback/…/" + file : path);
                if (File.Exists(full))
                {
                    var data = await File.ReadAllBytesAsync(full);
                    ctx.Response.ContentType = file.EndsWith(".m3u8") ? "application/x-mpegURL" : "video/mp2t";
                    ctx.Response.ContentLength64 = data.Length;
                    await ctx.Response.OutputStream.WriteAsync(data);
                }
                else ctx.Response.StatusCode = 404;
                ctx.Response.Close();
            }
        });
    }

    public void Dispose() => _listener.Close();
}

/// <summary>Writes a moving test pattern with a tone as MPEG-TS segments (H.264 + AAC).</summary>
internal static unsafe class TestFootage
{
    private const int W = 640, H = 360, Fps = 30;

    public static int Write(string dir, int seconds, int segmentSeconds)
    {
        FFmpegLoader.EnsureLoaded();
        var vcodec = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_H264);
        var v = ffmpeg.avcodec_alloc_context3(vcodec);
        v->width = W; v->height = H; v->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUV420P;
        v->time_base = new AVRational { num = 1, den = Fps };
        v->framerate = new AVRational { num = Fps, den = 1 };
        v->gop_size = Fps * segmentSeconds; v->max_b_frames = 0;
        v->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
        ffmpeg.av_opt_set(v->priv_data, "preset", "veryfast", 0);
        ffmpeg.av_opt_set(v->priv_data, "tune", "zerolatency", 0); // no frame delay: segments start exactly on their keyframe
        if (ffmpeg.avcodec_open2(v, vcodec, null) < 0) throw new Exception("H.264 encoder");

        var acodec = ffmpeg.avcodec_find_encoder_by_name("aac");
        var a = ffmpeg.avcodec_alloc_context3(acodec);
        a->sample_rate = 44100; a->sample_fmt = AVSampleFormat.AV_SAMPLE_FMT_FLTP; a->bit_rate = 128000;
        a->time_base = new AVRational { num = 1, den = 44100 };
        ffmpeg.av_channel_layout_default(&a->ch_layout, 2);
        a->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
        if (ffmpeg.avcodec_open2(a, acodec, null) < 0) throw new Exception("AAC encoder");

        var vf = ffmpeg.av_frame_alloc(); vf->format = (int)v->pix_fmt; vf->width = W; vf->height = H; ffmpeg.av_frame_get_buffer(vf, 0);
        var af = ffmpeg.av_frame_alloc(); af->format = (int)a->sample_fmt; af->nb_samples = a->frame_size; af->sample_rate = 44100;
        ffmpeg.av_channel_layout_default(&af->ch_layout, 2); ffmpeg.av_frame_get_buffer(af, 0);
        var pkt = ffmpeg.av_packet_alloc();

        var segments = seconds / segmentSeconds;
        long audioSamples = 0;
        for (var s = 0; s < segments; s++)
        {
            AVFormatContext* oc = null;
            var path = Path.Combine(dir, $"seg{s}.ts");
            ffmpeg.avformat_alloc_output_context2(&oc, null, "mpegts", path);
            var vs = ffmpeg.avformat_new_stream(oc, null); ffmpeg.avcodec_parameters_from_context(vs->codecpar, v); vs->time_base = v->time_base;
            var ast = ffmpeg.avformat_new_stream(oc, null); ffmpeg.avcodec_parameters_from_context(ast->codecpar, a); ast->time_base = a->time_base;
            ffmpeg.avio_open(&oc->pb, path, ffmpeg.AVIO_FLAG_WRITE);
            ffmpeg.avformat_write_header(oc, null);

            static void Drain(AVCodecContext* c, AVStream* st, AVFormatContext* oc, AVPacket* pkt)
            {
                while (ffmpeg.avcodec_receive_packet(c, pkt) == 0)
                {
                    ffmpeg.av_packet_rescale_ts(pkt, c->time_base, st->time_base);
                    pkt->stream_index = st->index;
                    ffmpeg.av_interleaved_write_frame(oc, pkt);
                }
            }

            for (var f = 0; f < Fps * segmentSeconds; f++)
            {
                var n = s * Fps * segmentSeconds + f;
                ffmpeg.av_frame_make_writable(vf);
                var box = n * 8 % (W - 80);
                for (var y = 0; y < H; y++)
                {
                    var row = vf->data[0] + y * vf->linesize[0];
                    for (var x = 0; x < W; x++)
                        row[x] = (byte)(x >= box && x < box + 80 && y > 140 && y < 220 ? 235 : 40 + (x + n * 2) % 160);
                }
                for (var y = 0; y < H / 2; y++)
                {
                    new Span<byte>(vf->data[1] + y * vf->linesize[1], W / 2).Fill((byte)(100 + s * 20));
                    new Span<byte>(vf->data[2] + y * vf->linesize[2], W / 2).Fill(150);
                }
                vf->pts = n;
                ffmpeg.avcodec_send_frame(v, vf);
                Drain(v, vs, oc, pkt);

                while (audioSamples * Fps < (long)(n + 1) * 44100)
                {
                    ffmpeg.av_frame_make_writable(af);
                    for (var i = 0; i < af->nb_samples; i++)
                    {
                        var t = (audioSamples + i) / 44100.0;
                        ((float*)af->data[0])[i] = (float)(0.2 * Math.Sin(2 * Math.PI * 523 * t));
                        ((float*)af->data[1])[i] = (float)(0.2 * Math.Sin(2 * Math.PI * 659 * t));
                    }
                    af->pts = audioSamples;
                    audioSamples += af->nb_samples;
                    ffmpeg.avcodec_send_frame(a, af);
                    Drain(a, ast, oc, pkt);
                }
            }
            ffmpeg.av_write_trailer(oc);
            ffmpeg.avio_closep(&oc->pb);
            ffmpeg.avformat_free_context(oc);
        }
        ffmpeg.av_packet_free(&pkt);
        ffmpeg.av_frame_free(&vf);
        ffmpeg.av_frame_free(&af);
        ffmpeg.avcodec_free_context(&v);
        ffmpeg.avcodec_free_context(&a);
        return segments;
    }
}
