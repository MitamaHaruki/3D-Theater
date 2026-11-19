using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Theater3D.Tracking.Vmc;

/// <summary>この PC の IP（ほかの PC の 3D Theater VCamera に、送り先として入れてもらう。設計判断記録 98 章）。</summary>
public static class LocalNetwork
{
    /// <summary>
    /// 使っている IPv4 の住所（家の Wi-Fi・有線・USB4 のケーブル）。ケーブルで PC どうしを直につないだときの 169.254.x.x も入れる
    /// （ルーターが無いと、Windows がこの住所を付けるため）。ルーターにつながっている住所（ほかの PC から届く見込みが高い）を先に、
    /// 仮の網（WSL・Hyper-V など、ルーターの無いもの）を次に、169.254.x.x を最後に並べる。読めなければ空。
    /// </summary>
    public static IReadOnlyList<string> Addresses()
    {
        try
        {
            return [.. NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n =>
                {
                    IPInterfaceProperties properties = n.GetIPProperties();
                    bool routed = properties.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                    return properties.UnicastAddresses.Select(a => (a.Address, Routed: routed));
                })
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address))
                .OrderBy(a => IsLinkLocal(a.Address) ? 2 : a.Routed ? 0 : 1)
                .Select(a => a.Address.ToString())
                .Distinct()];
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }

    /// <summary>ケーブルで直につないだときの住所（169.254.x.x）か。</summary>
    public static bool IsLinkLocal(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        byte[] b = address.GetAddressBytes();
        return b.Length == 4 && b[0] == 169 && b[1] == 254;
    }
}
