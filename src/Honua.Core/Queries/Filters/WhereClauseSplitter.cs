// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;

namespace Honua.Core.Queries.Filters;

/// <summary>
/// Splits a simple WHERE clause on logical AND without treating the token as
/// part of a quoted literal or an identifier such as <c>start_and_end</c>.
/// </summary>
internal static class WhereClauseSplitter
{
    internal static List<string> SplitOnAnd(string whereClause)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < whereClause.Length; i++)
        {
            var c = whereClause[i];
            if (!inQuotes && c == '\'')
            {
                inQuotes = true;
                current.Append(c);
            }
            else if (inQuotes && c == '\'')
            {
                if (i + 1 < whereClause.Length && whereClause[i + 1] == '\'')
                {
                    current.Append("''");
                    i++;
                }
                else
                {
                    inQuotes = false;
                    current.Append(c);
                }
            }
            else if (!inQuotes && i + 3 <= whereClause.Length &&
                     whereClause.AsSpan(i, 3).Equals("AND", StringComparison.OrdinalIgnoreCase) &&
                     (i == 0 || !IsIdentifierChar(whereClause[i - 1])) &&
                     (i + 3 >= whereClause.Length || !IsIdentifierChar(whereClause[i + 3])))
            {
                parts.Add(current.ToString());
                current.Clear();
                i += 2;
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
        {
            parts.Add(current.ToString());
        }

        return parts;
    }

    private static bool IsIdentifierChar(char c)
        => char.IsLetterOrDigit(c) || c == '_';
}
