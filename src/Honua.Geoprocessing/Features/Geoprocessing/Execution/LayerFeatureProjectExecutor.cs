// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Geoprocessing.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NetTopologySuite.Features;

namespace Honua.Geoprocessing.Execution;

/// <summary>
/// <c>conversion.feature-project</c> layer-aware executor (#2325). The job-executable
/// counterpart of the layer-scoped "Project Feature Layer" op: it reprojects every
/// feature in a Honua catalog layer into <c>targetSrid</c>.
///
/// <para>
/// Reprojection reuses the canonical query pipeline rather than re-implementing
/// geodesy: the executor requests server-side reprojection to <c>targetSrid</c> from
/// <c>source.honua-layer</c> via <see cref="DagSourceRequest.OutputSrid"/> (the same
/// CRS handling every read path inherits), so the streamed features already carry the
/// target CRS coordinates. The op body is therefore a pass-through that re-emits the
/// reprojected layer as the canonical FeatureCollection artifact. This keeps a single
/// source of truth for datum/projection math and avoids the first-slice in-memory
/// transform limits of the single-geometry <c>geometry.project</c> executor.
/// </para>
/// </summary>
internal sealed class LayerFeatureProjectExecutor : LayerSourcedFeatureExecutor
{
    internal const string HandledProcessId = "conversion.feature-project";

    public LayerFeatureProjectExecutor(
        IServiceScopeFactory serviceScopeFactory,
        IOptionsMonitor<GeoprocessingExecutorOptions> options,
        ILogger<LayerFeatureProjectExecutor> logger)
        : base(serviceScopeFactory, options, logger)
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
