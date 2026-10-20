using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using AirPlay.Core.Audio;
using AirPlay.Media;
using FFmpeg.AutoGen;

namespace AirPlay.Tests;

public class ReorderBufferTests
{
    private readonly List<uint> _delivered = new();
    private readonly List<(ushort First, ushort Count, bool GaveUp)> _gaps = new();

    private AudioStream.ReorderBuffer NewBuffer() =>
        new((ts, _) => _delivered.Add(ts), (first, count, gaveUp) => _gaps.Add((first, count, gaveUp)));

    [Fact]
    public void ReordersAndHandlesSequenceWrapAround()
    {
        var b = NewBuffer();
        // seq 65534, 65535, 0, 1, 2 arriving as 65534, 0, 65535, 2, 1 (timestamp = order index)
        b.Add(65534, 0, [1]);
        b.Add(0, 2, [1]);
        b.Add(65535, 1, [1]);
        b.Add(2, 4, [1]);
        b.Add(1, 3, [1]);
        Assert.Equal(new uint[] { 0, 1, 2, 3, 4 }, _delivered);
        Assert.Empty(_gaps);
    }

    [Fact]
    public void RequestsResendThenSkipsAPacketThatNeverComes()
    {
        var b = NewBuffer();
        b.Add(100, 100, [1]);
        // 101 is lost
        for (ushort s = 102; s < 102 + AudioStream.ReorderBuffer.GiveUpAfter; s++) b.Add(s, s, [1]);

        Assert.Contains((ushort)101, _gaps.Where(g => !g.GaveUp).Select(g => g.First));
        Assert.Contains(((ushort)101, (ushort)1, true), _gaps);
        Assert.Equal(new uint[] { 100 }.Concat(Enumerable.Range(102, AudioStream.ReorderBuffer.GiveUpAfter).Select(i => (uint)i)), _delivered);
    }

    [Fact]
    public void LateResendFillsTheGap()
    {
        var b = NewBuffer();
        b.Add(10, 10, [1]);
        b.Add(12, 12, [1]);
        b.Add(13, 13, [1]);
        b.Add(14, 14, [1]);
        Assert.Single(_gaps); // resend requested for 11
        b.Add(11, 11, [1]);   // arrives via 0x56
        Assert.Equal(new uint[] { 10, 11, 12, 13, 14 }, _delivered);
    }
}

public class AudioStreamTests
{
    private static readonly byte[] Key = Enumerable.Range(0, 16).Select(i => (byte)(i * 3)).ToArray();
    private static readonly byte[] Iv = Enumerable.Range(0, 16).Select(i => (byte)(200 - i)).ToArray();

    /// <summary>What the iPhone does: AES-CBC over whole blocks, fresh IV per packet, tail in clear.</summary>
    private static byte[] Encrypt(byte[] payload)
    {
        using var aes = Aes.Create();
        aes.Key = Key;
        var output = (byte[])payload.Clone();
        var n = payload.Length / 16 * 16;
        if (n > 0) aes.EncryptCbc(payload.AsSpan(0, n), Iv, output.AsSpan(0, n), PaddingMode.None);
        return output;
    }

    private static byte[] Rtp(ushort seq, uint ts, byte[] payload)
    {
        var p = new byte[12 + payload.Length];
        p[0] = 0x80; p[1] = 0x60;
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(2), seq);
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(4), ts);
        payload.CopyTo(p, 12);
        return p;
    }

    private sealed class Sink : IAudioSink
    {
        public readonly BlockingCollection<(byte[] Frame, uint Ts)> Frames = new();
        public double Volume;
        public void OnAudioFrame(ReadOnlySpan<byte> frame, uint ts) => Frames.Add((frame.ToArray(), ts));
        public void SetVolume(double db) => Volume = db;
        public void Flush() { }
        public void Dispose() { }
    }

    [Fact]
    public async Task DecryptsAndOrdersPacketsOverUdp()
    {
        var sink = new Sink();
        using var phoneControl = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var stream = new AudioStream(new AudioFormat(8, 480), Key, Iv,
            (IPEndPoint)phoneControl.Client.LocalEndPoint!, sink, _ => { });
        stream.Start();

        var frames = Enumerable.Range(0, 4).Select(i =>
            Enumerable.Range(0, 37 + i * 20).Select(b => (byte)(b * 7 + i)).ToArray()).ToList(); // odd sizes: tail in clear
        using var phone = new UdpClient();
        var target = new IPEndPoint(IPAddress.Loopback, stream.DataPort);
        await phone.SendAsync(Rtp(500, 0, [0x00, 0x68, 0x34, 0x00]), target); // "no data" marker: skipped
        foreach (var i in new[] { 1, 0, 3, 2 })
            await phone.SendAsync(Rtp((ushort)(501 + i), (uint)(480 * i), Encrypt(frames[i])), target);

        for (var i = 0; i < 4; i++)
        {
            Assert.True(sink.Frames.TryTake(out var got, TimeSpan.FromSeconds(5)), $"frame {i} not delivered");
            Assert.Equal(frames[i], got.Frame);
            Assert.Equal((uint)(480 * i), got.Ts);
        }

        stream.SetVolume(-12.5);
        Assert.Equal(-12.5, sink.Volume);
    }

    [Fact]
    public async Task AsksThePhoneToResendAMissingPacket()
    {
        var sink = new Sink();
        var logs = new ConcurrentQueue<string>();
        using var phoneControl = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var stream = new AudioStream(new AudioFormat(8, 480), Key, Iv,
            (IPEndPoint)phoneControl.Client.LocalEndPoint!, sink, logs.Enqueue);
        stream.Start();

        using var phone = new UdpClient();
        var target = new IPEndPoint(IPAddress.Loopback, stream.DataPort);
        await phone.SendAsync(Rtp(10, 0, Encrypt(new byte[20])), target);
        for (ushort s = 12; s < 16; s++) await phone.SendAsync(Rtp(s, s, Encrypt(new byte[20])), target);

        var request = await phoneControl.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0xD5, request.Buffer[1]);
        Assert.Equal(11, BinaryPrimitives.ReadUInt16BigEndian(request.Buffer.AsSpan(4)));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(request.Buffer.AsSpan(6)));

        // The phone answers on our control port with 0x56 + the original packet.
        var resent = new byte[] { 0x80, 0xD6, 0, 1 }.Concat(Rtp(11, 11, Encrypt(new byte[20]))).ToArray();
        await phone.SendAsync(resent, new IPEndPoint(IPAddress.Loopback, stream.ControlPort));

        var timestamps = new List<uint>();
        while (timestamps.Count < 6 && sink.Frames.TryTake(out var f, TimeSpan.FromSeconds(5))) timestamps.Add(f.Ts);
        Assert.True(timestamps.SequenceEqual(new uint[] { 0, 11, 12, 13, 14, 15 }), $"{string.Join(",", timestamps)} ctrl={stream.ControlPort} logs: {string.Join(" / ", logs)}");
    }
}

public unsafe class AudioDecoderTests
{
    /// <summary>Encodes a stereo sine with an FFmpeg encoder; returns raw packets and the input PCM.</summary>
    private static (List<byte[]> Packets, short[] Pcm, byte[] Config) Encode(string encoderName, AVSampleFormat fmt, int frameSize, int frames)
    {
        FFmpegLoader.EnsureLoaded();
        var codec = ffmpeg.avcodec_find_encoder_by_name(encoderName);
        Assert.True(codec != null, $"no {encoderName} encoder");
        var ctx = ffmpeg.avcodec_alloc_context3(codec);
        ctx->sample_rate = 44100;
        ctx->sample_fmt = fmt;
        ffmpeg.av_channel_layout_default(&ctx->ch_layout, 2);
        ctx->bit_rate = 128000;
        if (frameSize > 0) ctx->frame_size = frameSize;
        Assert.True(ffmpeg.avcodec_open2(ctx, codec, null) >= 0);
        var n = ctx->frame_size;

        var frame = ffmpeg.av_frame_alloc();
        frame->nb_samples = n;
        frame->format = (int)fmt;
        frame->sample_rate = 44100;
        ffmpeg.av_channel_layout_default(&frame->ch_layout, 2);
        ffmpeg.av_frame_get_buffer(frame, 0);
        var packet = ffmpeg.av_packet_alloc();
        var packets = new List<byte[]>();
        var pcm = new short[n * frames * 2];

        for (var f = 0; f < frames; f++)
        {
            ffmpeg.av_frame_make_writable(frame);
            for (var i = 0; i < n; i++)
            {
                var t = (f * n + i) / 44100.0;
                var l = (short)(8000 * Math.Sin(2 * Math.PI * 440 * t));
                var r = (short)(8000 * Math.Sin(2 * Math.PI * 660 * t));
                pcm[(f * n + i) * 2] = l;
                pcm[(f * n + i) * 2 + 1] = r;
                if (fmt == AVSampleFormat.AV_SAMPLE_FMT_FLTP)
                {
                    ((float*)frame->data[0])[i] = l / 32768f;
                    ((float*)frame->data[1])[i] = r / 32768f;
                }
                else // S16P
                {
                    ((short*)frame->data[0])[i] = l;
                    ((short*)frame->data[1])[i] = r;
                }
            }
            frame->pts = f * n;
            Assert.True(ffmpeg.avcodec_send_frame(ctx, frame) >= 0);
            while (ffmpeg.avcodec_receive_packet(ctx, packet) == 0)
            {
                packets.Add(new ReadOnlySpan<byte>(packet->data, packet->size).ToArray());
                ffmpeg.av_packet_unref(packet);
            }
        }
        var config = new ReadOnlySpan<byte>(ctx->extradata, ctx->extradata_size).ToArray();
        var pk = packet; ffmpeg.av_packet_free(&pk);
        var fr = frame; ffmpeg.av_frame_free(&fr);
        var cx = ctx; ffmpeg.avcodec_free_context(&cx);
        return (packets, pcm.AsSpan(0, n * frames * 2).ToArray(), config);
    }

    private static short[] DecodeAll(int ct, List<byte[]> packets, out long errors, byte[]? config = null)
    {
        using var decoder = new AudioDecoder(ct, config);
        var output = new List<short>();
        foreach (var p in packets)
        {
            var pcm = decoder.Decode(p);
            for (var i = 0; i + 1 < pcm.Length; i += 2) output.Add(BinaryPrimitives.ReadInt16LittleEndian(pcm[i..]));
        }
        errors = decoder.Errors;
        return output.ToArray();
    }

    [Fact]
    public void AlacDecodingIsLossless()
    {
        // FFmpeg's ALAC encoder always makes 4096-sample frames, which the AirPlay cookie
        // (352-sample frames) rejects, so decode with the encoder's own cookie here.
        var (packets, pcm, config) = Encode("alac", AVSampleFormat.AV_SAMPLE_FMT_S16P, 0, 4);
        var decoded = DecodeAll(2, packets, out var errors, config);
        Assert.Equal(0, errors);
        Assert.Equal(pcm, decoded);
    }

    [Fact]
    public void AlacDecoderAcceptsTheAirPlayCookie()
    {
        using var decoder = new AudioDecoder(2);
        Assert.Equal(0, decoder.Errors);
    }

    [Fact]
    public void AacLcDecodesToTheRightTone()
    {
        var (packets, _, _) = Encode("aac", AVSampleFormat.AV_SAMPLE_FMT_FLTP, 0, 60);
        var decoded = DecodeAll(4, packets, out var errors);
        Assert.Equal(0, errors);

        // Skip the encoder warm-up, then check the left channel: ~440 Hz, amplitude ~8000.
        var left = decoded.Where((_, i) => i % 2 == 0).Skip(4096).Take(44100 / 2).ToArray();
        var crossings = left.Zip(left.Skip(1)).Count(p => p.First < 0 && p.Second >= 0);
        var rms = Math.Sqrt(left.Average(s => (double)s * s));
        Assert.InRange(crossings, 210, 230);   // 440 Hz over 0.5 s = 220
        Assert.InRange(rms, 4500, 6800);       // 8000 / √2 ≈ 5657
    }

    [Fact]
    public void AacEldDecoderAcceptsTheAirPlayConfig()
    {
        // FFmpeg has no AAC-ELD encoder, so only the decoder setup can be tested here.
        using var decoder = new AudioDecoder(8);
        Assert.Equal(0, decoder.Errors);
    }

    [Fact]
    public void RejectsUnknownFormat() => Assert.Throws<NotSupportedException>(() => new AudioDecoder(99));
}
