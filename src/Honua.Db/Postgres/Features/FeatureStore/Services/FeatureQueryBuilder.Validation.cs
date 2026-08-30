// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Honua.Db.Postgres.Features.FeatureStore.Services;

internal sealed partial class FeatureQueryBuilder
{
    private const int PostgreSqlMaxIdentifierBytes = 63;

    // `\z`, not `$`: in .NET `$` also matches immediately before a trailing newline, so
    // `"field\n"` satisfied `...$` and slipped past this guard. These names reach SQL as
    // quoted identifiers and result aliases, where an embedded newline is exactly the
    // shape an injection attempt takes.
    [GeneratedRegex(@"^[a-zA-Z_][a-zA-Z0-9_]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex ValidFieldNameRegex();

    [GeneratedRegex(@"^[a-zA-Z0-9_][a-zA-Z0-9_:.\-]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex ValidJsonAttributeKeyRegex();

    [GeneratedRegex(@"@p(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex NamedParameterRegex();

    /// <summary>
    /// Validates a field name that is emitted into SQL as an <b>identifier</b> — a
    /// quoted column reference or a result-set alias.
    /// </summary>
    /// <remarks>
    /// An identifier cannot be bound as a query parameter, so for these callers the
    /// character class is what makes the interpolation safe and must stay strict.
    /// Use <see cref="IsValidJsonAttributeKey"/> instead for names that are only ever
    /// used as a <c>jsonb</c> key; see the remarks there for why the two must not
    /// share one validator.
    /// </remarks>
    internal static bool IsValidFieldName(string fieldName)
    {
        if (string.IsNullOrWhiteSpace(fieldName))
        {
            return false;
        }

        return ValidFieldNameRegex().IsMatch(fieldName);
    }

    /// <summary>
    /// Validates a field name that is used as a <b>jsonb key</b> — the string that
    /// indexes the attributes document, either as the key of a projected pair or as
    /// the operand of the <c>-&gt;</c>/<c>-&gt;&gt;</c> accessor.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A jsonb key is not a SQL identifier and the two deliberately do not share a
    /// validator. Prefixed extension property names — the STAC EO extension's
    /// <c>eo:cloud_cover</c> is the concrete case — are legitimate, declared,
    /// queryable fields that <see cref="IsValidFieldName"/> rejects because of the
    /// colon. Gating the projection on the identifier regex therefore made the server
    /// advertise a field in <c>/queryables</c>, the CSV header and the GeoServices
    /// <c>fields</c> array and then silently omit it from every feature payload
    /// (honua-server#3392).
    /// </para>
    /// <para>
    /// Parameter-bound JSON access does not inherit PostgreSQL's identifier-length
    /// limit. The remaining allow-list is defense in depth only: it keeps quotes,
    /// semicolons, whitespace, parentheses and control characters out of names that
    /// reach the query builder while admitting real prefixed/hyphenated/dotted shapes.
    /// Callers that reuse a key as an encoded-row alias must additionally use
    /// <see cref="IsValidEncodedColumnAlias"/>.
    /// </para>
    /// </remarks>
    internal static bool IsValidJsonAttributeKey(string fieldName)
    {
        if (string.IsNullOrWhiteSpace(fieldName))
        {
            return false;
        }

        return ValidJsonAttributeKeyRegex().IsMatch(fieldName);
    }

    /// <summary>
    /// Validates a declared JSON key that will also be emitted as an encoded-row
    /// column alias for PostgreSQL's FlatGeobuf/Geobuf encoders.
    /// </summary>
    /// <remarks>
    /// PostgreSQL truncates identifiers after 63 bytes. Rejecting aliases beyond that
    /// boundary prevents encoded fields from acquiring a different name or colliding.
    /// Ordinary parameter-bound JSON keys deliberately do not inherit this limit.
    /// </remarks>
    internal static bool IsValidEncodedColumnAlias(string fieldName)
        => IsValidJsonAttributeKey(fieldName)
            && Encoding.UTF8.GetByteCount(fieldName) <= PostgreSqlMaxIdentifierBytes;
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
