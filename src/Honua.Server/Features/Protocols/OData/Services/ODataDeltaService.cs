// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;

namespace Honua.Server.Features.Protocols.OData.Services;

/// <summary>
/// Provides encoding and decoding of OData $deltatoken values for change tracking.
/// Tokens encode the defining query state so delta links remain opaque and round-trip safe.
/// </summary>
internal static class ODataDeltaService
{
    private const char Separator = '|';
    private const string VersionPrefix = "v1";

    internal sealed record DeltaQueryState
    {
        public required DateTimeOffset Timestamp { get; init; }

        public required int LayerId { get; init; }

        public string? Filter { get; init; }

        public string? Select { get; init; }

        public string? OrderBy { get; init; }

        public string? Expand { get; init; }

        public string? Compute { get; init; }

        public string? Format { get; init; }

        public bool? Count { get; init; }
    }

    /// <summary>
    /// Encodes a delta token from the defining query state.
    /// </summary>
    /// <param name="state">The opaque delta state.</param>
    /// <returns>A Base64Url-encoded delta token string.</returns>
    public static string Encode(DeltaQueryState state)
    {
        var payload = string.Join(
            Separator,
            VersionPrefix,
            state.Timestamp.UtcTicks.ToString(CultureInfo.InvariantCulture),
            state.LayerId.ToString(CultureInfo.InvariantCulture),
            EncodeSegment(state.Filter),
            EncodeSegment(state.Select),
            EncodeSegment(state.OrderBy),
            EncodeSegment(state.Expand),
            EncodeSegment(state.Compute),
            EncodeSegment(state.Format),
            EncodeSegment(state.Count?.ToString().ToLowerInvariant()));
        return Base64UrlEncode(payload);
    }

    /// <summary>
    /// Attempts to decode a $deltatoken into its defining query state.
    /// </summary>
    /// <param name="token">The opaque $deltatoken string.</param>
    /// <param name="state">The decoded query state.</param>
    /// <param name="errorMessage">An error message if decoding fails.</param>
    /// <returns>true if decoding succeeded; false otherwise.</returns>
    public static bool TryDecode(
        string token,
        out DeltaQueryState state,
        out string? errorMessage)
    {
        state = null!;
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

        if (!TryParsePayload(payload, out state, out errorMessage))
        {
            return false;
        }
        return true;
    }

    /// <summary>
    /// Generates a delta link URL for inclusion in OData responses.
    /// </summary>
    /// <param name="request">The current HTTP request.</param>
    /// <param name="baseUrl">The resolved base URL.</param>
    /// <param name="state">The defining query state to encode.</param>
    /// <returns>A complete delta link URL string.</returns>
    public static string GenerateDeltaLink(
        HttpRequest request,
        string baseUrl,
        DeltaQueryState state)
    {
        var token = Encode(state);
        return $"{baseUrl}{request.Path}?$deltatoken={token}";
    }

    /// <summary>
    /// Builds the defining OData filter used by a delta request.
    /// </summary>
    public static string BuildDeltaFilter(string? filter, DateTimeOffset timestamp)
    {
        var encodedTimestamp = timestamp.ToString("O", CultureInfo.InvariantCulture);
        var deltaPredicate = $"updated_at gt datetimeoffset'{encodedTimestamp}'";

        return string.IsNullOrWhiteSpace(filter)
            ? deltaPredicate
            : $"({filter}) and {deltaPredicate}";
    }

    private static bool TryParsePayload(
        string payload,
        out DeltaQueryState state,
        out string? errorMessage)
    {
        state = null!;
        errorMessage = null;

        var parts = payload.Split(Separator);
        if (parts.Length == 2)
        {
            return TryParseLegacyPayload(parts, out state, out errorMessage);
        }

        if (parts.Length != 10 || !string.Equals(parts[0], VersionPrefix, StringComparison.Ordinal))
        {
            errorMessage = "$deltatoken is invalid or malformed.";
            return false;
        }

        if (!TryParseCore(parts[1], parts[2], out var timestamp, out var layerId, out errorMessage))
        {
            return false;
        }

        var countSegment = DecodeSegment(parts[9], out var countDecodeError);
        if (countDecodeError != null)
        {
            errorMessage = countDecodeError;
            return false;
        }

        bool? count = null;
        if (!string.IsNullOrWhiteSpace(countSegment))
        {
            if (!bool.TryParse(countSegment, out var parsedCount))
            {
                errorMessage = "$deltatoken is invalid or malformed.";
                return false;
            }

            count = parsedCount;
        }

        if (!TryDecodeOptionalSegment(parts[3], out var filter, out errorMessage) ||
            !TryDecodeOptionalSegment(parts[4], out var select, out errorMessage) ||
            !TryDecodeOptionalSegment(parts[5], out var orderBy, out errorMessage) ||
            !TryDecodeOptionalSegment(parts[6], out var expand, out errorMessage) ||
            !TryDecodeOptionalSegment(parts[7], out var compute, out errorMessage) ||
            !TryDecodeOptionalSegment(parts[8], out var format, out errorMessage))
        {
            return false;
        }

        state = new DeltaQueryState
        {
            Timestamp = timestamp,
            LayerId = layerId,
            Filter = filter,
            Select = select,
            OrderBy = orderBy,
            Expand = expand,
            Compute = compute,
            Format = format,
            Count = count
        };

        return true;
    }

    private static bool TryParseLegacyPayload(
        string[] parts,
        out DeltaQueryState state,
        out string? errorMessage)
    {
        state = null!;
        if (!TryParseCore(parts[0], parts[1], out var timestamp, out var layerId, out errorMessage))
        {
            return false;
        }

        state = new DeltaQueryState
        {
            Timestamp = timestamp,
            LayerId = layerId
        };

        return true;
    }

    private static bool TryParseCore(
        string ticksPart,
        string layerIdPart,
        out DateTimeOffset timestamp,
        out int layerId,
        out string? errorMessage)
    {
        timestamp = default;
        layerId = default;
        errorMessage = null;

        if (!long.TryParse(ticksPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks) ||
            !int.TryParse(layerIdPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedLayerId) ||
            parsedLayerId < 0)
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

    private static bool TryDecodeOptionalSegment(string segment, out string? value, out string? errorMessage)
    {
        value = DecodeSegment(segment, out errorMessage);
        return errorMessage == null;
    }

    private static string EncodeSegment(string? value)
        => string.IsNullOrEmpty(value) ? string.Empty : Base64UrlEncode(value);

    private static string? DecodeSegment(string segment, out string? errorMessage)
    {
        errorMessage = null;
        if (string.IsNullOrEmpty(segment))
        {
            return null;
        }

        try
        {
            return Base64UrlDecode(segment);
        }
        catch (FormatException)
        {
            errorMessage = "$deltatoken is invalid or malformed.";
            return null;
        }
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
