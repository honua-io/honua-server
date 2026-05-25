// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Runtime.CompilerServices;
using Honua.Core.Features.GeoETL.Abstractions;
using Honua.Core.Features.GeoETL.Domain;
using NetTopologySuite.Features;

namespace Honua.Core.Features.GeoETL.Services.Transforms;

/// <summary>
/// Phase 1 attribute-filter transform. Passes through only features whose attribute
/// satisfies a simple comparison, dropping the rest. Streaming and constant-memory.
/// </summary>
/// <remarks>
/// Required <see cref="TransformConfig.Options"/>:
/// <list type="bullet">
/// <item><c>field</c> — attribute name to test.</item>
/// <item><c>op</c> — one of <c>eq</c>, <c>neq</c>, <c>gt</c>, <c>gte</c>, <c>lt</c>,
/// <c>lte</c>, <c>contains</c>, <c>exists</c>.</item>
/// <item><c>value</c> — comparison operand (omitted for <c>exists</c>).</item>
/// </list>
/// Numeric operators parse both operands as doubles; string operators compare ordinally.
/// </remarks>
public sealed class AttributeFilterTransform : IPipelineTransform, ISchemaAwareTransform
{
    /// <summary>
    /// The transform type discriminator.
    /// </summary>
    public const string TransformType = "attribute-filter";

    /// <inheritdoc />
    public string Type => TransformType;

    /// <inheritdoc />
    public TransformSchemaEffect DescribeSchema(TransformConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!config.Options.TryGetValue("field", out var field) || string.IsNullOrWhiteSpace(field))
        {
            throw new InvalidOperationException("Attribute-filter transform requires a 'field' option.");
        }

        // A filter reads the field but does not change the schema (rows are dropped, not columns).
        return new TransformSchemaEffect(RequiredFields: [field], ProducedFields: [], RemovedFields: []);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<IFeature> TransformAsync(
        TransformConfig config,
        IAsyncEnumerable<IFeature> source,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(source);

        if (!config.Options.TryGetValue("field", out var field) || string.IsNullOrWhiteSpace(field))
        {
            throw new InvalidOperationException("Attribute-filter transform requires a 'field' option.");
        }

        var op = config.Options.TryGetValue("op", out var rawOp) ? rawOp : "eq";
        config.Options.TryGetValue("value", out var value);

        await foreach (var feature in source.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Matches(feature, field, op, value))
            {
                yield return feature;
            }
        }
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
            _ => throw new InvalidOperationException($"Attribute-filter operator '{op}' is not supported.")
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
