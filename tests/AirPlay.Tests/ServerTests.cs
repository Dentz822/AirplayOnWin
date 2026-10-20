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
    private AirPlayConnection? _connection;
    private readonly CollectingSink _sink = new();

    public async Task InitializeAsync()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        _port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var options = new ReceiverOptions { MirrorSinkFactory = () => _sink };
        var server = new RtspServer(_port, conn => _connection = new AirPlayConnection(_id, options, conn, _ => { }), _ => { });
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
    public Task PairingAndFairPlayOverTcp() => PairAndFairPlay();

    private async Task PairAndFairPlay()
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

    private const string BinaryPlist = "application/x-apple-binary-plist";

    [Fact]
    public async Task FullSessionStreamsDecryptedVideo()
    {
        await PairAndFairPlay();

        // Plays the iPhone's NTP responder: echo the request's transmit time as "originate".
        using var ntpPeer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var ntpRequest = ntpPeer.ReceiveAsync().ContinueWith(async t =>
        {
            var req = t.Result;
            var reply = new byte[32];
            reply[0] = 0x80; reply[1] = 0xd3; reply[3] = 0x07;
            req.Buffer.AsSpan(24, 8).CopyTo(reply.AsSpan(8));
            await ntpPeer.SendAsync(reply, req.RemoteEndPoint);
            return req.Buffer;
        }).Unwrap();

        var setup1 = new NSDictionary
        {
            { "ekey", new NSData(Enumerable.Range(0, 72).Select(i => (byte)i).ToArray()) },
            { "eiv", new NSData(new byte[16]) },
            { "timingProtocol", new NSString("NTP") },
            { "timingPort", new NSNumber(((IPEndPoint)ntpPeer.Client.LocalEndPoint!).Port) },
            { "isScreenMirroringSession", new NSNumber(true) },
        };
        var r1 = await Send("SETUP", "rtsp://127.0.0.1/1", BinaryPlistWriter(setup1), BinaryPlist);
        Assert.Equal(200, r1.Status);
        var reply1 = (NSDictionary)PropertyListParser.Parse(r1.Body);
        Assert.True(((NSNumber)reply1.ObjectForKey("timingPort")).ToInt() > 0);

        var ntp = await ntpRequest.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal((byte)0x80, ntp[0]);
        Assert.Equal((byte)0xd2, ntp[1]);

        const ulong streamId = 9876543210123456789UL;
        var setup2 = new NSDictionary
        {
            { "streams", new NSArray(
                new NSDictionary { { "type", new NSNumber(110) }, { "streamConnectionID", new NSNumber(unchecked((long)streamId)) } },
                new NSDictionary { { "type", new NSNumber(96) }, { "ct", new NSNumber(8) }, { "controlPort", new NSNumber(0) } }) },
        };
        var r2 = await Send("SETUP", "rtsp://127.0.0.1/1", BinaryPlistWriter(setup2), BinaryPlist);
        Assert.Equal(200, r2.Status);
        var streams = ((NSArray)((NSDictionary)PropertyListParser.Parse(r2.Body)).ObjectForKey("streams"))
            .OfType<NSDictionary>().ToDictionary(d => ((NSNumber)d.ObjectForKey("type")).ToInt());
        var videoPort = ((NSNumber)streams[110].ObjectForKey("dataPort")).ToInt();
        Assert.True(((NSNumber)streams[96].ObjectForKey("dataPort")).ToInt() > 0);

        Assert.Equal(200, (await Send("RECORD", "rtsp://127.0.0.1/1")).Status);

        using var phone = new FakeMirrorSender(_connection!.AesKey!, streamId);
        await phone.ConnectAsync(videoPort);
        await phone.SendCodecAsync(1290, 2796);
        var expected = await phone.SendFrameAsync(1, FakeMirrorSender.Nal(0x65, 777));

        var frame = _sink.Next();
        Assert.True(frame.IsKeyFrame);
        Assert.EndsWith(Convert.ToHexString(expected), Convert.ToHexString(frame.AnnexB));

        Assert.Equal(200, (await Send("TEARDOWN", "rtsp://127.0.0.1/1")).Status);
    }

    private static byte[] BinaryPlistWriter(NSObject o) => BinaryPropertyListWriter.WriteToArray(o);
}
