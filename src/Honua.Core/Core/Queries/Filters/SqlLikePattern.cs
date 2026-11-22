// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Honua.Core.Queries.Filters;

// Every in-memory evaluator matches client-supplied SQL LIKE patterns here, so a pattern costs the
// same and meets the same limits on every surface. '%' matches any run of characters and '_' one
// character; everything else is literal. The non-backtracking engine keeps matching linear in the
// input. Patterns over the length or wildcard limit are refused before compilation, and the cache of
// compiled patterns is bounded so unique patterns cannot pin one automaton each for the process life.
public static class SqlLikePattern
{
    public const int MaxPatternLength = 256;
    public const int MaxWildcards = 32;
    private const int MaxCachedPatterns = 1024;

    private const RegexOptions CaseSensitive = RegexOptions.NonBacktracking | RegexOptions.CultureInvariant;

    private static readonly ConcurrentDictionary<(string Pattern, RegexOptions Options), Regex> Cache = new();

    // Throws UnevaluableLikePatternException when the pattern is over a limit.
    public static bool IsMatch(string input, string pattern) => Match(input, pattern, CaseSensitive);

    public static bool IsMatchIgnoringCase(string input, string pattern)
        => Match(input, pattern, CaseSensitive | RegexOptions.IgnoreCase);

    private static bool Match(string input, string pattern, RegexOptions options)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(pattern);
        if (pattern.Length > MaxPatternLength || CountWildcards(pattern) > MaxWildcards)
        {
            throw new UnevaluableLikePatternException();
        }

        var key = (pattern, options);
        if (!Cache.TryGetValue(key, out var regex))
        {
            // A full cache starts over rather than compiling every later pattern per call.
            if (Cache.Count >= MaxCachedPatterns)
            {
                Cache.Clear();
            }

            regex = Cache.GetOrAdd(key, static k => Create(k.Pattern, k.Options));
        }

        return regex.IsMatch(input);
    }

    private static int CountWildcards(string pattern)
    {
        var count = 0;
        foreach (var character in pattern)
        {
            if (character is '%' or '_')
            {
                count++;
            }
        }

        return count;
    }

    private static Regex Create(string pattern, RegexOptions options)
    {
        var expression = "^" + Regex.Escape(pattern)
            .Replace("%", ".*", StringComparison.Ordinal)
            .Replace("_", ".", StringComparison.Ordinal) + "$";
        return new Regex(expression, options);
    }
}
