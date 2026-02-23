// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.RegularExpressions;

namespace Honua.Postgres.Features.FeatureStore.Services;

internal sealed partial class FeatureQueryBuilder
{
    [GeneratedRegex(@"^[a-zA-Z_][a-zA-Z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex ValidFieldNameRegex();

    internal static bool IsValidFieldName(string fieldName)
    {
        if (string.IsNullOrWhiteSpace(fieldName))
        {
            return false;
        }

        return ValidFieldNameRegex().IsMatch(fieldName);
    }

    internal static string ConvertNamedParametersToPositional(string sql, ref int paramIndex)
    {
        var startingParamIndex = paramIndex;

        var result = Regex.Replace(
            sql,
            @"@p(\d+)",
            match =>
            {
                var paramNumber = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                return $"${startingParamIndex + paramNumber}";
            });

        var maxParamNumber = Regex.Matches(sql, @"@p(\d+)")
            .Cast<Match>()
            .Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
            .DefaultIfEmpty(-1)
            .Max();

        if (maxParamNumber >= 0)
        {
            paramIndex = startingParamIndex + maxParamNumber + 1;
        }

        return result;
    }

    private static bool ContainsOutsideQuotes(string input, string pattern)
    {
        var inQuotes = false;
        var quoteChar = '\0';

        for (var i = 0; i <= input.Length - pattern.Length; i++)
        {
            var c = input[i];

            if (!inQuotes && (c == '\'' || c == '"'))
            {
                inQuotes = true;
                quoteChar = c;
            }
            else if (inQuotes && c == quoteChar)
            {
                if (i + 1 < input.Length && input[i + 1] == quoteChar)
                {
                    i++;
                }
                else
                {
                    inQuotes = false;
                    quoteChar = '\0';
                }
            }
            else if (!inQuotes)
            {
                var substring = input.Substring(i, Math.Min(pattern.Length, input.Length - i));
                if (substring.Equals(pattern, StringComparison.OrdinalIgnoreCase))
                {
                    var beforeChar = i > 0 ? input[i - 1] : ' ';
                    var afterChar = i + pattern.Length < input.Length ? input[i + pattern.Length] : ' ';

                    if (!IsIdentifierChar(beforeChar) && !IsIdentifierChar(afterChar))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }
}
