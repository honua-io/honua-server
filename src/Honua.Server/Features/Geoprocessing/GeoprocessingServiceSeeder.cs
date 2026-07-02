// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Domain;
using MetadataV2ServiceProtocols = Honua.Core.Features.Metadata.Domain.V2.ServiceProtocols;

namespace Honua.Server.Features.Geoprocessing;

/// <summary>
/// Ensures a fresh/default instance exposes at least one GeoServices <c>GPServer</c>
/// service so the geoprocessing facade is drivable out of the box (honua-server#2349).
///
/// The GPServer facade resolves the <c>{serviceId}</c> route segment against the
/// Metadata v2 graph and requires the resolved <see cref="MetadataV2Service"/> to
/// carry <see cref="ServiceProtocols.GPServer"/>; the task catalog itself always
/// exists (the built-in process catalog). On a bare instance with no published
/// layers and no activated snapshot the graph carries no such service, so every
/// <c>/rest/services/{serviceId}/GPServer/...</c> request 404s and GPServer cannot
/// be demonstrated end-to-end without a manual admin publish.
///
/// This seeder adds one purpose-built, layerless GP service (<c>geoprocessing</c>)
/// only when no service already exposes GPServer. Because every published service
/// (protocols default to all) and every V1-compat-projected service already carry
/// GPServer, the seeder is a no-op on any instance that already has services — it
/// only materializes the default GP entry point on a genuinely empty graph, and it
/// merges (never replaces) so it can never wipe an existing snapshot.
/// </summary>
internal sealed partial class GeoprocessingServiceSeeder(
    IMetadataV2GraphStore graphStore,
    ILogger<GeoprocessingServiceSeeder> logger)
{
    /// <summary>Stable graph identity of the seeded default GP service.</summary>
    internal const string ServiceId = "svc-geoprocessing";

    /// <summary>
    /// Route/name segment used to address the seeded service, i.e.
    /// <c>/rest/services/geoprocessing/GPServer</c>.
    /// </summary>
    internal const string ServiceName = "geoprocessing";

    /// <summary>
    /// Ensures a GPServer-capable service exists in the Metadata v2 graph. Idempotent:
    /// a no-op when any service already exposes <see cref="ServiceProtocols.GPServer"/>.
    /// </summary>
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

        foreach (var existing in baseGraph.Services)
        {
            if (MetadataV2ServiceProtocols.IsProtocolEnabled(existing, MetadataV2ServiceProtocols.GPServer))
            {
                Log.AlreadyPresent(logger, existing.Metadata.Name);
                return;
            }
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
