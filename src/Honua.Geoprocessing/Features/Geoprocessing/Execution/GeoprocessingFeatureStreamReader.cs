// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NetTopologySuite.Features;

namespace Honua.Geoprocessing.Execution;

/// <summary>Canonical receipt authorization and verified stream access for protocol adapters.</summary>
/// <summary>A verified payload row and whether its coordinate frame came from the producer tag.</summary>
internal readonly record struct VerifiedFeatureRow(IFeature Feature, bool HasProducerSpatialReference);

internal interface IGeoprocessingFeatureStreamReader
{
    IAsyncEnumerable<IFeature> ReadAsync(string reference, ExecutionJobRecord authorizedJob,
        long maximumBytes, CancellationToken cancellationToken);

    IAsyncEnumerable<VerifiedFeatureRow> ReadRowsAsync(string reference, ExecutionJobRecord authorizedJob,
        long maximumBytes, CancellationToken cancellationToken);
}

/// <summary>Keeps durable job and workspace ownership checks inside the GP boundary.</summary>
internal sealed class GeoprocessingFeatureStreamReader(
    IOptionsMonitor<GeoprocessingOutputStagingOptions> options,
    IServiceScopeFactory workspaceScopes,
    IExecutionJobStore? jobs = null,
    IGeoprocessingOutputObjectStore? store = null) : IGeoprocessingFeatureStreamReader
{
    public IAsyncEnumerable<IFeature> ReadAsync(string reference, ExecutionJobRecord authorizedJob,
        long maximumBytes, CancellationToken cancellationToken)
    {
        if (jobs is null || store is null)
        {
            throw new InvalidOperationException("The shared feature stream output store is unavailable.");
        }

        return DurableFeatureStream.ReadAsync(reference, authorizedJob, jobs, store,
            options.CurrentValue, maximumBytes, workspaceScopes, cancellationToken);
    }

    public IAsyncEnumerable<VerifiedFeatureRow> ReadRowsAsync(string reference, ExecutionJobRecord authorizedJob,
        long maximumBytes, CancellationToken cancellationToken)
    {
        if (jobs is null || store is null)
        {
            throw new InvalidOperationException("The shared feature stream output store is unavailable.");
        }

        return DurableFeatureStream.ReadRowsAsync(reference, authorizedJob, jobs, store,
            options.CurrentValue, maximumBytes, workspaceScopes, cancellationToken);
    }
}
