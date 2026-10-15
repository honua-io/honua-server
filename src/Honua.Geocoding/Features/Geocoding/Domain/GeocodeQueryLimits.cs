// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Geocoding.Features.Geocoding.Domain;

/// <summary>
/// Bounds a forward-geocode query before it becomes one SQL predicate per token.
/// </summary>
internal static class GeocodeQueryLimits
{
    /// <summary>Maximum raw query length, in characters.</summary>
    public const int MaxQueryLength = 512;

    /// <summary>Maximum number of distinct normalized tokens.</summary>
    public const int MaxDistinctTokens = 16;

    /// <summary>
    /// Rejects a query that is longer than <see cref="MaxQueryLength"/> or that
    /// normalizes to more than <see cref="MaxDistinctTokens"/> distinct tokens.
    /// </summary>
    /// <param name="text">Raw query text.</param>
    /// <param name="parameterName">Client parameter the rejection names.</param>
    public static void EnsureBounded(string? text, string parameterName)
        => _ = DistinctTokens(text, parameterName);

    /// <summary>
    /// Distinct normalized tokens in first-seen order. Empty when <paramref name="text"/>
    /// is missing. Throws <see cref="GeocodeRequestException"/> when the query is too
    /// long or has too many distinct tokens. The message contains "Invalid" so protocol
    /// adapters map it to a client error.
    /// </summary>
    /// <param name="text">Raw query text.</param>
    /// <param name="parameterName">Client parameter the rejection names.</param>
    /// <returns>Distinct tokens, preserving first-seen order.</returns>
    public static List<string> DistinctTokens(string? text, string parameterName)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        if (text.Length > MaxQueryLength)
        {
            throw new GeocodeRequestException(
                $"Invalid {parameterName}: query length {text.Length} exceeds the maximum of {MaxQueryLength} characters.")
            {
                ParameterName = parameterName,
            };
        }

        var tokens = new List<string>(MaxDistinctTokens);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in GeocodeReferenceText.Normalize(text)
                     .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!seen.Add(token))
            {
                continue;
            }

            if (tokens.Count == MaxDistinctTokens)
            {
                throw new GeocodeRequestException(
                    $"Invalid {parameterName}: query exceeds the maximum of {MaxDistinctTokens} distinct tokens.")
                {
                    ParameterName = parameterName,
                };
            }

            tokens.Add(token);
        }

        return tokens;
    }
}
