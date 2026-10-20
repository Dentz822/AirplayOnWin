using System.Net;
using System.Net.Sockets;

namespace AirPlay.Core.Rtsp;

/// <summary>
/// Accepts TCP connections and runs a request/response loop on each.
/// A new handler object is created per connection (pairing state is per connection).
/// </summary>
public sealed class RtspServer(int port, Func<RtspConnectionInfo, IRtspHandler> handlerFactory, Action<string> log)
{
    public async Task RunAsync(CancellationToken ct)
    {
        // Exclusive: a second receiver (or another program) on the same port must fail loudly.
        var listener = new TcpListener(IPAddress.Any, port) { ExclusiveAddressUse = true };
        listener.Start();
        var connections = new List<Task>();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(ct); }
                catch (OperationCanceledException) { break; }
                lock (connections)
                {
                    connections.RemoveAll(t => t.IsCompleted);
                    connections.Add(HandleAsync(client, ct));
                }
            }
        }
        finally
        {
            listener.Stop();
            // Let open connections finish cleanup (e.g. close recording files) before returning.
            Task[] pending;
            lock (connections) pending = connections.ToArray();
            await Task.WhenAny(Task.WhenAll(pending), Task.Delay(3000));
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        var info = new RtspConnectionInfo(
            (IPEndPoint)client.Client.RemoteEndPoint!, (IPEndPoint)client.Client.LocalEndPoint!);
        log($"連線 {info.Remote}");
        var handler = handlerFactory(info);
        try
        {
            using (client)
            {
                client.NoDelay = true;
                var stream = client.GetStream();
                var reader = new RtspReader(stream);
                while (!ct.IsCancellationRequested)
                {
                    var request = await reader.ReadAsync(ct);
                    if (request is null) break;

                    RtspResponse response;
                    try
                    {
                        response = await handler.HandleAsync(request, ct);
                    }
                    catch (Exception ex)
                    {
                        log($"  !! {request.Method} {request.Uri} 處理失敗: {ex.Message}");
                        response = RtspResponse.Error(500, "Internal Server Error");
                    }

                    response.Headers.TryAdd("Server", "AirTunes/220.68");
                    if (request.Header("CSeq") is { } cseq) response.Headers["CSeq"] = cseq;
                    await stream.WriteAsync(response.Serialize(request.Protocol), ct);
                    if (response.Disconnect) break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
        {
        }
        finally
        {
            (handler as IDisposable)?.Dispose();
        }
        log($"斷線 {info.Remote}");
    }
}

public sealed record RtspConnectionInfo(IPEndPoint Remote, IPEndPoint Local);

public interface IRtspHandler
{
    Task<RtspResponse> HandleAsync(RtspRequest request, CancellationToken ct);
}
