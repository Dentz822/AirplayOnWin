using System.Text;
using AirPlay.Core.Identity;

namespace AirPlay.Core.Discovery;

/// <summary>
/// Registers the two Bonjour services an iPhone looks for before offering "Screen Mirroring":
///   _raop._tcp    "AABBCCDDEEFF@Name"  (audio / legacy AirPlay)
///   _airplay._tcp "Name"               (video / mirroring)
/// Both point at the same RTSP port. TXT values follow UxPlay (posing as an Apple TV 3,2).
/// </summary>
public sealed class AirPlayServices : IDisposable
{
    public const ushort Port = 7000;

    // Feature bits (low 32, high 32): mirroring, audio, FairPlay, bit 27 = legacy pairing.
    public const ulong Features = 0x5A7FFEE6;
    public const string Model = "AppleTV3,2";
    public const string SourceVersion = "220.68";
    private static readonly string FeaturesText = $"0x{Features & 0xFFFFFFFF:X},0x{Features >> 32:X}";

    private readonly List<DnsSdAdvertiser> _advertisers = new();

    public IReadOnlyList<string> ServiceNames => _advertisers.Select(a => a.ServiceName).ToList();

    public static KeyValuePair<string, string>[] RaopTxt(DeviceIdentity id) =>
    [
        new("ch", "2"),            // stereo
        new("cn", "0,1,2,3"),      // codecs: PCM, ALAC, AAC, AAC-ELD
        new("da", "true"),
        new("et", "0,3,5"),        // encryption: none, FairPlay, FairPlay SAPv2.5
        new("vv", "2"),
        new("ft", FeaturesText),
        new("am", Model),
        new("md", "0,1,2"),        // metadata: text, artwork, progress
        new("rhd", "5.6.0.0"),
        new("pw", "false"),
        new("sf", "0x4"),
        new("sr", "44100"),
        new("ss", "16"),
        new("sv", "false"),
        new("tp", "UDP"),
        new("txtvers", "1"),
        new("vs", SourceVersion),
        new("vn", "65537"),
        new("pk", PublicKeyHex(id)),
    ];

    public static KeyValuePair<string, string>[] AirPlayTxt(DeviceIdentity id) =>
    [
        new("deviceid", id.DeviceId),
        new("features", FeaturesText),
        new("pw", "false"),
        new("flags", "0x4"),
        new("model", Model),
        new("pk", PublicKeyHex(id)),
        new("pi", id.PairingId.ToString()),
        new("srcvers", SourceVersion),
        new("vv", "2"),
    ];

    /// <summary>TXT record wire format: each entry is a length byte followed by "key=value".</summary>
    public static byte[] EncodeTxt(IEnumerable<KeyValuePair<string, string>> txt)
    {
        using var ms = new MemoryStream();
        foreach (var (k, v) in txt)
        {
            var bytes = Encoding.UTF8.GetBytes($"{k}={v}");
            ms.WriteByte((byte)bytes.Length);
            ms.Write(bytes);
        }
        return ms.ToArray();
    }

    private static string PublicKeyHex(DeviceIdentity id) =>
        Convert.ToHexString(id.Ed25519PublicKey).ToLowerInvariant();

    public static async Task<AirPlayServices> StartAsync(DeviceIdentity id)
    {
        var services = new AirPlayServices();
        try
        {
            services._advertisers.Add(await DnsSdAdvertiser.RegisterAsync(
                $"{id.DeviceIdCompact}@{id.Name}", "_raop._tcp", Port, RaopTxt(id)));
            services._advertisers.Add(await DnsSdAdvertiser.RegisterAsync(
                id.Name, "_airplay._tcp", Port, AirPlayTxt(id)));
        }
        catch
        {
            services.Dispose();
            throw;
        }
        return services;
    }

    public void Dispose()
    {
        foreach (var a in _advertisers) a.Dispose();
        _advertisers.Clear();
    }
}
