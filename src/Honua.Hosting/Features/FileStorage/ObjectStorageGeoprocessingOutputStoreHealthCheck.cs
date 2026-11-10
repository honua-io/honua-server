// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Honua.FileStorage;

// Durability and backup of the bucket are the deployment's to state; this check proves the host can
// reach the store it will write outputs to.
internal sealed class ObjectStorageGeoprocessingOutputStoreHealthCheck(
    IGeoprocessingOutputObjectStore store,
    IOptions<GeoprocessingOutputStagingOptions> options) : IHealthCheck
{
    private const string ProbeFolderName = ".reachability-probe";
    private const string UnreachableMessage = "The geoprocessing output object store cannot be reached.";

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            // A listing fails on a bucket or container that does not exist, where a lookup of one absent
            // object reads the same as an empty store.
            var probe = store.ListAsync($"{options.Value.KeyPrefix.TrimEnd('/')}/{ProbeFolderName}", cancellationToken);
            await using var listing = probe.GetAsyncEnumerator(cancellationToken);
            await listing.MoveNextAsync().ConfigureAwait(false);
            return HealthCheckResult.Healthy("Output object store reachable.", new Dictionary<string, object>
            {
                ["provider"] = store.Provider.ToString(),
                ["storeReference"] = store.StoreReference,
            });
        }
        catch (Exception storeFailure) when (storeFailure is not OutOfMemoryException and not OperationCanceledException)
        {
            // Bucket names, endpoints and provider error text stay out of the health payload.
            return HealthCheckResult.Unhealthy(UnreachableMessage);
        }
    }
}
