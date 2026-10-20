using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace AirPlay.Media;

/// <summary>
/// Plays a video URL (HLS .m3u8, MP4, …, over http/https) with FFmpeg: one background thread
/// reads, decodes and hands out pictures (BGRA) and sound (44.1 kHz 16-bit stereo PCM),
/// each with its presentation time in nanoseconds from the start of the media.
/// Display timing is the caller's job; <see cref="ShouldWait"/> lets it slow the reader down.
/// </summary>
public sealed unsafe class UrlPlayer : IDisposable
{
    private readonly object _lock = new();
    private Thread? _thread;
    private volatile bool _cancel;
    private volatile bool _paused;
    private long _skipUntilNs;
    private int _reportResume;
    private double? _pendingSeek;
    private readonly AVIOInterruptCB_callback _interrupt; // kept alive: FFmpeg calls it during network I/O

    public UrlPlayer()
    {
        _interrupt = _ => _cancel ? 1 : 0;
    }

    /// <summary>Picture with presentation time (ns); the buffer is reused after the call returns.</summary>
    public event Action<BgraFrame, long>? Picture;
    /// <summary>PCM chunk with the presentation time (ns) of its first sample.</summary>
    public event Action<ReadOnlyMemory<byte>, long>? Sound;
    /// <summary>A seek finished: drop everything queued from before it.</summary>
    public event Action? SeekDone;
    public event Action? Ended;
    public event Action<string>? Failed;
    public event Action<string>? Info;

    /// <summary>Called between packets; while it returns true the reader waits (downstream is full).</summary>
    public Func<bool>? ShouldWait { get; set; }

    public double DurationSeconds { get; private set; }
    public bool HasAudio { get; private set; }
    public bool HasVideo { get; private set; }
    public bool IsOpen { get; private set; }

    public void Start(string url, double startSeconds)
    {
        _thread = new Thread(() => Run(url, startSeconds)) { IsBackground = true, Name = "UrlPlayer" };
        _thread.Start();
    }

    public void SetPaused(bool paused) => _paused = paused;

    public void Seek(double seconds)
    {
        lock (_lock) _pendingSeek = seconds;
    }

    private void Run(string url, double startSeconds)
    {
        AVFormatContext* format = null;
        AVCodecContext* video = null, audio = null;
        AVFrame* frame = null;
        AVPacket* packet = null;
        SwsContext* sws = null;
        SwrContext* swr = null;
        try
        {
            FFmpegLoader.EnsureLoaded();
            format = ffmpeg.avformat_alloc_context();
            format->interrupt_callback.callback = _interrupt;

            AVDictionary* options = null;
            ffmpeg.av_dict_set(&options, "user_agent", "AppleCoreMedia/1.0.0.21B91 (Apple TV; U; CPU OS 17_1 like Mac OS X; en_us)", 0);
            ffmpeg.av_dict_set(&options, "reconnect", "1", 0);
            ffmpeg.av_dict_set(&options, "reconnect_streamed", "1", 0);
            ffmpeg.av_dict_set(&options, "reconnect_delay_max", "4", 0);
            ffmpeg.av_dict_set(&options, "rw_timeout", "15000000", 0); // 15 s per network operation
            var rc = ffmpeg.avformat_open_input(&format, url, null, &options);
            ffmpeg.av_dict_free(&options);
            if (rc < 0) throw new InvalidOperationException($"無法開啟影片：{FFmpegLoader.ErrorText(rc)}");
            Check(ffmpeg.avformat_find_stream_info(format, null), "avformat_find_stream_info");

            var videoIndex = ffmpeg.av_find_best_stream(format, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
            var audioIndex = ffmpeg.av_find_best_stream(format, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, videoIndex, null, 0);
            for (var i = 0; i < format->nb_streams; i++)
                if (i != videoIndex && i != audioIndex) format->streams[i]->discard = AVDiscard.AVDISCARD_ALL;

            if (videoIndex >= 0) video = OpenDecoder(format->streams[videoIndex]);
            if (audioIndex >= 0) audio = OpenDecoder(format->streams[audioIndex]);
            HasVideo = video != null;
            HasAudio = audio != null;
            if (!HasVideo && !HasAudio) throw new InvalidOperationException("影片裡沒有可播放的畫面或聲音");

            var startTime = format->start_time == ffmpeg.AV_NOPTS_VALUE ? 0 : format->start_time; // AV_TIME_BASE units
            DurationSeconds = format->duration > 0 ? format->duration / (double)ffmpeg.AV_TIME_BASE : 0;
            Info?.Invoke($"     影片已開啟: {DurationSeconds:F0} 秒，" +
                         (HasVideo ? $"畫面 {CodecName(video)} {video->width}x{video->height}" : "沒有畫面") + "，" +
                         (HasAudio ? $"聲音 {CodecName(audio)} {audio->sample_rate} Hz" : "沒有聲音"));
            IsOpen = true;

            if (startSeconds > 0.5) SeekTo(format, videoIndex, startSeconds, startTime, video, audio);

            frame = ffmpeg.av_frame_alloc();
            packet = ffmpeg.av_packet_alloc();
            var pcm = new byte[64 * 1024];
            byte[] bgra = [];

            while (!_cancel)
            {
                double? seek;
                lock (_lock) { seek = _pendingSeek; _pendingSeek = null; }
                if (seek is { } target)
                {
                    SeekTo(format, videoIndex, target, startTime, video, audio);
                    SeekDone?.Invoke();
                }

                if (_paused || (ShouldWait?.Invoke() ?? false))
                {
                    Thread.Sleep(10);
                    continue;
                }

                rc = ffmpeg.av_read_frame(format, packet);
                if (rc == ffmpeg.AVERROR_EOF) { Flush(video); Flush(audio); Ended?.Invoke(); WaitWhileIdle(); continue; }
                if (rc < 0)
                {
                    if (_cancel) break;
                    throw new InvalidOperationException($"讀取影片失敗：{FFmpegLoader.ErrorText(rc)}");
                }

                var stream = format->streams[packet->stream_index];
                var codec = packet->stream_index == videoIndex ? video : packet->stream_index == audioIndex ? audio : null;
                if (codec != null && ffmpeg.avcodec_send_packet(codec, packet) >= 0)
                {
                    while (ffmpeg.avcodec_receive_frame(codec, frame) == 0)
                    {
                        var pts = frame->best_effort_timestamp;
                        var ptsNs = pts == ffmpeg.AV_NOPTS_VALUE ? 0
                            : (long)(pts * ffmpeg.av_q2d(stream->time_base) * 1e9) - startTime * 1000L;
                        if (ptsNs < _skipUntilNs) { ffmpeg.av_frame_unref(frame); continue; } // accurate seek
                        var kind = codec == video ? 1 : 2;
                        if ((_reportResume & kind) != 0)
                        {
                            _reportResume &= ~kind;
                            Info?.Invoke($"     跳轉後第一個{(codec == video ? "畫面" : "聲音")}: {ptsNs / 1e9:F3} 秒");
                        }
                        if (codec == video) EmitPicture(frame, ptsNs, ref sws, ref bgra);
                        else EmitSound(frame, ptsNs, ref swr, ref pcm);
                        ffmpeg.av_frame_unref(frame);
                    }
                }
                ffmpeg.av_packet_unref(packet);
            }
        }
        catch (Exception ex)
        {
            if (!_cancel) Failed?.Invoke(ex.Message);
        }
        finally
        {
            if (packet != null) ffmpeg.av_packet_free(&packet);
            if (frame != null) ffmpeg.av_frame_free(&frame);
            if (video != null) ffmpeg.avcodec_free_context(&video);
            if (audio != null) ffmpeg.avcodec_free_context(&audio);
            if (sws != null) ffmpeg.sws_freeContext(sws);
            if (swr != null) ffmpeg.swr_free(&swr);
            if (format != null) ffmpeg.avformat_close_input(&format);
        }

        void Flush(AVCodecContext* codec)
        {
            // Draining at end of file isn't needed for display: the last frames were already sent.
            if (codec != null) ffmpeg.avcodec_flush_buffers(codec);
        }
    }

    /// <summary>At end of file: wait for a seek (to play again) or cancellation.</summary>
    private void WaitWhileIdle()
    {
        while (!_cancel)
        {
            lock (_lock) if (_pendingSeek is not null) return;
            Thread.Sleep(50);
        }
    }

    private static AVCodecContext* OpenDecoder(AVStream* stream)
    {
        var codec = ffmpeg.avcodec_find_decoder(stream->codecpar->codec_id);
        if (codec == null) return null;
        var ctx = ffmpeg.avcodec_alloc_context3(codec);
        ffmpeg.avcodec_parameters_to_context(ctx, stream->codecpar);
        ctx->pkt_timebase = stream->time_base;
        ctx->thread_count = 0; // auto
        if (ffmpeg.avcodec_open2(ctx, codec, null) < 0)
        {
            ffmpeg.avcodec_free_context(&ctx);
            return null;
        }
        return ctx;
    }

    /// <summary>
    /// Seeks to the keyframe at or before (target − 1 s) on the video stream, then skips output
    /// until the target, which makes the seek exact for both audio and video.
    /// The extra second matters for HLS with audio and video muxed in one MPEG-TS stream: the
    /// muxer stores audio up to 0.7 s ahead of its video, and FFmpeg's HLS seek drops everything
    /// before the video keyframe — that lost audio now falls before the target.
    /// </summary>
    private void SeekTo(AVFormatContext* format, int videoIndex, double seconds, long startTime, AVCodecContext* video, AVCodecContext* audio)
    {
        var targetUs = (long)(Math.Max(0, seconds - 1.0) * ffmpeg.AV_TIME_BASE) + startTime;
        int rc;
        if (videoIndex >= 0)
        {
            var tb = format->streams[videoIndex]->time_base;
            var ts = ffmpeg.av_rescale_q(targetUs, new AVRational { num = 1, den = ffmpeg.AV_TIME_BASE }, tb);
            rc = ffmpeg.av_seek_frame(format, videoIndex, ts, ffmpeg.AVSEEK_FLAG_BACKWARD);
        }
        else
        {
            rc = ffmpeg.avformat_seek_file(format, -1, long.MinValue, targetUs, targetUs, 0);
        }
        if (rc < 0) Info?.Invoke($"     ⚠ 跳轉失敗：{FFmpegLoader.ErrorText(rc)}");
        _skipUntilNs = (long)(seconds * 1e9);
        _reportResume = (video != null ? 1 : 0) | (audio != null ? 2 : 0); // bit flags: log first of each
        if (video != null) ffmpeg.avcodec_flush_buffers(video);
        if (audio != null) ffmpeg.avcodec_flush_buffers(audio);
    }

    private void EmitPicture(AVFrame* f, long ptsNs, ref SwsContext* sws, ref byte[] bgra)
    {
        int w = f->width, h = f->height;
        sws = ffmpeg.sws_getCachedContext(sws, w, h, (AVPixelFormat)f->format,
            w, h, AVPixelFormat.AV_PIX_FMT_BGRA, ffmpeg.SWS_BILINEAR, null, null, null);
        var stride = w * 4;
        if (bgra.Length != stride * h) bgra = new byte[stride * h];
        fixed (byte* dst = bgra)
        {
            ffmpeg.sws_scale(sws, f->data.ToArray(), f->linesize.ToArray(), 0, h,
                new byte*[] { dst, null, null, null }, new[] { stride, 0, 0, 0 });
        }
        Picture?.Invoke(new BgraFrame(w, h, stride, bgra), ptsNs);
    }

    private void EmitSound(AVFrame* f, long ptsNs, ref SwrContext* swr, ref byte[] pcm)
    {
        if (swr == null)
        {
            AVChannelLayout outLayout;
            ffmpeg.av_channel_layout_default(&outLayout, AudioDecoder.Channels);
            SwrContext* s = null;
            if (ffmpeg.swr_alloc_set_opts2(&s, &outLayout, AVSampleFormat.AV_SAMPLE_FMT_S16, AudioDecoder.SampleRate,
                    &f->ch_layout, (AVSampleFormat)f->format, f->sample_rate, 0, null) < 0 || ffmpeg.swr_init(s) < 0)
                throw new InvalidOperationException("swresample 初始化失敗");
            swr = s;
        }
        var maxSamples = (int)ffmpeg.av_rescale_rnd(f->nb_samples, AudioDecoder.SampleRate, f->sample_rate, AVRounding.AV_ROUND_UP) + 256;
        var needed = maxSamples * AudioDecoder.Channels * 2;
        if (pcm.Length < needed) pcm = new byte[needed];
        int converted;
        fixed (byte* outPtr = pcm)
        {
            var planes = outPtr;
            converted = ffmpeg.swr_convert(swr, &planes, maxSamples, f->extended_data, f->nb_samples);
        }
        if (converted > 0) Sound?.Invoke(pcm.AsMemory(0, converted * AudioDecoder.Channels * 2), ptsNs);
    }

    private static string CodecName(AVCodecContext* ctx) =>
        ffmpeg.avcodec_get_name(ctx->codec_id) ?? "?";

    private static void Check(int rc, string what)
    {
        if (rc < 0) throw new InvalidOperationException($"{what}: {FFmpegLoader.ErrorText(rc)}");
    }

    public void Dispose()
    {
        _cancel = true;
        _thread?.Join(TimeSpan.FromSeconds(3));
        GC.KeepAlive(_interrupt);
    }
}
