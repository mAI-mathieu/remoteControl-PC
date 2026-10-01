using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using TvRemote.Configuration;

namespace TvRemote.Services;
// Best-effort IPv4 mDNS; LAN URLs remain the authoritative fallback.
public sealed class MdnsService(DiscoveryService discovery, IConfigStore store, ILogger<MdnsService> logger) : BackgroundService
{
    public string Status { get; private set; } = "Starting";
    private static readonly IPEndPoint Multicast = new(IPAddress.Parse("224.0.0.251"), 5353);
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var addresses = discovery.Addresses().Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToArray();
        if (addresses.Length == 0) { Status = "Unavailable — use LAN IP"; return; }
        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, 5353));
            udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
            foreach (var address in addresses) udp.JoinMulticastGroup(Multicast.Address, address);
            // Ask current owners first. Incoming conflicting answers disable the alias.
            var probe = Query();
            for (var i = 0; i < 3; i++)
            {
                foreach (var address in addresses) { udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, address.GetAddressBytes()); await udp.SendAsync(probe, Multicast, stoppingToken); }
                await Task.Delay(250, stoppingToken);
            }
            Status = "Available: tvpc.local (best effort)";
            var lastResponse = 0L;
            while (!stoppingToken.IsCancellationRequested)
            {
                var received = await udp.ReceiveAsync(stoppingToken);
                if (!DiscoveryService.IsLan(received.RemoteEndPoint.Address) || received.Buffer.Length is < 12 or > 4096) continue;
                try
                {
                    var data = received.Buffer;
                    var offset = 12; var questions = Read16(data, 4); var relevant = false;
                    if (questions > 32) continue;
                    for (var i = 0; i < questions; i++)
                    {
                        var name = ReadName(data, ref offset); var type = Read16(data, offset); offset += 4;
                        relevant |= name is "tvpc.local" or "_http._tcp.local" or "tv remote._http._tcp.local" && type is 1 or 12 or 16 or 33 or 255;
                    }
                    if ((data[2] & 0x80) != 0)
                    {
                        var records = Read16(data, 6) + Read16(data, 8) + Read16(data, 10);
                        if (records > 64) continue;
                        for (var i = 0; i < records; i++)
                        {
                            var name = ReadName(data, ref offset); var type = Read16(data, offset); var length = Read16(data, offset + 8); offset += 10;
                            if (name == "tvpc.local" && type == 1 && length == 4 && !addresses.Contains(new IPAddress(data.AsSpan(offset, 4))))
                            { Status = "Name conflict — use LAN IP"; logger.LogWarning("mDNS tvpc.local name conflict; alias disabled"); return; }
                            offset += length;
                        }
                    }
                    else if (relevant && Environment.TickCount64 - lastResponse >= 100)
                    {
                        lastResponse = Environment.TickCount64;
                        var response = BuildResponse(addresses, store.Current.ServerPort);
                        foreach (var address in addresses)
                        {
                            udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, address.GetAddressBytes());
                            await udp.SendAsync(response, Multicast, stoppingToken);
                        }
                    }
                }
                catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or FormatException) { }
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) { Status = "Unavailable — use LAN IP"; logger.LogWarning("mDNS unavailable; LAN IP addresses remain usable"); }
    }
    public static byte[] BuildResponse(IPAddress[] addresses, int port)
    {
        using var stream = new MemoryStream();
        void U16(int value) { stream.WriteByte((byte)(value >> 8)); stream.WriteByte((byte)value); }
        void U32(uint value) { stream.WriteByte((byte)(value >> 24)); stream.WriteByte((byte)(value >> 16)); stream.WriteByte((byte)(value >> 8)); stream.WriteByte((byte)value); }
        void Record(string name, int type, byte[] data, bool shared = false)
        { stream.Write(Name(name)); U16(type); U16(shared ? 1 : 0x8001); U32(120); U16(data.Length); stream.Write(data); }
        U16(0); U16(0x8400); U16(0); U16(addresses.Length + 3); U16(0); U16(0);
        foreach (var address in addresses) Record("tvpc.local", address.AddressFamily == AddressFamily.InterNetwork ? 1 : 28, address.GetAddressBytes());
        Record("_http._tcp.local", 12, Name("TV Remote._http._tcp.local"), true);
        Record("TV Remote._http._tcp.local", 33, new byte[] { 0, 0, 0, 0, (byte)(port >> 8), (byte)port }.Concat(Name("tvpc.local")).ToArray());
        Record("TV Remote._http._tcp.local", 16, new byte[] { 6 }.Concat(Encoding.ASCII.GetBytes("path=/")).ToArray());
        return stream.ToArray();
    }
    private static byte[] Query() => new byte[] { 0,0,0,0,0,1,0,0,0,0,0,0 }.Concat(Name("tvpc.local")).Concat(new byte[] { 0,255,0,1 }).ToArray();
    private static byte[] Name(string name) => name.Split('.').SelectMany(label => new byte[] { (byte)label.Length }.Concat(Encoding.UTF8.GetBytes(label))).Append((byte)0).ToArray();
    private static int Read16(byte[] data, int offset) => BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2));
    private static string ReadName(byte[] data, ref int offset)
    {
        var labels = new List<string>(); var position = offset; var jumped = false;
        for (var hops = 0; hops < 64; hops++)
        {
            var length = data[position++];
            if (length == 0) { if (!jumped) offset = position; return string.Join('.', labels).ToLowerInvariant(); }
            if ((length & 0xC0) == 0xC0) { if (!jumped) offset = position + 1; position = ((length & 0x3F) << 8) | data[position]; jumped = true; continue; }
            if (length > 63) throw new FormatException();
            labels.Add(Encoding.UTF8.GetString(data, position, length)); position += length;
        }
        throw new FormatException();
    }
}
