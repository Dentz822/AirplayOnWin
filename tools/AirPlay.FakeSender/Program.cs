// Simulated iPhone: runs the real AirPlay mirroring protocol against a running receiver,
// streaming a recorded H.264 file as video and a generated tone as audio.
//
// Usage: AirPlay.FakeSender <recording.h264> [--host 127.0.0.1] [--seconds 8] [--volume -25]
//                           [--audio-only [--cover image.png]]   (music-style session: sound + track info)
//        AirPlay.FakeSender --video-url | --youtube [--seconds 10]  (AirPlay video, see VideoMode.cs)
//
// Audio is AAC-LC (ct=4) because FFmpeg has no AAC-ELD encoder; a real iPhone uses AAC-ELD (ct=8)
// while mirroring.
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using AirPlay.Core.Crypto;
using AirPlay.Core.FairPlay;
using AirPlay.Core.Mirroring;
using AirPlay.FakeSender;
using AirPlay.Media;
using Claunia.PropertyList;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

if (args.Contains("--video-url") || args.Contains("--youtube") || args.Contains("--twitch")) return await VideoMode.RunAsync(args);
if (args.Length < 1) { Console.WriteLine("Usage: AirPlay.FakeSender <recording.h264> [--host 127.0.0.1] [--seconds 8] [--volume -25]"); return 1; }
var recording = args[0];
var host = "127.0.0.1";
var seconds = 8.0;
var volume = -25.0;
var audioOnly = args.Contains("--audio-only");
string? cover = null;
for (var i = 1; i < args.Length - 1; i++)
{
    if (args[i] == "--host") host = args[++i];
    else if (args[i] == "--seconds") seconds = double.Parse(args[++i], CultureInfo.InvariantCulture);
    else if (args[i] == "--volume") volume = double.Parse(args[++i], CultureInfo.InvariantCulture);
    else if (args[i] == "--cover") cover = args[++i];
}
void Log(string s) => Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [假 iPhone] {s}");

using var rtsp = new RtspClient(host, 7000);
Log($"已連線 {host}:7000");

// ---- /info ----
var info = await rtsp.Send("GET", "/info");
Log($"/info → {info.Status}, {((NSDictionary)PropertyListParser.Parse(info.Body)).ObjectForKey("name")}");

// ---- pairing (Ed25519 + X25519, AES-CTR encrypted signatures) ----
var ed = new Ed25519PrivateKeyParameters(new SecureRandom());
var edPublic = ed.GeneratePublicKey().GetEncoded();
var serverEd = (await rtsp.Send("POST", "/pair-setup", edPublic, "application/octet-stream")).Body;
var ecdh = new X25519PrivateKeyParameters(new SecureRandom());
var ecdhPublic = ecdh.GeneratePublicKey().GetEncoded();
var v1 = (await rtsp.Send("POST", "/pair-verify", [1, 0, 0, 0, .. ecdhPublic, .. edPublic], "application/octet-stream")).Body;
var serverEcdh = v1[..32];
var shared = new byte[32];
ecdh.GenerateSecret(new X25519PublicKeyParameters(serverEcdh), shared, 0);
byte[] Derive(string salt) => SHA512.HashData([.. Encoding.ASCII.GetBytes(salt), .. shared])[..16];
using (var ctr = new AesCtr(Derive("Pair-Verify-AES-Key"), Derive("Pair-Verify-AES-IV")))
{
    var serverSig = ctr.TransformCopy(v1.AsSpan(32, 64));
    var verifier = new Ed25519Signer();
    verifier.Init(false, new Ed25519PublicKeyParameters(serverEd));
    byte[] signed = [.. serverEcdh, .. ecdhPublic];
    verifier.BlockUpdate(signed, 0, signed.Length);
    if (!verifier.VerifySignature(serverSig)) throw new Exception("接收端簽章錯誤");
    var signer = new Ed25519Signer();
    signer.Init(true, ed);
    byte[] mine = [.. ecdhPublic, .. serverEcdh];
    signer.BlockUpdate(mine, 0, mine.Length);
    var sig = signer.GenerateSignature();
    ctr.Transform(sig.AsSpan());
    var v2 = await rtsp.Send("POST", "/pair-verify", [0, 0, 0, 0, .. sig], "application/octet-stream");
    Log($"配對 → {v2.Status}");
}

// ---- FairPlay: run the same playfair locally so both sides derive the same key ----
var fp = new FairPlaySession();
var fp1 = new byte[16]; "FPLY"u8.CopyTo(fp1); fp1[4] = 3; fp1[5] = 1; fp1[6] = 1; fp1[14] = 2;
await rtsp.Send("POST", "/fp-setup", fp1, "application/octet-stream");
fp.Setup(fp1);
// Key message: "FPLY" 03 01 03 00 00 00 00 98 | mode (same as fp1) | 151 bytes of key data
var fp2 = RandomNumberGenerator.GetBytes(164);
byte[] fp2Header = [0x46, 0x50, 0x4C, 0x59, 0x03, 0x01, 0x03, 0x00, 0x00, 0x00, 0x00, 0x98, fp1[14]];
fp2Header.CopyTo(fp2, 0);
await rtsp.Send("POST", "/fp-setup", fp2, "application/octet-stream");
fp.Setup(fp2);
var ekey = RandomNumberGenerator.GetBytes(72);
var eiv = RandomNumberGenerator.GetBytes(16);
var aesKey = SHA512.HashData([.. fp.DecryptKey(ekey), .. shared])[..16];
Log("FairPlay 完成");

// ---- NTP responder (the receiver asks us for the time) ----
using var ntp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
var ntpCts = new CancellationTokenSource();
_ = Task.Run(async () =>
{
    var clock = Stopwatch.StartNew();
    while (!ntpCts.IsCancellationRequested)
    {
        try
        {
            var req = await ntp.ReceiveAsync(ntpCts.Token);
            var reply = new byte[32];
            reply[0] = 0x80; reply[1] = 0xD3; reply[3] = 0x07;
            req.Buffer.AsSpan(24, 8).CopyTo(reply.AsSpan(8));
            var now = (ulong)(clock.Elapsed.TotalSeconds * 4294967296.0) + (100UL << 32);
            BinaryPrimitives.WriteUInt64BigEndian(reply.AsSpan(16), now);
            BinaryPrimitives.WriteUInt64BigEndian(reply.AsSpan(24), now);
            await ntp.SendAsync(reply, req.RemoteEndPoint);
        }
        catch { break; }
    }
});

// ---- SETUP 1: keys + timing ----
var setup1 = new NSDictionary
{
    { "ekey", new NSData(ekey) }, { "eiv", new NSData(eiv) },
    { "timingProtocol", new NSString("NTP") },
    { "timingPort", new NSNumber(((IPEndPoint)ntp.Client.LocalEndPoint!).Port) },
    { "name", new NSString("假 iPhone") }, { "model", new NSString("iPhone16,2") }, { "osVersion", new NSString("26.6") },
    { "isScreenMirroringSession", new NSNumber(!audioOnly) },
};
var r1 = await rtsp.Send("SETUP", "rtsp://x/1", BinaryPropertyListWriter.WriteToArray(setup1), "application/x-apple-binary-plist");
Log($"SETUP 1 → {r1.Status}");
await rtsp.Send("RECORD", "rtsp://x/1");

// ---- SETUP 2: video (110) + audio (96, AAC-LC) ----
using var audioControl = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
const ulong streamId = 1234567890123456789UL;
var audioStream = new NSDictionary { { "type", new NSNumber(96) }, { "ct", new NSNumber(4) }, { "spf", new NSNumber(1024) },
                                     { "controlPort", new NSNumber(((IPEndPoint)audioControl.Client.LocalEndPoint!).Port) } };
var setup2 = new NSDictionary
{
    { "streams", audioOnly
        ? new NSArray(audioStream)
        : new NSArray(new NSDictionary { { "type", new NSNumber(110) }, { "streamConnectionID", new NSNumber(unchecked((long)streamId)) } }, audioStream) },
};
var r2 = await rtsp.Send("SETUP", "rtsp://x/1", BinaryPropertyListWriter.WriteToArray(setup2), "application/x-apple-binary-plist");
var streams = ((NSArray)((NSDictionary)PropertyListParser.Parse(r2.Body)).ObjectForKey("streams"))
    .OfType<NSDictionary>().ToDictionary(d => ((NSNumber)d.ObjectForKey("type")).ToInt());
var videoPort = audioOnly ? 0 : ((NSNumber)streams[110].ObjectForKey("dataPort")).ToInt();
var audioPort = ((NSNumber)streams[96].ObjectForKey("dataPort")).ToInt();
var audioControlPort = ((NSNumber)streams[96].ObjectForKey("controlPort")).ToInt();
Log($"SETUP 2 → {r2.Status}, 畫面 TCP {videoPort}, 聲音 UDP {audioPort}");
await rtsp.Send("SET_PARAMETER", "rtsp://x/1", Encoding.ASCII.GetBytes($"volume: {volume.ToString("F6", CultureInfo.InvariantCulture)}\r\n"), "text/parameters");

// ---- track info (music-style sessions) ----
if (audioOnly)
{
    static byte[] Dmap(string tag, byte[] value) =>
        [.. Encoding.ASCII.GetBytes(tag), (byte)(value.Length >> 24), (byte)(value.Length >> 16), (byte)(value.Length >> 8), (byte)value.Length, .. value];
    var items = Dmap("minm", Encoding.UTF8.GetBytes("測試歌曲"))
        .Concat(Dmap("asar", Encoding.UTF8.GetBytes("假 iPhone 樂團")))
        .Concat(Dmap("asal", Encoding.UTF8.GetBytes("測試專輯"))).ToArray();
    await rtsp.Send("SET_PARAMETER", "rtsp://x/1", Dmap("mlit", items), "application/x-dmap-tagged");
    if (cover is not null) await rtsp.Send("SET_PARAMETER", "rtsp://x/1", File.ReadAllBytes(cover), "image/png");
    // 3:20 song, currently at 0:15
    await rtsp.Send("SET_PARAMETER", "rtsp://x/1", Encoding.ASCII.GetBytes($"progress: 1000/{1000 + 15 * 44100}/{1000 + 200 * 44100}\r\n"), "text/parameters");
    Log("已送出歌曲資訊");
}

// ---- stream ----
var video = new VideoSender(host, videoPort, aesKey, streamId);
if (!audioOnly) await video.ConnectAsync();
var audio = new AudioSender(host, audioPort, aesKey, eiv);
var accessUnits = AnnexBReader.AccessUnits(File.ReadAllBytes(recording)).ToList();
Log($"開始串流 {seconds} 秒（錄影 {accessUnits.Count} 幀，聲音 440/660 Hz）");

var clock = Stopwatch.StartNew();
int videoSent = 0, audioSent = 0;
var nextFeedback = 2.0;
var nextSync = 0.0;
// Video timestamps are stream time (seconds since start) WITHOUT the 1900→1970 offset;
// audio sync packets give NTP time WITH it, as a real iPhone does.
const ulong Seconds1900To1970 = 2_208_988_800UL;
static ulong ToQ32(double seconds) => (ulong)(seconds * 4294967296.0);
while (clock.Elapsed.TotalSeconds < seconds)
{
    var t = clock.Elapsed.TotalSeconds;
    while (!audioOnly && videoSent / 30.0 <= t && videoSent < accessUnits.Count)
    {
        var ts = ToQ32(videoSent / 30.0);
        await video.SendAccessUnitAsync(accessUnits[videoSent++], ts);
    }
    while (audioSent * 1024 / 44100.0 <= t + 0.05) // stay ~50 ms ahead like a real sender
        audio.SendFrame(audioSent++);
    if (t >= nextFeedback) { await rtsp.Send("POST", "/feedback"); nextFeedback += 2; }
    if (t >= nextSync)
    {
        // 0x54 sync: "RTP timestamp rtp plays at NTP time ntp"
        var rtpNow = (uint)(audioSent * 1024);
        var sync = new byte[20];
        sync[0] = nextSync == 0 ? (byte)0x90 : (byte)0x80; sync[1] = 0xD4; sync[3] = 0x04;
        BinaryPrimitives.WriteUInt32BigEndian(sync.AsSpan(4), rtpNow);
        BinaryPrimitives.WriteUInt64BigEndian(sync.AsSpan(8), ToQ32(rtpNow / 44100.0) + (Seconds1900To1970 << 32));
        BinaryPrimitives.WriteUInt32BigEndian(sync.AsSpan(16), rtpNow + 7497);
        await audioControl.SendAsync(sync, new IPEndPoint(IPAddress.Parse(host), audioControlPort));
        nextSync += 1;
    }
    await Task.Delay(5);
}
Log($"送出 {videoSent} 幀畫面、{audioSent} 幀聲音");

await rtsp.Send("TEARDOWN", "rtsp://x/1");
Log("TEARDOWN 完成");
ntpCts.Cancel();
video.Dispose();
audio.Dispose();
return 0;
