using System.Net.NetworkInformation;
using System.Text.Json;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace AirPlay.Core.Identity;

/// <summary>
/// The receiver's persistent identity: display name, device id (a MAC address),
/// a pairing GUID and the Ed25519 key pair iOS uses to recognise this receiver.
/// Saved under %LOCALAPPDATA%\WindowsAirplay so the iPhone sees the same device every run.
/// </summary>
public sealed class DeviceIdentity
{
    public required string Name { get; init; }
    public required string DeviceId { get; init; }        // "AA:BB:CC:DD:EE:FF"
    public required Guid PairingId { get; init; }
    public required byte[] Ed25519PrivateKey { get; init; } // 32-byte seed

    public byte[] Ed25519PublicKey =>
        new Ed25519PrivateKeyParameters(Ed25519PrivateKey).GeneratePublicKey().GetEncoded();

    /// <summary>Device id without colons, used in the RAOP service name.</summary>
    public string DeviceIdCompact => DeviceId.Replace(":", "");

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WindowsAirplay", "identity.json");

    public static DeviceIdentity LoadOrCreate(string? name = null, string? path = null)
    {
        path ??= DefaultPath;
        DeviceIdentity? identity = null;

        if (File.Exists(path))
        {
            var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(path));
            if (stored is not null)
            {
                identity = new DeviceIdentity
                {
                    Name = stored.Name,
                    DeviceId = stored.DeviceId,
                    PairingId = stored.PairingId,
                    Ed25519PrivateKey = Convert.FromBase64String(stored.Ed25519PrivateKey),
                };
            }
        }

        identity ??= new DeviceIdentity
        {
            Name = Environment.MachineName,
            DeviceId = FindMacAddress(),
            PairingId = Guid.NewGuid(),
            Ed25519PrivateKey = new Ed25519PrivateKeyParameters(new SecureRandom()).GetEncoded(),
        };

        // A name passed on the command line overrides (and replaces) the saved one.
        if (!string.IsNullOrWhiteSpace(name) && name != identity.Name)
            identity = new DeviceIdentity
            {
                Name = name,
                DeviceId = identity.DeviceId,
                PairingId = identity.PairingId,
                Ed25519PrivateKey = identity.Ed25519PrivateKey,
            };

        identity.Save(path);
        return identity;
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var stored = new Stored(Name, DeviceId, PairingId, Convert.ToBase64String(Ed25519PrivateKey));
        File.WriteAllText(path, JsonSerializer.Serialize(stored, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// Picks the MAC of an active Ethernet/Wi-Fi adapter that has a default gateway
    /// (the one on the LAN the iPhone shares). Falls back to a random locally-administered MAC.
    /// </summary>
    private static string FindMacAddress()
    {
        var candidates = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up
                     && n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211
                     && n.GetPhysicalAddress().GetAddressBytes().Length == 6)
            .OrderByDescending(n => n.GetIPProperties().GatewayAddresses.Count > 0)
            .ToList();

        byte[] mac;
        if (candidates.Count > 0)
        {
            mac = candidates[0].GetPhysicalAddress().GetAddressBytes();
        }
        else
        {
            mac = new byte[6];
            Random.Shared.NextBytes(mac);
            mac[0] = (byte)((mac[0] & 0xFC) | 0x02); // unicast, locally administered
        }
        return string.Join(":", mac.Select(b => b.ToString("X2")));
    }

    private sealed record Stored(string Name, string DeviceId, Guid PairingId, string Ed25519PrivateKey);
}
