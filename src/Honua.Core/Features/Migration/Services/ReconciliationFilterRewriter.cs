// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;

namespace Honua.Core.Features.Migration.Services;

/// <summary>
/// Rewrites a shared-target filter mirror so source field names become the published
/// target names. Quoted literals are left unchanged. Dedicated import targets do not
/// use this path: their filter is provenance and is not executed (#4826).
/// </summary>
internal static class ReconciliationFilterRewriter
{
    public static string? Rewrite(string? filter, IReadOnlyDictionary<string, string>? fieldMappings)
    {
        if (string.IsNullOrWhiteSpace(filter) || fieldMappings is null || fieldMappings.Count == 0)
        {
            return filter;
        }

        var builder = new StringBuilder(filter.Length);
        var index = 0;
        while (index < filter.Length)
        {
            var current = filter[index];
            if (current is '\'' or '"')
            {
                AppendQuoted(filter, builder, ref index, current);
                continue;
            }

            if (current == '[')
            {
                AppendBracketedIdentifier(filter, fieldMappings, builder, ref index);
                continue;
            }

            if (IsIdentifierStart(current))
            {
                var start = index;
                index++;
                while (index < filter.Length && IsIdentifierPart(filter[index]))
                {
                    index++;
                }

                var token = filter[start..index];
                builder.Append(TryMap(fieldMappings, token, out var mapped) ? mapped : token);
                continue;
            }

            builder.Append(current);
            index++;
        }

        return builder.ToString();
    }

    private static void AppendQuoted(string filter, StringBuilder builder, ref int index, char quote)
    {
        builder.Append(quote);
        index++;
        while (index < filter.Length)
        {
            var inner = filter[index];
            builder.Append(inner);
            index++;
            if (inner != quote)
            {
                continue;
            }

            // A doubled quote is an escaped quote, not the end of the literal.
            if (index < filter.Length && filter[index] == quote)
            {
                builder.Append(filter[index]);
                index++;
                continue;
            }

            break;
        }
    }

    private static void AppendBracketedIdentifier(
        string filter,
        IReadOnlyDictionary<string, string> fieldMappings,
        StringBuilder builder,
        ref int index)
    {
        var close = filter.IndexOf(']', index + 1);
        if (close < 0)
        {
            builder.Append(filter[index]);
            index++;
            return;
        }

        var name = filter[(index + 1)..close];
        if (TryMap(fieldMappings, name, out var mapped))
        {
            builder.Append('[').Append(mapped).Append(']');
        }
        else
        {
            builder.Append(filter.AsSpan(index, close - index + 1));
        }

        index = close + 1;
    }

    private static bool TryMap(
        IReadOnlyDictionary<string, string> fieldMappings,
        string token,
        out string mapped)
    {
        if (fieldMappings.TryGetValue(token, out mapped!))
        {
            return true;
        }

        foreach (var pair in fieldMappings)
        {
            if (string.Equals(pair.Key, token, StringComparison.OrdinalIgnoreCase))
            {
                mapped = pair.Value;
                return true;
            }
        }

        mapped = string.Empty;
        return false;
    }

    private static bool IsIdentifierStart(char value)
        => value is '_' or >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static bool IsIdentifierPart(char value)
        => IsIdentifierStart(value) || value is >= '0' and <= '9';
}
