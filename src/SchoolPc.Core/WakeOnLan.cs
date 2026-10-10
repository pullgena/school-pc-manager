using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace SchoolPc.Core;

public static class WakeOnLan
{
    public static byte[] ParseMac(string text)
    {
        string value = text.Trim().Replace(":", "").Replace("-", "");
        if (value.Length != 12 || !value.All(Uri.IsHexDigit)) throw new InvalidDataException("MAC 주소는 AA-BB-CC-DD-EE-FF 형식으로 입력해 주세요.");
        byte[] mac = Convert.FromHexString(value);
        if ((mac[0] & 1) != 0 || mac.All(x => x == 0)) throw new InvalidDataException("노트북의 실제 네트워크 어댑터 MAC 주소가 필요합니다.");
        return mac;
    }
    public static byte[] CreatePacket(string text)
    {
        byte[] mac = ParseMac(text), packet = new byte[102]; Array.Fill(packet, (byte)255, 0, 6);
        for (int i = 0; i < 16; i++) mac.CopyTo(packet, 6 + i * 6);
        return packet;
    }
    public static IPAddress Broadcast(IPAddress address, IPAddress mask)
    {
        byte[] ip = address.GetAddressBytes(), subnet = mask.GetAddressBytes();
        if (ip.Length != 4 || subnet.Length != 4) throw new InvalidDataException("IPv4 네트워크가 필요합니다.");
        return new IPAddress(ip.Zip(subnet, (a, b) => (byte)(a | (byte)~b)).ToArray());
    }
    public static async Task<int> SendAsync(string mac, CancellationToken ct)
    {
        byte[] packet = CreatePacket(mac); int sent = 0;
        var addresses = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !a.IPv4Mask.Equals(IPAddress.Any)).Take(32).ToArray();
        foreach (var address in addresses)
        {
            try
            {
                using var udp = new UdpClient(new IPEndPoint(address.Address, 0)) { EnableBroadcast = true };
                var endpoint = new IPEndPoint(Broadcast(address.Address, address.IPv4Mask), 9);
                for (int i = 0; i < 3; i++) { await udp.SendAsync(packet, endpoint, ct); sent++; await Task.Delay(50, ct); }
            }
            catch (SocketException) { /* Another local interface may still support broadcasting. */ }
        }
        if (sent == 0) throw new IOException("켜기 신호를 보낼 수 없습니다. 로컬 네트워크 연결을 확인해 주세요.");
        return sent;
    }
}
