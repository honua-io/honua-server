// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NetTopologySuite.Features;
using NetTopologySuite.Simplify;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace Honua.Geoprocessing.Execution;

/// <summary>
/// <c>generalization.simplify-layer</c> layer-aware executor (#2325). The
/// job-executable counterpart of the layer-scoped "Simplify Layer" op: it streams a
/// Honua catalog layer through <c>source.honua-layer</c> and applies Douglas-Peucker
/// simplification to every geometry, carrying attributes through one-to-one. The
/// <c>tolerance</c> is expressed in the layer's spatial-reference units (degrees for
/// geographic, meters for projected), matching <c>geometry.simplify</c>. When
/// <c>preserveTopology</c> is true (the default) the topology-preserving simplifier is
/// used so rings do not collapse or self-intersect; otherwise the plain
/// Douglas-Peucker simplifier runs. Features whose geometry simplifies to empty are
/// dropped.
/// </summary>
internal sealed class LayerSimplifyExecutor : LayerSourcedFeatureExecutor
{
    internal const string HandledProcessId = "generalization.simplify-layer";

    public LayerSimplifyExecutor(
        IServiceScopeFactory serviceScopeFactory,
        IOptionsMonitor<GeoprocessingExecutorOptions> options,
        ILogger<LayerSimplifyExecutor> logger)
        : base(serviceScopeFactory, options, logger)
    {
    }

    protected override string ProcessId => HandledProcessId;

    protected override List<IFeature> Apply(
        List<IFeature> source,
        StepInputReader inputs,
        CancellationToken cancellationToken)
    {
        var tolerance = ReadTolerance(inputs);
        var preserveTopology = ReadBool(inputs, "preserveTopology", defaultValue: true);

        var output = new List<IFeature>(source.Count);
        foreach (var feature in source)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var geometry = feature.Geometry;
            if (geometry is null || geometry.IsEmpty)
            {
                continue;
            }

            NtsGeometry simplified = preserveTopology
                ? TopologyPreservingSimplifier.Simplify(geometry, tolerance)
                : DouglasPeuckerSimplifier.Simplify(geometry, tolerance);

            if (simplified is null || simplified.IsEmpty)
            {
                continue;
            }

            output.Add(new Feature(simplified, OverlayExecutorSupport.CopyAttributes(feature)));
        }

        return output;
    }

    private static double ReadTolerance(StepInputReader inputs)
    {
        if (!inputs.TryGet("tolerance", out var raw)
            || !double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            || !double.IsFinite(value)
            || value <= 0)
        {
            throw new TransformInputException("missing or invalid required input 'tolerance'; expected a finite number > 0.");
        }

        return value;
    }

    private static bool ReadBool(StepInputReader inputs, string name, bool defaultValue)
    {
        if (!inputs.TryGet(name, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "true" or "1" or "yes" => true,
            "false" or "0" or "no" => false,
            _ => throw new TransformInputException($"'{name}' must be a boolean (true|false)"),
        };
    }
}
