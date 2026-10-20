using FFmpeg.AutoGen;

namespace AirPlay.Media;

/// <summary>A decoded picture in BGRA (4 bytes per pixel, top-down).</summary>
public readonly record struct BgraFrame(int Width, int Height, int Stride, byte[] Pixels);

/// <summary>
/// Decodes H.264 access units (Annex-B, one picture per call, as MirrorStream delivers them)
/// and converts each picture to BGRA. Not thread-safe: use from one thread.
/// </summary>
public sealed unsafe class H264Decoder : IDisposable
{
    private AVCodecContext* _ctx;
    private AVFrame* _frame;
    private AVPacket* _packet;
    private SwsContext* _sws;
    private byte[] _bgra = [];

    public H264Decoder()
    {
        FFmpegLoader.EnsureLoaded();
        var codec = ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_H264);
        if (codec == null) throw new InvalidOperationException("FFmpeg 沒有 H.264 解碼器");

        _ctx = ffmpeg.avcodec_alloc_context3(codec);
        // Live screen: output each picture as soon as it is decoded. Slice threads add no latency
        // (frame threads would buffer several pictures).
        _ctx->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;
        _ctx->thread_type = ffmpeg.FF_THREAD_SLICE;
        _ctx->thread_count = 0;
        Check(ffmpeg.avcodec_open2(_ctx, codec, null), "avcodec_open2");

        _frame = ffmpeg.av_frame_alloc();
        _packet = ffmpeg.av_packet_alloc();
    }

    public long DecodedFrames { get; private set; }
    public long Errors { get; private set; }

    /// <summary>
    /// Feeds one access unit; calls <paramref name="onPicture"/> for every decoded picture.
    /// The BgraFrame buffer is reused by the next call: copy it if you keep it.
    /// </summary>
    public void Decode(ReadOnlySpan<byte> annexB, Action<BgraFrame> onPicture)
    {
        // av_new_packet adds the zero padding FFmpeg's bitstream reader requires.
        Check(ffmpeg.av_new_packet(_packet, annexB.Length), "av_new_packet");
        annexB.CopyTo(new Span<byte>(_packet->data, annexB.Length));
        var rc = ffmpeg.avcodec_send_packet(_ctx, _packet);
        ffmpeg.av_packet_unref(_packet);
        if (rc < 0 && rc != ffmpeg.AVERROR(ffmpeg.EAGAIN))
        {
            Errors++; // a corrupt picture: skip it, the decoder recovers at the next key frame
            return;
        }

        while (true)
        {
            rc = ffmpeg.avcodec_receive_frame(_ctx, _frame);
            if (rc == ffmpeg.AVERROR(ffmpeg.EAGAIN) || rc == ffmpeg.AVERROR_EOF) break;
            if (rc < 0) { Errors++; break; }
            DecodedFrames++;
            onPicture(ConvertToBgra(_frame));
            ffmpeg.av_frame_unref(_frame);
        }
    }

    private BgraFrame ConvertToBgra(AVFrame* f)
    {
        int w = f->width, h = f->height;
        _sws = ffmpeg.sws_getCachedContext(_sws, w, h, (AVPixelFormat)f->format,
            w, h, AVPixelFormat.AV_PIX_FMT_BGRA, ffmpeg.SWS_BILINEAR, null, null, null);
        if (_sws == null) throw new InvalidOperationException("sws_getCachedContext failed");

        var stride = w * 4;
        if (_bgra.Length != stride * h) _bgra = new byte[stride * h];
        fixed (byte* dst = _bgra)
        {
            var dstData = new byte*[] { dst, null, null, null };
            var dstStride = new[] { stride, 0, 0, 0 };
            ffmpeg.sws_scale(_sws, f->data.ToArray(), f->linesize.ToArray(), 0, h, dstData, dstStride);
        }
        return new BgraFrame(w, h, stride, _bgra);
    }

    private static void Check(int rc, string what)
    {
        if (rc < 0) throw new InvalidOperationException($"{what}: {FFmpegLoader.ErrorText(rc)}");
    }

    public void Dispose()
    {
        if (_ctx != null) { var c = _ctx; ffmpeg.avcodec_free_context(&c); _ctx = null; }
        if (_frame != null) { var fr = _frame; ffmpeg.av_frame_free(&fr); _frame = null; }
        if (_packet != null) { var p = _packet; ffmpeg.av_packet_free(&p); _packet = null; }
        if (_sws != null) { ffmpeg.sws_freeContext(_sws); _sws = null; }
    }
}
