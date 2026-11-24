using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Beacon.Services;

public static class NetworkHelper
{
    /// <summary>Returns the Tailscale IPv4 when available, otherwise the first LAN IPv4.</summary>
    public static string GetReachableAddress()
    {
        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                            n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .ToList();

            var tailscale = interfaces.FirstOrDefault(n =>
                n.Name.Contains("Tailscale", StringComparison.OrdinalIgnoreCase) ||
                n.Description.Contains("Tailscale", StringComparison.OrdinalIgnoreCase));
            var chosen = tailscale ?? interfaces.FirstOrDefault(n =>
                n.GetIPProperties().UnicastAddresses.Any(a => a.Address.AddressFamily == AddressFamily.InterNetwork));

            var address = chosen?.GetIPProperties().UnicastAddresses
                .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address;
            if (address is not null) return address.ToString();
        }
        catch { /* fall through */ }
        return "127.0.0.1";
    }

    public static bool IsValidHexColor(string? value) =>
        value is not null && System.Text.RegularExpressions.Regex.IsMatch(value.TrimStart('#'), "^[0-9a-fA-F]{6}$");
}
