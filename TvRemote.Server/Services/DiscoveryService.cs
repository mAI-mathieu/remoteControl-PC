using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace TvRemote.Services;
public sealed class DiscoveryService
{
    public static bool IsLan(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        var bytes = address.GetAddressBytes();
        return address.AddressFamily == AddressFamily.InterNetwork
            ? bytes[0] == 10 || bytes[0] == 192 && bytes[1] == 168 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31
            : address.IsIPv6LinkLocal || (bytes[0] & 0xFE) == 0xFC;
    }
    public IPAddress[] Addresses() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address)
        .Where(a => IsLan(a) && !a.IsIPv6LinkLocal).Distinct().ToArray();
    public string[] Urls(int port, bool secure = false) => Addresses().Select(a => $"{(secure ? "https" : "http")}://{(a.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{a}]" : a.ToString())}:{port}").ToArray();
}
