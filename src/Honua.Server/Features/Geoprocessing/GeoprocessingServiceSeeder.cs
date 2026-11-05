// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Domain;
using MetadataV2ServiceProtocols = Honua.Core.Features.Metadata.Domain.V2.ServiceProtocols;

namespace Honua.Server.Features.Geoprocessing;

// The seeder adds the default GP service only on an empty graph, and it merges so it cannot replace an existing snapshot.
internal sealed partial class GeoprocessingServiceSeeder(
    IMetadataV2GraphStore graphStore,
    ILogger<GeoprocessingServiceSeeder> logger)
{
    internal const string ServiceId = "svc-geoprocessing";

    internal const string ServiceName = "geoprocessing";

    public async Task EnsureSeededAsync(CancellationToken cancellationToken = default)
    {
        MetadataV2Graph baseGraph;
        try
        {
            var snapshot = await graphStore.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            baseGraph = snapshot.Graph;
        }
        catch (InvalidOperationException)
        {
            // No activated snapshot and no compat catalog to synthesize from: start
            // from an empty graph and force the first write below.
            baseGraph = new MetadataV2Graph();
        }

        var existingGpService = baseGraph.Services.FirstOrDefault(
            existing => MetadataV2ServiceProtocols.IsProtocolEnabled(existing, MetadataV2ServiceProtocols.GPServer));
        if (existingGpService is not null)
        {
            Log.AlreadyPresent(logger, existingGpService.Metadata.Name);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var gpService = new MetadataV2Service
        {
            Metadata = new MetadataV2ObjectMetadata
            {
                Id = ServiceId,
                Name = ServiceName,
                Description =
                    "Default geoprocessing service exposing the built-in GP task catalog "
                    + "through the GeoServices GPServer facade (honua-server#2349)."
            },
            ServiceType = MetadataV2ServiceType.Custom,
            Route = $"/rest/services/{ServiceName}/GPServer",
            Protocols = [MetadataV2ServiceProtocols.GPServer],
            Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
            // Discovery/submit on the GP facade is anonymous by design (execution is
            // still authorized by the shared process-execute grant), matching the
            // AllowAnonymous GPServer endpoints.
            AccessPolicy = new AccessPolicy { AllowAnonymous = true }
        };

        // Merge (append) rather than replace: preserve any services already present in
        // the base graph. We only reach here when none of them exposes GPServer.
        var updatedGraph = baseGraph with
        {
            Revision = Math.Max(baseGraph.Revision + 1, 1),
            GeneratedAt = now,
            Services = [.. baseGraph.Services, gpService]
        };

        // Force-write (null expectedEtag). We only write on a graph with no GPServer
        // service — in practice an empty/unactivated graph — so there is no persisted
        // state to lose and no concurrent writer at startup to race.
        await graphStore.SaveAsync(updatedGraph, expectedEtag: null, cancellationToken).ConfigureAwait(false);
        Log.Seeded(logger, ServiceName);
    }

    private static partial class Log
    {
        [LoggerMessage(9730, LogLevel.Information,
            "Default geoprocessing GPServer service seeded as '{ServiceName}'")]
        public static partial void Seeded(ILogger logger, string serviceName);

        [LoggerMessage(9731, LogLevel.Debug,
            "GPServer already reachable via service '{ServiceName}'; default GP service seed skipped")]
        public static partial void AlreadyPresent(ILogger logger, string serviceName);
    }
}
