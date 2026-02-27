// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;

namespace Honua.Server.Features.OData.Services;

/// <summary>
/// Provides encoding and decoding of OData $deltatoken values for change tracking.
/// Tokens encode a UTC timestamp and layer ID to support incremental data retrieval.
/// </summary>
internal static class ODataDeltaService
{
    private const char Separator = '|';

    /// <summary>
    /// Encodes a delta token from a UTC timestamp and layer ID.
    /// </summary>
    /// <param name="timestamp">The UTC timestamp marking the snapshot point.</param>
    /// <param name="layerId">The layer ID the token is scoped to.</param>
    /// <returns>A Base64Url-encoded delta token string.</returns>
    public static string Encode(DateTimeOffset timestamp, int layerId)
    {
        var ticks = timestamp.UtcTicks.ToString(CultureInfo.InvariantCulture);
        var payload = $"{ticks}{Separator}{layerId.ToString(CultureInfo.InvariantCulture)}";
        return Base64UrlEncode(payload);
    }

    /// <summary>
    /// Attempts to decode a $deltatoken into its timestamp and layer ID components.
    /// </summary>
    /// <param name="token">The opaque $deltatoken string.</param>
    /// <param name="timestamp">The decoded UTC timestamp.</param>
    /// <param name="layerId">The decoded layer ID.</param>
    /// <param name="errorMessage">An error message if decoding fails.</param>
    /// <returns>true if decoding succeeded; false otherwise.</returns>
    public static bool TryDecode(
        string token,
        out DateTimeOffset timestamp,
        out int layerId,
        out string? errorMessage)
    {
        timestamp = default;
        layerId = default;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(token))
        {
            errorMessage = "$deltatoken value is required.";
            return false;
        }

        string payload;
        try
        {
            payload = Base64UrlDecode(token);
        }
        catch (FormatException)
        {
            errorMessage = "$deltatoken is invalid or malformed.";
            return false;
        }

        var separatorIndex = payload.IndexOf(Separator);
        if (separatorIndex < 0)
        {
            errorMessage = "$deltatoken is invalid or malformed.";
            return false;
        }

        var ticksPart = payload[..separatorIndex];
        var layerIdPart = payload[(separatorIndex + 1)..];

        if (!long.TryParse(ticksPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks))
        {
            errorMessage = "$deltatoken is invalid or malformed.";
            return false;
        }

        if (!int.TryParse(layerIdPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedLayerId))
        {
            errorMessage = "$deltatoken is invalid or malformed.";
            return false;
        }

        if (parsedLayerId < 0)
        {
            errorMessage = "$deltatoken is invalid or malformed.";
            return false;
        }

        try
        {
            timestamp = new DateTimeOffset(ticks, TimeSpan.Zero);
        }
        catch (ArgumentException)
        {
            errorMessage = "$deltatoken is invalid or malformed.";
            return false;
        }

        layerId = parsedLayerId;
        return true;
    }

    /// <summary>
    /// Generates a delta link URL for inclusion in OData responses.
    /// </summary>
    /// <param name="request">The current HTTP request.</param>
    /// <param name="baseUrl">The resolved base URL.</param>
    /// <param name="layerId">The layer ID.</param>
    /// <param name="timestamp">The snapshot timestamp to encode.</param>
    /// <returns>A complete delta link URL string.</returns>
    public static string GenerateDeltaLink(
        HttpRequest request,
        string baseUrl,
        int layerId,
        DateTimeOffset timestamp)
    {
        var token = Encode(timestamp, layerId);
        return $"{baseUrl}{request.Path}?$deltatoken={token}";
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
