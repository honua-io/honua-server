// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.RegularExpressions;

namespace Honua.Protocols.GeoServices.Sharing;

// A Portal search q is evaluated against every visible item, so its size and nesting are bounded up
// front and caller text is matched on the linear-time regex engine.
internal static class PortalSearchQueryLimits
{
    internal const int MaxLength = 4096;
    internal const int MaxDepth = 8;

    internal static readonly Regex QualifiedTerm = new(
        """(?<negative>-)?(?<field>[A-Za-z]+):\s*(?:\((?<group>[^)]*)\)|"(?<quoted>[^"]*)"|(?<value>[^\s()]+))""",
        RegexOptions.NonBacktracking | RegexOptions.CultureInvariant);

    internal static readonly Regex AlternativeSeparator = new(
        @"\s+OR\s+",
        RegexOptions.NonBacktracking | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    internal static string RefusalMessage
        => $"Search 'q' must be at most {MaxLength} characters with at most {MaxDepth} levels of parentheses.";

    // One linear pass: the length cap and the parenthesis depth outside quotes bound every later scan and recursion.
    internal static bool IsAdmissible(string query)
    {
        if (query.Length > MaxLength)
        {
            return false;
        }

        var depth = 0;
        var inQuotes = false;
        foreach (var ch in query)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (!inQuotes && ch == '(' && ++depth > MaxDepth)
            {
                return false;
            }
            else if (!inQuotes && ch == ')' && depth > 0)
            {
                depth--;
            }
        }

        return true;
    }
}
