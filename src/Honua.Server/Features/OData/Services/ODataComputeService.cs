// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Honua.Server.Features.OData.Services;

internal enum ODataComputeOperator
{
    Add,
    Subtract,
    Multiply,
    Divide
}

internal readonly record struct ODataComputeExpression(
    string LeftOperand,
    ODataComputeOperator Operator,
    string RightOperand,
    bool RightIsLiteral,
    double RightLiteral,
    string Alias);

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

        var parts = compute.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var parsed = new List<ODataComputeExpression>(parts.Length);
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawPart in parts)
        {
            var match = ComputeExpressionRegex().Match(rawPart);
            if (!match.Success)
            {
                errorMessage = $"Invalid $compute expression segment '{rawPart}'.";
                return false;
            }

            var left = match.Groups["left"].Value;
            var opToken = match.Groups["op"].Value;
            var right = match.Groups["right"].Value;
            var alias = match.Groups["alias"].Value;

            if (!TryMapOperator(opToken, out var op))
            {
                errorMessage = $"Unsupported $compute operator '{opToken}'.";
                return false;
            }

            var rightIsLiteral = double.TryParse(
                right,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var rightLiteral);

            if (!aliases.Add(alias))
            {
                errorMessage = $"Duplicate $compute alias '{alias}'.";
                return false;
            }

            parsed.Add(new ODataComputeExpression(
                left,
                op,
                right,
                rightIsLiteral,
                rightLiteral,
                alias));
        }

        expressions = parsed.ToImmutableArray();
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
            if (!TryResolveOperand(payload, expression.LeftOperand, out var left))
            {
                payload[expression.Alias] = null;
                continue;
            }

            double right;
            if (expression.RightIsLiteral)
            {
                right = expression.RightLiteral;
            }
            else if (!TryResolveOperand(payload, expression.RightOperand, out right))
            {
                payload[expression.Alias] = null;
                continue;
            }

            payload[expression.Alias] = expression.Operator switch
            {
                ODataComputeOperator.Add => left + right,
                ODataComputeOperator.Subtract => left - right,
                ODataComputeOperator.Multiply => left * right,
                ODataComputeOperator.Divide when right != 0 => left / right,
                ODataComputeOperator.Divide => null,
                _ => null
            };
        }
    }

    private static bool TryResolveOperand(
        Dictionary<string, object?> values,
        string operand,
        out double numericValue)
    {
        numericValue = default;
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
            _ => default
        };

        return normalized is "add" or "sub" or "mul" or "div";
    }

    [GeneratedRegex(@"^(?<left>[A-Za-z_][A-Za-z0-9_]*)\s+(?<op>add|sub|mul|div)\s+(?<right>[-+]?\d+(?:\.\d+)?(?:[eE][-+]?\d+)?|[A-Za-z_][A-Za-z0-9_]*)\s+as\s+(?<alias>[A-Za-z_][A-Za-z0-9_]*)$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ComputeExpressionRegex();
}
