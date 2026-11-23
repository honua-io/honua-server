// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Protocols.OData.Services;

internal static class ODataContextSelection
{
    internal static string? BuildContextSelection(string? select, string? expand)
    {
        var selections = new List<string>();

        if (!string.IsNullOrWhiteSpace(select))
        {
            selections.AddRange(select.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(item => !item.Equals("*", StringComparison.OrdinalIgnoreCase)));
        }

        if (!string.IsNullOrWhiteSpace(expand))
        {
            foreach (var segment in SplitTopLevel(expand))
            {
                var expansion = segment.Trim();
                var optionIndex = expansion.IndexOf('(');
                var name = optionIndex < 0 ? expansion : expansion[..optionIndex].Trim();
                if (name.Length == 0)
                {
                    continue;
                }

                var expandedSelect = GetExpandedSelect(expansion, optionIndex);

                // Parentheses distinguish expanded navigation properties from selected
                // structural properties in an OData context URL.
                selections.RemoveAll(item => item.Equals(name, StringComparison.OrdinalIgnoreCase));
                selections.Add($"{name}({BuildContextSelection(expandedSelect, null)})");
            }
        }

        return selections.Count == 0 ? null : string.Join(",", selections.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static string? GetExpandedSelect(string expansion, int optionIndex)
    {
        if (optionIndex < 0 || !expansion.EndsWith(')'))
        {
            return null;
        }

        return expansion[(optionIndex + 1)..^1]
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(option => option.StartsWith("$select=", StringComparison.OrdinalIgnoreCase))?[8..];
    }

    internal static List<string> SplitTopLevel(string expression)
    {
        var result = new List<string>();
        var depth = 0;
        var start = 0;

        for (var i = 0; i < expression.Length; i++)
        {
            var c = expression[i];
            if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                depth = Math.Max(0, depth - 1);
            }
            else if (c == ',' && depth == 0)
            {
                result.Add(expression[start..i]);
                start = i + 1;
            }
        }

        if (start < expression.Length)
        {
            result.Add(expression[start..]);
        }

        return result;
    }

}
