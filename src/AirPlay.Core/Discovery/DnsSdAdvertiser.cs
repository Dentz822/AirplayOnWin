using System.Runtime.InteropServices;
using static AirPlay.Core.Discovery.DnsSdNative;

namespace AirPlay.Core.Discovery;

/// <summary>
/// Advertises one DNS-SD service (e.g. "Name._airplay._tcp.local") through the Windows mDNS responder.
/// Dispose to withdraw it.
/// </summary>
public sealed class DnsSdAdvertiser : IDisposable
{
    private readonly string _serviceName;
    private readonly RegisterComplete _callback; // kept alive: native code holds a pointer to it
    private readonly TaskCompletionSource<uint> _registered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<uint> _deregistered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IntPtr _instance;
    private IntPtr _request;
    private bool _disposed;

    private DnsSdAdvertiser(string serviceName)
    {
        _serviceName = serviceName;
        _callback = OnComplete;
    }

    /// <param name="instanceName">e.g. "My PC"</param>
    /// <param name="serviceType">e.g. "_airplay._tcp"</param>
    public static async Task<DnsSdAdvertiser> RegisterAsync(
        string instanceName, string serviceType, ushort port, IReadOnlyList<KeyValuePair<string, string>> txt)
    {
        var fullName = $"{instanceName}.{serviceType}.local";
        var host = $"{Environment.MachineName}.local";
        var advertiser = new DnsSdAdvertiser(fullName);

        advertiser._instance = DnsServiceConstructInstance(
            fullName, host, IntPtr.Zero, IntPtr.Zero, port, 0, 0,
            (uint)txt.Count,
            txt.Select(p => p.Key).ToArray(),
            txt.Select(p => p.Value).ToArray());
        if (advertiser._instance == IntPtr.Zero)
            throw new InvalidOperationException($"DnsServiceConstructInstance failed for {fullName}");

        var request = new DNS_SERVICE_REGISTER_REQUEST
        {
            Version = DNS_QUERY_REQUEST_VERSION1,
            InterfaceIndex = 0, // all interfaces
            pServiceInstance = advertiser._instance,
            pRegisterCompletionCallback = Marshal.GetFunctionPointerForDelegate(advertiser._callback),
        };
        // The request must stay valid until deregistration, so it lives in unmanaged memory.
        advertiser._request = Marshal.AllocHGlobal(Marshal.SizeOf<DNS_SERVICE_REGISTER_REQUEST>());
        Marshal.StructureToPtr(request, advertiser._request, false);

        var rc = DnsServiceRegister(advertiser._request, IntPtr.Zero);
        if (rc != DNS_REQUEST_PENDING)
        {
            advertiser.Dispose();
            throw new InvalidOperationException($"DnsServiceRegister({fullName}) returned {rc}");
        }

        var finished = await Task.WhenAny(advertiser._registered.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        if (finished != advertiser._registered.Task)
        {
            advertiser.Dispose();
            throw new TimeoutException($"mDNS registration of {fullName} timed out");
        }
        var status = advertiser._registered.Task.Result;
        if (status != 0)
        {
            advertiser.Dispose();
            throw new InvalidOperationException($"mDNS registration of {fullName} failed with status {status}");
        }
        return advertiser;
    }

    public string ServiceName => _serviceName;

    // Windows calls this once when registration finishes and again when deregistration finishes.
    private void OnComplete(uint status, IntPtr context, IntPtr instance)
    {
        if (instance != IntPtr.Zero)
            DnsServiceFreeInstance(instance);
        if (!_registered.TrySetResult(status))
            _deregistered.TrySetResult(status);
    }

    /// <summary>
    /// Withdraws the service: Windows multicasts a "goodbye" (TTL 0) so iPhones drop it from
    /// their list at once. If the process dies without this, iPhones keep showing it until
    /// their cache expires.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_request != IntPtr.Zero)
        {
            var wasRegistered = _registered.Task.IsCompletedSuccessfully && _registered.Task.Result == 0;
            if (wasRegistered && DnsServiceDeRegister(_request, IntPtr.Zero) == DNS_REQUEST_PENDING)
                _deregistered.Task.Wait(TimeSpan.FromSeconds(2)); // the native side still uses _request until then
            Marshal.FreeHGlobal(_request);
            _request = IntPtr.Zero;
        }
        if (_instance != IntPtr.Zero)
        {
            DnsServiceFreeInstance(_instance);
            _instance = IntPtr.Zero;
        }
        GC.KeepAlive(_callback);
    }
}
