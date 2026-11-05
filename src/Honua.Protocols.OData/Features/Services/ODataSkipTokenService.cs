// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Honua.Protocols.OData.Services;

internal static class ODataSkipTokenService
{
    private const char Separator = '|';
    private const int FingerprintLength = 8;

    public static string Encode(int offset, string? filter, string? orderby, string requestDiscriminator, string? bbox = null)
    {
        ArgumentNullException.ThrowIfNull(requestDiscriminator);
        var fingerprint = ComputeFingerprint(filter, orderby, requestDiscriminator, bbox);
        var payload = string.Create(
            CultureInfo.InvariantCulture,
            $"{offset}{Separator}{fingerprint}");
        return Base64UrlEncode(payload);
    }

    public static bool TryDecode(
        string token,
        string? filter,
        string? orderby,
        string requestDiscriminator,
        out int offset,
        out string? errorMessage,
        string? bbox = null)
    {
        ArgumentNullException.ThrowIfNull(requestDiscriminator);
        offset = 0;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(token))
        {
            errorMessage = "$skiptoken value is required.";
            return false;
        }

        // Support legacy integer-only tokens for backward compatibility, but ONLY when the
        // request carries no $filter/$orderby. A bare integer has no embedded fingerprint, so
        // accepting it for a filtered/ordered query lets a token minted for one query be reused
        // as a raw offset against a different one — defeating the cross-query-reuse guard the
        // opaque tokens enforce (#1989). When a filter or orderby is present, require the
        // fingerprinted opaque token.
        if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var legacyOffset))
        {
            if (legacyOffset < 0)
            {
                errorMessage = "$skiptoken offset must not be negative.";
                return false;
            }

            if (!string.IsNullOrEmpty(filter) || !string.IsNullOrEmpty(orderby) || !string.IsNullOrEmpty(bbox))
            {
                errorMessage = "$skiptoken is not valid for this query. The token may have been generated for a different filter, orderby clause, or bbox.";
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

        var expectedFingerprint = ComputeFingerprint(filter, orderby, requestDiscriminator, bbox);
        if (!string.Equals(fingerprintPart, expectedFingerprint, StringComparison.Ordinal))
        {
            errorMessage = "$skiptoken is not valid for this request. The token may have been generated for a different filter, orderby clause, bbox, tenant, or principal.";
            return false;
        }

        offset = parsedOffset;
        return true;
    }

    private static string ComputeFingerprint(string? filter, string? orderby, string requestDiscriminator, string? bbox)
    {
        var input = $"{filter ?? string.Empty}:{orderby ?? string.Empty}:{requestDiscriminator}";
        if (!string.IsNullOrEmpty(bbox))
        {
            input += $":bbox:{bbox.Length}:{bbox}";
        }
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
