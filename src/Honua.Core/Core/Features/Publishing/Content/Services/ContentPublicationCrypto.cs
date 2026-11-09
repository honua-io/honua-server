// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using Honua.Core.Features.Publishing.Content.Domain;

namespace Honua.Core.Features.Publishing.Content.Services;

public static class ContentPublicationCrypto
{
    public const string TokenHashAlgorithm = "SHA-256";

    public static string Sha256Hex(string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string HashToken(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }

    public static bool TokenMatchesHash(string token, string expectedBase64Hash)
    {
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(expectedBase64Hash))
        {
            return false;
        }

        byte[] expected;
        try
        {
            expected = Convert.FromBase64String(expectedBase64Hash);
        }
        catch (FormatException)
        {
            return false;
        }

        var actual = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    public static string NewEtag() => "\"" + Guid.NewGuid().ToString("N") + "\"";
}

// A public-link read compares stable ids and token hashes and never compares or returns the raw token.
public static class ContentPublicLinkVerifier
{
    public static bool TryAuthorize(
        ContentPublicLinkPolicy? policy,
        string? linkId,
        string? token,
        DateTimeOffset now)
    {
        if (policy is null || !policy.Enabled || string.IsNullOrWhiteSpace(linkId))
        {
            return false;
        }

        var link = policy.Links.FirstOrDefault(candidate => string.Equals(candidate.LinkId, linkId, StringComparison.Ordinal));

        if (link is null || link.Revoked)
        {
            return false;
        }

        if (link.ExpiresAt is { } expiresAt && expiresAt <= now)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(link.TokenHash))
        {
            return !string.IsNullOrEmpty(token)
                && ContentPublicationCrypto.TokenMatchesHash(token!, link.TokenHash!);
        }

        // No token configured for this link: a valid, non-revoked, non-expired id authorizes.
        return true;
    }
}
