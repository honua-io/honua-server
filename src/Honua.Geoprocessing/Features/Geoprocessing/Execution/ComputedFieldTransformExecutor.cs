// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Runtime.CompilerServices;
using Honua.Geoprocessing.Execution.Expressions;
using Microsoft.Extensions.Options;
using NetTopologySuite.Features;

namespace Honua.Geoprocessing.Execution;

/// <summary>
/// <c>transform.computed-field</c> executor. Adds a new attribute derived from the
/// existing attributes. Two modes are supported and both are fully AOT-safe (no
/// reflection, no runtime code generation):
/// <list type="bullet">
/// <item>
/// The legacy fixed operation set selected via <c>op</c>: <c>concat</c>,
/// <c>add</c>, <c>subtract</c>, <c>multiply</c>, <c>divide</c>, <c>const</c>.
/// Rows whose arithmetic operands are non-numeric are dropped as row-level data
/// errors. This path is unchanged for back-compatibility.
/// </item>
/// <item>
/// The <c>op=expression</c> mode (or supplying the <c>expression</c> parameter)
/// evaluates a whitelisted expression such as
/// <c>upper(trim(name)) + "-" + cast(year, string)</c> through the
/// <see cref="ExpressionEngine"/>: a parsed AST over arithmetic, string,
/// conditional, comparison/logical, math, date, and field-reference primitives.
/// The expression is parsed once and evaluated per feature.
/// </item>
/// </list>
/// Ported from the GeoETL baseline ComputedFieldTransform onto the #1185
/// process/executor contract. Streams: a per-feature map with no cross-feature state.
/// </summary>
internal sealed class ComputedFieldTransformExecutor(
    IOptionsMonitor<GeoprocessingExecutorOptions> options)
    : FeatureCollectionTransformExecutor(options)
{
    internal const string HandledProcessId = "transform.computed-field";

    protected override string ProcessId => HandledProcessId;

    protected override async IAsyncEnumerable<IFeature> ApplyStream(
        IAsyncEnumerable<IFeature> source,
        StepInputReader inputs,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var target = inputs.Require("target");

        // Expression mode: triggered by op=expression or by supplying 'expression'
        // directly (so a caller can omit 'op' entirely for the new engine). The
        // expression is parsed once up front so a syntax error fails the job before
        // any feature is processed and the AST is reused across the whole stream.
        var hasExpression = inputs.TryGet("expression", out var expressionSource)
            && !string.IsNullOrWhiteSpace(expressionSource);
        var op = inputs.TryGet("op", out var opRaw) && !string.IsNullOrWhiteSpace(opRaw)
            ? opRaw!.ToLowerInvariant()
            : hasExpression ? "expression" : throw new TransformInputException("missing required input 'op'");

        if (op == "expression")
        {
            if (!hasExpression)
            {
                throw new TransformInputException(
                    "op 'expression' requires the 'expression' input (e.g. upper(trim(name)) + \"-\" + cast(year, string)).");
            }

            // Parse once up front so a syntax error fails the job before any feature is
            // processed; the AST is then reused across the whole stream.
            ExpressionNode tree;
            try
            {
                tree = ExpressionEngine.Parse(expressionSource!);
            }
            catch (ExpressionParseException ex)
            {
                throw new TransformInputException($"invalid expression: {ex.PublicMessage}");
            }

            await foreach (var feature in source.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var attributes = feature.Attributes ?? new AttributesTable();
                object? value;
                try
                {
                    value = tree.Evaluate(new AttributesExpressionContext(attributes));
                }
                catch (ExpressionEvaluationException)
                {
                    // Row-level data error (e.g. a non-numeric operand for this row) —
                    // drop the row, matching the legacy arithmetic-op behavior.
                    continue;
                }

                if (attributes.Exists(target))
                {
                    attributes[target] = value!;
                }
                else
                {
                    attributes.Add(target, value);
                }

                yield return new Feature(feature.Geometry, attributes);
            }

            yield break;
        }

        await foreach (var feature in source.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var attributes = feature.Attributes ?? new AttributesTable();
            if (TryCompute(inputs, op, attributes, out var value))
            {
                if (attributes.Exists(target))
                {
                    attributes[target] = value!;
                }
                else
                {
                    attributes.Add(target, value);
                }

                yield return new Feature(feature.Geometry, attributes);
            }

            // else: row-level data error (non-numeric operand) — drop the row.
        }
    }

    /// <summary>
    /// Adapts a NetTopologySuite <see cref="IAttributesTable"/> to the expression
    /// engine's field-reference context. Only named-attribute lookups are exposed —
    /// the grammar has no path to arbitrary members.
    /// </summary>
    private sealed class AttributesExpressionContext(IAttributesTable attributes) : IExpressionContext
    {
        public bool TryGetField(string name, out object? value)
        {
            if (attributes.Exists(name))
            {
                value = attributes.GetOptionalValue(name);
                return true;
            }

            value = null;
            return false;
        }
    }

    private static bool TryCompute(
        StepInputReader inputs,
        string op,
        IAttributesTable attributes,
        out object? value)
    {
        value = null;

        if (op == "const")
        {
            value = inputs.GetOrDefault("value", string.Empty);
            return true;
        }

        if (op == "concat")
        {
            var fields = SplitFields(inputs, "fields");
            var separator = inputs.GetOrDefault("separator", string.Empty);
            var parts = fields.Select(f =>
                attributes.Exists(f)
                    ? Convert.ToString(attributes.GetOptionalValue(f), CultureInfo.InvariantCulture) ?? string.Empty
                    : string.Empty);
            value = string.Join(separator, parts);
            return true;
        }

        if (!TryReadOperand(inputs, "left", attributes, out var left) ||
            !TryReadOperand(inputs, "right", attributes, out var right))
        {
            return false;
        }

        double result;
        switch (op)
        {
            case "add":
                result = left + right;
                break;
            case "subtract":
                result = left - right;
                break;
            case "multiply":
                result = left * right;
                break;
            case "divide":
                if (right == 0)
                {
                    return false;
                }

                result = left / right;
                break;
            default:
                throw new TransformInputException(
                    $"computed-field operation '{op}' is not supported. " +
                    "Supported: concat, add, subtract, multiply, divide, const.");
        }

        value = result;
        return true;
    }

    private static bool TryReadOperand(
        StepInputReader inputs,
        string key,
        IAttributesTable attributes,
        out double result)
    {
        result = 0;
        var token = inputs.Require(key);

        if (token.StartsWith('='))
        {
            return double.TryParse(
                token.AsSpan(1), NumberStyles.Float, CultureInfo.InvariantCulture, out result);
        }

        if (!attributes.Exists(token))
        {
            return false;
        }

        return double.TryParse(
            Convert.ToString(attributes.GetOptionalValue(token), CultureInfo.InvariantCulture),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out result);
    }

    private static string[] SplitFields(StepInputReader inputs, string key)
    {
        var raw = inputs.Require(key);
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
