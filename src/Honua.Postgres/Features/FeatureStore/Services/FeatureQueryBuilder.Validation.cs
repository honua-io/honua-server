// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.RegularExpressions;

namespace Honua.Postgres.Features.FeatureStore.Services;

internal sealed partial class FeatureQueryBuilder
{
    [GeneratedRegex(@"^[a-zA-Z_][a-zA-Z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex ValidFieldNameRegex();

    [GeneratedRegex(@"@p(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex NamedParameterRegex();

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

        // Collect unique @pN indices in ascending order. SqlFragment.Parameters is always
        // ordered by @pN index (CombineSqlFilters renumbers right-side indices to follow
        // the left count), so sorting unique indices gives the correct Parameters[rank]
        // <-> positional-$index alignment. Using a dense mapping also prevents sparse
        // filters (@p0, @p3 with only 2 entries) from advancing paramIndex by 4 and
        // misaligning every subsequent spatial/temporal/pagination parameter.
        var sortedIndices = new SortedSet<int>();
        foreach (Match match in NamedParameterRegex().Matches(sql))
        {
            sortedIndices.Add(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture));
        }

        if (sortedIndices.Count == 0)
        {
            return sql;
        }

        // Build dense mapping: @pN -> startingParamIndex + rank(N)
        var indexMap = new Dictionary<int, int>(sortedIndices.Count);
        var rank = 0;
        foreach (var paramN in sortedIndices)
        {
            indexMap[paramN] = startingParamIndex + rank++;
        }

        var result = NamedParameterRegex().Replace(
            sql,
            match =>
            {
                var paramNumber = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                return $"${indexMap[paramNumber]}";
            });

        paramIndex = startingParamIndex + sortedIndices.Count;
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

                    // Only apply the identifier-boundary guard for word-like patterns such as
                    // SQL keywords (e.g. "UNION", "SELECT"). Punctuation sequences like --, /*, and
                    // */ are not identifiers; an adjacent alphanumeric character (e.g. `field = 1--`)
                    // must not suppress their detection as comment delimiters.
                    var isWordPattern = IsAllIdentifierChars(pattern);
                    if (!isWordPattern || (!IsIdentifierChar(beforeChar) && !IsIdentifierChar(afterChar)))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }
}
