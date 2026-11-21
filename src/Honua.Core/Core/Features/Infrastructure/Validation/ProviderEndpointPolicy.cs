// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Validation;

// Operator-configured provider endpoints receive bearer tokens or signed requests, so a typo or a
// hostile parameter must not redirect them to plain HTTP or a foreign host. Loopback stays allowed
// (HTTP or HTTPS) because emulators and test fakes listen there and the credential never leaves the host.
public static class ProviderEndpointPolicy
{
    public static string? Validate(string? url, IReadOnlyList<string> allowedHostSuffixes)
    {
        ArgumentNullException.ThrowIfNull(allowedHostSuffixes);

        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return "must be an absolute URL without embedded credentials.";
        }

        if (uri.IsLoopback && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return null;
        }

        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            return "must use https.";
        }

        return HasAllowedSuffix(uri.IdnHost.TrimEnd('.'), allowedHostSuffixes)
            ? null
            : $"host must end with one of: {string.Join(", ", allowedHostSuffixes)}.";
    }

    // Suffixes carry their leading dot, so a match always lands on a DNS label boundary and the
    // bare suffix itself (no account or service label) is rejected.
    private static bool HasAllowedSuffix(string host, IReadOnlyList<string> allowedHostSuffixes)
        => allowedHostSuffixes.Any(suffix =>
            host.Length > suffix.Length && host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
}
