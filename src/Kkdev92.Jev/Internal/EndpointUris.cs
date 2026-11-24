using System.Net;

namespace Kkdev92.Jev.Internal;

/// <summary>Validates the spelling before Uri canonicalization can discard routing information.</summary>
internal static class EndpointUris
{
    public static Uri Directory(JevClientOptions options)
    {
        var allowed = options.AllowedInsecureHttpOrigins
            ?? throw Invalid(nameof(options.AllowedInsecureHttpOrigins));
        var address = options.BaseAddress;
        var host = Validate(address, nameof(options.BaseAddress), originOnly: false);
        bool permitted = address.Scheme == Uri.UriSchemeHttps
            || (options.AllowLoopbackHttp && host is "localhost" or "127.0.0.1" or "::1");

        // The endpoints are fixed at construction. Only the result of this membership check
        // survives, so later edits to the supplied collection cannot change destination policy.
        foreach (var origin in allowed)
        {
            var allowedHost = Validate(origin, nameof(options.AllowedInsecureHttpOrigins), originOnly: true);
            permitted |= address.Scheme == Uri.UriSchemeHttp && origin.Port == address.Port && host == allowedHost;
        }

        if (!permitted)
        {
            throw Invalid(nameof(options.BaseAddress));
        }

        var spelling = address.OriginalString;
        return new Uri(spelling.EndsWith('/') ? spelling : spelling + "/", UriKind.Absolute);
    }

    public static Uri Join(Uri directory, string endpoint)
    {
        var result = new Uri(directory, endpoint.TrimStart('/'));
        if (directory.Scheme != result.Scheme || directory.IdnHost != result.IdnHost || directory.Port != result.Port)
        {
            throw new InvalidOperationException("The endpoint must preserve the configured origin.");
        }

        return result;
    }

    private static string Validate(Uri? uri, string option, bool originOnly)
    {
        if (uri is null || !uri.IsAbsoluteUri || (uri.Scheme != "http" && (originOnly || uri.Scheme != "https")))
        {
            throw Invalid(option);
        }

        var raw = uri.OriginalString;
        if (!raw.StartsWith(uri.Scheme + "://", StringComparison.OrdinalIgnoreCase)
            || raw.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '\\' or '?' or '#'))
        {
            throw Invalid(option);
        }

        var start = uri.Scheme.Length + 3;
        var slash = raw.IndexOf('/', start);
        var authority = slash < 0 ? raw[start..] : raw[start..slash];
        if (authority.Length == 0 || authority.Contains('@') || authority.EndsWith(':'))
        {
            throw Invalid(option);
        }

        if (slash >= 0)
        {
            if (originOnly && slash != raw.Length - 1)
            {
                throw Invalid(option);
            }

            foreach (var segment in raw[slash..].Split('/'))
            {
                var decoded = Uri.UnescapeDataString(segment);
                if (decoded is "." or ".." || decoded.Contains('/') || decoded.Contains('\\'))
                {
                    throw Invalid(option);
                }
            }
        }

        var bracketed = authority.StartsWith('[');
        var end = bracketed ? authority.IndexOf(']') + 1 : authority.IndexOf(':');
        if (end < 0)
        {
            end = authority.Length;
        }

        if (end == 0 || (end < authority.Length && (authority[end] != ':' || !authority[(end + 1)..].All(char.IsAsciiDigit))))
        {
            throw Invalid(option);
        }

        if (uri.Scheme == Uri.UriSchemeHttps)
        {
            return uri.IdnHost;
        }

        var rawHost = authority[..end];
        if (bracketed)
        {
            if (rawHost.Contains('%') || !IPAddress.TryParse(rawHost, out var ip) || ip.Equals(IPAddress.IPv6Any))
            {
                throw Invalid(option);
            }

            return ip.ToString();
        }

        if (!rawHost.All(char.IsAscii) || !string.Equals(rawHost, uri.IdnHost, StringComparison.OrdinalIgnoreCase))
        {
            throw Invalid(option);
        }

        if (IPAddress.TryParse(rawHost, out var ipv4))
        {
            if (ipv4.Equals(IPAddress.Any) || rawHost != ipv4.ToString())
            {
                throw Invalid(option);
            }
        }
        else if (Uri.CheckHostName(rawHost) != UriHostNameType.Dns || rawHost.Contains('*'))
        {
            throw Invalid(option);
        }

        return rawHost.ToLowerInvariant();
    }

    private static ArgumentException Invalid(string option) =>
        new($"JevClientOptions.{option} requires a valid permitted endpoint.", option);
}
