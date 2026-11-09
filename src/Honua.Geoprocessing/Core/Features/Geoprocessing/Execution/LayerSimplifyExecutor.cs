// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NetTopologySuite.Features;
using NetTopologySuite.Simplify;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace Honua.Geoprocessing.Execution;

internal sealed class LayerSimplifyExecutor : LayerSourcedFeatureExecutor
{
    internal const string HandledProcessId = "generalization.simplify-layer";

    public LayerSimplifyExecutor(
        IServiceScopeFactory serviceScopeFactory,
        IOptionsMonitor<GeoprocessingExecutorOptions> options,
        ILogger<LayerSimplifyExecutor> logger,
        IOptions<LimitsOptions>? limitsOptions = null)
        : base(serviceScopeFactory, options, logger, limitsOptions)
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

        var vertices = LayerComputationBudget.CountVertices(source);
        LayerComputationBudget.EnsureTopologyWork(vertices, vertices, Options.CurrentValue.MaxTopologyWork);
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
