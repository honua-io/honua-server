// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Queries.Filters;

/// <summary>Composes a text search using the provider's column, parameter and substring syntax.</summary>
public static class FeatureTextSearchSql
{
    /// <summary>
    /// Builds a predicate without embedding caller text in SQL. The contains callback receives
    /// SQL operands and must return a numeric substring position (zero for no match).
    /// </summary>
    public static string Build(
        FeatureTextSearch search,
        Func<string, string> resolveField,
        Func<string, string> addParameter,
        Func<string, string, string> substringPosition)
    {
        ArgumentNullException.ThrowIfNull(search);
        ArgumentNullException.ThrowIfNull(resolveField);
        ArgumentNullException.ThrowIfNull(addParameter);
        ArgumentNullException.ThrowIfNull(substringPosition);
        if (search.Groups.Count == 0)
        {
            return "1=1";
        }
        if (search.Fields.Count == 0)
        {
            return "1=0";
        }

        var groups = search.Groups.Select(group =>
        {
            var terms = group.Select(term =>
            {
                // Bind once per field: positional drivers cannot reuse a '?' placeholder.
                var fields = search.Fields.Select(field =>
                {
                    var column = $"LOWER({resolveField(field)})";
                    var parameter = $"LOWER({addParameter(term.Text)})";
                    return $"COALESCE({substringPosition(column, parameter)}, 0) > 0";
                });
                var condition = $"({string.Join(" OR ", fields)})";
                return term.Negated ? $"NOT {condition}" : condition;
            });
            return $"({string.Join(" AND ", terms)})";
        });
        return $"({string.Join(" OR ", groups)})";
    }
}
