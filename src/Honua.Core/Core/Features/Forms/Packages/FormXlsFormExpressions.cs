// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Honua.Sdk.Field.Forms.Expressions;

namespace Honua.Core.Features.Forms.Packages;

internal static partial class FormXlsFormExpressions
{
    internal const string Language = "xlsform";

    internal static bool IsEnabled(FormPackageDocument package)
        => string.Equals(package.ExpressionLanguage, Language, StringComparison.Ordinal);

    [GeneratedRegex("""'(?:(?:'')|[^'])*'|"[^"]*"|\$\{(?<field>[A-Za-z_][A-Za-z0-9_-]*)\}|(?<name>[A-Za-z_][A-Za-z_0-9-]*)|(?<number>[0-9]+(?:\.[0-9]*)?|\.[0-9]+)|(?<symbol>[.(),+*<>!=\-])|(?<space>\s+)""", RegexOptions.CultureInvariant)]
    private static partial Regex Tokens();

    [GeneratedRegex("""(?<![\w-])selected\s*\(\s*(\$\{[A-Za-z_][A-Za-z0-9_-]*\}|\.)\s*,\s*('(?:''|[^'])*')\s*\)""", RegexOptions.CultureInvariant)]
    private static partial Regex Selected();

    internal static bool TryCompile(
        string expression,
        IReadOnlySet<string> allowedFields,
        bool allowSelf,
        out string compiled)
    {
        compiled = string.Empty;
        if (expression.Length is 0 or > 4000)
        {
            return false;
        }

        var position = 0;
        var depth = 0;
        var parts = new List<string>();
        var calls = new Stack<(string? Function, int Arguments)>();
        string? pendingFunction = null;
        string? previousToken = null;
        foreach (Match token in Tokens().Matches(expression))
        {
            if (token.Index != position)
            {
                return false;
            }

            position += token.Length;
            if (token.Groups["field"].Success && !allowedFields.Contains(token.Groups["field"].Value))
            {
                return false;
            }

            if (token.Groups["space"].Success)
            {
                parts.Add(token.Value);
                continue;
            }

            if (pendingFunction is not null && token.Value != "(" && pendingFunction is not ("true" or "false"))
            {
                return false;
            }

            if (token.Value != "(" && pendingFunction is ("true" or "false"))
            {
                pendingFunction = null;
            }

            if (token.Value == "(" && ++depth > 32 || token.Value == ")" && --depth < 0)
            {
                return false;
            }

            switch (token.Value)
            {
                case "(":
                    calls.Push((pendingFunction, 0));
                    pendingFunction = null;
                    break;
                case ",":
                    if (!calls.TryPop(out var call) || call.Function is null)
                    {
                        return false;
                    }
                    calls.Push((call.Function, call.Arguments + 1));
                    break;
                case ")":
                    var completed = calls.Pop();
                    var argumentCount = previousToken == "(" ? 0 : completed.Arguments + 1;
                    if (completed.Function is not null && !HasSupportedArity(completed.Function, argumentCount))
                    {
                        return false;
                    }
                    if (completed.Function is "true" or "false")
                    {
                        // The SDK represents booleans as literals.
                        while (parts[^1] != "(")
                        {
                            parts.RemoveAt(parts.Count - 1);
                        }
                        parts.RemoveAt(parts.Count - 1);
                        previousToken = ")";
                        continue;
                    }
                    break;
                default:
                    if (token.Groups["name"].Success && token.Value is not ("and" or "or" or "not" or "div" or "mod"))
                    {
                        pendingFunction = token.Value;
                    }
                    break;
            }

            if (token.Value == "." && !allowSelf)
            {
                return false;
            }

            previousToken = token.Value;
            parts.Add(token.Value switch
            {
                "div" => "/",
                "mod" => "%",
                _ when token.Value[0] == '"' => "'" + token.Value[1..^1].Replace("'", "''", StringComparison.Ordinal) + "'",
                _ => token.Value
            });
        }

        if (position != expression.Length || depth != 0 || pendingFunction is not (null or "true" or "false"))
        {
            return false;
        }

        var normalized = string.Concat(parts);
        var selectedTokens = Tokens().Matches(normalized)
            .Where(static token => token.Groups["name"].Success && token.Value == "selected")
            .Select(static token => token.Index).ToHashSet();
        compiled = Selected().Replace(normalized, match => selectedTokens.Contains(match.Index)
            ? $"contains(concat(' ', {match.Groups[1].Value}, ' '), concat(' ', {match.Groups[2].Value}, ' '))"
            : match.Value);
        if (Tokens().Matches(compiled).Any(static token => token.Groups["name"].Success && token.Value == "selected"))
        {
            return false;
        }

        // The SDK parses both arms before evaluation. A false guard lets us validate the
        // syntax without evaluating expressions against fabricated input values.
        return ExpressionEvaluator.EvaluateDetailed($"if(false, ({compiled}), true)", null).Succeeded;
    }

    private static bool HasSupportedArity(string function, int count) => function switch
    {
        "true" or "false" => count == 0,
        "floor" or "ceiling" or "round" or "string-length" or "number" or "string" => count == 1,
        "contains" or "selected" => count == 2,
        "if" => count == 3,
        "concat" => count >= 2,
        _ => false
    };

    internal static object? ToContextValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number when value.TryGetDouble(out var number) && double.IsFinite(number) => number,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Array when value.EnumerateArray().All(static item => item.ValueKind == JsonValueKind.String)
            => string.Join(' ', value.EnumerateArray().Select(static item => item.GetString())),
        _ => null
    };

    internal static bool TryEvaluateBoolean(string expression, IReadOnlyDictionary<string, object?> context, out bool value)
    {
        var result = ExpressionEvaluator.EvaluateDetailed($"if(({expression}), true, false)", context);
        value = result.Value is true;
        return result.Succeeded;
    }

    internal static bool TryCalculate(
        string expression, string? fieldType, IReadOnlyDictionary<string, object?> context, out JsonElement value)
    {
        var result = ExpressionEvaluator.EvaluateDetailed(expression, context);
        value = default;
        if (!result.Succeeded)
        {
            return false;
        }

        value = result.Value switch
        {
            null => JsonSerializer.SerializeToElement<string?>(null, FormPackageJsonContext.Default.String),
            string text => JsonSerializer.SerializeToElement(text, FormPackageJsonContext.Default.String),
            bool boolean when fieldType == "text"
                => JsonSerializer.SerializeToElement(boolean ? "true" : "false", FormPackageJsonContext.Default.String),
            bool boolean => JsonSerializer.SerializeToElement(boolean, FormPackageJsonContext.Default.Boolean),
            double number when fieldType == "integer" && double.IsInteger(number) &&
                number >= long.MinValue && number < (double)long.MaxValue
                => JsonSerializer.SerializeToElement((long)number, FormPackageJsonContext.Default.Int64),
            double number when double.IsFinite(number) && fieldType == "text"
                => JsonSerializer.SerializeToElement(number.ToString(CultureInfo.InvariantCulture), FormPackageJsonContext.Default.String),
            double number when double.IsFinite(number)
                => JsonSerializer.SerializeToElement(number, FormPackageJsonContext.Default.Double),
            _ => default
        };
        return value.ValueKind != JsonValueKind.Undefined;
    }
}
