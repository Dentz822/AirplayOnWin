using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using AirPlay.Core.Net;

namespace AirPlay.Core.Audio;

/// <summary>Audio stream parameters from SETUP (stream type 96).</summary>
public sealed record AudioFormat(int CompressionType, int SamplesPerFrame, int SampleRate = 44100, int Channels = 2)
{
    /// <summary>ct: 2 = ALAC (music), 4 = AAC-LC, 8 = AAC-ELD (screen mirroring).</summary>
    public string CodecName => CompressionType switch
    {
        1 => "PCM",
        2 => "ALAC",
        4 => "AAC",
        8 => "AAC-ELD",
        _ => $"未知({CompressionType})",
    };
}

/// <summary>Receives decrypted, in-order compressed audio frames of one session.</summary>
public interface IAudioSink : IDisposable
{
    void OnAudioFrame(ReadOnlySpan<byte> frame, uint rtpTimestamp);
    /// <summary>iPhone volume in dB: 0 = full, -30 = quietest, -144 = mute.</summary>
    void SetVolume(double db);
    /// <summary>Playback paused/seeked: drop queued audio.</summary>
    void Flush();
}

/// <summary>
/// Audio stream (SETUP stream type 96). RTP over UDP:
///   data port    : 0x80 0x60 | seq (BE16) | timestamp (BE32) | ssrc | payload (AES-128-CBC)
///   control port : 0x54 = time sync (RTP time ↔ NTP time), 0x56 = a resent data packet
/// Each payload is decrypted on its own with the session key and the SETUP eiv; only whole
/// 16-byte blocks are encrypted, a shorter tail is sent in the clear.
/// Packets are put back in order; a gap triggers a resend request (0x55) to the iPhone.
/// </summary>
public sealed class AudioStream : IDisposable
{
    public const int PreferredDataPort = 6000;
    public const int PreferredControlPort = 6001;
    private static readonly byte[] NoDataMarker = [0x00, 0x68, 0x34, 0x00];

    private readonly UdpClient _data;
    private readonly UdpClient _control;
    private readonly Aes _aes;
    private readonly byte[] _iv;
    private readonly IPEndPoint? _remoteControl;
    private readonly IAudioSink? _sink;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _loops = new();
    private readonly ReorderBuffer _reorder;
    private readonly object _lock = new();
    private ushort _resendSeq;

    private long _packets, _frames, _lost, _resendRequests;

    public AudioFormat Format { get; }
    public int DataPort => ((IPEndPoint)_data.Client.LocalEndPoint!).Port;
    public int ControlPort => ((IPEndPoint)_control.Client.LocalEndPoint!).Port;

    /// <summary>Latest 0x54 sync: this RTP timestamp plays at this NTP time on the iPhone's clock.</summary>
    public (uint Rtp, ulong Ntp)? LastSync { get; private set; }

    /// <param name="remoteControl">The iPhone's control port (for resend requests), or null.</param>
    public AudioStream(AudioFormat format, byte[] aesKey, byte[] aesIv, IPEndPoint? remoteControl,
        IAudioSink? sink, Action<string> log)
    {
        Format = format;
        _sink = sink;
        _log = log;
        _remoteControl = remoteControl;
        _aes = Aes.Create();
        _aes.Key = aesKey;
        _iv = aesIv;
        _data = PortBinder.BindUdp(PreferredDataPort, log);
        _control = PortBinder.BindUdp(PreferredControlPort, log);
        _reorder = new ReorderBuffer(Deliver, RequestResend);
    }

    public void Start()
    {
        _loops.Add(ReceiveLoop(_data, OnDataPacket, _cts.Token));
        _loops.Add(ReceiveLoop(_control, OnControlPacket, _cts.Token));
    }

    private static async Task ReceiveLoop(UdpClient udp, Action<byte[]> handle, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
                handle((await udp.ReceiveAsync(ct)).Buffer);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { }
    }

    private void OnDataPacket(byte[] packet)
    {
        if (packet.Length < 12) return;
        if (Interlocked.Increment(ref _packets) == 1)
            _log($"     收到第一個音訊封包 ({Format.CodecName})");
        Enqueue(packet);
    }

    private void OnControlPacket(byte[] packet)
    {
        if (packet.Length < 8) return;
        switch (packet[1] & 0x7F)
        {
            case 0x54 when packet.Length >= 20: // time sync
                LastSync = (BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(4)),
                            BinaryPrimitives.ReadUInt64BigEndian(packet.AsSpan(8)));
                break;
            case 0x56 when packet.Length >= 16: // resent data packet, wrapped after a 4-byte header
                Enqueue(packet[4..]);
                break;
        }
    }

    private void Enqueue(byte[] rtp)
    {
        var seq = BinaryPrimitives.ReadUInt16BigEndian(rtp.AsSpan(2));
        var timestamp = BinaryPrimitives.ReadUInt32BigEndian(rtp.AsSpan(4));
        var payload = rtp.AsSpan(12);
        byte[] frame;
        if (payload.Length == 0 || payload.SequenceEqual(NoDataMarker))
            frame = []; // keeps the sequence going, nothing to play
        else
            frame = Decrypt(payload);
        lock (_lock) _reorder.Add(seq, timestamp, frame);
    }

    /// <summary>AES-128-CBC, fresh IV per packet; the trailing partial block is plain text.</summary>
    public byte[] Decrypt(ReadOnlySpan<byte> payload)
    {
        var output = payload.ToArray();
        var encrypted = payload.Length / 16 * 16;
        if (encrypted > 0)
            _aes.DecryptCbc(payload[..encrypted], _iv, output.AsSpan(0, encrypted), PaddingMode.None);
        return output;
    }

    private void Deliver(uint timestamp, byte[] frame)
    {
        if (frame.Length == 0) return;
        _frames++;
        try { _sink?.OnAudioFrame(frame, timestamp); }
        catch (Exception ex) { if (_frames < 5) _log($"     音訊處理錯誤: {ex.Message}"); }
    }

    private void RequestResend(ushort firstMissing, ushort count, bool givingUp)
    {
        if (givingUp)
        {
            _lost += count;
            return;
        }
        if (_remoteControl is null) return;
        _resendRequests++;
        var packet = new byte[8];
        packet[0] = 0x80;
        packet[1] = 0x55 | 0x80;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), _resendSeq++);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4), firstMissing);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(6), count);
        try { _control.Send(packet, _remoteControl); }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { }
    }

    /// <summary>FLUSH from the iPhone (pause/seek): drop everything queued.</summary>
    public void Flush()
    {
        lock (_lock) _reorder.Reset();
        _sink?.Flush();
    }

    public void SetVolume(double db) => _sink?.SetVolume(db);

    public void Dispose()
    {
        _cts.Cancel();
        _data.Dispose();
        _control.Dispose();
        try { Task.WaitAll(_loops.ToArray(), 1000); } catch { /* loops end on cancel */ }
        _sink?.Dispose();
        _aes.Dispose();
        _log($"     音訊統計: {_packets} 封包, {_frames} 幀已播放, 補送請求 {_resendRequests} 次, 遺失 {_lost} 幀");
        _cts.Dispose();
    }

    /// <summary>
    /// Puts RTP packets back in sequence order. A missing packet is waited for (and a resend
    /// requested) while later packets pile up; after <see cref="GiveUpAfter"/> it is skipped.
    /// </summary>
    internal sealed class ReorderBuffer(Action<uint, byte[]> deliver, Action<ushort, ushort, bool> onGap)
    {
        public const int RequestResendAfter = 3;
        public const int GiveUpAfter = 25; // ≈ 0.27 s of AAC-ELD

        // Sequence numbers are 16-bit and wrap; internally they are extended to a running 64-bit count.
        private readonly SortedDictionary<long, (uint Ts, byte[] Frame)> _pending = new();
        private long _next;
        private bool _started;
        private long _resendRequestedFor = -1;

        public void Add(ushort seq, uint timestamp, byte[] frame)
        {
            if (!_started)
            {
                _started = true;
                _next = seq;
            }
            var abs = _next + (short)(seq - (ushort)_next); // nearest extension of seq to _next
            if (abs < _next) return;                         // late duplicate or already skipped
            if (abs - _next > 1000) { Reset(); Add(seq, timestamp, frame); return; } // stream jumped

            _pending[abs] = (timestamp, frame);
            Drain();

            if (_pending.Count == 0) return;
            if (_pending.Count >= RequestResendAfter && _resendRequestedFor != _next)
            {
                _resendRequestedFor = _next;
                onGap((ushort)_next, GapLength(), false);
            }
            if (_pending.Count >= GiveUpAfter)
            {
                onGap((ushort)_next, GapLength(), true);
                _next = _pending.Keys.First();
                Drain();
            }
        }

        private ushort GapLength() => (ushort)(_pending.Keys.First() - _next);

        private void Drain()
        {
            while (_pending.Remove(_next, out var item))
            {
                deliver(item.Ts, item.Frame);
                _next++;
            }
        }

        public void Reset()
        {
            _pending.Clear();
            _started = false;
            _resendRequestedFor = -1;
        }
    }
}
