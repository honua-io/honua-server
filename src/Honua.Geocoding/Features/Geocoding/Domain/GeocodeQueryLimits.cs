// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Geocoding.Features.Geocoding.Domain;

internal static class GeocodeQueryLimits
{
    public const int MaxQueryLength = 512;

    public const int MaxDistinctTokens = 16;

    public static void EnsureBounded(string? text, string parameterName)
        => _ = DistinctTokens(text, parameterName);

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
