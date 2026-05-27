// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using Honua.Core.Features.Publishing.Content.Domain;

namespace Honua.Core.Features.Publishing.Content.Services;

/// <summary>
/// Crypto helpers for the content publication registry: content hashing, public-link
/// token hashing (never stores raw tokens), constant-time token comparison, and
/// opaque etag generation.
/// </summary>
public static class ContentPublicationCrypto
{
    /// <summary>Algorithm label recorded alongside a public-link token hash.</summary>
    public const string TokenHashAlgorithm = "SHA-256";

    /// <summary>Returns the lowercase hex SHA-256 of a content payload.</summary>
    public static string Sha256Hex(string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>Returns the base64 SHA-256 of a public-link token. The raw token is never stored.</summary>
    public static string HashToken(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }

    /// <summary>Constant-time comparison of a presented token against a stored base64 hash.</summary>
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

    /// <summary>Generates a fresh opaque, quoted etag for optimistic concurrency.</summary>
    public static string NewEtag() => "\"" + Guid.NewGuid().ToString("N") + "\"";
}

/// <summary>
/// Authorizes a public-link read by comparing only stable ids and token hashes.
/// Never compares or returns raw tokens.
/// </summary>
public static class ContentPublicLinkVerifier
{
    /// <summary>
    /// Returns <see langword="true"/> when the presented link id (and optional token)
    /// authorizes a read under the supplied policy at the given time.
    /// </summary>
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

        ContentPublicLink? link = null;
        foreach (var candidate in policy.Links)
        {
            if (string.Equals(candidate.LinkId, linkId, StringComparison.Ordinal))
            {
                link = candidate;
                break;
            }
        }

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
