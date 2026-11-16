// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
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
            : throw new ArgumentException(unsupportedMessage);
    }

    internal static bool IsTrueLiteral(string term) => PostgresTrueLiteralRegex().IsMatch(term);

    private static Regex NullCheckPattern(LegacyWhereGrammar grammar) => grammar switch
    {
        LegacyWhereGrammar.Basic or LegacyWhereGrammar.StorageMapped => BasicNullCheckRegex(),
        LegacyWhereGrammar.Warehouse => WarehouseNullCheckRegex(),
        _ => PostgresNullCheckRegex(),
    };

    private static Regex ComparisonPattern(LegacyWhereGrammar grammar) => grammar switch
    {
        LegacyWhereGrammar.Basic or LegacyWhereGrammar.StorageMapped => BasicComparisonRegex(),
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

    [GeneratedRegex(
        @"^(?<field>[a-zA-Z_][a-zA-Z0-9_]*)\s+IN\s*\((?<values>(?:'(?:''|[^'])*'|-?\d+(?:\.\d+)?)(?:\s*,\s*(?:'(?:''|[^'])*'|-?\d+(?:\.\d+)?))*)\)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StorageMappedInRegex();

    [GeneratedRegex(
        @"^(?:1\s*=\s*1|TRUE)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PostgresTrueLiteralRegex();
}
