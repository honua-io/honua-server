// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;

namespace Honua.Protocols.OData.Services;

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

        public DateTimeOffset? UpperBoundTimestamp { get; init; }

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
            EncodeSegment(state.Count?.ToString().ToLowerInvariant()),
            state.UpperBoundTimestamp?.UtcTicks.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
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
    /// The OData property name the delta "changed since" predicate targets.
    /// </summary>
    /// <remarks>
    /// This is the reserved audit-trail system alias (DatabaseSchema.UpdatedColumn)
    /// that the shared filter pipeline maps to the physical <c>updated_at</c> "last
    /// modified" column on managed feature tables. The alias resolves to the bare
    /// system column when the resource does not declare a queryable of the same name,
    /// so the delta filter applies again without reintroducing #1415's bug (treating
    /// an arbitrary <c>updated_at</c> attribute field name as a bare timestamp
    /// column). The predicate previously used the literal physical name
    /// <c>updated_at</c>, which #1415 stopped mapping to a column, silently dropping
    /// the delta filter (it was swallowed during filter translation, returning all
    /// rows instead of only those changed since the token timestamp).
    /// </remarks>
    private const string SystemUpdatedField = "updated";

    /// <summary>
    /// Builds the defining OData filter used by a delta request.
    /// </summary>
    public static string BuildDeltaFilter(string? filter, DateTimeOffset timestamp, DateTimeOffset? upperBoundTimestamp = null)
    {
        var encodedTimestamp = timestamp.ToString("O", CultureInfo.InvariantCulture);
        var deltaPredicate = $"{SystemUpdatedField} gt datetimeoffset'{encodedTimestamp}'";
        if (upperBoundTimestamp.HasValue)
        {
            var encodedUpperBound = upperBoundTimestamp.Value.ToString("O", CultureInfo.InvariantCulture);
            deltaPredicate = $"{deltaPredicate} and {SystemUpdatedField} le datetimeoffset'{encodedUpperBound}'";
        }

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

        if ((parts.Length != 10 && parts.Length != 11) || !string.Equals(parts[0], VersionPrefix, StringComparison.Ordinal))
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

        DateTimeOffset? upperBoundTimestamp = null;
        if (parts.Length == 11 && !string.IsNullOrWhiteSpace(parts[10]))
        {
            if (!long.TryParse(parts[10], NumberStyles.Integer, CultureInfo.InvariantCulture, out var upperBoundTicks))
            {
                errorMessage = "$deltatoken is invalid or malformed.";
                return false;
            }

            try
            {
                upperBoundTimestamp = new DateTimeOffset(upperBoundTicks, TimeSpan.Zero);
            }
            catch (ArgumentException)
            {
                errorMessage = "$deltatoken is invalid or malformed.";
                return false;
            }
        }

        state = new DeltaQueryState
        {
            Timestamp = timestamp,
            UpperBoundTimestamp = upperBoundTimestamp,
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
