using System.Text;

namespace AirPlay.Core.Video;

/// <summary>
/// "Reverse HTTP" (PTTH/1.0): after POST /reverse is answered with 101, the iPhone's connection
/// carries requests from us to it — POST /event, used for FCUP requests ("please fetch this URL").
/// The iPhone answers each with an HTTP response, which is read and discarded.
/// </summary>
public sealed class ReverseChannel
{
    private readonly Stream _stream;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ReverseChannel(Stream stream) => _stream = stream;

    public Task Closed => _closed.Task;

    /// <summary>Keeps the connection open, draining the iPhone's responses until it closes.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (!ct.IsCancellationRequested)
                if (await _stream.ReadAsync(buffer, ct) == 0) break;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
        _closed.TrySetResult();
    }

    public async Task SendEventAsync(string sessionId, string xmlPlist)
    {
        var body = Encoding.UTF8.GetBytes(xmlPlist);
        var head = $"POST /event HTTP/1.1\r\n" +
                   $"X-Apple-Session-ID: {sessionId}\r\n" +
                   $"Content-Type: text/x-apple-plist+xml\r\n" +
                   $"Content-Length: {body.Length}\r\n\r\n";
        await _sendLock.WaitAsync();
        try
        {
            await _stream.WriteAsync(Encoding.ASCII.GetBytes(head));
            await _stream.WriteAsync(body);
            await _stream.FlushAsync();
        }
        finally
        {
            _sendLock.Release();
        }
    }
}
