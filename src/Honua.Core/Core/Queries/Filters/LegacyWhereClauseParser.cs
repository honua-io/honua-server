// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Honua.Core.Queries.Filters;

internal enum LegacyWhereGrammar
{
    Basic,
    Warehouse,
    Postgres,
    StorageMapped,
}

internal enum LegacyWhereValuePolicy
{
    NumericOrText,
    Text,
    NumericRequired,
}

internal enum LegacyWhereExpressionKind
{
    True,
    NullCheck,
    In,
    Comparison,
}

internal readonly record struct LegacyWhereExpression(LegacyWhereExpressionKind Kind, Match Match);

internal static partial class LegacyWhereClauseParser
{
    internal static IEnumerable<LegacyWhereExpression> Parse(
        List<string> expressions, LegacyWhereGrammar grammar, string unsupportedMessage = "WHERE clause format not supported.")
    {
        if (expressions.Count == 0)
        {
            throw new ArgumentException(unsupportedMessage);
        }

        // Keep parameter accumulation order when a later term fails.
        foreach (var expression in expressions)
        {
            yield return ParseTerm(expression.Trim(), grammar, unsupportedMessage);
        }
    }

    private static LegacyWhereExpression ParseTerm(string term, LegacyWhereGrammar grammar, string unsupportedMessage)
    {
        var isTrue = grammar == LegacyWhereGrammar.Postgres
            ? IsTrueLiteral(term)
            : grammar != LegacyWhereGrammar.StorageMapped &&
              (term.Equals("1=1", StringComparison.Ordinal) || term.Equals("TRUE", StringComparison.OrdinalIgnoreCase));
        if (isTrue)
        {
            return new(LegacyWhereExpressionKind.True, Match.Empty);
        }

        var expression = MatchTerm(term, grammar)
            ?? throw new ArgumentException(unsupportedMessage);
        if (grammar == LegacyWhereGrammar.StorageMapped
            && !SqlIdentifier.HasUnicodeIdentifierShape(expression.Match.Groups["field"].Value))
        {
            throw new ArgumentException(unsupportedMessage);
        }

        return expression;
    }

    private static LegacyWhereExpression? MatchTerm(string term, LegacyWhereGrammar grammar)
    {
        var nullMatch = NullCheckPattern(grammar).Match(term);
        if (nullMatch.Success)
        {
            return new(LegacyWhereExpressionKind.NullCheck, nullMatch);
        }

        if (grammar != LegacyWhereGrammar.Basic)
        {
            var inMatch = InPattern(grammar).Match(term);
            if (inMatch.Success)
            {
                return new(LegacyWhereExpressionKind.In, inMatch);
            }
        }

        var comparison = ComparisonPattern(grammar).Match(term);
        return comparison.Success
            ? new(LegacyWhereExpressionKind.Comparison, comparison)
            : null;
    }

    internal static bool IsTrueLiteral(string term) => PostgresTrueLiteralRegex().IsMatch(term);

    private static Regex NullCheckPattern(LegacyWhereGrammar grammar) => grammar switch
    {
        LegacyWhereGrammar.Basic => BasicNullCheckRegex(),
        LegacyWhereGrammar.StorageMapped => StorageMappedNullCheckRegex(),
        LegacyWhereGrammar.Warehouse => WarehouseNullCheckRegex(),
        _ => PostgresNullCheckRegex(),
    };

    private static Regex ComparisonPattern(LegacyWhereGrammar grammar) => grammar switch
    {
        LegacyWhereGrammar.Basic => BasicComparisonRegex(),
        LegacyWhereGrammar.StorageMapped => StorageMappedComparisonRegex(),
        LegacyWhereGrammar.Warehouse => WarehouseComparisonRegex(),
        _ => PostgresComparisonRegex(),
    };

    private static Regex InPattern(LegacyWhereGrammar grammar) => grammar switch
    {
        LegacyWhereGrammar.Warehouse => WarehouseInExpressionRegex(),
        LegacyWhereGrammar.StorageMapped => StorageMappedInRegex(),
        _ => PostgresInRegex(),
    };

    internal static string NormalizeOperator(string value, LegacyWhereGrammar grammar, string? unsupportedMessage = null)
        => value.Trim().ToUpperInvariant() switch
        {
            "NOT LIKE" => "NOT LIKE",
            "LIKE" => "LIKE",
            ">=" => ">=",
            "<=" => "<=",
            "!=" or "<>" => grammar == LegacyWhereGrammar.Warehouse ? "<>" : "!=",
            "=" => "=",
            ">" => ">",
            "<" => "<",
            _ => throw new ArgumentException(unsupportedMessage ?? $"Unsupported operator: {value}"),
        };

    internal static object ParseValueToken(string value, LegacyWhereValuePolicy policy = LegacyWhereValuePolicy.NumericOrText)
    {
        if (value.StartsWith('\'') && value.EndsWith('\''))
        {
            return value[1..^1].Replace("''", "'", StringComparison.Ordinal);
        }
        if (policy != LegacyWhereValuePolicy.Text && decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return number;
        }
        return policy == LegacyWhereValuePolicy.NumericRequired ? throw new ArgumentException($"Unrecognized value token: {value}") : value;
    }

    internal static MatchCollection MatchInValues(string values) => WarehouseInValueRegex().Matches(values);

    // Splits a PostgreSQL IN list on commas outside single-quoted literals; '' stays an escaped quote.
    internal static List<string> SplitValueTokens(string values)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        for (var index = 0; index < values.Length; index++)
        {
            var value = values[index];
            if (value == '\'' && inQuotes && index + 1 < values.Length && values[index + 1] == '\'')
            {
                current.Append("''");
                index++;
            }
            else if (value == '\'')
            {
                inQuotes = !inQuotes;
                current.Append(value);
            }
            else if (value == ',' && !inQuotes)
            {
                tokens.Add(current.ToString().Trim());
                current.Clear();
            }
            else
            {
                current.Append(value);
            }
        }

        tokens.Add(current.ToString().Trim());
        return tokens;
    }

    [GeneratedRegex(
        @"^(?<field>[a-zA-Z_][a-zA-Z0-9_]*)\s*(?<op>NOT\s+LIKE|LIKE|>=|<=|!=|<>|=|>|<)\s*(?<value>'(?:''|[^'])*'|-?\d+(?:\.\d+)?)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BasicComparisonRegex();

    [GeneratedRegex(
        @"^(?<field>[a-zA-Z_][a-zA-Z0-9_]*)\s+IS\s+(?<not>NOT\s+)?NULL$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BasicNullCheckRegex();

    [GeneratedRegex(
        @"^(?<field>[a-zA-Z_][a-zA-Z0-9_]*)\s*(?<op>NOT\s+LIKE|LIKE|>=|<=|!=|<>|=|>|<)\s*(?<value>'(?:''|[^'])*'|-?\d+(?:\.\d+)?)\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WarehouseComparisonRegex();

    [GeneratedRegex(
        @"^(?<field>[a-zA-Z_][a-zA-Z0-9_]*)\s+IS\s+(?<not>NOT\s+)?NULL\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WarehouseNullCheckRegex();

    [GeneratedRegex(@"^(?<field>[a-zA-Z_][a-zA-Z0-9_]*)\s+IN\s*\((?<values>(?:'(?:''|[^'])*'|-?\d+(?:\.\d+)?)(?:\s*,\s*(?:'(?:''|[^'])*'|-?\d+(?:\.\d+)?))*)\)\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WarehouseInExpressionRegex();

    [GeneratedRegex(@"'(?:''|[^'])*'|-?\d+(?:\.\d+)?", RegexOptions.CultureInvariant)]
    private static partial Regex WarehouseInValueRegex();

    [GeneratedRegex(
        @"^(?<field>[a-zA-Z_][a-zA-Z0-9_]*(?:->>'[^']+')?)\s*(?<op>NOT\s+LIKE|LIKE|>=|<=|!=|<>|=|>|<)\s*(?<value>'(?:''|[^'])*'|-?\d+(?:\.\d+)?)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PostgresComparisonRegex();

    [GeneratedRegex(
        @"^(?<field>[a-zA-Z_][a-zA-Z0-9_]*(?:->>'[^']+')?)\s+IS\s+(?<not>NOT\s+)?NULL$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PostgresNullCheckRegex();

    [GeneratedRegex(
        @"^(?<field>[a-zA-Z_][a-zA-Z0-9_]*(?:->>'[^']+')?)\s+IN\s*\((?<values>(?:'(?:''|[^'])*'|-?\d+(?:\.\d+)?)(?:\s*,\s*(?:'(?:''|[^'])*'|-?\d+(?:\.\d+)?))*)\)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PostgresInRegex();

    // The storage-mapped reader resolves every field token against the resource's declared
    // fields before quoting it, so its bare field names admit letters of any script
    // (`名前 = 'x'`), matching the GeoServices SQL and CQL2 lexers. Regex classes see UTF-16
    // units, so the token also admits surrogate pairs and ParseTerm then judges the captured
    // name per Unicode scalar with SqlIdentifier.HasUnicodeIdentifierShape. The byte limit
    // is left to the reader: JSONB-backed field names are bound keys and may be longer.
    private const string StorageMappedField =
        @"(?<field>(?:[\p{L}_]|[\uD800-\uDBFF][\uDC00-\uDFFF])(?:[\p{L}\p{Mn}\p{Mc}\p{Nd}_]|[\uD800-\uDBFF][\uDC00-\uDFFF])*)";

    [GeneratedRegex(
        @"^" + StorageMappedField + @"\s*(?<op>NOT\s+LIKE|LIKE|>=|<=|!=|<>|=|>|<)\s*(?<value>'(?:''|[^'])*'|-?\d+(?:\.\d+)?)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StorageMappedComparisonRegex();

    [GeneratedRegex(
        @"^" + StorageMappedField + @"\s+IS\s+(?<not>NOT\s+)?NULL$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StorageMappedNullCheckRegex();

    [GeneratedRegex(
        @"^" + StorageMappedField + @"\s+IN\s*\((?<values>(?:'(?:''|[^'])*'|-?\d+(?:\.\d+)?)(?:\s*,\s*(?:'(?:''|[^'])*'|-?\d+(?:\.\d+)?))*)\)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StorageMappedInRegex();

    [GeneratedRegex(
        @"^(?:1\s*=\s*1|TRUE)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PostgresTrueLiteralRegex();
}
