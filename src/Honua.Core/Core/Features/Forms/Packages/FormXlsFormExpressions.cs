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

    internal static string? Compile(string expression, IReadOnlySet<string> allowedFields)
        => Compile(expression, new ExpressionScope(allowedFields));

    internal static string? CompileConstraint(string expression, IReadOnlySet<string> allowedFields)
        => Compile(expression, new ExpressionScope(allowedFields) { AllowsCurrentAnswer = true });

    private sealed record ExpressionScope(IReadOnlySet<string> AllowedFields)
    {
        internal bool AllowsCurrentAnswer { get; init; }

        internal bool Allows(Match token) =>
            (token.Value != "." || AllowsCurrentAnswer) &&
            (!token.Groups["field"].Success || AllowedFields.Contains(token.Groups["field"].Value));
    }

    private static string? Compile(string expression, ExpressionScope scope)
    {
        if (expression.Length is 0 or > 4000)
        {
            return null;
        }

        var position = 0;
        var normalizer = new TokenNormalizer();
        foreach (Match token in Tokens().Matches(expression))
        {
            if (token.Index != position || !scope.Allows(token) || !normalizer.Append(token))
            {
                return null;
            }
            position += token.Length;
        }

        return position == expression.Length && normalizer.IsComplete
            ? ValidateNormalizedExpression(normalizer.GetExpression())
            : null;
    }

    private static string? ValidateNormalizedExpression(string normalized)
    {
        var selectedTokens = Tokens().Matches(normalized)
            .Where(static token => token.Groups["name"].Success && token.Value == "selected")
            .Select(static token => token.Index).ToHashSet();
        var compiled = Selected().Replace(normalized, match => selectedTokens.Contains(match.Index)
            ? $"contains(concat(' ', {match.Groups[1].Value}, ' '), concat(' ', {match.Groups[2].Value}, ' '))"
            : match.Value);
        if (Tokens().Matches(compiled).Any(static token => token.Groups["name"].Success && token.Value == "selected"))
        {
            return null;
        }

        // The SDK parses both arms. The guard checks syntax without evaluating fabricated answers.
        return ExpressionEvaluator.EvaluateDetailed($"if(false, ({compiled}), true)", null).Succeeded ? compiled : null;
    }

    private sealed class TokenNormalizer
    {
        private readonly List<string> _parts = [];
        private readonly Stack<(string? Function, int Arguments)> _calls = new();
        private string? _pendingFunction;
        private string? _previousToken;

        internal bool IsComplete => _calls.Count == 0 && _pendingFunction is (null or "true" or "false");

        internal string GetExpression() => string.Concat(_parts);

        internal bool Append(Match token)
        {
            if (token.Groups["space"].Success)
            {
                _parts.Add(token.Value);
                return true;
            }

            if (token.Value != "(")
            {
                if (_pendingFunction is not (null or "true" or "false"))
                {
                    return false;
                }
                _pendingFunction = null;
            }

            var accepted = token.Value switch
            {
                "(" => OpenCall(),
                "," => AddArgument(),
                ")" => CloseCall(),
                _ => AppendOperand(token)
            };
            _previousToken = token.Value;
            return accepted;
        }

        private bool OpenCall()
        {
            if (_calls.Count >= 32)
            {
                return false;
            }
            _calls.Push((_pendingFunction, 0));
            _pendingFunction = null;
            _parts.Add("(");
            return true;
        }

        private bool AddArgument()
        {
            if (!_calls.TryPop(out var call) || call.Function is null)
            {
                return false;
            }
            _calls.Push((call.Function, call.Arguments + 1));
            _parts.Add(",");
            return true;
        }

        private bool CloseCall()
        {
            if (!_calls.TryPop(out var call))
            {
                return false;
            }

            var argumentCount = _previousToken == "(" ? 0 : call.Arguments + 1;
            if (call.Function is not null && !HasSupportedArity(call.Function, argumentCount))
            {
                return false;
            }

            if (call.Function is "true" or "false")
            {
                var start = _parts.LastIndexOf("(");
                _parts.RemoveRange(start, _parts.Count - start);
            }
            else
            {
                _parts.Add(")");
            }
            return true;
        }

        private bool AppendOperand(Match token)
        {
            if (token.Groups["name"].Success && token.Value is not ("and" or "or" or "not" or "div" or "mod"))
            {
                _pendingFunction = token.Value;
            }
            _parts.Add(token.Value switch
            {
                "div" => "/",
                "mod" => "%",
                _ when token.Value[0] == '"' => "'" + token.Value[1..^1].Replace("'", "''", StringComparison.Ordinal) + "'",
                _ => token.Value
            });
            return true;
        }
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

    internal static bool? EvaluateBoolean(string expression, IReadOnlyDictionary<string, object?> context)
    {
        var result = ExpressionEvaluator.EvaluateDetailed($"if(({expression}), true, false)", context);
        return result.Succeeded ? result.Value is true : null;
    }

    internal static JsonElement? Calculate(
        string expression, string? fieldType, IReadOnlyDictionary<string, object?> context)
    {
        var result = ExpressionEvaluator.EvaluateDetailed(expression, context);
        if (!result.Succeeded)
        {
            return null;
        }

        return result.Value switch
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
            _ => null
        };
    }
}
