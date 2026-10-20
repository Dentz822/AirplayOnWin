using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using AirPlay.Core.Rtsp;

namespace AirPlay.App.PcAudio;

/// <summary>
/// "Listen to the PC on the iPhone": a web page plus a WebSocket audio stream on the AirPlay
/// port (already allowed through the firewall). Safari plays it with Web Audio.
/// The address contains a random code so others on the same Wi-Fi can't simply guess it.
/// The PC's sound is only captured while at least one phone is listening.
/// </summary>
public sealed class PcAudioShare : IDisposable
{
    private const int ChunkMs = 20;
    private const int ChunkBytes = PcAudioCapture.SampleRate * ChunkMs / 1000 * PcAudioCapture.Channels * 2;

    private readonly Action<string> _log;
    private readonly int _port;
    private readonly object _lock = new();
    private readonly List<WebSocketSession> _listeners = new();
    private readonly MemoryStream _pending = new();
    private PcAudioCapture? _capture;
    private bool _excludesSelf = true;

    public PcAudioShare(string token, int port, Action<string> log)
    {
        Token = token;
        _port = port;
        _log = log;
    }

    public bool Enabled { get; set; }
    public string Token { get; set; }
    public int ListenerCount { get { lock (_lock) return _listeners.Count; } }
    public event Action? ListenersChanged;

    public static string NewToken() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();

    /// <summary>Address to open on the iPhone (this PC's LAN address).</summary>
    public string Url => $"http://{LanAddress()}:{_port}/listen/{Token}";

    /// <summary>HTTP hook for the AirPlay server; null = not ours.</summary>
    public RtspResponse? Handle(RtspRequest req, RtspConnectionInfo connection)
    {
        var path = req.Uri.Split('?')[0];
        if (!path.StartsWith("/listen")) return null;
        if (!Enabled) return Text(403, "Forbidden", "電腦端沒有開啟「讓手機收聽電腦聲音」。");
        if (path == $"/listen/{Token}" && req.Method == "GET") return Page();
        if (path == $"/listen/{Token}/ws" && req.Method == "GET" &&
            req.Header("Upgrade")?.Equals("websocket", StringComparison.OrdinalIgnoreCase) == true &&
            req.Header("Sec-WebSocket-Key") is { } key)
            return Upgrade(key, connection);
        return Text(404, "Not Found", "網址不對：請重新掃描電腦上的 QR code。");
    }

    private RtspResponse Upgrade(string key, RtspConnectionInfo connection)
    {
        var r = new RtspResponse { Status = 101, Reason = "Switching Protocols" };
        r.Headers["Upgrade"] = "websocket";
        r.Headers["Connection"] = "Upgrade";
        r.Headers["Sec-WebSocket-Accept"] = WebSocketSession.AcceptKey(key);
        r.TakeOver = async (stream, ct) =>
        {
            var session = new WebSocketSession(stream, maxQueuedFrames: 25); // ≤ 0.5 s queued per phone
            session.SendText($$"""{"rate":{{PcAudioCapture.SampleRate}},"channels":{{PcAudioCapture.Channels}},"excludesSelf":{{(_excludesSelf ? "true" : "false")}}}""");
            await AddListener(session, connection);
            try { await session.RunAsync(ct); }
            finally { RemoveListener(session, connection); }
        };
        return r;
    }

    private async Task AddListener(WebSocketSession session, RtspConnectionInfo connection)
    {
        bool first;
        lock (_lock)
        {
            _listeners.Add(session);
            first = _listeners.Count == 1;
        }
        _log($"手機開始收聽電腦聲音（{connection.Remote.Address}）");
        if (first)
        {
            var capture = new PcAudioCapture(_log);
            capture.Data += OnCaptured;
            await capture.StartAsync();
            _excludesSelf = capture.ExcludesSelf;
            lock (_lock)
            {
                if (_listeners.Count == 0) { capture.Dispose(); }
                else { _capture?.Dispose(); _capture = capture; }
            }
        }
        ListenersChanged?.Invoke();
    }

    private void RemoveListener(WebSocketSession session, RtspConnectionInfo connection)
    {
        PcAudioCapture? stop = null;
        lock (_lock)
        {
            _listeners.Remove(session);
            if (_listeners.Count == 0)
            {
                stop = _capture;
                _capture = null;
                _pending.SetLength(0);
            }
        }
        stop?.Dispose(); // nobody listening: stop capturing
        _log($"手機停止收聽電腦聲音（{connection.Remote.Address}）");
        ListenersChanged?.Invoke();
    }

    /// <summary>Re-chunks captured audio into 20 ms frames and sends them to every listener.</summary>
    private void OnCaptured(ReadOnlyMemory<byte> pcm)
    {
        lock (_lock)
        {
            if (_listeners.Count == 0) return;
            _pending.Write(pcm.Span);
            var buffer = _pending.GetBuffer();
            var length = (int)_pending.Length;
            var offset = 0;
            while (length - offset >= ChunkBytes)
            {
                var chunk = buffer.AsSpan(offset, ChunkBytes).ToArray();
                foreach (var listener in _listeners) listener.SendBinary(chunk);
                offset += ChunkBytes;
            }
            var rest = buffer.AsSpan(offset, length - offset).ToArray();
            _pending.SetLength(0);
            _pending.Write(rest);
        }
    }

    /// <summary>IPv4 address of the adapter with a default gateway (the LAN the iPhone is on).</summary>
    public static string LanAddress()
    {
        var candidates = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                        n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211)
            .OrderByDescending(n => n.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork))
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
        return candidates.FirstOrDefault()?.ToString() ?? "127.0.0.1";
    }

    private static RtspResponse Text(int status, string reason, string message)
    {
        var r = new RtspResponse { Status = status, Reason = reason };
        r.SetBody(Encoding.UTF8.GetBytes(message), "text/plain; charset=utf-8");
        return r;
    }

    private static RtspResponse Page()
    {
        var r = new RtspResponse();
        r.SetBody(Encoding.UTF8.GetBytes(ListenPage.Html), "text/html; charset=utf-8");
        r.Headers["Cache-Control"] = "no-store";
        return r;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _capture?.Dispose();
            _capture = null;
        }
    }
}
