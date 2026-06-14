// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Honua.Protocols.OData.Services;

internal enum ODataComputeOperator
{
    Add,
    Subtract,
    Multiply,
    Divide,
    Modulo
}

/// <summary>
/// Optional unary numeric function wrapped around a <c>$compute</c> expression
/// (OData v4 canonical numeric functions). <see cref="None"/> means the raw
/// arithmetic value is emitted with no rounding.
/// </summary>
internal enum ODataComputeFunction
{
    None,
    Floor,
    Ceiling,
    Round
}

internal readonly record struct ODataComputeExpression(
    string LeftOperand,
    bool HasOperator,
    ODataComputeOperator Operator,
    string RightOperand,
    bool RightIsLiteral,
    double RightLiteral,
    ODataComputeFunction Function,
    string Alias);

/// <summary>
/// Shared evaluator for OData <c>$compute</c> expressions. It is the single source of
/// truth for the compute grammar so the <c>$compute</c> query option and the
/// <c>$apply=compute(...)</c> transform stay behaviorally identical.
/// </summary>
/// <remarks>
/// Grammar (per comma-separated segment):
/// <code>
///   [func(] operand [ op operand ] [)] as alias
/// </code>
/// where <c>op</c> is <c>add</c>, <c>sub</c>, <c>mul</c>, <c>div</c>, or <c>mod</c>,
/// <c>func</c> is the optional canonical numeric function <c>floor</c>, <c>ceiling</c>,
/// or <c>round</c>, and each operand is a field name or a numeric literal. The optional
/// <c>floor</c> wrapper makes histogram binning first-class — e.g.
/// <c>floor(population div 100000) as bin</c> — instead of requiring the
/// <c>x sub (x mod w)</c> arithmetic workaround.
/// </remarks>
internal static partial class ODataComputeService
{
    public static bool TryParse(
        string? compute,
        out ImmutableArray<ODataComputeExpression> expressions,
        out string? errorMessage)
    {
        expressions = ImmutableArray<ODataComputeExpression>.Empty;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(compute))
        {
            return true;
        }

        if (ODataParsingUtilities.HasEmptyCommaSeparatedToken(compute))
        {
            errorMessage = "$compute contains an empty expression segment.";
            return false;
        }

        var parts = compute.Split(',', StringSplitOptions.TrimEntries);
        var parsed = new List<ODataComputeExpression>(parts.Length);
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawPart in parts)
        {
            if (!TryParseSegment(rawPart, out var expression, out errorMessage))
            {
                return false;
            }

            if (!aliases.Add(expression.Alias))
            {
                errorMessage = $"Duplicate $compute alias '{expression.Alias}'.";
                return false;
            }

            parsed.Add(expression);
        }

        expressions = parsed.ToImmutableArray();
        return true;
    }

    /// <summary>
    /// Parses a single <c>$compute</c> segment. Public so the <c>$apply=compute(...)</c>
    /// transform can reuse the exact same grammar instead of re-implementing it.
    /// </summary>
    public static bool TryParseSegment(
        string segment,
        out ODataComputeExpression expression,
        out string? errorMessage)
    {
        expression = default;
        errorMessage = null;

        var match = ComputeExpressionRegex().Match(segment);
        if (!match.Success)
        {
            errorMessage = $"Invalid $compute expression segment '{segment}'.";
            return false;
        }

        var func = ODataComputeFunction.None;
        if (match.Groups["func"].Success && match.Groups["func"].Value.Length > 0)
        {
            if (!TryMapFunction(match.Groups["func"].Value, out func))
            {
                errorMessage = $"Unsupported $compute function '{match.Groups["func"].Value}'.";
                return false;
            }
        }

        var left = match.Groups["left"].Value;
        var alias = match.Groups["alias"].Value;

        var hasOperator = match.Groups["op"].Success && match.Groups["op"].Value.Length > 0;
        var op = ODataComputeOperator.Add;
        var right = string.Empty;
        var rightIsLiteral = false;
        var rightLiteral = 0d;

        if (hasOperator)
        {
            if (!TryMapOperator(match.Groups["op"].Value, out op))
            {
                errorMessage = $"Unsupported $compute operator '{match.Groups["op"].Value}'.";
                return false;
            }

            right = match.Groups["right"].Value;
            rightIsLiteral = double.TryParse(
                right,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out rightLiteral);
        }

        expression = new ODataComputeExpression(
            left,
            hasOperator,
            op,
            right,
            rightIsLiteral,
            rightLiteral,
            func,
            alias);
        return true;
    }

    public static void ApplyCompute(
        Dictionary<string, object?> payload,
        ImmutableArray<ODataComputeExpression> expressions)
    {
        if (expressions.IsDefaultOrEmpty)
        {
            return;
        }

        foreach (var expression in expressions)
        {
            payload[expression.Alias] = Evaluate(payload, expression);
        }
    }

    /// <summary>
    /// Evaluates a single compute expression against a value bag, returning the numeric
    /// result (or <see langword="null"/> when an operand is missing or a divide/modulo by
    /// zero occurs).
    /// </summary>
    public static double? Evaluate(
        IReadOnlyDictionary<string, object?> values,
        ODataComputeExpression expression)
    {
        if (!TryResolveOperand(values, expression.LeftOperand, out var left))
        {
            return null;
        }

        double result;
        if (!expression.HasOperator)
        {
            result = left;
        }
        else
        {
            double right;
            if (expression.RightIsLiteral)
            {
                right = expression.RightLiteral;
            }
            else if (!TryResolveOperand(values, expression.RightOperand, out right))
            {
                return null;
            }

            switch (expression.Operator)
            {
                case ODataComputeOperator.Add:
                    result = left + right;
                    break;
                case ODataComputeOperator.Subtract:
                    result = left - right;
                    break;
                case ODataComputeOperator.Multiply:
                    result = left * right;
                    break;
                case ODataComputeOperator.Divide when right != 0:
                    result = left / right;
                    break;
                case ODataComputeOperator.Modulo when right != 0:
                    result = left % right;
                    break;
                default:
                    return null;
            }
        }

        return expression.Function switch
        {
            ODataComputeFunction.Floor => Math.Floor(result),
            ODataComputeFunction.Ceiling => Math.Ceiling(result),
            ODataComputeFunction.Round => Math.Round(result, MidpointRounding.ToEven),
            _ => result
        };
    }

    private static bool TryResolveOperand(
        IReadOnlyDictionary<string, object?> values,
        string operand,
        out double numericValue)
    {
        numericValue = default;

        if (double.TryParse(operand, NumberStyles.Float, CultureInfo.InvariantCulture, out var literal))
        {
            numericValue = literal;
            return true;
        }

        if (!values.TryGetValue(operand, out var raw) || raw == null)
        {
            return false;
        }

        return raw switch
        {
            byte b => (numericValue = b) == b,
            short s => (numericValue = s) == s,
            int i => (numericValue = i) == i,
            long l => (numericValue = l) == l,
            float f => (numericValue = f) == f,
            double d => (numericValue = d) == d,
            decimal dec => (numericValue = (double)dec) == (double)dec,
            string text when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) =>
                (numericValue = parsed) == parsed,
            System.Text.Json.JsonElement json when json.ValueKind == System.Text.Json.JsonValueKind.Number && json.TryGetDouble(out var parsed) =>
                (numericValue = parsed) == parsed,
            _ => false
        };
    }

    private static bool TryMapOperator(string token, out ODataComputeOperator op)
    {
        var normalized = token.ToLowerInvariant();
        op = normalized switch
        {
            "add" => ODataComputeOperator.Add,
            "sub" => ODataComputeOperator.Subtract,
            "mul" => ODataComputeOperator.Multiply,
            "div" => ODataComputeOperator.Divide,
            "mod" => ODataComputeOperator.Modulo,
            _ => default
        };

        return normalized is "add" or "sub" or "mul" or "div" or "mod";
    }

    private static bool TryMapFunction(string token, out ODataComputeFunction function)
    {
        var normalized = token.ToLowerInvariant();
        function = normalized switch
        {
            "floor" => ODataComputeFunction.Floor,
            "ceiling" => ODataComputeFunction.Ceiling,
            "round" => ODataComputeFunction.Round,
            _ => default
        };

        return normalized is "floor" or "ceiling" or "round";
    }

    // Matches: [func(] left [ op right ] [)] as alias
    // - func: optional canonical numeric function (floor|ceiling|round) wrapping the value.
    // - op:   optional arithmetic operator; when absent the segment is a bare field/literal copy
    //         (useful as floor(field) for binning a single column).
    [GeneratedRegex(
        @"^(?:(?<func>floor|ceiling|round)\s*\(\s*)?(?<left>[-+]?\d+(?:\.\d+)?(?:[eE][-+]?\d+)?|[A-Za-z_][A-Za-z0-9_]*)(?:\s+(?<op>add|sub|mul|div|mod)\s+(?<right>[-+]?\d+(?:\.\d+)?(?:[eE][-+]?\d+)?|[A-Za-z_][A-Za-z0-9_]*))?(?(func)\s*\))\s+as\s+(?<alias>[A-Za-z_][A-Za-z0-9_]*)$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ComputeExpressionRegex();
}
