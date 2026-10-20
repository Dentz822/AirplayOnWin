using System.IO;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;

namespace AirPlay.App.PcAudio;

/// <summary>
/// Minimal server side of a WebSocket (RFC 6455), on a connection that was upgraded from HTTP.
/// Sends binary/text frames from a bounded queue (oldest dropped when the client is slow, so
/// audio never piles up into latency) and reads the client's frames only for ping/close.
/// </summary>
public sealed class WebSocketSession
{
    private const string Magic = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    private readonly Stream _stream;
    private readonly Channel<(byte Opcode, byte[] Payload)> _outgoing;

    public WebSocketSession(Stream stream, int maxQueuedFrames)
    {
        _stream = stream;
        _outgoing = Channel.CreateBounded<(byte, byte[])>(new BoundedChannelOptions(maxQueuedFrames)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
    }

    /// <summary>Value of Sec-WebSocket-Accept for the client's Sec-WebSocket-Key.</summary>
    public static string AcceptKey(string clientKey) =>
        Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(clientKey.Trim() + Magic)));

    public void SendBinary(byte[] payload) => _outgoing.Writer.TryWrite((0x2, payload));
    public void SendText(string text) => _outgoing.Writer.TryWrite((0x1, Encoding.UTF8.GetBytes(text)));

    /// <summary>Runs until the client disconnects or <paramref name="ct"/> is cancelled.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var reader = ReadLoop(stop.Token);
        var writer = WriteLoop(stop.Token);
        await Task.WhenAny(reader, writer);
        stop.Cancel();
        _outgoing.Writer.TryComplete();
        try { await Task.WhenAll(reader, writer); } catch { /* connection gone */ }
    }

    private async Task WriteLoop(CancellationToken ct)
    {
        try
        {
            await foreach (var (opcode, payload) in _outgoing.Reader.ReadAllAsync(ct))
            {
                await _stream.WriteAsync(FrameHeader(opcode, payload.Length), ct);
                await _stream.WriteAsync(payload, ct);
                if (opcode == 0x8) return; // close sent
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
    }

    private async Task ReadLoop(CancellationToken ct)
    {
        var header = new byte[2];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await _stream.ReadExactlyAsync(header, ct);
                var opcode = header[0] & 0x0F;
                var masked = (header[1] & 0x80) != 0;
                long length = header[1] & 0x7F;
                if (length == 126)
                {
                    var ext = new byte[2];
                    await _stream.ReadExactlyAsync(ext, ct);
                    length = BinaryPrimitives.ReadUInt16BigEndian(ext);
                }
                else if (length == 127)
                {
                    var ext = new byte[8];
                    await _stream.ReadExactlyAsync(ext, ct);
                    length = (long)BinaryPrimitives.ReadUInt64BigEndian(ext);
                }
                if (length > 1 << 20) return; // clients only send tiny control frames
                var mask = new byte[4];
                if (masked) await _stream.ReadExactlyAsync(mask, ct);
                var payload = new byte[length];
                await _stream.ReadExactlyAsync(payload, ct);
                if (masked) for (var i = 0; i < payload.Length; i++) payload[i] ^= mask[i % 4];

                switch (opcode)
                {
                    case 0x8: // close: answer and stop
                        _outgoing.Writer.TryWrite((0x8, payload.Length >= 2 ? payload[..2] : []));
                        return;
                    case 0x9: // ping → pong
                        _outgoing.Writer.TryWrite((0xA, payload));
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or OperationCanceledException or ObjectDisposedException) { }
    }

    private static byte[] FrameHeader(byte opcode, int length)
    {
        // FIN + opcode; server frames are never masked
        if (length < 126) return [(byte)(0x80 | opcode), (byte)length];
        if (length <= ushort.MaxValue) return [(byte)(0x80 | opcode), 126, (byte)(length >> 8), (byte)length];
        var h = new byte[10];
        h[0] = (byte)(0x80 | opcode);
        h[1] = 127;
        BinaryPrimitives.WriteUInt64BigEndian(h.AsSpan(2), (ulong)length);
        return h;
    }
}
