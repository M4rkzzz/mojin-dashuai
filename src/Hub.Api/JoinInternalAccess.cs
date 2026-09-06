using System.Net;

namespace Boshan.Hub;

public static class JoinInternalAccess
{
    // Use the TCP peer, never a client-controlled proxy/Cloudflare header. Empty lists fail closed.
    public static bool IsAllowed(IPAddress? peer, string? networks)
    {
        if (peer is null || string.IsNullOrWhiteSpace(networks)) return false;
        if (peer.IsIPv4MappedToIPv6) peer = peer.MapToIPv4();
        foreach (var entry in networks.Split([',', ';', ' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (System.Net.IPNetwork.TryParse(entry, out var network) && network.Contains(peer)) return true;
        }
        return false;
    }
}
