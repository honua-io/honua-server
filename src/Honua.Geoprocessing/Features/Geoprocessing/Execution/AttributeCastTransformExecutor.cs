// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;
using NetTopologySuite.Features;

namespace Honua.Geoprocessing.Execution;

/// <summary>
/// <c>transform.attribute-cast</c> executor. Coerces one attribute to a target CLR
/// type (<c>int</c>, <c>long</c>, <c>double</c>, <c>bool</c>, or <c>string</c>).
/// A value that cannot be coerced is a row-level data error: per the <c>onError</c>
/// policy it is dropped (default), set to null, or kept. Ported from the GeoETL
/// baseline AttributeCastTransform onto the #1185 process/executor contract. Streams:
/// a per-feature map with no cross-feature state.
/// </summary>
internal sealed class AttributeCastTransformExecutor(
    IOptionsMonitor<GeoprocessingExecutorOptions> options)
    : FeatureCollectionTransformExecutor(options)
{
    internal const string HandledProcessId = "transform.attribute-cast";

    protected override string ProcessId => HandledProcessId;

    protected override async IAsyncEnumerable<IFeature> ApplyStream(
        IAsyncEnumerable<IFeature> source,
        StepInputReader inputs,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var field = inputs.Require("field");
        var to = inputs.Require("to").ToLowerInvariant();
        var onError = inputs.GetOrDefault("onError", "drop").ToLowerInvariant();

        await foreach (var feature in source.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var attributes = feature.Attributes;
            if (attributes is null || !attributes.Exists(field))
            {
                yield return feature;
                continue;
            }

            var raw = attributes.GetOptionalValue(field);
            if (TryCoerce(raw, to, out var coerced))
            {
                attributes[field] = coerced!;
                yield return feature;
                continue;
            }

            switch (onError)
            {
                case "null":
                    attributes[field] = null!;
                    yield return feature;
                    break;
                case "keep":
                    yield return feature;
                    break;
                default:
                    // drop: row-level error, omit the feature from the output.
                    break;
            }
        }
    }

    private static bool TryCoerce(object? value, string to, out object? result)
    {
        result = null;
        var text = Convert.ToString(value, CultureInfo.InvariantCulture);

        switch (to)
        {
            case "string":
                result = text;
                return true;
            case "int":
                if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
                {
                    result = i;
                    return true;
                }

                return false;
            case "long":
                if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
                {
                    result = l;
                    return true;
                }

                return false;
            case "double":
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                {
                    result = d;
                    return true;
                }

                return false;
            case "bool":
                if (bool.TryParse(text, out var b))
                {
                    result = b;
                    return true;
                }

                if (string.Equals(text, "1", StringComparison.Ordinal))
                {
                    result = true;
                    return true;
                }

                if (string.Equals(text, "0", StringComparison.Ordinal))
                {
                    result = false;
                    return true;
                }

                return false;
            default:
                throw new TransformInputException(
                    $"attribute-cast target type '{to}' is not supported. " +
                    "Supported: int, long, double, bool, string.");
        }
    }
}
