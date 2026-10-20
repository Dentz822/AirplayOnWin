using System.Security.Cryptography;
using AirPlay.Core.Discovery;
using AirPlay.Core.FairPlay;
using AirPlay.Core.Identity;
using AirPlay.Core.Pairing;
using AirPlay.Core.Rtsp;
using Claunia.PropertyList;

namespace AirPlay.Core;

public sealed class ReceiverOptions
{
    public int DisplayWidth { get; init; } = 1920;
    public int DisplayHeight { get; init; } = 1080;
    public int RefreshRate { get; init; } = 60;
    public int MaxFps { get; init; } = 30;
    /// <summary>Log headers and decoded plist bodies of every request.</summary>
    public bool Verbose { get; init; }
}

/// <summary>
/// Handles the RTSP control connection of one iPhone: device info, pairing, FairPlay, then
/// (from stage 5 on) SETUP/RECORD of the mirroring and audio streams.
/// </summary>
public sealed class AirPlayConnection : IRtspHandler
{
    private readonly DeviceIdentity _id;
    private readonly ReceiverOptions _options;
    private readonly Action<string> _log;
    private readonly PairingSession _pairing;
    private readonly FairPlaySession _fairPlay = new();

    /// <summary>16-byte AES key decrypted from SETUP's ekey (stage 4 result).</summary>
    public byte[]? AesKey { get; private set; }
    public byte[]? AesIv { get; private set; }

    public AirPlayConnection(DeviceIdentity id, ReceiverOptions options, Action<string> log)
    {
        _id = id;
        _options = options;
        _log = log;
        _pairing = new PairingSession(id);
    }

    public Task<RtspResponse> HandleAsync(RtspRequest req, CancellationToken ct)
    {
        _log($"  >> {req.Method} {req.Uri}{(req.Body.Length > 0 ? $" ({req.Body.Length} bytes)" : "")}");
        if (_options.Verbose) LogDetails(req);

        var path = req.Uri.Split('?')[0];
        var response = (req.Method, path) switch
        {
            ("GET", "/info") => Info(req),
            ("OPTIONS", _) => Options(),
            ("POST", "/pair-setup") => PairSetup(req),
            ("POST", "/pair-verify") => PairVerify(req),
            ("POST", "/fp-setup") => FpSetup(req),
            ("POST", "/feedback") => new RtspResponse(),       // keep-alive every ~2 s
            ("SETUP", _) => Setup(req),
            ("GET_PARAMETER" or "SET_PARAMETER" or "RECORD" or "FLUSH" or "TEARDOWN", _) => new RtspResponse(),
            _ => RtspResponse.Error(501, "Not Implemented"),
        };
        if (response.Status != 200) _log($"  << {response.Status} {response.Reason}");
        return Task.FromResult(response);
    }

    // ---- GET /info -------------------------------------------------------------------------

    private RtspResponse Info(RtspRequest req)
    {
        var dict = new NSDictionary();

        // The first /info carries a plist {"qualifier": ["txtAirPlay"]} and only wants the TXT record.
        if (req.IsBinaryPlist && req.Body.Length > 0)
        {
            var qualifier = (PropertyListParser.Parse(req.Body) as NSDictionary)?
                .ObjectForKey("qualifier") as NSArray;
            var first = qualifier is { Count: > 0 } ? qualifier[0].ToString() : null;
            if (first == "txtAirPlay")
                dict.Add("txtAirPlay", new NSData(AirPlayServices.EncodeTxt(AirPlayServices.AirPlayTxt(_id))));
            else if (first == "txtRAOP")
                dict.Add("txtRAOP", new NSData(AirPlayServices.EncodeTxt(AirPlayServices.RaopTxt(_id))));
            return Plist(dict);
        }

        dict.Add("deviceID", new NSString(_id.DeviceId));
        dict.Add("macAddress", new NSString(_id.DeviceId));
        dict.Add("pk", new NSData(_id.Ed25519PublicKey));
        dict.Add("features", new NSNumber((long)AirPlayServices.Features));
        dict.Add("name", new NSString(_id.Name));
        dict.Add("pi", new NSString(_id.PairingId.ToString()));
        dict.Add("vv", new NSNumber(2));
        dict.Add("statusFlags", new NSNumber(68));
        dict.Add("keepAliveLowPower", new NSNumber(true));
        dict.Add("sourceVersion", new NSString(AirPlayServices.SourceVersion));
        dict.Add("keepAliveSendStatsAsBody", new NSNumber(true));
        dict.Add("model", new NSString(AirPlayServices.Model));
        dict.Add("initialVolume", new NSNumber(0.0));

        var latencies = new NSArray();
        var formats = new NSArray();
        foreach (var type in new[] { 100, 101 })
        {
            var latency = new NSDictionary();
            latency.Add("type", new NSNumber(type));
            latency.Add("inputLatencyMicros", new NSNumber(0));
            latency.Add("audioType", new NSString("default"));
            latency.Add("outputLatencyMicros", new NSNumber(false));
            latencies.Add(latency);

            var format = new NSDictionary();
            format.Add("type", new NSNumber(type));
            format.Add("audioInputFormats", new NSNumber(0x3fffffc));
            format.Add("audioOutputFormats", new NSNumber(0x3fffffc));
            formats.Add(format);
        }
        dict.Add("audioLatencies", latencies);
        dict.Add("audioFormats", formats);

        var display = new NSDictionary();
        display.Add("uuid", new NSString("e0ff8a27-6738-3d56-8a16-cc53aacee925"));
        display.Add("widthPhysical", new NSNumber(0));
        display.Add("heightPhysical", new NSNumber(0));
        display.Add("width", new NSNumber(_options.DisplayWidth));
        display.Add("height", new NSNumber(_options.DisplayHeight));
        display.Add("widthPixels", new NSNumber(_options.DisplayWidth));
        display.Add("heightPixels", new NSNumber(_options.DisplayHeight));
        display.Add("rotation", new NSNumber(false));
        display.Add("refreshRate", new NSNumber(1.0 / _options.RefreshRate));
        display.Add("maxFPS", new NSNumber(_options.MaxFps));
        display.Add("overscanned", new NSNumber(false));
        display.Add("features", new NSNumber(14));
        dict.Add("displays", new NSArray(display));

        return Plist(dict);
    }

    private static RtspResponse Options()
    {
        var r = new RtspResponse();
        r.Headers["Public"] = "SETUP, RECORD, FLUSH, TEARDOWN, OPTIONS, GET_PARAMETER, SET_PARAMETER";
        return r;
    }

    // ---- pairing -------------------------------------------------------------------------

    private RtspResponse PairSetup(RtspRequest req)
    {
        var r = new RtspResponse();
        r.SetBody(_pairing.Setup(req.Body), "application/octet-stream");
        _log("     pair-setup 完成");
        return r;
    }

    private RtspResponse PairVerify(RtspRequest req)
    {
        if (req.Body.Length < 4) return RtspResponse.Error(400, "Bad Request");
        var r = new RtspResponse();
        if (req.Body[0] == 1)
        {
            r.SetBody(_pairing.VerifyStart(req.Body), "application/octet-stream");
        }
        else
        {
            try
            {
                _pairing.VerifyFinish(req.Body);
                _log("     pair-verify 完成，簽章驗證成功 ✓");
            }
            catch (PairingException ex)
            {
                _log($"     pair-verify 失敗: {ex.Message}");
                return new RtspResponse { Status = 470, Reason = "Connection Authorization Required", Disconnect = true };
            }
            r.Headers["Content-Type"] = "application/octet-stream";
        }
        return r;
    }

    // ---- FairPlay ------------------------------------------------------------------------

    private RtspResponse FpSetup(RtspRequest req)
    {
        try
        {
            var r = new RtspResponse();
            r.SetBody(_fairPlay.Setup(req.Body), "application/octet-stream");
            _log(req.Body.Length == 16
                ? $"     FairPlay 第 1 步 (mode {req.Body[14]})"
                : "     FairPlay 第 2 步完成 ✓");
            return r;
        }
        catch (FairPlayException ex)
        {
            _log($"     FairPlay 失敗: {ex.Message}");
            return RtspResponse.Error(501, "Not Implemented");
        }
    }

    // ---- SETUP (stage 5 will open the streams here) ----------------------------------------

    private RtspResponse Setup(RtspRequest req)
    {
        if (!req.IsBinaryPlist || req.Body.Length == 0) return RtspResponse.Error(400, "Bad Request");
        var plist = PropertyListParser.Parse(req.Body) as NSDictionary;
        _log("     SETUP 內容:\n" + Indent(plist?.ToXmlPropertyList() ?? "(無法解析)"));

        if (plist?.ObjectForKey("ekey") is NSData ekey)
        {
            var key = _fairPlay.DecryptKey(ekey.Bytes);
            // With pairing, the effective key is SHA-512(aeskey | ecdh secret)[0..16].
            AesKey = _pairing.SharedSecret is { } secret
                ? SHA512.HashData([.. key, .. secret])[..16]
                : key;
            AesIv = (plist.ObjectForKey("eiv") as NSData)?.Bytes;
            _log($"     FairPlay 解出金鑰 ✓ aeskey={Convert.ToHexString(key)} eiv={(AesIv is null ? "-" : Convert.ToHexString(AesIv))}");
        }

        // Streams are not implemented yet (stage 5): refuse so the iPhone stops cleanly.
        return RtspResponse.Error(501, "Not Implemented");
    }

    // ---- helpers ---------------------------------------------------------------------------

    private static RtspResponse Plist(NSObject root)
    {
        var r = new RtspResponse();
        r.SetBody(BinaryPropertyListWriter.WriteToArray(root), "application/x-apple-binary-plist");
        return r;
    }

    private void LogDetails(RtspRequest req)
    {
        foreach (var (k, v) in req.Headers) _log($"     {k}: {v}");
        if (req.Body.Length == 0) return;
        if (req.IsBinaryPlist)
        {
            try { _log(Indent(PropertyListParser.Parse(req.Body).ToXmlPropertyList())); }
            catch (Exception ex) { _log($"     (plist 解析失敗: {ex.Message})"); }
        }
        else
        {
            var n = Math.Min(64, req.Body.Length);
            _log($"     body: {Convert.ToHexString(req.Body, 0, n)}{(req.Body.Length > n ? "…" : "")}");
        }
    }

    private static string Indent(string text) =>
        string.Join("\n", text.Split('\n').Select(l => "        " + l.TrimEnd('\r')));
}
