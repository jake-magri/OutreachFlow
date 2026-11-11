using System.Net;
using System.Net.Sockets;

namespace OutreachFlow.Services.Research;

/// <summary>Guards read-only research against localhost/private-network and metadata requests.</summary>
public static class ResearchUrlPolicy
{
    public static Uri ParseWebsite(string input)
    {
        if (!Uri.TryCreate(input?.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) ||
            !string.IsNullOrEmpty(uri.UserInfo) || uri.HostNameType != UriHostNameType.Dns ||
            uri.Host.Length > 253 || input.Length > 2048 ||
            (uri.Port != 80 && uri.Port != 443))
            throw new InvalidOperationException("Enter a public http(s) company website (no credentials, IP addresses, or custom ports).");
        var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        if (!host.Contains('.') || host.EndsWith(".local", StringComparison.Ordinal) ||
            host.EndsWith(".localhost", StringComparison.Ordinal) ||
            host.EndsWith(".internal", StringComparison.Ordinal) ||
            host.EndsWith(".test", StringComparison.Ordinal) ||
            host.EndsWith(".example", StringComparison.Ordinal) ||
            host.EndsWith(".invalid", StringComparison.Ordinal) ||
            host is "localhost" or "metadata.google.internal" || host.StartsWith("metadata.", StringComparison.Ordinal))
            throw new InvalidOperationException("Research only supports publicly hosted company domains, not demo or internal websites.");
        return uri;
    }

    public static bool IsSameSite(Uri root, Uri proposed)
    {
        if ((proposed.Scheme != Uri.UriSchemeHttps && proposed.Scheme != Uri.UriSchemeHttp) ||
            !string.IsNullOrEmpty(proposed.UserInfo) || (proposed.Port != 80 && proposed.Port != 443)) return false;
        static string NormalizeHost(Uri uri) => uri.IdnHost.ToLowerInvariant().TrimEnd('.').TrimStart(' ');
        var source = NormalizeHost(root);
        var other = NormalizeHost(proposed);
        if (source.StartsWith("www.", StringComparison.Ordinal)) source = source[4..];
        if (other.StartsWith("www.", StringComparison.Ordinal)) other = other[4..];
        return source == other;
    }

    public static bool IsPublicAddress(IPAddress ip)
    {
        // Require globally routable addresses, checked *when connecting*, not in a separate DNS preflight.
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) ||
            ip.Equals(IPAddress.IPv6None) || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast)
            return false;
        var b = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var a = b[0]; var n = b[1];
            return a is >= 1 and <= 223 &&
                a != 10 && a != 127 && a != 0 &&
                !(a == 100 && n is >= 64 and <= 127) &&
                !(a == 169 && n == 254) &&
                !(a == 172 && n is >= 16 and <= 31) &&
                !(a == 192 && (n == 168 || n == 0 || n == 2 || (n == 88 && b[2] == 99))) &&
                !(a == 198 && (n == 18 || n == 19 || n == 51 && b[2] == 100)) &&
                !(a == 203 && n == 0 && b[2] == 113) &&
                !(a == 192 && n == 0 && b[2] == 2);
        }
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // Permit ordinary IPv6 global unicast only. Block IPv4-mapped, NAT64 and documentation prefix.
            return (b[0] & 0xE0) == 0x20 &&
                !(b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8);
        }
        return false;
    }
}
