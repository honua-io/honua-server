// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Configuration;
using Honua.Core.Features.Geoprocessing.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NetTopologySuite.Features;

namespace Honua.Geoprocessing.Execution;

internal sealed class LayerFeatureProjectExecutor : LayerSourcedFeatureExecutor
{
    internal const string HandledProcessId = "conversion.feature-project";

    public LayerFeatureProjectExecutor(
        IServiceScopeFactory serviceScopeFactory,
        IOptionsMonitor<GeoprocessingExecutorOptions> options,
        ILogger<LayerFeatureProjectExecutor> logger,
        IOptions<LimitsOptions>? limitsOptions = null)
        : base(serviceScopeFactory, options, logger, limitsOptions)
    {
    }

    protected override string ProcessId => HandledProcessId;

    protected override DagSourceRequest CustomizeRequest(DagSourceRequest request, StepInputReader inputs)
    {
        // Drive reprojection through the shared query pipeline: request the target CRS
        // from source.honua-layer so features stream back already reprojected.
        return request with { OutputSrid = ReadTargetSrid(inputs) };
    }

    protected override List<IFeature> Apply(
        List<IFeature> source,
        StepInputReader inputs,
        CancellationToken cancellationToken)
    {
        // Validate the target SRID here too so a request that never reached
        // CustomizeRequest (defensive) still surfaces a classified input error.
        _ = ReadTargetSrid(inputs);
        return source;
    }

    private static int ReadTargetSrid(StepInputReader inputs)
    {
        if (!inputs.TryGet("targetSrid", out var raw)
            || !int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            || value <= 0)
        {
            throw new TransformInputException("missing or invalid required input 'targetSrid'; expected a positive integer.");
        }

        return value;
    }
}
