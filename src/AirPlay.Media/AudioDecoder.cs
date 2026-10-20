using FFmpeg.AutoGen;

namespace AirPlay.Media;

/// <summary>
/// Decodes AirPlay audio frames to 16-bit interleaved PCM (44.1 kHz stereo).
///   ct 8 = AAC-ELD (screen mirroring), 480 samples/frame
///   ct 4 = AAC-LC, 1024 samples/frame
///   ct 2 = ALAC (music), 352 samples/frame
/// The decoder setup ("codec config") isn't sent by the iPhone; these fixed values are the
/// ones every AirPlay sender uses (same as UxPlay's GStreamer caps).
/// </summary>
public sealed unsafe class AudioDecoder : IDisposable
{
    public const int SampleRate = 44100;
    public const int Channels = 2;

    // MPEG-4 AudioSpecificConfig: AAC-ELD 44.1 kHz stereo, 480-sample frames
    private static readonly byte[] AacEldConfig = Convert.FromHexString("F8E85000");
    // AudioSpecificConfig: AAC-LC 44.1 kHz stereo
    private static readonly byte[] AacLcConfig = Convert.FromHexString("1210");
    // ALAC magic cookie ('alac' atom): 352 frames, 16 bit, pb 40 / mb 10 / kb 14, 2 ch, 44100 Hz
    private static readonly byte[] AlacConfig = Convert.FromHexString(
        "00000024" + "616C6163" + "00000000" + "00000160" + "0010280A" + "0E0200FF" + "00000000" + "00000000" + "0000AC44");

    private AVCodecContext* _ctx;
    private AVFrame* _frame;
    private AVPacket* _packet;
    private SwrContext* _swr;
    private byte[] _pcm = [];

    public long Errors { get; private set; }

    /// <param name="configOverride">Codec config to use instead of the AirPlay default (tests).</param>
    public AudioDecoder(int compressionType, byte[]? configOverride = null)
    {
        FFmpegLoader.EnsureLoaded();
        var (codecId, config) = compressionType switch
        {
            8 => (AVCodecID.AV_CODEC_ID_AAC, AacEldConfig),
            4 => (AVCodecID.AV_CODEC_ID_AAC, AacLcConfig),
            2 => (AVCodecID.AV_CODEC_ID_ALAC, AlacConfig),
            _ => throw new NotSupportedException($"不支援的音訊格式 ct={compressionType}"),
        };
        config = configOverride ?? config;

        var codec = ffmpeg.avcodec_find_decoder(codecId);
        if (codec == null) throw new InvalidOperationException($"FFmpeg 沒有 {codecId} 解碼器");
        _ctx = ffmpeg.avcodec_alloc_context3(codec);
        _ctx->sample_rate = SampleRate;
        ffmpeg.av_channel_layout_default(&_ctx->ch_layout, Channels);

        _ctx->extradata = (byte*)ffmpeg.av_mallocz((ulong)(config.Length + ffmpeg.AV_INPUT_BUFFER_PADDING_SIZE));
        config.CopyTo(new Span<byte>(_ctx->extradata, config.Length));
        _ctx->extradata_size = config.Length;

        var rc = ffmpeg.avcodec_open2(_ctx, codec, null);
        if (rc < 0) throw new InvalidOperationException($"avcodec_open2: {FFmpegLoader.ErrorText(rc)}");

        _frame = ffmpeg.av_frame_alloc();
        _packet = ffmpeg.av_packet_alloc();
    }

    /// <summary>
    /// Decodes one compressed frame and returns the PCM (16-bit stereo interleaved).
    /// The returned span is only valid until the next call. Empty on error.
    /// </summary>
    public ReadOnlySpan<byte> Decode(ReadOnlySpan<byte> frame)
    {
        if (ffmpeg.av_new_packet(_packet, frame.Length) < 0) return [];
        frame.CopyTo(new Span<byte>(_packet->data, frame.Length));
        var rc = ffmpeg.avcodec_send_packet(_ctx, _packet);
        ffmpeg.av_packet_unref(_packet);
        if (rc < 0) { Errors++; return []; }

        var total = 0;
        while (ffmpeg.avcodec_receive_frame(_ctx, _frame) == 0)
        {
            total += ConvertToS16(_frame, total);
            ffmpeg.av_frame_unref(_frame);
        }
        return _pcm.AsSpan(0, total);
    }

    /// <summary>Converts a decoded frame (any sample format, usually float planar) to S16 stereo.</summary>
    private int ConvertToS16(AVFrame* f, int offset)
    {
        if (_swr == null)
        {
            AVChannelLayout outLayout;
            ffmpeg.av_channel_layout_default(&outLayout, Channels);
            SwrContext* swr = null;
            var rc = ffmpeg.swr_alloc_set_opts2(&swr,
                &outLayout, AVSampleFormat.AV_SAMPLE_FMT_S16, SampleRate,
                &f->ch_layout, (AVSampleFormat)f->format, f->sample_rate, 0, null);
            if (rc < 0 || ffmpeg.swr_init(swr) < 0) throw new InvalidOperationException("swresample 初始化失敗");
            _swr = swr;
        }

        var maxSamples = f->nb_samples + 256;
        var needed = offset + maxSamples * Channels * 2;
        if (_pcm.Length < needed) Array.Resize(ref _pcm, needed);
        fixed (byte* outPtr = &_pcm[offset])
        {
            var outPlanes = outPtr;
            var converted = ffmpeg.swr_convert(_swr, &outPlanes, maxSamples, f->extended_data, f->nb_samples);
            return converted < 0 ? 0 : converted * Channels * 2;
        }
    }

    public void Dispose()
    {
        if (_ctx != null) { var c = _ctx; ffmpeg.avcodec_free_context(&c); _ctx = null; }
        if (_frame != null) { var fr = _frame; ffmpeg.av_frame_free(&fr); _frame = null; }
        if (_packet != null) { var p = _packet; ffmpeg.av_packet_free(&p); _packet = null; }
        if (_swr != null) { var s = _swr; ffmpeg.swr_free(&s); _swr = null; }
    }
}
