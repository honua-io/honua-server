// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Honua.Server.Features.Protocols.OData.Services;

/// <summary>
/// Provides encoding and decoding of opaque OData $skiptoken cursors.
/// Tokens encode the current offset and a query fingerprint to prevent reuse across different queries.
/// </summary>
internal static class ODataSkipTokenService
{
    private const char Separator = '|';
    private const int FingerprintLength = 8;

    /// <summary>
    /// Encodes a skip offset and query context into an opaque Base64Url token.
    /// </summary>
    /// <param name="offset">The pagination offset.</param>
    /// <param name="filter">The $filter query parameter (may be null).</param>
    /// <param name="orderby">The $orderby query parameter (may be null).</param>
    /// <returns>A Base64Url-encoded opaque cursor string.</returns>
    public static string Encode(int offset, string? filter, string? orderby)
    {
        var fingerprint = ComputeFingerprint(filter, orderby);
        var payload = string.Create(
            CultureInfo.InvariantCulture,
            $"{offset}{Separator}{fingerprint}");
        return Base64UrlEncode(payload);
    }

    /// <summary>
    /// Attempts to decode an opaque $skiptoken into its offset, validating the query fingerprint.
    /// </summary>
    /// <param name="token">The opaque $skiptoken string.</param>
    /// <param name="filter">The $filter query parameter for fingerprint validation (may be null).</param>
    /// <param name="orderby">The $orderby query parameter for fingerprint validation (may be null).</param>
    /// <param name="offset">The decoded pagination offset.</param>
    /// <param name="errorMessage">An error message if decoding fails.</param>
    /// <returns>true if decoding succeeded; false otherwise.</returns>
    public static bool TryDecode(
        string token,
        string? filter,
        string? orderby,
        out int offset,
        out string? errorMessage)
    {
        offset = 0;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(token))
        {
            errorMessage = "$skiptoken value is required.";
            return false;
        }

        // Support legacy integer-only tokens for backward compatibility
        if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var legacyOffset))
        {
            if (legacyOffset < 0)
            {
                errorMessage = "$skiptoken offset must not be negative.";
                return false;
            }

            offset = legacyOffset;
            return true;
        }

        string payload;
        try
        {
            payload = Base64UrlDecode(token);
        }
        catch (FormatException)
        {
            errorMessage = "$skiptoken is invalid or malformed.";
            return false;
        }

        var separatorIndex = payload.IndexOf(Separator);
        if (separatorIndex < 0)
        {
            errorMessage = "$skiptoken is invalid or malformed.";
            return false;
        }

        var offsetPart = payload[..separatorIndex];
        var fingerprintPart = payload[(separatorIndex + 1)..];

        if (!int.TryParse(offsetPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedOffset))
        {
            errorMessage = "$skiptoken is invalid or malformed.";
            return false;
        }

        if (parsedOffset < 0)
        {
            errorMessage = "$skiptoken offset must not be negative.";
            return false;
        }

        var expectedFingerprint = ComputeFingerprint(filter, orderby);
        if (!string.Equals(fingerprintPart, expectedFingerprint, StringComparison.Ordinal))
        {
            errorMessage = "$skiptoken is not valid for this query. The token may have been generated for a different filter or orderby clause.";
            return false;
        }

        offset = parsedOffset;
        return true;
    }

    /// <summary>
    /// Computes a short hash fingerprint from the filter and orderby parameters.
    /// </summary>
    private static string ComputeFingerprint(string? filter, string? orderby)
    {
        var input = $"{filter ?? string.Empty}:{orderby ?? string.Empty}";
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hashBytes)[..FingerprintLength].ToLowerInvariant();
    }

    private static string Base64UrlEncode(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static string Base64UrlDecode(string input)
    {
        var padded = input.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2:
                padded += "==";
                break;
            case 3:
                padded += "=";
                break;
        }

        var bytes = Convert.FromBase64String(padded);
        return Encoding.UTF8.GetString(bytes);
    }
}
