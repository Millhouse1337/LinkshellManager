using System.Net;
using System.Net.Sockets;

namespace LinkshellManagerDiscordApp.Utils;

// Reads one "ForwardedHeaders:KnownNetworks" entry such as "173.245.48.0/20" (Cloudflare's ranges).
//
// .NET 10 retired ASP.NET's own IPNetwork in favour of System.Net.IPNetwork. The new type refuses
// an address with bits set past the prefix ("10.0.0.1/8"), which the old one quietly accepted.
// Refusing would silently drop a trusted range from the server's env file, and then every visitor
// would look like they came from the proxy. So the extra bits are cleared first, which is exactly
// how the old type matched addresses anyway.
public static class TrustedNetworkParser
{
    public static bool TryParse(string? text, out IPNetwork network)
    {
        network = default;
        var parts = (text ?? string.Empty).Trim().Split('/', 2);
        if (parts.Length != 2 ||
            !IPAddress.TryParse(parts[0], out var address) ||
            !int.TryParse(parts[1], out var prefixLength))
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        var maxBits = address.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32;
        if (prefixLength < 0 || prefixLength > maxBits)
        {
            return false;
        }

        // Keep the first prefixLength bits, zero the rest.
        for (var i = 0; i < bytes.Length; i++)
        {
            var bitsKeptInByte = Math.Clamp(prefixLength - (i * 8), 0, 8);
            bytes[i] &= (byte)(0xFF << (8 - bitsKeptInByte));
        }

        network = new IPNetwork(new IPAddress(bytes), prefixLength);
        return true;
    }
}
