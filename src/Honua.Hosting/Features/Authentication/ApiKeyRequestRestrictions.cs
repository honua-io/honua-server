// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Sockets;

namespace Honua.Infrastructure.Authentication;

internal sealed record ApiKeyRequestContext(string? Origin, string? Referer, IPAddress? ClientIp)
{
    public static ApiKeyRequestContext From(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new ApiKeyRequestContext(
            context.Request.Headers.Origin.FirstOrDefault(),
            context.Request.Headers.Referer.FirstOrDefault(),
            context.Connection.RemoteIpAddress);
    }
}

internal enum ApiKeyRestrictionViolation
{
    None,

    Referrer,

    IpRange,
}

// A referrer list refuses a request with neither Origin nor Referer, a leading *. matches only a subdomain, and those headers are not a secrecy control because any client can set them.
internal static class ApiKeyRequestRestrictions
{
    internal const int MaxEntries = 50;

    internal const int MaxEntryLength = 255;

    private const string WildcardLabel = "*.";

    public static bool IsRestricted(AdminApiKeyRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return record.AllowedReferrers is { Count: > 0 } || record.AllowedIpRanges is { Count: > 0 };
    }

    public static ApiKeyRestrictionViolation Evaluate(AdminApiKeyRecord record, ApiKeyRequestContext? request)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (record.AllowedReferrers is { Count: > 0 } referrers &&
            !IsReferrerAllowed(referrers, request?.Origin, request?.Referer))
        {
            return ApiKeyRestrictionViolation.Referrer;
        }

        if (record.AllowedIpRanges is { Count: > 0 } ranges && !IsIpAllowed(ranges, request?.ClientIp))
        {
            return ApiKeyRestrictionViolation.IpRange;
        }

        return ApiKeyRestrictionViolation.None;
    }

    public static bool IsReferrerAllowed(IReadOnlyList<string>? patterns, string? origin, string? referer)
    {
        if (patterns is not { Count: > 0 })
        {
            return true;
        }

        var requestOrigin = TryReadOrigin(origin) ?? TryReadOrigin(referer);
        if (requestOrigin is null)
        {
            return false;
        }

        foreach (var pattern in patterns)
        {
            if (TryParsePattern(pattern, out var allowed) && allowed.Matches(requestOrigin))
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsIpAllowed(IReadOnlyList<string>? ranges, IPAddress? clientIp)
    {
        if (ranges is not { Count: > 0 })
        {
            return true;
        }

        if (clientIp is null)
        {
            return false;
        }

        // Dual-stack sockets report IPv4 peers as ::ffff:a.b.c.d; compare them as IPv4.
        var address = clientIp.IsIPv4MappedToIPv6 ? clientIp.MapToIPv4() : clientIp;
        foreach (var range in ranges)
        {
            if (TryParseIpRange(range, out var network) &&
                network.BaseAddress.AddressFamily == address.AddressFamily &&
                network.Contains(address))
            {
                return true;
            }
        }

        return false;
    }

    public static bool TryNormalizeReferrerPattern(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (!TryParsePattern(value, out var pattern))
        {
            return false;
        }

        normalized = pattern.ToString();
        return true;
    }

    // A CIDR whose address has bits past the prefix is refused so a typo cannot widen the range.
    public static bool TryNormalizeIpRange(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (!TryParseIpRange(value, out var network))
        {
            return false;
        }

        normalized = network.ToString();
        return true;
    }

    private static bool TryParseIpRange(string? value, out IPNetwork network)
    {
        network = default;
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxEntryLength)
        {
            return false;
        }

        var trimmed = value.Trim();
        if (!trimmed.Contains('/', StringComparison.Ordinal))
        {
            if (!IPAddress.TryParse(trimmed, out var single) ||
                (single.AddressFamily == AddressFamily.InterNetworkV6 && single.ScopeId != 0))
            {
                return false;
            }

            single = single.IsIPv4MappedToIPv6 ? single.MapToIPv4() : single;
            network = new IPNetwork(single, single.AddressFamily == AddressFamily.InterNetwork ? 32 : 128);
            return true;
        }

        var slash = trimmed.IndexOf('/', StringComparison.Ordinal);
        if (!IPAddress.TryParse(trimmed[..slash], out var written) ||
            !IPNetwork.TryParse(trimmed, out network) ||
            !written.Equals(network.BaseAddress))
        {
            // Bits set beyond the prefix length are refused rather than masked away.
            network = default;
            return false;
        }

        if (network.BaseAddress.IsIPv4MappedToIPv6)
        {
            // An IPv4-mapped CIDR would never match: client addresses are compared as IPv4.
            network = default;
            return false;
        }

        return true;
    }

    private static Origin? TryReadOrigin(string? header)
    {
        if (string.IsNullOrWhiteSpace(header) ||
            string.Equals(header.Trim(), "null", StringComparison.OrdinalIgnoreCase) ||
            !Uri.TryCreate(header.Trim(), UriKind.Absolute, out var uri) ||
            !IsWebScheme(uri.Scheme) ||
            string.IsNullOrEmpty(uri.Host))
        {
            return null;
        }

        return new Origin(uri.Scheme.ToLowerInvariant(), uri.IdnHost.ToLowerInvariant(), uri.Port, Wildcard: false);
    }

    private static bool TryParsePattern(string? value, out Origin pattern)
    {
        pattern = default!;
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxEntryLength)
        {
            return false;
        }

        var trimmed = value.Trim();
        var schemeEnd = trimmed.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd <= 0)
        {
            return false;
        }

        var scheme = trimmed[..schemeEnd];
        var rest = trimmed[(schemeEnd + 3)..];
        var wildcard = rest.StartsWith(WildcardLabel, StringComparison.Ordinal);
        var candidate = wildcard ? $"{scheme}://wildcard-label.{rest[WildcardLabel.Length..]}" : trimmed;

        // An origin pattern carries no path, query, fragment or credentials. A bare trailing
        // slash is tolerated because browsers and operators commonly paste one.
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
            !IsWebScheme(uri.Scheme) ||
            string.IsNullOrEmpty(uri.Host) ||
            uri.HostNameType is not (UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            uri.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            (!wildcard && rest.Contains('*', StringComparison.Ordinal)) ||
            (wildcard && (rest[WildcardLabel.Length..].Contains('*', StringComparison.Ordinal) || uri.HostNameType != UriHostNameType.Dns)))
        {
            return false;
        }

        var host = uri.IdnHost.ToLowerInvariant();
        if (wildcard)
        {
            host = host["wildcard-label.".Length..];
            // A wildcard over a single label ("*.com") would admit every site under a TLD.
            if (!host.Contains('.', StringComparison.Ordinal))
            {
                return false;
            }
        }

        pattern = new Origin(uri.Scheme.ToLowerInvariant(), host, uri.Port, wildcard);
        return true;
    }

    private static bool IsWebScheme(string scheme)
        => string.Equals(scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
           string.Equals(scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);

    private sealed record Origin(string Scheme, string Host, int Port, bool Wildcard)
    {
        public bool Matches(Origin request)
        {
            if (!string.Equals(Scheme, request.Scheme, StringComparison.Ordinal) || Port != request.Port)
            {
                return false;
            }

            return Wildcard
                ? request.Host.Length > Host.Length + 1 &&
                  request.Host.EndsWith("." + Host, StringComparison.Ordinal)
                : string.Equals(Host, request.Host, StringComparison.Ordinal);
        }

        public override string ToString()
        {
            var isDefaultPort = (Scheme == Uri.UriSchemeHttps && Port == 443) || (Scheme == Uri.UriSchemeHttp && Port == 80);
            var host = Host.Contains(':', StringComparison.Ordinal) ? $"[{Host}]" : Host;
            return $"{Scheme}://{(Wildcard ? WildcardLabel : string.Empty)}{host}{(isDefaultPort ? string.Empty : ":" + Port)}";
        }
    }
}
