using System.Net;
using System.Net.Sockets;
using AirPlay.Core.Timing;

namespace AirPlay.Core.Audio;

/// <summary>
/// Audio stream (SETUP stream type 96): RTP over UDP on a data port, plus a control port for
/// sync/resend packets. Stage 5 only opens the ports and counts packets so the iPhone can finish
/// SETUP; decryption (AES-CBC) and AAC-ELD/ALAC decoding come in stage 7.
/// </summary>
public sealed class AudioStream : IDisposable
{
    public const int PreferredDataPort = 6000;
    public const int PreferredControlPort = 6001;

    private readonly UdpClient _data;
    private readonly UdpClient _control;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _loops = new();
    private long _dataPackets, _controlPackets;

    /// <summary>Compression type from SETUP: 2 = ALAC, 4 = AAC, 8 = AAC-ELD (mirroring).</summary>
    public int CompressionType { get; }
    public int DataPort => ((IPEndPoint)_data.Client.LocalEndPoint!).Port;
    public int ControlPort => ((IPEndPoint)_control.Client.LocalEndPoint!).Port;

    public AudioStream(int compressionType, Action<string> log)
    {
        CompressionType = compressionType;
        _log = log;
        _data = NtpClient.BindUdp(PreferredDataPort);
        _control = NtpClient.BindUdp(PreferredControlPort);
    }

    public static string CodecName(int ct) => ct switch
    {
        1 => "PCM",
        2 => "ALAC",
        4 => "AAC",
        8 => "AAC-ELD",
        _ => $"未知({ct})",
    };

    public void Start()
    {
        _loops.Add(CountAsync(_data, () => Interlocked.Increment(ref _dataPackets) == 1, "音訊資料", _cts.Token));
        _loops.Add(CountAsync(_control, () => Interlocked.Increment(ref _controlPackets) == 1, "音訊控制", _cts.Token));
    }

    private async Task CountAsync(UdpClient udp, Func<bool> isFirst, string name, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var r = await udp.ReceiveAsync(ct);
                if (isFirst()) _log($"     收到第一個{name}封包 ({r.Buffer.Length} bytes, UDP {((IPEndPoint)udp.Client.LocalEndPoint!).Port})");
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _data.Dispose();
        _control.Dispose();
        try { Task.WaitAll(_loops.ToArray(), 1000); } catch { /* loops end on cancel */ }
        _log($"     音訊統計: 資料 {_dataPackets} 封包, 控制 {_controlPackets} 封包（尚未解碼）");
        _cts.Dispose();
    }
}
