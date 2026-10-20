using System.Runtime.InteropServices;

namespace AirPlay.Core.Discovery;

/// <summary>
/// P/Invoke for the DNS-SD (mDNS) registration API built into Windows 10+ (dnsapi.dll),
/// so no Bonjour install is needed. See windns.h.
/// </summary>
internal static class DnsSdNative
{
    public const uint DNS_QUERY_REQUEST_VERSION1 = 1;
    public const uint DNS_REQUEST_PENDING = 9506;

    [StructLayout(LayoutKind.Sequential)]
    public struct DNS_SERVICE_REGISTER_REQUEST
    {
        public uint Version;
        public uint InterfaceIndex;
        public IntPtr pServiceInstance;
        public IntPtr pRegisterCompletionCallback;
        public IntPtr pQueryContext;
        public IntPtr hCredentials;
        public int unicastEnabled; // BOOL
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate void RegisterComplete(uint status, IntPtr queryContext, IntPtr instance);

    [DllImport("dnsapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern IntPtr DnsServiceConstructInstance(
        string pServiceName,
        string pHostName,
        IntPtr pIp4,
        IntPtr pIp6,
        ushort wPort,
        ushort wPriority,
        ushort wWeight,
        uint dwPropertiesCount,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[] keys,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[] values);

    [DllImport("dnsapi.dll", ExactSpelling = true)]
    public static extern void DnsServiceFreeInstance(IntPtr pInstance);

    [DllImport("dnsapi.dll", ExactSpelling = true)]
    public static extern uint DnsServiceRegister(IntPtr pRequest, IntPtr pCancel);

    [DllImport("dnsapi.dll", ExactSpelling = true)]
    public static extern uint DnsServiceDeRegister(IntPtr pRequest, IntPtr pCancel);
}
