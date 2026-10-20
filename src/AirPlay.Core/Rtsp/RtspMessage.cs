using System.Text;

namespace AirPlay.Core.Rtsp;

/// <summary>An RTSP/1.0 or HTTP/1.1 request as the iPhone sends it.</summary>
public sealed class RtspRequest
{
    public required string Method { get; init; }
    public required string Uri { get; init; }
    public required string Protocol { get; init; }
    public required Dictionary<string, string> Headers { get; init; }
    public required byte[] Body { get; init; }

    public string? Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;
    public string? ContentType => Header("Content-Type");
    public bool IsBinaryPlist => ContentType?.Contains("application/x-apple-binary-plist") == true;

    public override string ToString() => $"{Method} {Uri} {Protocol}";
}

public sealed class RtspResponse
{
    public int Status { get; set; } = 200;
    public string Reason { get; set; } = "OK";
    public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);
    public byte[] Body { get; set; } = [];
    /// <summary>Close the connection after sending.</summary>
    public bool Disconnect { get; set; }
    /// <summary>
    /// After sending, hand the connection over (e.g. "101 Switching Protocols" to reverse HTTP);
    /// the request/response loop ends and the connection closes when this task completes.
    /// </summary>
    public Func<Stream, CancellationToken, Task>? TakeOver { get; set; }

    public static RtspResponse Error(int status, string reason) => new() { Status = status, Reason = reason };

    public void SetBody(byte[] body, string contentType)
    {
        Body = body;
        Headers["Content-Type"] = contentType;
    }

    public byte[] Serialize(string protocol)
    {
        var sb = new StringBuilder($"{protocol} {Status} {Reason}\r\n");
        foreach (var (k, v) in Headers) sb.Append($"{k}: {v}\r\n");
        sb.Append($"Content-Length: {Body.Length}\r\n\r\n");
        var head = Encoding.ASCII.GetBytes(sb.ToString());
        var result = new byte[head.Length + Body.Length];
        head.CopyTo(result, 0);
        Body.CopyTo(result, head.Length);
        return result;
    }
}

/// <summary>Parses RTSP/HTTP requests off a stream (header block + Content-Length body).</summary>
internal sealed class RtspReader(Stream stream)
{
    private readonly byte[] _buf = new byte[256 * 1024];
    private int _start, _end;

    public async Task<RtspRequest?> ReadAsync(CancellationToken ct)
    {
        var head = await ReadHeaderAsync(ct);
        if (head is null) return null;

        var lines = head.Split("\r\n");
        var first = lines[0].Split(' ');
        if (first.Length < 3) throw new IOException($"bad request line: {lines[0]}");

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var p = line.Split(':', 2);
            if (p.Length == 2) headers[p[0].Trim()] = p[1].Trim();
        }
        var length = headers.TryGetValue("Content-Length", out var cl) ? int.Parse(cl) : 0;
        var body = await ReadBytesAsync(length, ct);

        return new RtspRequest
        {
            Method = first[0],
            Uri = first[1],
            Protocol = first[^1],
            Headers = headers,
            Body = body,
        };
    }

    private async Task<string?> ReadHeaderAsync(CancellationToken ct)
    {
        while (true)
        {
            var idx = _buf.AsSpan(_start, _end - _start).IndexOf("\r\n\r\n"u8);
            if (idx >= 0)
            {
                var text = Encoding.ASCII.GetString(_buf, _start, idx);
                _start += idx + 4;
                return text;
            }
            if (!await FillAsync(ct)) return null;
        }
    }

    private async Task<byte[]> ReadBytesAsync(int count, CancellationToken ct)
    {
        var result = new byte[count];
        var copied = 0;
        while (copied < count)
        {
            if (_start == _end && !await FillAsync(ct)) throw new IOException("connection closed mid-body");
            var n = Math.Min(count - copied, _end - _start);
            Buffer.BlockCopy(_buf, _start, result, copied, n);
            _start += n;
            copied += n;
        }
        return result;
    }

    private async Task<bool> FillAsync(CancellationToken ct)
    {
        if (_start > 0)
        {
            Buffer.BlockCopy(_buf, _start, _buf, 0, _end - _start);
            _end -= _start;
            _start = 0;
        }
        if (_end == _buf.Length) throw new IOException("request header too large");
        var n = await stream.ReadAsync(_buf.AsMemory(_end), ct);
        if (n == 0) return false;
        _end += n;
        return true;
    }
}
