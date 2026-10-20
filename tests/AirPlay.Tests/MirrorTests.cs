using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using AirPlay.Core;
using AirPlay.Core.Crypto;
using AirPlay.Core.Mirroring;

namespace AirPlay.Tests;

/// <summary>Builds mirroring packets the way an iPhone sends them.</summary>
internal sealed class FakeMirrorSender : IDisposable
{
    public static readonly byte[] Sps = [0x67, 0x64, 0x00, 0x28, 0xAC, 0x2B, 0x40, 0x3C, 0x01, 0x13, 0xF2];
    public static readonly byte[] Pps = [0x68, 0xEE, 0x3C, 0xB0];

    private readonly AesCtr _cipher;
    private readonly TcpClient _tcp = new();
    private NetworkStream _stream = null!;

    public FakeMirrorSender(byte[] aesKey, ulong streamConnectionId)
    {
        var (key, iv) = MirrorStream.DeriveKey(aesKey, streamConnectionId);
        _cipher = new AesCtr(key, iv);
    }

    public async Task ConnectAsync(int port)
    {
        await _tcp.ConnectAsync(IPAddress.Loopback, port);
        _stream = _tcp.GetStream();
    }

    public Task SendCodecAsync(int width, int height)
    {
        byte[] avcc = [1, Sps[1], Sps[2], Sps[3], 0xFF, 0xE1,
                       (byte)(Sps.Length >> 8), (byte)Sps.Length, .. Sps,
                       1, (byte)(Pps.Length >> 8), (byte)Pps.Length, .. Pps];
        var header = Header(avcc.Length, type: 1, ts: 1000);
        header[6] = 0x16; header[7] = 0x01;
        BinaryPrimitives.WriteSingleLittleEndian(header.AsSpan(56), width);
        BinaryPrimitives.WriteSingleLittleEndian(header.AsSpan(60), height);
        return SendAsync(header, avcc);
    }

    /// <summary>Sends NAL units as one encrypted AVCC packet; returns the expected Annex-B bytes.</summary>
    public async Task<byte[]> SendFrameAsync(ulong ts, params byte[][] nals)
    {
        var avcc = nals.SelectMany(n => new byte[] { (byte)(n.Length >> 24), (byte)(n.Length >> 16), (byte)(n.Length >> 8), (byte)n.Length }.Concat(n)).ToArray();
        var annexB = nals.SelectMany(n => new byte[] { 0, 0, 0, 1 }.Concat(n)).ToArray();
        _cipher.Transform(avcc.AsSpan());
        await SendAsync(Header(avcc.Length, type: 0, ts), avcc);
        return annexB;
    }

    public Task SendReportAsync() => SendAsync(Header(4, type: 5, ts: 0), [1, 2, 3, 4]);

    private static byte[] Header(int size, byte type, ulong ts)
    {
        var h = new byte[128];
        BinaryPrimitives.WriteInt32LittleEndian(h, size);
        h[4] = type;
        BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(8), ts);
        return h;
    }

    private async Task SendAsync(byte[] header, byte[] payload)
    {
        await _stream.WriteAsync(header);
        await _stream.WriteAsync(payload);
        await _stream.FlushAsync();
    }

    public static byte[] Nal(byte header, int length)
    {
        var n = new byte[length];
        n[0] = header;
        for (var i = 1; i < length; i++) n[i] = (byte)(i * 31 + header);
        return n;
    }

    public void Dispose()
    {
        _tcp.Dispose();
        _cipher.Dispose();
    }
}

internal sealed class CollectingSink : IMirrorSink
{
    public readonly BlockingCollection<VideoFrame> Frames = new();
    public VideoFormat? Format;
    public void OnFormat(VideoFormat format) => Format = format;
    public void OnFrame(VideoFrame frame) => Frames.Add(frame);
    public void Dispose() { }

    public VideoFrame Next() =>
        Frames.TryTake(out var f, TimeSpan.FromSeconds(5)) ? f : throw new TimeoutException("no frame received");
}

public class AvccTests
{
    [Fact]
    public void ConvertsLengthPrefixesToStartCodes()
    {
        byte[] data = [0, 0, 0, 2, 0x65, 0xAA, 0, 0, 0, 1, 0x06];
        Assert.True(MirrorStream.AvccToAnnexB(data, out var key));
        Assert.True(key);
        Assert.Equal(new byte[] { 0, 0, 0, 1, 0x65, 0xAA, 0, 0, 0, 1, 0x06 }, data);
    }

    [Theory]
    [InlineData(new byte[] { 0, 0, 0, 9, 0x65, 0xAA })]            // length runs past the end
    [InlineData(new byte[] { 0, 0, 0, 1, 0x65, 0xAA })]            // trailing garbage
    [InlineData(new byte[] { 0, 0, 0, 2, 0xE5, 0xAA })]            // forbidden bit set
    [InlineData(new byte[] { 0x91, 0x3C, 0x07, 0x22, 0x10, 0x01 })] // what a wrong key looks like
    public void RejectsMalformedData(byte[] data) =>
        Assert.False(MirrorStream.AvccToAnnexB(data, out _));

    [Fact]
    public void ParsesAvcCRecord()
    {
        var s = FakeMirrorSender.Sps; var p = FakeMirrorSender.Pps;
        byte[] avcc = [1, s[1], s[2], s[3], 0xFF, 0xE1, 0, (byte)s.Length, .. s, 1, 0, (byte)p.Length, .. p];
        var f = MirrorStream.ParseAvcC(avcc, 1170, 2532);
        Assert.Equal(s, f.Sps);
        Assert.Equal(p, f.Pps);
        Assert.Equal((1170, 2532), (f.Width, f.Height));
    }
}

public class MirrorStreamTests
{
    [Fact]
    public async Task DecryptsFramesAcrossUnalignedPacketsAndPrependsParameterSets()
    {
        var aesKey = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
        const ulong id = 12345678901234567890UL;
        using var mirror = new MirrorStream(aesKey, id, _ => { });
        var sink = new CollectingSink();
        mirror.FormatChanged += sink.OnFormat;
        mirror.FrameReceived += sink.OnFrame;
        mirror.Start();

        using var phone = new FakeMirrorSender(aesKey, id);
        await phone.ConnectAsync(mirror.Port);
        await phone.SendCodecAsync(1290, 2796);
        // Sizes chosen so packets end mid-AES-block: the keystream must carry over.
        var idr = await phone.SendFrameAsync(1000, FakeMirrorSender.Nal(0x06, 13), FakeMirrorSender.Nal(0x65, 4001));
        await phone.SendReportAsync();
        var p1 = await phone.SendFrameAsync(2000, FakeMirrorSender.Nal(0x41, 37));
        var p2 = await phone.SendFrameAsync(3000, FakeMirrorSender.Nal(0x41, 1));

        var f1 = sink.Next();
        Assert.True(f1.IsKeyFrame);
        Assert.Equal((byte[])[0, 0, 0, 1, .. FakeMirrorSender.Sps, 0, 0, 0, 1, .. FakeMirrorSender.Pps, .. idr], f1.AnnexB);
        Assert.Equal(1000UL, f1.Timestamp);

        var f2 = sink.Next();
        Assert.False(f2.IsKeyFrame);
        Assert.Equal(p1, f2.AnnexB);
        Assert.Equal(p2, sink.Next().AnnexB);
        Assert.Equal(1290, sink.Format!.Width);
    }
}
