using AirPlay.Media;
using FFmpeg.AutoGen;

namespace AirPlay.Tests;

public unsafe class DecoderTests
{
    /// <summary>Encodes solid-color frames with FFmpeg's H.264 encoder (Annex-B output).</summary>
    private static List<byte[]> EncodeSolidFrames(int width, int height, (byte Y, byte U, byte V)[] colors)
    {
        FFmpegLoader.EnsureLoaded();
        var codec = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_H264);
        Assert.True(codec != null, "FFmpeg build has no H.264 encoder");
        var ctx = ffmpeg.avcodec_alloc_context3(codec);
        ctx->width = width;
        ctx->height = height;
        ctx->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUV420P;
        ctx->time_base = new AVRational { num = 1, den = 30 };
        ctx->gop_size = 30;
        ctx->max_b_frames = 0;
        ffmpeg.av_opt_set(ctx->priv_data, "tune", "zerolatency", 0);
        Assert.True(ffmpeg.avcodec_open2(ctx, codec, null) >= 0);

        var frame = ffmpeg.av_frame_alloc();
        frame->format = (int)ctx->pix_fmt;
        frame->width = width;
        frame->height = height;
        ffmpeg.av_frame_get_buffer(frame, 0);
        var packet = ffmpeg.av_packet_alloc();
        var output = new List<byte[]>();

        void Drain()
        {
            while (ffmpeg.avcodec_receive_packet(ctx, packet) == 0)
            {
                output.Add(new ReadOnlySpan<byte>(packet->data, packet->size).ToArray());
                ffmpeg.av_packet_unref(packet);
            }
        }

        for (var i = 0; i < colors.Length; i++)
        {
            ffmpeg.av_frame_make_writable(frame);
            var (y, u, v) = colors[i];
            for (var row = 0; row < height; row++)
                new Span<byte>(frame->data[0] + row * frame->linesize[0], width).Fill(y);
            for (var row = 0; row < height / 2; row++)
            {
                new Span<byte>(frame->data[1] + row * frame->linesize[1], width / 2).Fill(u);
                new Span<byte>(frame->data[2] + row * frame->linesize[2], width / 2).Fill(v);
            }
            frame->pts = i;
            ffmpeg.avcodec_send_frame(ctx, frame);
            Drain();
        }
        ffmpeg.avcodec_send_frame(ctx, null);
        Drain();

        var pk = packet; ffmpeg.av_packet_free(&pk);
        var fr = frame; ffmpeg.av_frame_free(&fr);
        var cx = ctx; ffmpeg.avcodec_free_context(&cx);
        return output;
    }

    [Fact]
    public void DecodesEveryAccessUnitToBgra()
    {
        // white, black, then pure-ish red in BT.601 limited range
        var colors = new (byte, byte, byte)[] { (235, 128, 128), (16, 128, 128), (81, 90, 240) };
        var packets = EncodeSolidFrames(64, 48, colors);
        Assert.Equal(colors.Length, packets.Count);

        using var decoder = new H264Decoder();
        var pictures = new List<(int W, int H, byte B, byte G, byte R)>();
        foreach (var p in packets)
            decoder.Decode(p, f =>
            {
                var center = (f.Height / 2) * f.Stride + (f.Width / 2) * 4;
                pictures.Add((f.Width, f.Height, f.Pixels[center], f.Pixels[center + 1], f.Pixels[center + 2]));
            });

        Assert.Equal(0, decoder.Errors);
        Assert.Equal(3, pictures.Count);
        Assert.All(pictures, p => Assert.Equal((64, 48), (p.W, p.H)));
        Assert.True(pictures[0] is { B: > 240, G: > 240, R: > 240 }, $"white: {pictures[0]}");
        Assert.True(pictures[1] is { B: < 15, G: < 15, R: < 15 }, $"black: {pictures[1]}");
        Assert.True(pictures[2] is { R: > 200, G: < 60, B: < 60 }, $"red: {pictures[2]}");
    }

    [Fact]
    public void AnnexBReaderSplitsRecordingBackIntoPictures()
    {
        var packets = EncodeSolidFrames(32, 32, Enumerable.Repeat(((byte)128, (byte)128, (byte)128), 5).ToArray());
        var file = packets.SelectMany(p => p).ToArray(); // what the recorder writes

        var units = AnnexBReader.AccessUnits(file).Select(s => s.ToArray()).ToList();

        Assert.Equal(packets.Count, units.Count);
        for (var i = 0; i < units.Count; i++) Assert.Equal(packets[i], units[i]);
    }

    [Fact]
    public void SurvivesGarbageInput()
    {
        using var decoder = new H264Decoder();
        var called = false;
        decoder.Decode(new byte[] { 0, 0, 0, 1, 0x65, 0xFF, 0x00, 0x12, 0x34 }, _ => called = true);
        decoder.Decode(Enumerable.Range(0, 500).Select(i => (byte)(i * 13)).ToArray(), _ => called = true);
        Assert.False(called);
    }
}
