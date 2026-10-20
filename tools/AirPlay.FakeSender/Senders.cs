using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using AirPlay.Core.Crypto;
using AirPlay.Core.Mirroring;
using AirPlay.Media;
using FFmpeg.AutoGen;

namespace AirPlay.FakeSender;

/// <summary>Minimal RTSP client (one request at a time).</summary>
internal sealed class RtspClient : IDisposable
{
    private readonly TcpClient _tcp = new();
    private readonly NetworkStream _stream;
    private int _cseq;

    public RtspClient(string host, int port)
    {
        _tcp.Connect(host, port);
        _stream = _tcp.GetStream();
    }

    public async Task<(int Status, byte[] Body)> Send(string method, string uri, byte[]? body = null, string? contentType = null)
    {
        var head = new StringBuilder($"{method} {uri} RTSP/1.0\r\nCSeq: {_cseq++}\r\nUser-Agent: AirPlay/960.13.1\r\n");
        if (contentType is not null) head.Append($"Content-Type: {contentType}\r\n");
        if (body is not null) head.Append($"Content-Length: {body.Length}\r\n");
        head.Append("\r\n");
        await _stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()));
        if (body is not null) await _stream.WriteAsync(body);

        var header = new List<byte>();
        while (!(header.Count >= 4 && header[^4] == '\r' && header[^3] == '\n' && header[^2] == '\r' && header[^1] == '\n'))
        {
            var b = _stream.ReadByte();
            if (b < 0) throw new IOException("接收端關閉連線");
            header.Add((byte)b);
        }
        var lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n");
        var status = int.Parse(lines[0].Split(' ')[1]);
        var length = lines.Where(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            .Select(l => int.Parse(l[15..].Trim())).FirstOrDefault();
        var data = new byte[length];
        await _stream.ReadExactlyAsync(data);
        return (status, data);
    }

    public void Dispose() => _tcp.Dispose();
}

/// <summary>Sends H.264 the way an iPhone does: 128-byte header + AES-CTR encrypted AVCC payload.</summary>
internal sealed class VideoSender(string host, int port, byte[] aesKey, ulong streamId) : IDisposable
{
    private readonly TcpClient _tcp = new();
    private readonly AesCtr _cipher = CreateCipher(aesKey, streamId);
    private NetworkStream _stream = null!;

    private static AesCtr CreateCipher(byte[] key, ulong id)
    {
        var (k, iv) = MirrorStream.DeriveKey(key, id);
        return new AesCtr(k, iv);
    }

    public async Task ConnectAsync()
    {
        await _tcp.ConnectAsync(host, port);
        _stream = _tcp.GetStream();
    }

    public async Task SendAccessUnitAsync(ArraySegment<byte> annexB, ulong timestamp)
    {
        var nals = SplitNals(annexB);
        var sps = nals.FirstOrDefault(n => (n[0] & 0x1F) == 7);
        var pps = nals.FirstOrDefault(n => (n[0] & 0x1F) == 8);
        if (sps is not null && pps is not null)
        {
            // Unencrypted codec packet (type 1) with an avcC record and the picture size.
            byte[] avcc = [1, sps[1], sps[2], sps[3], 0xFF, 0xE1, (byte)(sps.Length >> 8), (byte)sps.Length, .. sps,
                           1, (byte)(pps.Length >> 8), (byte)pps.Length, .. pps];
            var h = Header(avcc.Length, 1, timestamp);
            h[6] = 0x16; h[7] = 0x01;
            BinaryPrimitives.WriteSingleLittleEndian(h.AsSpan(56), 498);
            BinaryPrimitives.WriteSingleLittleEndian(h.AsSpan(60), 1080);
            await Send(h, avcc);
        }

        var payload = nals.Where(n => (n[0] & 0x1F) is not (7 or 8))
            .SelectMany(n => new[] { (byte)(n.Length >> 24), (byte)(n.Length >> 16), (byte)(n.Length >> 8), (byte)n.Length }.Concat(n))
            .ToArray();
        if (payload.Length == 0) return;
        _cipher.Transform(payload.AsSpan());
        await Send(Header(payload.Length, 0, timestamp), payload);
    }

    private static List<byte[]> SplitNals(ArraySegment<byte> au)
    {
        var data = au.AsSpan();
        var starts = new List<(int Code, int Nal)>();
        for (var i = 0; i + 3 < data.Length; i++)
            if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1)
            {
                starts.Add((i > 0 && data[i - 1] == 0 ? i - 1 : i, i + 3));
                i += 2;
            }
        var nals = new List<byte[]>();
        for (var k = 0; k < starts.Count; k++)
        {
            var end = k + 1 < starts.Count ? starts[k + 1].Code : data.Length;
            nals.Add(data[starts[k].Nal..end].ToArray());
        }
        return nals;
    }

    private static byte[] Header(int size, byte type, ulong ts)
    {
        var h = new byte[128];
        BinaryPrimitives.WriteInt32LittleEndian(h, size);
        h[4] = type;
        BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(8), ts);
        return h;
    }

    private async Task Send(byte[] header, byte[] payload)
    {
        await _stream.WriteAsync(header);
        await _stream.WriteAsync(payload);
    }

    public void Dispose()
    {
        _tcp.Dispose();
        _cipher.Dispose();
    }
}

/// <summary>Sends AAC-LC frames of a stereo tone as encrypted RTP packets (AES-CBC, fresh IV each).</summary>
internal sealed unsafe class AudioSender : IDisposable
{
    private readonly UdpClient _udp = new();
    private readonly IPEndPoint _target;
    private readonly Aes _aes = Aes.Create();
    private readonly byte[] _iv;
    private readonly AVCodecContext* _ctx;
    private readonly AVFrame* _frame;
    private readonly AVPacket* _packet;
    private readonly Queue<byte[]> _encoded = new();
    private int _samplesEncoded;
    private ushort _seq = 1000;

    public AudioSender(string host, int port, byte[] key, byte[] iv)
    {
        _target = new IPEndPoint(IPAddress.Parse(host), port);
        _aes.Key = key;
        _iv = iv;

        FFmpegLoader.EnsureLoaded();
        var codec = ffmpeg.avcodec_find_encoder_by_name("aac");
        _ctx = ffmpeg.avcodec_alloc_context3(codec);
        _ctx->sample_rate = 44100;
        _ctx->sample_fmt = AVSampleFormat.AV_SAMPLE_FMT_FLTP;
        ffmpeg.av_channel_layout_default(&_ctx->ch_layout, 2);
        _ctx->bit_rate = 128000;
        if (ffmpeg.avcodec_open2(_ctx, codec, null) < 0) throw new Exception("AAC encoder open failed");
        _frame = ffmpeg.av_frame_alloc();
        _frame->nb_samples = 1024;
        _frame->format = (int)_ctx->sample_fmt;
        _frame->sample_rate = 44100;
        ffmpeg.av_channel_layout_default(&_frame->ch_layout, 2);
        ffmpeg.av_frame_get_buffer(_frame, 0);
        _packet = ffmpeg.av_packet_alloc();
    }

    public void SendFrame(int index)
    {
        while (_encoded.Count == 0) EncodeNext();
        var aac = _encoded.Dequeue();

        var payload = (byte[])aac.Clone();
        var n = payload.Length / 16 * 16;
        if (n > 0) _aes.EncryptCbc(aac.AsSpan(0, n), _iv, payload.AsSpan(0, n), PaddingMode.None);

        var rtp = new byte[12 + payload.Length];
        rtp[0] = 0x80; rtp[1] = 0x60;
        BinaryPrimitives.WriteUInt16BigEndian(rtp.AsSpan(2), _seq++);
        BinaryPrimitives.WriteUInt32BigEndian(rtp.AsSpan(4), (uint)(index * 1024));
        payload.CopyTo(rtp, 12);
        _udp.Send(rtp, _target);
    }

    private void EncodeNext()
    {
        ffmpeg.av_frame_make_writable(_frame);
        for (var i = 0; i < 1024; i++)
        {
            var t = (_samplesEncoded + i) / 44100.0;
            ((float*)_frame->data[0])[i] = (float)(0.25 * Math.Sin(2 * Math.PI * 440 * t));
            ((float*)_frame->data[1])[i] = (float)(0.25 * Math.Sin(2 * Math.PI * 660 * t));
        }
        _frame->pts = _samplesEncoded;
        _samplesEncoded += 1024;
        ffmpeg.avcodec_send_frame(_ctx, _frame);
        while (ffmpeg.avcodec_receive_packet(_ctx, _packet) == 0)
        {
            _encoded.Enqueue(new ReadOnlySpan<byte>(_packet->data, _packet->size).ToArray());
            ffmpeg.av_packet_unref(_packet);
        }
    }

    public void Dispose()
    {
        _udp.Dispose();
        _aes.Dispose();
        var c = _ctx; ffmpeg.avcodec_free_context(&c);
        var f = _frame; ffmpeg.av_frame_free(&f);
        var p = _packet; ffmpeg.av_packet_free(&p);
    }
}
