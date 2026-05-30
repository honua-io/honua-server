// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Microsoft.Extensions.Options;
using NetTopologySuite.Features;

namespace Honua.Geoprocessing.Execution;

/// <summary>
/// <c>transform.attribute-filter</c> executor. Passes through only features whose
/// attribute satisfies a simple comparison, dropping the rest. Supported <c>op</c>
/// values: <c>eq</c>, <c>neq</c>, <c>gt</c>, <c>gte</c>, <c>lt</c>, <c>lte</c>,
/// <c>contains</c>, <c>exists</c>. Numeric operators parse both operands as doubles;
/// string operators compare ordinally. Ported from the GeoETL baseline
/// AttributeFilterTransform onto the #1185 process/executor contract.
/// </summary>
internal sealed class AttributeFilterTransformExecutor(
    IOptionsMonitor<GeoprocessingExecutorOptions> options)
    : FeatureCollectionTransformExecutor(options)
{
    internal const string HandledProcessId = "transform.attribute-filter";

    protected override string ProcessId => HandledProcessId;

    protected override List<IFeature> Apply(
        FeatureCollection source,
        StepInputReader inputs,
        CancellationToken cancellationToken)
    {
        var field = inputs.Require("field");
        var op = inputs.GetOrDefault("op", "eq");
        inputs.TryGet("value", out var value);

        var output = new List<IFeature>(source.Count);
        foreach (var feature in source)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Matches(feature, field, op, value))
            {
                output.Add(feature);
            }
        }

        return output;
    }

    private static bool Matches(IFeature feature, string field, string op, string? value)
    {
        var attributes = feature.Attributes;
        var present = attributes is not null && attributes.Exists(field);

        if (string.Equals(op, "exists", StringComparison.OrdinalIgnoreCase))
        {
            return present;
        }

        if (!present)
        {
            return false;
        }

        var actual = attributes!.GetOptionalValue(field);

        return op.ToLowerInvariant() switch
        {
            "eq" => StringEquals(actual, value),
            "neq" => !StringEquals(actual, value),
            "contains" => actual?.ToString()?.Contains(value ?? "", StringComparison.OrdinalIgnoreCase) ?? false,
            "gt" => CompareNumeric(actual, value) > 0,
            "gte" => CompareNumeric(actual, value) >= 0,
            "lt" => CompareNumeric(actual, value) < 0,
            "lte" => CompareNumeric(actual, value) <= 0,
            _ => throw new TransformInputException($"attribute-filter operator '{op}' is not supported.")
        };
    }

    private static bool StringEquals(object? actual, string? expected)
        => string.Equals(
            Convert.ToString(actual, CultureInfo.InvariantCulture),
            expected,
            StringComparison.Ordinal);

    private static int CompareNumeric(object? actual, string? expected)
    {
        if (!TryToDouble(actual, out var a) ||
            !double.TryParse(expected, NumberStyles.Float, CultureInfo.InvariantCulture, out var b))
        {
            // Non-numeric operands never satisfy a numeric comparison.
            return int.MinValue;
        }

        return a.CompareTo(b);
    }

    private static bool TryToDouble(object? value, out double result)
    {
        switch (value)
        {
            case null:
                result = 0;
                return false;
            case double d:
                result = d;
                return true;
            case long l:
                result = l;
                return true;
            case int i:
                result = i;
                return true;
            default:
                return double.TryParse(
                    Convert.ToString(value, CultureInfo.InvariantCulture),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out result);
        }
    }
}
