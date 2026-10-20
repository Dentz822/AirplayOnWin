using AirPlay.Core.Discovery;
using AirPlay.Core.Identity;
using AirPlay.Core.Rtsp;

namespace AirPlay.Core;

/// <summary>
/// The whole receiver: RTSP server + mDNS advertising. Shared by the console host and the window app.
/// StopAsync withdraws mDNS first so iPhones drop this PC from their list immediately.
/// </summary>
public sealed class AirPlayReceiver : IAsyncDisposable
{
    private readonly ReceiverOptions _options;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _cts = new();
    private AirPlayServices? _services;
    private Task? _serverTask;
    private int _stopped;

    public DeviceIdentity Identity { get; }

    public AirPlayReceiver(DeviceIdentity identity, ReceiverOptions options, Action<string> log)
    {
        Identity = identity;
        _options = options;
        _log = log;
    }

    public async Task StartAsync()
    {
        var video = _options.VideoPlayer is { } player
            ? new Video.AirPlayVideoService(player, AirPlayServices.Port, _log)
            : null;
        var server = new RtspServer(AirPlayServices.Port,
            conn => new AirPlayConnection(Identity, _options, conn, _log, video), _log);
        // Run on the thread pool: if StartAsync is called from a UI thread, the server's async
        // continuations must not be queued onto it (that would also deadlock blocking cleanup).
        _serverTask = Task.Run(() => server.RunAsync(_cts.Token));
        await Task.WhenAny(_serverTask, Task.Delay(200));
        if (_serverTask.IsFaulted) await _serverTask; // e.g. port 7000 already in use

        _services = await AirPlayServices.StartAsync(Identity, _options.Features);
        foreach (var s in _services.ServiceNames) _log($"mDNS 已廣播: {s}");
    }

    /// <summary>Completes when the RTSP server stops on its own (e.g. a fatal socket error).</summary>
    public Task Completion => _serverTask ?? Task.CompletedTask;

    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 1) return;
        _log("正在停止…");
        _cts.Cancel();
        if (_services is not null)
        {
            await Task.Run(_services.Dispose); // mDNS goodbye
            _log("mDNS 已停止廣播");
        }
        if (_serverTask is not null)
        {
            try { await _serverTask; }
            catch (Exception ex) { _log($"伺服器結束時發生錯誤: {ex.Message}"); }
        }
        _log("已停止。");
    }

    /// <summary>Blocking version for shutdown paths that can't await (window close, logoff).</summary>
    public void Stop(TimeSpan timeout) => Task.Run(StopAsync).Wait(timeout);

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _cts.Dispose();
    }
}
