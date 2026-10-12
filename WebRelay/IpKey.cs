using System.Net;
using System.Net.Sockets;

namespace WebRelay;

/// <summary>
/// The key the relay's per-address limits (the connection budget and the account-creation cap) count under.
///
/// Review 2026-10-01 (S2): the limits used to key on the exact address string. One IPv6 subscriber controls a whole /64 (2^64 addresses) and can rotate through
/// them freely, so "8 concurrent / 20 per minute / 3 accounts an hour per address" was no limit at all for an IPv6 client. An IPv6 address is therefore keyed by its
/// /64 prefix (the allocation an ordinary home or mobile subscriber receives), and an IPv4-mapped IPv6 address (<c>::ffff:1.2.3.4</c>) is the IPv4 address it wraps.
/// IPv4 and anything that does not parse pass through unchanged, so every existing key is exactly what it was.
/// </summary>
internal static class IpKey
{
    internal static string Normalize(string ip)
    {
        if (!IPAddress.TryParse(ip, out IPAddress? address)) return ip;
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return ip;
        if (address.IsIPv4MappedToIPv6) return address.MapToIPv4().ToString();
        if (IPAddress.IsLoopback(address)) return ip; // ::1 is one machine, not a subscriber's prefix
        byte[] bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes).ToString() + "/64";
    }
}
