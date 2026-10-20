using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using AirPlay.Core.Crypto;
using AirPlay.Core.Net;

namespace AirPlay.Core.Mirroring;

/// <summary>Codec parameters from an unencrypted type-1 packet.</summary>
public sealed record VideoFormat(int Width, int Height, byte[] Sps, byte[] Pps);

/// <summary>One H.264 access unit in Annex-B form (00 00 00 01 start codes).</summary>
public sealed record VideoFrame(byte[] AnnexB, ulong Timestamp, bool IsKeyFrame);

/// <summary>
/// Receives the screen-mirroring video stream (SETUP stream type 110) on its own TCP port.
/// Each packet = 128-byte header + payload:
///   header[0..4)  payload size (little-endian)
///   header[4]     0 = encrypted video, 1 = codec (SPS/PPS, plain), 5 = stats report
///   header[8..16) timestamp (NTP Q32.32 on the iPhone's clock, little-endian)
/// Video payloads are AES-128-CTR encrypted as one continuous keystream, and hold
/// AVCC NAL units (4-byte big-endian length prefixes) which we turn into Annex-B.
/// </summary>
public sealed class MirrorStream : IDisposable
{
    public const int PreferredPort = 7100;
    private const int HeaderSize = 128;

    private readonly AesCtr _cipher;
    private readonly Action<string> _log;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private byte[]? _pendingParameterSets; // SPS+PPS to prepend to the next frame

    private long _frames, _bytes, _badFrames;
    private readonly Stopwatch _statsClock = Stopwatch.StartNew();
    private long _framesAtLastReport;

    public event Action<VideoFormat>? FormatChanged;
    public event Action<VideoFrame>? FrameReceived;

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public VideoFormat? Format { get; private set; }

    /// <param name="aesKey">The session AES key (FairPlay-decrypted ekey, hashed with the pairing secret).</param>
    public MirrorStream(byte[] aesKey, ulong streamConnectionId, Action<string> log)
    {
        _log = log;
        var (key, iv) = DeriveKey(aesKey, streamConnectionId);
        _cipher = new AesCtr(key, iv);

        _listener = PortBinder.ListenTcp(PreferredPort, log);
    }

    /// <summary>key = SHA-512("AirPlayStreamKey{id}" | aesKey)[0..16], iv likewise with "AirPlayStreamIV{id}".</summary>
    public static (byte[] Key, byte[] Iv) DeriveKey(byte[] aesKey, ulong streamConnectionId)
    {
        byte[] Hash(string prefix) =>
            SHA512.HashData([.. Encoding.ASCII.GetBytes($"{prefix}{streamConnectionId}"), .. aesKey])[..16];
        return (Hash("AirPlayStreamKey"), Hash("AirPlayStreamIV"));
    }

    public void Start() => _loop = RunAsync(_cts.Token);

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            using var client = await _listener.AcceptTcpClientAsync(ct);
            client.NoDelay = true;
            _log($"     畫面串流已連線 (TCP {Port})");
            var stream = client.GetStream();
            var header = new byte[HeaderSize];
            while (!ct.IsCancellationRequested)
            {
                await stream.ReadExactlyAsync(header, ct);
                var size = BinaryPrimitives.ReadInt32LittleEndian(header);
                if (size < 0 || size > 32 * 1024 * 1024) throw new IOException($"bad payload size {size}");
                var payload = new byte[size];
                await stream.ReadExactlyAsync(payload, ct);
                Handle(header, payload);
            }
        }
        catch (OperationCanceledException) { }
        catch (EndOfStreamException) { _log("     畫面串流結束"); }
        catch (IOException ex) { _log($"     畫面串流中斷: {ex.Message}"); }
        catch (ObjectDisposedException) { }
        finally
        {
            _log($"     畫面統計: {_frames} 幀, {_bytes / 1024 / 1024.0:F1} MB, 解析失敗 {_badFrames} 幀");
        }
    }

    private void Handle(byte[] header, byte[] payload)
    {
        var timestamp = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(8));
        switch (header[4])
        {
            case 0: // encrypted video
                _cipher.Transform(payload.AsSpan());
                if (!AvccToAnnexB(payload, out var isKeyFrame))
                {
                    if (_badFrames++ < 3)
                        _log($"     ⚠ 解密後的資料格式不對（金鑰可能錯誤），前 16 bytes: {Convert.ToHexString(payload, 0, Math.Min(16, payload.Length))}");
                    return;
                }
                if (_frames == 0) _log("     解密驗證 ✓ 第一幀 H.264 格式正確");
                byte[] frame = payload;
                if (_pendingParameterSets is { } ps)
                {
                    frame = [.. ps, .. payload];
                    _pendingParameterSets = null;
                }
                _frames++;
                _bytes += frame.Length;
                FrameReceived?.Invoke(new VideoFrame(frame, timestamp, isKeyFrame));
                ReportStats();
                break;

            case 1: // codec parameters, unencrypted
                var width = (int)BinaryPrimitives.ReadSingleLittleEndian(header.AsSpan(56));
                var height = (int)BinaryPrimitives.ReadSingleLittleEndian(header.AsSpan(60));
                if (payload.Length == 0)
                {
                    _log("     ⚠ 收到空的編碼參數（iPhone 想用 H.265，但我們只宣告支援 H.264）");
                    return;
                }
                var format = ParseAvcC(payload, width, height);
                Format = format;
                _pendingParameterSets = [0, 0, 0, 1, .. format.Sps, 0, 0, 0, 1, .. format.Pps];
                _log($"     畫面格式: H.264 {width}x{height}{(header[6] == 0x56 ? "（畫面暫停）" : "")}");
                FormatChanged?.Invoke(format);
                break;

            // 5 = once-per-second stats report, 2 = old-protocol heartbeat: nothing to do.
        }
    }

    /// <summary>
    /// avcC record: [0] version, [1..4) profile/level, [4] length size, [5] SPS count,
    /// [6..8) SPS length (BE), SPS, [n] PPS count, PPS length (BE), PPS.
    /// </summary>
    public static VideoFormat ParseAvcC(byte[] p, int width, int height)
    {
        var spsLen = BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(6));
        var sps = p.AsSpan(8, spsLen).ToArray();
        var ppsLen = BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(8 + spsLen + 1));
        var pps = p.AsSpan(8 + spsLen + 3, ppsLen).ToArray();
        return new VideoFormat(width, height, sps, pps);
    }

    /// <summary>
    /// Replaces each 4-byte length prefix with a start code, in place. Returns false if the lengths
    /// don't add up exactly to the buffer size or a NAL header is invalid, which after decryption
    /// almost always means the key or keystream position is wrong.
    /// </summary>
    public static bool AvccToAnnexB(byte[] data, out bool isKeyFrame)
    {
        isKeyFrame = false;
        var pos = 0;
        while (pos < data.Length)
        {
            if (pos + 5 > data.Length) return false;
            var len = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(pos));
            if (len <= 0 || len > data.Length - pos - 4) return false;
            var nalHeader = data[pos + 4];
            if ((nalHeader & 0x80) != 0) return false; // forbidden_zero_bit
            if ((nalHeader & 0x1F) == 5) isKeyFrame = true;
            data[pos] = 0; data[pos + 1] = 0; data[pos + 2] = 0; data[pos + 3] = 1;
            pos += 4 + len;
        }
        return pos == data.Length;
    }

    private void ReportStats()
    {
        if (_statsClock.Elapsed.TotalSeconds < 5) return;
        var fps = (_frames - _framesAtLastReport) / _statsClock.Elapsed.TotalSeconds;
        _log($"     畫面: {fps:F1} fps, 累計 {_frames} 幀");
        _framesAtLastReport = _frames;
        _statsClock.Restart();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        try { _loop?.Wait(1000); } catch { /* ends on cancel */ }
        _cipher.Dispose();
        _cts.Dispose();
    }
}
