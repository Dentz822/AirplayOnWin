using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using AirPlay.Core.Net;

namespace AirPlay.Core.Timing;

/// <summary>
/// AirPlay's NTP-style clock sync. The receiver is the client: every 3 s it sends a 32-byte
/// request to the iPhone's timingPort and the iPhone answers with its receive/transmit times.
/// Timestamps are NTP Q32.32 (seconds since 1900 | fraction); the iPhone's clock counts from its boot.
/// The offset is needed later (stage 8) to place video/audio timestamps on our clock.
/// </summary>
public sealed class NtpClient : IDisposable
{
    public const int PreferredPort = 7011;
    private const ulong SecondsFrom1900To1970 = 2_208_988_800UL;

    private static readonly long StartTicks = Stopwatch.GetTimestamp();

    private readonly UdpClient _udp;
    private readonly IPEndPoint _remote;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    /// <summary>remote clock − local clock, in nanoseconds (null until the first reply).</summary>
    public long? OffsetNanos { get; private set; }
    public int LocalPort => ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;

    public NtpClient(IPAddress remoteAddress, int remotePort, Action<string> log)
    {
        _remote = new IPEndPoint(remoteAddress, remotePort);
        _log = log;
        _udp = PortBinder.BindUdp(PreferredPort, log);
    }

    public void Start() => _loop = RunAsync(_cts.Token);

    /// <summary>Local monotonic clock as NTP Q32.32 (epoch 1900, starts at process start).</summary>
    public static ulong LocalNow()
    {
        var elapsed = Stopwatch.GetElapsedTime(StartTicks);
        var seconds = (ulong)elapsed.Ticks / TimeSpan.TicksPerSecond + SecondsFrom1900To1970;
        var fraction = ((ulong)(elapsed.Ticks % TimeSpan.TicksPerSecond) << 32) / TimeSpan.TicksPerSecond;
        return (seconds << 32) | fraction;
    }

    public static long Q32ToNanos(long q32) => (long)((Int128)q32 * 1_000_000_000 >> 32);

    private async Task RunAsync(CancellationToken ct)
    {
        var request = new byte[32];
        request[0] = 0x80; request[1] = 0xd2; request[2] = 0x00; request[3] = 0x07;
        var count = 0;
        var logged = false;

        while (!ct.IsCancellationRequested)
        {
            var t1 = LocalNow();
            BinaryPrimitives.WriteUInt64BigEndian(request.AsSpan(24), t1);
            try
            {
                await _udp.SendAsync(request, _remote, ct);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(300);
                var reply = await _udp.ReceiveAsync(timeout.Token);
                var t4 = LocalNow();
                if (reply.Buffer.Length >= 32 && BinaryPrimitives.ReadUInt64BigEndian(reply.Buffer.AsSpan(8)) == t1)
                {
                    var t2 = BinaryPrimitives.ReadUInt64BigEndian(reply.Buffer.AsSpan(16));
                    var t3 = BinaryPrimitives.ReadUInt64BigEndian(reply.Buffer.AsSpan(24));
                    // Unsigned wrap-around subtraction, then signed: works while the clocks differ by < 68 years.
                    var offsetQ32 = ((long)(t2 - t1) + (long)(t3 - t4)) / 2;
                    OffsetNanos = Q32ToNanos(offsetQ32);
                    if (!logged)
                    {
                        _log($"     NTP 時間同步成功，往返 {Q32ToNanos((long)(t4 - t1)) / 1e6:F1} ms");
                        logged = true;
                    }
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // reply timed out; try again next round
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException ex)
            {
                _log($"     NTP 錯誤: {ex.Message}");
            }

            count++;
            try { await Task.Delay(count < 5 ? 250 : 3000, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _udp.Dispose();
        try { _loop?.Wait(1000); } catch { /* loop ends on cancel */ }
        _cts.Dispose();
    }
}
