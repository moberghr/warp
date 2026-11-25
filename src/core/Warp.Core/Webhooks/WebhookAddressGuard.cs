using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace Warp.Core.Webhooks;

/// <summary>
/// Classifies IP addresses as publicly routable or not, and (when
/// <see cref="WarpWebhookOptions.PublicAddressesOnly"/> is on) connects the <c>warp-webhooks</c> client only
/// to public addresses. The ranges come from the IANA IPv4 and IPv6 special-purpose address registries
/// (RFC 6890 and its successors): every block there that is not globally reachable is refused, plus
/// documentation, benchmarking, multicast and reserved space. It is deliberately conservative: the whole of
/// <c>192.0.0.0/24</c> and <c>2001::/23</c> is refused, including the few protocol anycast addresses the
/// registries mark globally reachable there (for example <c>192.0.0.9</c>, <c>2001:4:112::/48</c>), and in
/// IPv6 only allocated global unicast (<c>2000::/3</c>) is accepted.
/// <para>
/// The check runs inside the connection itself (<see cref="SocketsHttpHandler.ConnectCallback"/>): the
/// host name is resolved once, non-public addresses are dropped, and the socket connects to exactly the
/// addresses that passed. The address checked is the address connected to, so a DNS answer that changes
/// between a check and the connect (DNS rebinding) cannot slip through, and redirects open their
/// connections through the same callback.
/// </para>
/// </summary>
public static class WebhookAddressGuard
{
    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="address"/> is a publicly routable unicast address.
    /// IPv4-mapped IPv6 (<c>::ffff:0:0/96</c>), NAT64 (<c>64:ff9b::/96</c>) and 6to4 (<c>2002::/16</c>)
    /// addresses are judged by the IPv4 address they embed.
    /// </summary>
    public static bool IsPublic(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => IsPublicIPv4(address.GetAddressBytes()),
            AddressFamily.InterNetworkV6 => IsPublicIPv6(address.GetAddressBytes()),
            _ => false,
        };
    }

    // The SocketsHttpHandler.ConnectCallback installed when PublicAddressesOnly is on. With an HTTP proxy
    // configured, the handler connects to the PROXY through this callback, so the proxy's address is what
    // gets checked and the proxy itself must enforce the policy for the final destination.
    internal static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var endPoint = context.DnsEndPoint;
        var resolved = IPAddress.TryParse(endPoint.Host, out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(endPoint.Host, cancellationToken);

        var allowed = resolved
            .Where(IsPublic)
            .Where(x => Socket.OSSupportsIPv6 || x.AddressFamily == AddressFamily.InterNetwork)
            .ToArray();

        if (allowed.Length == 0)
        {
            // The message names the host only, never the resolved addresses (internal topology).
            throw new WebhookAddressNotPublicException($"Webhook host '{endPoint.Host}' does not resolve to a public address.");
        }

        var socket = Socket.OSSupportsIPv6
            ? new Socket(SocketType.Stream, ProtocolType.Tcp)
            : new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

        try
        {
            socket.NoDelay = true;
            await socket.ConnectAsync(allowed, endPoint.Port, cancellationToken);

            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();

            throw;
        }
    }

    private static bool IsPublicIPv4(ReadOnlySpan<byte> b)
    {
        return b[0] switch
        {
            0 => false,                                         // 0.0.0.0/8 "this network"
            10 => false,                                        // 10.0.0.0/8 private
            100 when (b[1] & 0xC0) == 64 => false,              // 100.64.0.0/10 shared address space (CGN)
            127 => false,                                       // 127.0.0.0/8 loopback
            169 when b[1] == 254 => false,                      // 169.254.0.0/16 link-local (cloud metadata)
            172 when (b[1] & 0xF0) == 16 => false,              // 172.16.0.0/12 private
            192 when b[1] == 0 && b[2] == 0 => false,           // 192.0.0.0/24 IETF protocol assignments
            192 when b[1] == 0 && b[2] == 2 => false,           // 192.0.2.0/24 TEST-NET-1
            192 when b[1] == 88 && b[2] == 99 => false,         // 192.88.99.0/24 deprecated 6to4 relay anycast
            192 when b[1] == 168 => false,                      // 192.168.0.0/16 private
            198 when (b[1] & 0xFE) == 18 => false,              // 198.18.0.0/15 benchmarking
            198 when b[1] == 51 && b[2] == 100 => false,        // 198.51.100.0/24 TEST-NET-2
            203 when b[1] == 0 && b[2] == 113 => false,         // 203.0.113.0/24 TEST-NET-3
            >= 224 => false,                                    // 224/4 multicast, 240/4 reserved, broadcast
            _ => true,
        };
    }

    private static bool IsPublicIPv6(ReadOnlySpan<byte> b)
    {
        // ::ffff:0:0/96 IPv4-mapped: judged by the embedded IPv4 address.
        if (IsZero(b[..10]) && b[10] == 0xFF && b[11] == 0xFF)
        {
            return IsPublicIPv4(b[12..]);
        }

        // 64:ff9b::/96 well-known NAT64 prefix: judged by the embedded IPv4 address.
        if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B && IsZero(b[4..12]))
        {
            return IsPublicIPv4(b[12..]);
        }

        // 2002::/16 6to4: judged by the IPv4 address in bits 16-47.
        if (b[0] == 0x20 && b[1] == 0x02)
        {
            return IsPublicIPv4(b[2..6]);
        }

        // Only 2000::/3 is allocated global unicast. Everything outside it is refused, which covers
        // :: and ::1, the rest of ::/8, 64:ff9b:1::/48 (local-use NAT64), 100::/64 (discard),
        // fc00::/7 (unique local), fe80::/10 (link-local), fec0::/10 (site-local) and ff00::/8 (multicast).
        if ((b[0] & 0xE0) != 0x20)
        {
            return false;
        }

        // Special-purpose blocks inside 2000::/3.
        if (b[0] == 0x20 && b[1] == 0x01 && b[2] < 0x02)
        {
            return false;                                       // 2001::/23 IETF protocol assignments (incl. Teredo)
        }

        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0D && b[3] == 0xB8)
        {
            return false;                                       // 2001:db8::/32 documentation
        }

        if (b[0] == 0x3F && b[1] == 0xFF && (b[2] & 0xF0) == 0x00)
        {
            return false;                                       // 3fff::/20 documentation
        }

        return true;
    }

    private static bool IsZero(ReadOnlySpan<byte> bytes) => !bytes.ContainsAnyExcept((byte)0);
}
