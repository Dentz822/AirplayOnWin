using System.Net;
using System.Net.Sockets;
using System.Text;
using AirPlay.Core;
using AirPlay.Core.Identity;
using AirPlay.Core.Rtsp;
using Claunia.PropertyList;

namespace AirPlay.Tests;

/// <summary>End-to-end over TCP: what an iPhone does up to the FairPlay handshake.</summary>
public class ServerTests : IAsyncLifetime
{
    private readonly CancellationTokenSource _cts = new();
    private readonly DeviceIdentity _id =
        DeviceIdentity.LoadOrCreate("TestPC", Path.Combine(Path.GetTempPath(), $"ap-id-{Guid.NewGuid()}.json"));
    private int _port;
    private TcpClient _client = null!;
    private NetworkStream _stream = null!;
    private int _cseq;

    public async Task InitializeAsync()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        _port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var server = new RtspServer(_port, _ => new AirPlayConnection(_id, new ReceiverOptions(), _ => { }), _ => { });
        _ = server.RunAsync(_cts.Token);
        await Task.Delay(100);

        _client = new TcpClient();
        await _client.ConnectAsync(IPAddress.Loopback, _port);
        _stream = _client.GetStream();
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _cts.Cancel();
        return Task.CompletedTask;
    }

    private async Task<(int Status, Dictionary<string, string> Headers, byte[] Body)> Send(
        string method, string uri, byte[]? body = null, string? contentType = null)
    {
        var head = new StringBuilder($"{method} {uri} RTSP/1.0\r\nCSeq: {++_cseq}\r\n");
        if (contentType is not null) head.Append($"Content-Type: {contentType}\r\n");
        if (body is not null) head.Append($"Content-Length: {body.Length}\r\n");
        head.Append("User-Agent: AirPlay/690.7.1\r\n\r\n");
        await _stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()));
        if (body is not null) await _stream.WriteAsync(body);

        // Read response header
        var buf = new List<byte>();
        while (!(buf.Count >= 4 && buf[^4] == '\r' && buf[^3] == '\n' && buf[^2] == '\r' && buf[^1] == '\n'))
        {
            var b = _stream.ReadByte();
            if (b < 0) throw new IOException("closed");
            buf.Add((byte)b);
        }
        var lines = Encoding.ASCII.GetString(buf.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var status = int.Parse(lines[0].Split(' ')[1]);
        var headers = lines.Skip(1).Select(l => l.Split(':', 2))
            .ToDictionary(p => p[0].Trim(), p => p[1].Trim(), StringComparer.OrdinalIgnoreCase);
        var data = new byte[int.Parse(headers["Content-Length"])];
        await _stream.ReadExactlyAsync(data);
        return (status, headers, data);
    }

    [Fact]
    public async Task InfoReturnsDeviceDescription()
    {
        var (status, headers, body) = await Send("GET", "/info");

        Assert.Equal(200, status);
        Assert.Equal("1", headers["CSeq"]);
        var plist = (NSDictionary)PropertyListParser.Parse(body);
        Assert.Equal(_id.DeviceId, plist.ObjectForKey("deviceID").ToString());
        Assert.Equal(_id.Ed25519PublicKey, ((NSData)plist.ObjectForKey("pk")).Bytes);
        Assert.Equal(0x5A7FFEE6L, ((NSNumber)plist.ObjectForKey("features")).ToLong());
        Assert.True(plist.ContainsKey("displays"));
    }

    [Fact]
    public async Task InfoWithQualifierReturnsOnlyTxtRecord()
    {
        var req = new NSDictionary { { "qualifier", new NSArray(new NSString("txtAirPlay")) } };
        var (status, _, body) = await Send("GET", "/info",
            BinaryPropertyListWriter.WriteToArray(req), "application/x-apple-binary-plist");

        Assert.Equal(200, status);
        var plist = (NSDictionary)PropertyListParser.Parse(body);
        Assert.Single(plist.Keys);
        var txt = Encoding.UTF8.GetString(((NSData)plist.ObjectForKey("txtAirPlay")).Bytes);
        Assert.Contains($"deviceid={_id.DeviceId}", txt);
    }

    [Fact]
    public async Task PairingAndFairPlayOverTcp()
    {
        var phone = new FakeIPhone();
        const string octet = "application/octet-stream";

        var setup = await Send("POST", "/pair-setup", phone.PairSetupBody(), octet);
        Assert.Equal(200, setup.Status);
        phone.OnPairSetupResponse(setup.Body);

        var v1 = await Send("POST", "/pair-verify", phone.VerifyStep1Body(), octet);
        Assert.Equal(200, v1.Status);
        var v2 = await Send("POST", "/pair-verify", phone.OnVerifyStep1Response(v1.Body), octet);
        Assert.Equal(200, v2.Status);

        var fpRequest = new byte[16];
        "FPLY"u8.CopyTo(fpRequest);
        fpRequest[4] = 3; fpRequest[14] = 2;
        var fp1 = await Send("POST", "/fp-setup", fpRequest, octet);
        Assert.Equal(200, fp1.Status);
        Assert.Equal(142, fp1.Body.Length);

        var fp2Request = new byte[164];
        "FPLY"u8.CopyTo(fp2Request);
        fp2Request[4] = 3;
        var fp2 = await Send("POST", "/fp-setup", fp2Request, octet);
        Assert.Equal(200, fp2.Status);
        Assert.Equal(32, fp2.Body.Length);
    }
}
