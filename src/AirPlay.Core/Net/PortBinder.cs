using System.Net;
using System.Net.Sockets;

namespace AirPlay.Core.Net;

/// <summary>
/// Binds the receiver's sockets so that no other program can steal their traffic.
///
/// Windows quirk: binding 0.0.0.0:6001 succeeds even if another program has 192.168.1.x:6001,
/// and then packets to that address go to the *other* program. (Seen with the Sentinel license
/// service spnsrvnt.exe, which owns UDP 6001.) Exclusive binding makes such a bind fail, so we
/// move on to the next port in the range the firewall rules allow.
/// </summary>
public static class PortBinder
{
    /// <summary>UDP range opened by scripts\setup-firewall.ps1.</summary>
    public static readonly int[] FirewallUdpPorts = [.. Enumerable.Range(6000, 11), 7011];

    private const int SIO_UDP_CONNRESET = -1744830452;

    public static UdpClient BindUdp(int preferredPort, Action<string>? log = null)
    {
        foreach (var port in new[] { preferredPort }.Concat(FirewallUdpPorts).Distinct())
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                socket.ExclusiveAddressUse = true;
                socket.Bind(new IPEndPoint(IPAddress.Any, port));
                if (port != preferredPort) log?.Invoke($"     UDP {preferredPort} 被其他程式占用，改用 {port}");
                return Wrap(socket);
            }
            catch (SocketException)
            {
                socket.Dispose();
            }
        }
        var any = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        any.Bind(new IPEndPoint(IPAddress.Any, 0));
        log?.Invoke($"     ⚠ UDP {preferredPort} 附近的連接埠都被占用，改用 {((IPEndPoint)any.LocalEndPoint!).Port}（防火牆可能會擋）");
        return Wrap(any);
    }

    private static UdpClient Wrap(Socket socket)
    {
        // Don't let an ICMP "port unreachable" (e.g. after the iPhone closed a port) turn the
        // next receive into an exception, which would end our receive loop.
        socket.IOControl(SIO_UDP_CONNRESET, [0], null);
        return new UdpClient { Client = socket };
    }

    /// <summary>Starts an exclusive TCP listener on the preferred port, or any free port.</summary>
    public static TcpListener ListenTcp(int preferredPort, Action<string>? log = null)
    {
        var listener = new TcpListener(IPAddress.Any, preferredPort) { ExclusiveAddressUse = true };
        try
        {
            listener.Start();
            return listener;
        }
        catch (SocketException)
        {
            listener = new TcpListener(IPAddress.Any, 0) { ExclusiveAddressUse = true };
            listener.Start();
            log?.Invoke($"     ⚠ TCP {preferredPort} 被占用，改用 {((IPEndPoint)listener.LocalEndpoint).Port}（防火牆可能會擋）");
            return listener;
        }
    }
}
