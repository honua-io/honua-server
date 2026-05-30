// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Microsoft.Extensions.Options;
using NetTopologySuite.Features;

namespace Honua.Geoprocessing.Execution;

/// <summary>
/// <c>transform.computed-field</c> executor. Adds a new attribute derived from the
/// existing attributes via a small, AOT-safe operation set — no expression engine,
/// no reflection. Supported <c>op</c> values: <c>concat</c>, <c>add</c>,
/// <c>subtract</c>, <c>multiply</c>, <c>divide</c>, <c>const</c>. Rows whose
/// arithmetic operands are non-numeric are dropped as row-level data errors.
/// Ported from the GeoETL baseline ComputedFieldTransform onto the #1185
/// process/executor contract.
/// </summary>
internal sealed class ComputedFieldTransformExecutor(
    IOptionsMonitor<GeoprocessingExecutorOptions> options)
    : FeatureCollectionTransformExecutor(options)
{
    internal const string HandledProcessId = "transform.computed-field";

    protected override string ProcessId => HandledProcessId;

    protected override List<IFeature> Apply(
        FeatureCollection source,
        StepInputReader inputs,
        CancellationToken cancellationToken)
    {
        var target = inputs.Require("target");
        var op = inputs.Require("op").ToLowerInvariant();

        var output = new List<IFeature>(source.Count);
        foreach (var feature in source)
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

                output.Add(new Feature(feature.Geometry, attributes));
            }

            // else: row-level data error (non-numeric operand) — drop the row.
        }

        return output;
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
