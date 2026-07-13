// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Core.Configuration;
using System.Collections.Immutable;
using Honua.Infrastructure.Licensing;
using Honua.Protocols.GeoServices;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.Protocols.GeoServices.FeatureServer.Services;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Validation;
using Honua.ServiceDefaults;
using Microsoft.Extensions.Options;

// Behavior reference: Replication durability (#383)
// Uses IChangeTracker for monotonic generation counters and incremental delta extraction

namespace Honua.Protocols.GeoServices.FeatureServer;

internal static partial class FeatureServerEndpoints
{
    private static IResult? RequireOfflineSyncEntitlement(HttpContext context)
        => LicenseGate.RequireEntitlement(
            context,
            FeatureCatalog.FieldOpsOfflineSyncKey,
            "GeoServices offline sync");

    /// <summary>
    /// Rejects replica write operations (createReplica / synchronizeReplica / unRegisterReplica) on
    /// backends that cannot durably persist replicas — read-only providers (DuckDB, MySQL/MariaDB)
    /// whose <see cref="IReplicaRepository"/> is a no-op. Returns a conformant Esri-shaped
    /// <c>501 Not Implemented</c> error so an Esri client receives an explicit "operation not
    /// supported" response instead of a silently no-op'd sync that is never durably applied (#2136).
    /// Returns <c>null</c> when the active backend supports replica persistence.
    /// </summary>
    private static IResult? RequireReplicaPersistenceSupport(HttpContext context)
    {
        var replicaRepository = context.RequestServices.GetRequiredService<IReplicaRepository>();
        if (replicaRepository.SupportsReplicaPersistence)
        {
            return null;
        }

        return StandardErrorHelpers.CreateNotImplemented(
            context,
            "Offline replica synchronization is not supported by this service's data store.",
            ["The active feature provider is read-only and cannot durably persist replicas. createReplica, synchronizeReplica, and unRegisterReplica require a Postgres-backed service."]);
    }

    private static async Task<IResult> HandleReplicas(
        string serviceId,
        HttpContext context)
    {
        using var activity = HonuaTelemetry.ActivitySource.StartActivity("featureserver.replicas");
        activity?.SetTag(HonuaTelemetry.Tags.ServiceId, serviceId);

        var entitlementGate = RequireOfflineSyncEntitlement(context);
        if (entitlementGate is not null)
        {
            return entitlementGate;
        }

        var cancellationToken = GetTimeoutAwareCancellationToken(context);
        var serviceValidationResult = await ValidateReplicationServiceV2Async(serviceId, context, cancellationToken);
        if (serviceValidationResult.ErrorResult is not null)
        {
            return serviceValidationResult.ErrorResult!;
        }

        if (!TryValidateOutputFormat(
                context.Request.Query["f"],
                JsonOnlyFormats,
                out _,
                out var formatError))
        {
            return StandardErrorHelpers.CreateBadRequest(context, formatError!);
        }

        if (!TryParseReplicaBooleanQuery(context, "returnLastSyncDate", out var returnLastSyncDate, out var boolError))
        {
            return boolError!;
        }

        var service = serviceValidationResult.Service!;
        var snapshot = serviceValidationResult.Snapshot!;
        var serviceLayers = ResolveServiceReplicaLayersV2(service, snapshot);
        var accessError = AccessPolicyHelpers.RequireAnyResourceAccess(
            context,
            serviceLayers.Select(layer => layer.Resource),
            service);
        if (accessError != null)
        {
            return accessError;
        }

        var accessibleLayerIds = serviceLayers
            .Where(layer => AccessPolicyHelpers.IsResourceAccessible(context, layer.Resource, service))
            .Select(layer => layer.PublicLayerId)
            .ToHashSet();

        // Serve /replicas from the live registry (IReplicaStore) rather than a lagging cached snapshot, so
        // the enumeration reflects createReplica / unRegisterReplica immediately (#1775).
        var replicaStore = context.RequestServices.GetRequiredService<IReplicaStore>();
        var replicas = await replicaStore.ListByServiceAsync(serviceId, cancellationToken).ConfigureAwait(false);

        var response = replicas
            .Where(record => record.LayerIds.Length > 0 && record.LayerIds.All(accessibleLayerIds.Contains))
            .Select(record => new ReplicaSummary
            {
                ReplicaName = record.ReplicaName,
                ReplicaId = record.ReplicaId,
                LastSyncDate = returnLastSyncDate ? record.LastSyncTime.ToUnixTimeMilliseconds() : null
            })
            .ToArray();

        return Results.Json(response, FeatureServerJsonContext.Default.ReplicaSummaryArray, contentType: "application/json");
    }

    private static async Task<IResult> HandleReplicaInfo(
        string serviceId,
        string replicaId,
        HttpContext context)
    {
        using var activity = HonuaTelemetry.ActivitySource.StartActivity("featureserver.replicaInfo");
        activity?.SetTag(HonuaTelemetry.Tags.ServiceId, serviceId);
        activity?.SetTag("honua.replicaId", replicaId);

        var entitlementGate = RequireOfflineSyncEntitlement(context);
        if (entitlementGate is not null)
        {
            return entitlementGate;
        }

        var cancellationToken = GetTimeoutAwareCancellationToken(context);
        var serviceValidationResult = await ValidateReplicationServiceV2Async(serviceId, context, cancellationToken);
        if (serviceValidationResult.ErrorResult is not null)
        {
            return serviceValidationResult.ErrorResult!;
        }

        if (!TryValidateOutputFormat(
                context.Request.Query["f"],
                JsonOnlyFormats,
                out _,
                out var formatError))
        {
            return StandardErrorHelpers.CreateBadRequest(context, formatError!);
        }

        var service = serviceValidationResult.Service!;
        var snapshot = serviceValidationResult.Snapshot!;
        var replicaRepository = context.RequestServices.GetRequiredService<IReplicaRepository>();
        var record = await replicaRepository.GetAsync(replicaId, cancellationToken).ConfigureAwait(false);
        if (record == null || !string.Equals(record.Value.ServiceId, serviceId, StringComparison.OrdinalIgnoreCase))
        {
            return StandardErrorHelpers.CreateNotFound(context,
                $"Replica '{replicaId}' not found for service '{serviceId}'.");
        }

        // Snapshot the resolved value once: the record is a nullable struct, and reading
        // it back through `record.Value` inside the closure below defeats the null guard
        // above from a static-analysis standpoint even though it is provably non-null here.
        var replicaRecord = record.Value;

        var replica = ToReplicaState(replicaRecord);
        if (!TryResolveReplicaLayersV2(context, service, snapshot, replica, AccessScope.Read, out var replicaLayers, out var replicaLayerError))
        {
            return replicaLayerError ?? StandardErrorHelpers.CreateNotFound(
                context,
                $"Replica '{replicaId}' not found for service '{serviceId}'.");
        }

        var layerServerGens = replicaRecord.SyncModel.Equals("perLayer", StringComparison.OrdinalIgnoreCase)
            ? System.Text.Json.JsonSerializer.Serialize(
                replicaRecord.LayerIds
                    .Distinct()
                    .Select(id => new ReplicaInfoLayerServerGeneration
                    {
                        Id = id,
                        ServerGen = replicaRecord.LastSyncGeneration,
                        ServerSibGen = replicaRecord.LastSyncGeneration
                    })
                    .ToArray(),
                FeatureServerJsonContext.Default.ReplicaInfoLayerServerGenerationArray)
            : null;

        var response = new ReplicaInfoResponse
        {
            ReplicaName = replicaRecord.ReplicaName,
            ReplicaId = replicaRecord.ReplicaId,
            SyncModel = replicaRecord.SyncModel,
            ReplicaServerGen = replicaRecord.SyncModel.Equals("perReplica", StringComparison.OrdinalIgnoreCase)
                ? replicaRecord.LastSyncGeneration
                : null,
            LayerServerGens = layerServerGens,
            CreationDate = replicaRecord.CreatedAt.ToUnixTimeMilliseconds(),
            LastSyncDate = replicaRecord.LastSyncTime.ToUnixTimeMilliseconds(),
            Layers = replicaLayers.Select(layer => new ReplicaInfoLayer
            {
                Id = layer.PublicLayerId
            }).ToArray()
        };

        return Results.Json(response, FeatureServerJsonContext.Default.ReplicaInfoResponse, contentType: "application/json");
    }

    private static async Task<IResult> HandleCreateReplica(
        string serviceId,
        HttpContext context)
    {
        using var activity = HonuaTelemetry.ActivitySource.StartActivity("featureserver.createReplica");
        activity?.SetTag(HonuaTelemetry.Tags.ServiceId, serviceId);

        var entitlementGate = RequireOfflineSyncEntitlement(context);
        if (entitlementGate is not null)
        {
            return entitlementGate;
        }

        var unsupportedBackend = RequireReplicaPersistenceSupport(context);
        if (unsupportedBackend is not null)
        {
            return unsupportedBackend;
        }

        var cancellationToken = GetTimeoutAwareCancellationToken(context);
        var serviceValidationResult = await ValidateReplicationServiceV2Async(serviceId, context, cancellationToken);
        if (serviceValidationResult.ErrorResult is not null)
        {
            return serviceValidationResult.ErrorResult!;
        }

        var service = serviceValidationResult.Service!;
        var snapshot = serviceValidationResult.Snapshot!;

        var writeAccessError = await RequireAnyServiceResourceWriteAccessBeforeBodyAsync(
            context,
            service,
            snapshot,
            cancellationToken);
        if (writeAccessError != null)
        {
            return writeAccessError;
        }

        var replicaStore = context.RequestServices.GetRequiredService<IReplicaStore>();

        var (values, readError) = await TryReadRequestValuesAsync(context.Request, cancellationToken);
        if (values == null)
        {
            if (TryGetUnsupportedMediaType(readError, out var receivedContentType))
            {
                return CreateUnsupportedRequestContentTypeResult(context, receivedContentType);
            }

            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid createReplica request",
                [readError ?? "Invalid request body."]);
        }

        var replicaName = GetValueString(values, "replicaName");
        if (string.IsNullOrWhiteSpace(replicaName))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "replicaName parameter is required");
        }

        var layersParam = GetValueString(values, "layers");
        var syncModel = GetValueString(values, "syncModel") ?? "perReplica";

        if (!TryResolveReplicaLayerIdsV2(
                context,
                service,
                snapshot,
                layersParam,
                out var layerIds,
                out var layerError,
                AccessScope.Write))
        {
            return layerError ?? StandardErrorHelpers.CreateBadRequest(
                context,
                "layers parameter contains one or more invalid layer IDs for this service.");
        }

        var createLayers = ResolveServiceReplicaLayersV2(service, snapshot)
            .Where(layer => layerIds.Contains(layer.PublicLayerId))
            .ToArray();
        var createRbacError = await RequireReplicaWriteAccessV2Async(
            context,
            service,
            createLayers,
            cancellationToken);
        if (createRbacError != null)
        {
            return createRbacError;
        }

        var replicaId = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;

        var changeTracker = context.RequestServices.GetRequiredService<IChangeTracker>();
        var currentGen = await changeTracker.GetCurrentGenerationAsync(cancellationToken);

        var record = new ReplicaState(
            replicaId,
            replicaName,
            serviceId,
            syncModel,
            layerIds,
            now)
        {
            LastSyncGeneration = currentGen
        };
        await replicaStore.SetAsync(record, cancellationToken: cancellationToken);

        var response = new CreateReplicaResponse
        {
            ReplicaId = replicaId,
            ReplicaName = replicaName,
            SyncModel = syncModel,
            ServerGen = currentGen,
            Layers = layerIds.Select(id => new ReplicaLayerInfo
            {
                Id = id,
                ServerGen = currentGen
            }).ToArray(),
            CreationDate = now.ToUnixTimeMilliseconds()
        };

        return Results.Json(response, FeatureServerJsonContext.Default.CreateReplicaResponse, contentType: "application/json");
    }

    private static async Task<IResult> HandleExtractChanges(
        string serviceId,
        HttpContext context)
    {
        using var activity = HonuaTelemetry.ActivitySource.StartActivity("featureserver.extractChanges");
        activity?.SetTag(HonuaTelemetry.Tags.ServiceId, serviceId);

        var entitlementGate = RequireOfflineSyncEntitlement(context);
        if (entitlementGate is not null)
        {
            return entitlementGate;
        }

        var cancellationToken = GetTimeoutAwareCancellationToken(context);
        var serviceValidationResult = await ValidateReplicationServiceV2Async(serviceId, context, cancellationToken);
        if (serviceValidationResult.ErrorResult is not null)
        {
            return serviceValidationResult.ErrorResult!;
        }

        var service = serviceValidationResult.Service!;
        var snapshot = serviceValidationResult.Snapshot!;
        var replicaStore = context.RequestServices.GetRequiredService<IReplicaStore>();

        var (values, readError) = await TryReadRequestValuesAsync(context.Request, cancellationToken);
        if (values == null)
        {
            if (TryGetUnsupportedMediaType(readError, out var receivedContentType))
            {
                return CreateUnsupportedRequestContentTypeResult(context, receivedContentType);
            }

            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid extractChanges request",
                [readError ?? "Invalid request body."]);
        }

        var replicaId = GetValueString(values, "replicaID");
        if (string.IsNullOrWhiteSpace(replicaId))
        {
            // No replicaID: the serverGen-based change-tracking flow used by the ArcGIS
            // SDK FeatureLayerCollection.extract_changes(). The caller supplies the
            // generation to extract from (serverGen/serverGens) and optionally a layers
            // filter; we return changes since that generation without requiring a
            // registered replica. Only available on sync/change-tracking-enabled services.
            return await HandleExtractChangesWithoutReplicaAsync(
                serviceId, context, service, snapshot, values, activity, cancellationToken);
        }

        activity?.SetTag("honua.replicaId", replicaId);

        var replica = await replicaStore.GetAsync(replicaId, cancellationToken);
        if (replica == null)
        {
            return StandardErrorHelpers.CreateNotFound(context,
                $"Replica '{replicaId}' not found.");
        }

        if (!string.Equals(replica.ServiceId, serviceId, StringComparison.OrdinalIgnoreCase))
        {
            return StandardErrorHelpers.CreateNotFound(context,
                $"Replica '{replicaId}' not found for service '{serviceId}'.");
        }

        if (!TryResolveReplicaLayersV2(context, service, snapshot, replica, AccessScope.Read, out var replicaLayers, out var replicaLayerError))
        {
            return replicaLayerError ?? StandardErrorHelpers.CreateNotFound(
                context,
                $"Replica '{replicaId}' not found for service '{serviceId}'.");
        }

        var changeTracker = context.RequestServices.GetRequiredService<IChangeTracker>();
        var currentGen = await changeTracker.GetCurrentGenerationAsync(cancellationToken);

        // Assemble the per-layer delta since the replica's last-sync generation. The same
        // change-tracking delta-assembly serves the synchronizeReplica(download) path so both
        // directions deliver identical changes (the download bug, #1775, was that the download
        // path never assembled this delta).
        var (layerChanges, deltaError) = await BuildReplicaLayerChangesAsync(
            context,
            replicaId,
            replica.LastSyncGeneration,
            replicaLayers,
            changeTracker,
            cancellationToken);
        if (deltaError is not null)
        {
            return deltaError;
        }

        var minGen = replica.LastSyncGeneration;
        var maxGen = currentGen;

        var response = new ExtractChangesResponse
        {
            Success = true,
            ReplicaId = replicaId,
            LayerChanges = layerChanges!,
            ServerGen = currentGen,
            MinServerGen = minGen,
            MaxServerGen = maxGen
        };

        return Results.Json(response, FeatureServerJsonContext.Default.ExtractChangesResponse, contentType: "application/json");
    }

    /// <summary>
    /// Assembles the per-layer server-to-client change delta for a replica since
    /// <paramref name="sinceGeneration"/>, reusing the change-tracking engine that backs
    /// <c>extractChanges</c>. Returns the per-layer adds/updates/deletes (with the actual inserted and
    /// updated feature payloads), or a bad-request <see cref="IResult"/> when a layer's change set exceeds
    /// the configured per-layer record limit. A <paramref name="sinceGeneration"/> of 0 means pre-migration
    /// or first sync and falls back to "all current features as adds" for backward compatibility. This is
    /// the shared download-assembly path consumed by both <c>extractChanges</c> and the
    /// <c>synchronizeReplica</c> download direction (#1775).
    /// </summary>
    private static async Task<(LayerChanges[]? LayerChanges, IResult? Error)> BuildReplicaLayerChangesAsync(
        HttpContext context,
        string replicaId,
        long sinceGeneration,
        ReplicaLayerV2[] replicaLayers,
        IChangeTracker changeTracker,
        CancellationToken cancellationToken,
        long? maxGeneration = null)
    {
        var layerChanges = new List<LayerChanges>(replicaLayers.Length);

        var featureReader = context.RequestServices.GetRequiredService<IFeatureReader>();
        var queryLimits = context.RequestServices.GetRequiredService<IOptions<LimitsOptions>>().Value.Query;

        // Query real incremental deltas from the change log. For sinceGeneration == 0 (a first sync,
        // or data that predates migration 012) this returns the baseline Insert rows seeded by
        // migration 059 for every pre-migration feature plus any subsequent edits, so the first
        // gen-0 sync delivers a one-time snapshot-as-adds and every later sync is a pure delta from
        // the recorded server generation (#1876). The all-features fallback below is only taken for a
        // layer that has features but NO change-log coverage at all — a backend whose change tracker
        // is a no-op (DuckDB/MySql/SQL Server) or a layer the trigger never observed — so those
        // backends still receive a full snapshot on first sync.
        var changes = await changeTracker.GetChangesSinceAsync(
            sinceGeneration,
            replicaLayers.Select(layer => layer.StorageLayerId).Distinct().ToArray(),
            cancellationToken);

        // Optional upper bound on the delta. Currently always null (callers pass null) — the full
        // delta from sinceGeneration to the current generation is delivered, including any edits the
        // uploading client just applied. Clients reconcile their own edits using the objectIds
        // returned in the upload response (BH5-015). The parameter is kept for backward compatibility
        // with any future caller that needs a bounded window (e.g. a selective replay path).
        if (maxGeneration is { } upperBound)
        {
            changes = changes.Where(c => c.Generation <= upperBound).ToList();
        }

        // Group collapsed changes by layer
        var changesByLayer = changes
            .GroupBy(c => c.LayerId)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var layer in replicaLayers)
        {
            if (changesByLayer.TryGetValue(layer.StorageLayerId, out var layerChangeList))
            {
                // Collect objectIds by operation type
                var insertIds = layerChangeList
                    .Where(c => c.Operation == FeatureChangeOperation.Insert)
                    .Select(c => c.ObjectId)
                    .ToArray();

                var updateIds = layerChangeList
                    .Where(c => c.Operation == FeatureChangeOperation.Update)
                    .Select(c => c.ObjectId)
                    .ToArray();

                var deleteIds = layerChangeList
                    .Where(c => c.Operation == FeatureChangeOperation.Delete)
                    .Select(c => c.ObjectId)
                    .ToArray();

                if (insertIds.Length > queryLimits.MaxRecordCount ||
                    updateIds.Length > queryLimits.MaxRecordCount ||
                    deleteIds.Length > queryLimits.MaxRecordCount)
                {
                    return (null, StandardErrorHelpers.CreateBadRequest(
                        context,
                        $"Replica '{replicaId}' extract exceeds the configured per-layer change limit.",
                        [$"Layer {layer.PublicLayerId} exceeded {queryLimits.MaxRecordCount} adds, updates, or deletes in a single extract."]));
                }

                // Query actual features for inserts and updates
                GeoServicesFeature[]? addFeatures = null;
                if (insertIds.Length > 0)
                {
                    var query = new FeatureQuery { ObjectIds = ImmutableArray.Create(insertIds) };
                    var result = await featureReader.QueryAsync(layer.StorageLayerId, query, cancellationToken);
                    addFeatures = result.Items
                        .Select(f => ConvertFeatureToGeoServices(f, layer.Resource))
                        .ToArray();
                }

                GeoServicesFeature[]? updateFeatures = null;
                if (updateIds.Length > 0)
                {
                    var query = new FeatureQuery { ObjectIds = ImmutableArray.Create(updateIds) };
                    var result = await featureReader.QueryAsync(layer.StorageLayerId, query, cancellationToken);
                    updateFeatures = result.Items
                        .Select(f => ConvertFeatureToGeoServices(f, layer.Resource))
                        .ToArray();
                }

                layerChanges.Add(new LayerChanges
                {
                    Id = layer.PublicLayerId,
                    Adds = insertIds.Length,
                    Updates = updateIds.Length,
                    Deletes = deleteIds.Length,
                    AddFeatures = addFeatures,
                    UpdateFeatures = updateFeatures,
                    DeleteIds = deleteIds.Length > 0 ? deleteIds : null
                });
            }
            else if (sinceGeneration == 0)
            {
                // First sync (gen 0) for a layer the change log does not cover: fall back to a full
                // snapshot delivered as adds. After migration 059 a Postgres layer with rows always
                // has baseline coverage and takes the change-log branch above; this fallback is the
                // first-sync snapshot for no-op-change-tracker backends and for a genuinely empty log.
                var (snapshotChanges, snapshotError) = await BuildLayerSnapshotAddsAsync(
                    context, replicaId, layer, featureReader, queryLimits, cancellationToken);
                if (snapshotError is not null)
                {
                    return (null, snapshotError);
                }

                layerChanges.Add(snapshotChanges!);
            }
            else
            {
                layerChanges.Add(new LayerChanges
                {
                    Id = layer.PublicLayerId,
                    Adds = 0,
                    Updates = 0,
                    Deletes = 0
                });
            }
        }

        return (layerChanges.ToArray(), null);
    }

    /// <summary>
    /// Builds a full-snapshot <see cref="LayerChanges"/> (every current feature reported as an add) for
    /// a layer that has no change-log coverage on a first (generation 0) sync. This is the first-sync
    /// baseline path for change-tracking backends whose tracker is a no-op (DuckDB / MySql / SQL Server)
    /// and for a genuinely empty change log; Postgres layers with rows are covered by the baseline rows
    /// migration 059 seeds and resolve through the incremental change-log path instead (#1876). Returns a
    /// bad-request <see cref="IResult"/> when the snapshot exceeds the configured per-layer record limit.
    /// </summary>
    private static async Task<(LayerChanges? Changes, IResult? Error)> BuildLayerSnapshotAddsAsync(
        HttpContext context,
        string replicaId,
        ReplicaLayerV2 layer,
        IFeatureReader featureReader,
        Honua.Core.Configuration.QueryLimits queryLimits,
        CancellationToken cancellationToken)
    {
        var result = await featureReader.QueryAsync(
            layer.StorageLayerId,
            new FeatureQuery { Limit = queryLimits.MaxRecordCount + 1 },
            cancellationToken);
        if (result.HasMoreResults || result.Items.Length > queryLimits.MaxRecordCount)
        {
            return (null, StandardErrorHelpers.CreateBadRequest(
                context,
                $"Replica '{replicaId}' initial extract exceeds the configured per-layer record limit.",
                [$"Layer {layer.PublicLayerId} returned more than {queryLimits.MaxRecordCount} features."]));
        }

        var addFeatures = result.Items
            .Select(f => ConvertFeatureToGeoServices(f, layer.Resource))
            .ToArray();

        return (new LayerChanges
        {
            Id = layer.PublicLayerId,
            Adds = addFeatures.Length,
            Updates = 0,
            Deletes = 0,
            AddFeatures = addFeatures,
            UpdateFeatures = null,
            DeleteIds = null
        }, null);
    }

    /// <summary>
    /// Serves the no-replicaID <c>extractChanges</c> flow: the serverGen-based change
    /// tracking the ArcGIS SDK <c>FeatureLayerCollection.extract_changes()</c> uses. The
    /// caller passes the generation to extract from (<c>serverGen</c> / <c>serverGens</c>)
    /// and optionally a <c>layers</c> filter, and receives changes since that generation
    /// without registering a replica. Only available on sync/change-tracking-enabled
    /// services. <c>returnIdsOnly=true</c> omits the per-feature attribute payload.
    /// </summary>
    private static async Task<IResult> HandleExtractChangesWithoutReplicaAsync(
        string serviceId,
        HttpContext context,
        MetadataV2Service service,
        MetadataV2GraphSnapshot snapshot,
        IReadOnlyDictionary<string, Microsoft.Extensions.Primitives.StringValues> values,
        System.Diagnostics.Activity? activity,
        CancellationToken cancellationToken)
    {
        // The serverGen-based change-tracking flow is served on the same change-tracking
        // backend the replica flow uses; it is not gated on the advertised "Sync"
        // capability token (the replica extractChanges path is served unconditionally too,
        // and the change tracker is always available). Layer access is still enforced
        // below so unauthorized callers cannot extract changes.

        // Resolve the layers to extract from: the optional layers filter, otherwise all
        // accessible service layers. This reuses the same access-checked resolution the
        // replica flow uses for the layers parameter.
        var layersParam = GetValueString(values, "layers");
        if (!TryResolveReplicaLayerIdsV2(context, service, snapshot, layersParam, out var requestedLayerIds, out var layerError))
        {
            return layerError ?? StandardErrorHelpers.CreateBadRequest(context,
                "Unable to resolve layers for extractChanges.");
        }

        var requestedIdSet = requestedLayerIds.ToHashSet();
        var extractLayers = ResolveServiceReplicaLayersV2(service, snapshot)
            .Where(layer => requestedIdSet.Contains(layer.PublicLayerId))
            .DistinctBy(layer => layer.PublicLayerId)
            .ToArray();

        if (extractLayers.Length == 0)
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "No accessible layers to extract changes from for this service.");
        }

        if (!TryParseBoolValue(values, "returnIdsOnly", false, out var returnIdsOnly, out var returnIdsOnlyError))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid returnIdsOnly parameter",
                [returnIdsOnlyError ?? "returnIdsOnly must be a boolean value."]);
        }

        var changeTracker = context.RequestServices.GetRequiredService<IChangeTracker>();
        var currentGen = await changeTracker.GetCurrentGenerationAsync(cancellationToken);

        // Resolve the "since" generation from serverGen / serverGens. When omitted we
        // extract from the beginning (generation 0), matching a first full extract.
        if (!TryResolveExtractSinceGeneration(values, currentGen, out var sinceGeneration, out var sinceError))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid serverGen parameter",
                [sinceError ?? "serverGen must be a non-negative integer."]);
        }

        activity?.SetTag("honua.extractChanges.sinceGen", sinceGeneration);
        activity?.SetTag("honua.extractChanges.returnIdsOnly", returnIdsOnly);

        var featureReader = context.RequestServices.GetRequiredService<IFeatureReader>();
        var queryLimits = context.RequestServices.GetRequiredService<IOptions<LimitsOptions>>().Value.Query;

        var changes = await changeTracker.GetChangesSinceAsync(
            sinceGeneration,
            extractLayers.Select(layer => layer.StorageLayerId).Distinct().ToArray(),
            cancellationToken);

        var changesByLayer = changes
            .GroupBy(c => c.LayerId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var layerChanges = new List<LayerChanges>(extractLayers.Length);
        foreach (var layer in extractLayers)
        {
            if (!changesByLayer.TryGetValue(layer.StorageLayerId, out var layerChangeList))
            {
                layerChanges.Add(new LayerChanges
                {
                    Id = layer.PublicLayerId,
                    Adds = 0,
                    Updates = 0,
                    Deletes = 0
                });
                continue;
            }

            var insertIds = layerChangeList
                .Where(c => c.Operation == FeatureChangeOperation.Insert)
                .Select(c => c.ObjectId)
                .ToArray();
            var updateIds = layerChangeList
                .Where(c => c.Operation == FeatureChangeOperation.Update)
                .Select(c => c.ObjectId)
                .ToArray();
            var deleteIds = layerChangeList
                .Where(c => c.Operation == FeatureChangeOperation.Delete)
                .Select(c => c.ObjectId)
                .ToArray();

            if (insertIds.Length > queryLimits.MaxRecordCount ||
                updateIds.Length > queryLimits.MaxRecordCount ||
                deleteIds.Length > queryLimits.MaxRecordCount)
            {
                return StandardErrorHelpers.CreateBadRequest(
                    context,
                    "extractChanges exceeds the configured per-layer change limit.",
                    [$"Layer {layer.PublicLayerId} exceeded {queryLimits.MaxRecordCount} adds, updates, or deletes in a single extract."]);
            }

            // returnIdsOnly omits the per-feature attribute payload; only the counts and
            // delete ids are reported (matching the SDK ids-only change-tracking flow).
            GeoServicesFeature[]? addFeatures = null;
            GeoServicesFeature[]? updateFeatures = null;
            if (!returnIdsOnly)
            {
                if (insertIds.Length > 0)
                {
                    var result = await featureReader.QueryAsync(
                        layer.StorageLayerId,
                        new FeatureQuery { ObjectIds = ImmutableArray.Create(insertIds) },
                        cancellationToken);
                    addFeatures = result.Items.Select(f => ConvertFeatureToGeoServices(f, layer.Resource)).ToArray();
                }

                if (updateIds.Length > 0)
                {
                    var result = await featureReader.QueryAsync(
                        layer.StorageLayerId,
                        new FeatureQuery { ObjectIds = ImmutableArray.Create(updateIds) },
                        cancellationToken);
                    updateFeatures = result.Items.Select(f => ConvertFeatureToGeoServices(f, layer.Resource)).ToArray();
                }
            }

            layerChanges.Add(new LayerChanges
            {
                Id = layer.PublicLayerId,
                Adds = insertIds.Length,
                Updates = updateIds.Length,
                Deletes = deleteIds.Length,
                AddFeatures = addFeatures,
                UpdateFeatures = updateFeatures,
                DeleteIds = deleteIds.Length > 0 ? deleteIds : null
            });
        }

        // No replica: ReplicaId is left null (omitted) and the change window is reported
        // through serverGen/minServerGen/maxServerGen for the serverGen-based flow.
        var response = new ExtractChangesResponse
        {
            Success = true,
            ReplicaId = null,
            LayerChanges = layerChanges.ToArray(),
            ServerGen = currentGen,
            MinServerGen = sinceGeneration,
            MaxServerGen = currentGen
        };

        return Results.Json(response, FeatureServerJsonContext.Default.ExtractChangesResponse, contentType: "application/json");
    }

    /// <summary>
    /// Resolves the "extract since" generation from the Esri <c>serverGen</c> /
    /// <c>serverGens</c> parameters. Accepts a single integer, or a JSON array of
    /// integers (in which case the minimum is used as the inclusive lower bound). When
    /// omitted, returns 0 (full extract). The resolved value is clamped to the current
    /// generation.
    /// </summary>
    private static bool TryResolveExtractSinceGeneration(
        IReadOnlyDictionary<string, Microsoft.Extensions.Primitives.StringValues> values,
        long currentGen,
        out long sinceGeneration,
        out string? error)
    {
        sinceGeneration = 0;
        error = null;

        var raw = GetValueString(values, "serverGen") ?? GetValueString(values, "serverGens");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        var trimmed = raw.Trim();
        if (long.TryParse(trimmed, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var single))
        {
            if (single < 0)
            {
                error = "serverGen must be a non-negative integer.";
                return false;
            }

            sinceGeneration = Math.Min(single, currentGen);
            return true;
        }

        if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
        {
            try
            {
                var parsed = System.Text.Json.JsonSerializer.Deserialize(trimmed, FeatureServerJsonContext.Default.Int64Array);
                if (parsed is { Length: > 0 })
                {
                    if (parsed.Any(g => g < 0))
                    {
                        error = "serverGens values must be non-negative integers.";
                        return false;
                    }

                    sinceGeneration = Math.Min(parsed.Min(), currentGen);
                    return true;
                }

                // Empty array → full extract.
                return true;
            }
            catch (System.Text.Json.JsonException)
            {
                error = "serverGens must be an integer or a JSON array of integers.";
                return false;
            }
        }

        error = "serverGen must be an integer or a JSON array of integers.";
        return false;
    }

    private static async Task<IResult> HandleSynchronizeReplica(
        string serviceId,
        HttpContext context)
    {
        using var activity = HonuaTelemetry.ActivitySource.StartActivity("featureserver.synchronizeReplica");
        activity?.SetTag(HonuaTelemetry.Tags.ServiceId, serviceId);

        var entitlementGate = RequireOfflineSyncEntitlement(context);
        if (entitlementGate is not null)
        {
            return entitlementGate;
        }

        var unsupportedBackend = RequireReplicaPersistenceSupport(context);
        if (unsupportedBackend is not null)
        {
            return unsupportedBackend;
        }

        var cancellationToken = GetTimeoutAwareCancellationToken(context);
        var serviceValidationResult = await ValidateReplicationServiceV2Async(serviceId, context, cancellationToken);
        if (serviceValidationResult.ErrorResult is not null)
        {
            return serviceValidationResult.ErrorResult!;
        }

        var service = serviceValidationResult.Service!;
        var snapshot = serviceValidationResult.Snapshot!;

        var writeAccessError = await RequireAnyServiceResourceWriteAccessBeforeBodyAsync(
            context,
            service,
            snapshot,
            cancellationToken);
        if (writeAccessError != null)
        {
            return writeAccessError;
        }

        var replicaStore = context.RequestServices.GetRequiredService<IReplicaStore>();

        var (values, readError) = await TryReadRequestValuesAsync(context.Request, cancellationToken);
        if (values == null)
        {
            if (TryGetUnsupportedMediaType(readError, out var receivedContentType))
            {
                return CreateUnsupportedRequestContentTypeResult(context, receivedContentType);
            }

            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid synchronizeReplica request",
                [readError ?? "Invalid request body."]);
        }

        var replicaId = GetValueString(values, "replicaID");
        if (string.IsNullOrWhiteSpace(replicaId))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "replicaID parameter is required");
        }

        activity?.SetTag("honua.replicaId", replicaId);

        var replica = await replicaStore.GetAsync(replicaId, cancellationToken);
        if (replica == null)
        {
            return StandardErrorHelpers.CreateNotFound(context,
                $"Replica '{replicaId}' not found.");
        }

        if (!string.Equals(replica.ServiceId, serviceId, StringComparison.OrdinalIgnoreCase))
        {
            return StandardErrorHelpers.CreateNotFound(context,
                $"Replica '{replicaId}' not found for service '{serviceId}'.");
        }

        if (!TryResolveReplicaLayersV2(context, service, snapshot, replica, AccessScope.Write, out var replicaLayers, out var replicaLayerError))
        {
            return replicaLayerError ?? StandardErrorHelpers.CreateNotFound(
                context,
                $"Replica '{replicaId}' not found for service '{serviceId}'.");
        }

        var synchronizeRbacError = await RequireReplicaWriteAccessV2Async(
            context,
            service,
            replicaLayers,
            cancellationToken);
        if (synchronizeRbacError != null)
        {
            return synchronizeRbacError;
        }

        var syncDirection = GetValueString(values, "syncDirection") ?? "download";
        var editsJson = GetValueString(values, "edits");
        // "upload" and "bidirectional" carry client edits to apply; "download" and "bidirectional" return
        // a server-to-client delta. The default (download) delivers the delta only.
        var isUploadDirection = !string.Equals(syncDirection, "download", StringComparison.OrdinalIgnoreCase);
        var isDownloadDirection = string.Equals(syncDirection, "download", StringComparison.OrdinalIgnoreCase)
            || string.Equals(syncDirection, "bidirectional", StringComparison.OrdinalIgnoreCase);

        // Esri sync protocol: the client echoes the server generation it actually
        // received (replicaServerGen, from the preceding extractChanges serverGen).
        // Honoring it prevents the download cursor from jumping over edits committed
        // between the client's extractChanges call and this acknowledgment, which
        // would silently never be delivered to the replica.
        long? acknowledgedServerGen = null;
        var replicaServerGenRaw = GetValueString(values, "replicaServerGen");
        if (!string.IsNullOrWhiteSpace(replicaServerGenRaw))
        {
            if (!long.TryParse(
                    replicaServerGenRaw.Trim(),
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var parsedServerGen) ||
                parsedServerGen < 0)
            {
                return StandardErrorHelpers.CreateBadRequest(context,
                    "replicaServerGen must be a non-negative integer.");
            }

            acknowledgedServerGen = parsedServerGen;
        }

        // Esri sync parameter: rollbackOnFailure=true applies each layer's uploaded edits atomically so
        // a single failing row rolls back that layer's whole batch, leaving the server state unchanged
        // (#2136). Defaults to false (best-effort per-row), matching the prior synchronize behavior.
        if (!TryParseBoolValue(values, "rollbackOnFailure", false, out var rollbackOnFailure, out var rollbackError))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid rollbackOnFailure parameter",
                [rollbackError ?? "rollbackOnFailure must be a boolean value."]);
        }

        var changeTracker = context.RequestServices.GetRequiredService<IChangeTracker>();

        SynchronizeReplicaConflict[]? conflicts = null;
        int? appliedAdds = null;
        int? appliedUpdates = null;
        int? appliedDeletes = null;

        // Upload/bidirectional sync with edits is applied through the canonical replica-sync
        // pipeline: it detects server-side conflicts against the replica's base generation, applies
        // non-conflicting edits via the shared edit pipeline, and writes durable conflict records
        // when supported. Download-only syncs and empty uploads skip the pipeline entirely (#1272).
        long uploadServerGen = 0;
        var didUpload = false;
        if (isUploadDirection && !string.IsNullOrWhiteSpace(editsJson))
        {
            if (!TryParseSynchronizeReplicaEdits(editsJson!, replicaLayers, out var layerEdits, out var parseError))
            {
                return StandardErrorHelpers.CreateBadRequest(context, "Invalid edits parameter", [parseError!]);
            }

            if (!layerEdits.IsDefaultOrEmpty)
            {
                var editsHandler = context.RequestServices.GetRequiredService<FeatureServerEditsHandler>();
                var limitsOptions = context.RequestServices
                    .GetRequiredService<Microsoft.Extensions.Options.IOptions<Honua.Core.Configuration.LimitsOptions>>();
                var syncService = context.RequestServices.GetRequiredService<IReplicaSyncService>();
                var applier = new FeatureServerReplicaEditApplier(editsHandler, limitsOptions.Value.Edits);

                // Snapshot the pre-apply server state of every uploaded update/delete target so durable
                // conflict records can carry the server side of the comparison for the review API
                // (#1287). Captured before apply because last-write-wins overwrites the conflicting row.
                var featureReader = context.RequestServices.GetRequiredService<IFeatureReader>();
                var serverConflictStates = await CaptureServerConflictStatesAsync(
                    featureReader, layerEdits, cancellationToken);

                var syncRequest = new ReplicaSyncRequest(
                    ReplicaId: replicaId,
                    ServiceId: serviceId,
                    Direction: string.Equals(syncDirection, "upload", StringComparison.OrdinalIgnoreCase)
                        ? ReplicaSyncDirection.Upload
                        : ReplicaSyncDirection.Bidirectional,
                    BaseGeneration: replica.LastSyncGeneration,
                    LayerEdits: layerEdits,
                    LastWriteWins: true,
                    SyncOperationId: context.TraceIdentifier,
                    RollbackOnFailure: rollbackOnFailure);

                var report = await syncService.ApplyUploadAsync(syncRequest, applier, cancellationToken);
                if (!report.Success)
                {
                    return StandardErrorHelpers.CreateBadRequest(
                        context,
                        "Uploaded replica edits failed to apply.");
                }

                appliedAdds = report.AppliedAdds;
                appliedUpdates = report.AppliedUpdates;
                appliedDeletes = report.AppliedDeletes;
                conflicts = MapSyncConflicts(report.Conflicts);
                uploadServerGen = report.ServerGeneration;
                didUpload = true;

                // Attach the client (uploaded) and pre-apply server state snapshots to the durable
                // conflict records the sync service wrote, so the operator conflict-review API can
                // render the field/geometry comparison (#1287).
                if (!report.Conflicts.IsDefaultOrEmpty)
                {
                    var conflictRepository = context.RequestServices.GetRequiredService<IReplicaConflictRepository>();
                    var refinedTypes = await AttachConflictStatesAsync(
                        conflictRepository, report.Conflicts, layerEdits, serverConflictStates, cancellationToken);

                    // Mirror the refined classification onto the transient synchronize response so the
                    // wire hint matches the durable conflict record the review API returns (#1287).
                    if (conflicts is not null && refinedTypes.Count > 0)
                    {
                        foreach (var wireConflict in conflicts)
                        {
                            if (refinedTypes.TryGetValue((wireConflict.LayerId, wireConflict.ObjectId), out var refined))
                            {
                                wireConflict.ConflictType = (int)refined;
                            }
                        }
                    }
                }
            }
        }

        // The server generation cursor recorded for the replica. After an upload we record the
        // post-apply generation as both the last-sync and upload-base cursor so a subsequent download
        // delta excludes the client's own just-applied edits. Download/bidirectional syncs advance the
        // last-sync cursor to the live current generation once the server-to-client delta below has been
        // assembled, so the replica receives every change committed since its last sync exactly once.
        long currentGen;
        if (didUpload)
        {
            // BH-012: use the post-upload generation for both upload-only AND bidirectional
            // syncs so concurrent server edits committed after the upload are captured by
            // the NEXT sync rather than permanently skipped.
            currentGen = uploadServerGen;
        }
        else
        {
            currentGen = await changeTracker.GetCurrentGenerationAsync(cancellationToken);
        }

        // Download / bidirectional sync: assemble the server-to-client delta and deliver it in the
        // response. This is the core of the #1775 fix — previously the download direction returned
        // success with no edits and never advanced the serverGen, so server-side changes committed after
        // createReplica were never delivered. The delta is assembled with the same change-tracking engine
        // extractChanges uses. The "since" cursor is the generation the client says it already holds
        // (replicaServerGen, the serverGen from its preceding extractChanges/createReplica) when supplied,
        // otherwise the replica's recorded last-sync generation; an upload in the same bidirectional call
        // moves that baseline to the post-apply generation so the replica does not receive its own edits.
        LayerChanges[]? downloadEdits = null;
        ReplicaInfoLayerServerGeneration[]? downloadLayerServerGens = null;
        if (isDownloadDirection)
        {
            // The download lower bound is the generation the client already holds (replicaServerGen,
            // from its preceding extractChanges/createReplica) when supplied, otherwise the replica's
            // recorded last-sync generation. The upper bound is always the current (post-upload)
            // generation so the delta window is (downloadSinceGen, currentGen] — covering every
            // server-side change including edits committed by other clients during the upload window
            // (BH5-015). A previous cap at preUploadGen permanently excluded those concurrent edits,
            // because the cursor was then advanced to uploadServerGen (BH2-012), making them
            // undeliverable to this replica forever. Clients that do not want to reapply their own
            // just-committed edits can filter them by objectId from the upload response (#1775).
            var downloadSinceGen = acknowledgedServerGen is { } acknowledged
                ? Math.Min(acknowledged, currentGen)
                : replica.LastSyncGeneration;
            long? downloadMaxGen = null;

            var (assembledEdits, deltaError) = await BuildReplicaLayerChangesAsync(
                context,
                replicaId,
                downloadSinceGen,
                replicaLayers,
                changeTracker,
                cancellationToken,
                downloadMaxGen);
            if (deltaError is not null)
            {
                return deltaError;
            }

            downloadEdits = assembledEdits;
            downloadLayerServerGens = replicaLayers
                .Select(layer => layer.PublicLayerId)
                .Distinct()
                .Select(id => new ReplicaInfoLayerServerGeneration
                {
                    Id = id,
                    ServerGen = currentGen,
                    ServerSibGen = currentGen
                })
                .ToArray();
        }

        var updated = replica with
        {
            LastSyncTime = DateTimeOffset.UtcNow,
            LastSyncGeneration = currentGen,
            UploadBaseGeneration = didUpload ? currentGen : replica.UploadBaseGeneration
        };

        // Compare-and-set against the cursors read at the top of the handler: a concurrent
        // synchronizeReplica of the same replica (retrying mobile clients) that committed first
        // wins, and this request is rejected instead of regressing the winner's cursor — which
        // would make the replica re-download its own uploaded edits or skip server changes.
        var syncStateUpdated = await replicaStore.TrySetSyncStateAsync(
            updated,
            replica.LastSyncGeneration,
            replica.UploadBaseGeneration,
            cancellationToken: cancellationToken);
        if (!syncStateUpdated)
        {
            return StandardErrorHelpers.CreateConflict(
                context,
                $"Replica '{replicaId}' was synchronized by a concurrent request. Re-run extractChanges and retry the synchronization from the new server generation.");
        }

        var response = new SynchronizeReplicaResponse
        {
            Success = true,
            ReplicaId = replicaId,
            SyncDirection = syncDirection,
            ServerGen = currentGen,
            Edits = downloadEdits,
            LayerServerGens = downloadLayerServerGens,
            AppliedAdds = appliedAdds,
            AppliedUpdates = appliedUpdates,
            AppliedDeletes = appliedDeletes,
            Conflicts = conflicts
        };

        return Results.Json(response, FeatureServerJsonContext.Default.SynchronizeReplicaResponse, contentType: "application/json");
    }

    /// <summary>
    /// Maps canonical sync conflicts to the wire conflict summary.
    /// </summary>
    private static SynchronizeReplicaConflict[]? MapSyncConflicts(
        ImmutableArray<ReplicaSyncConflict> conflicts)
    {
        if (conflicts.IsDefaultOrEmpty)
        {
            return null;
        }

        var result = new SynchronizeReplicaConflict[conflicts.Length];
        for (var i = 0; i < conflicts.Length; i++)
        {
            var conflict = conflicts[i];
            result[i] = new SynchronizeReplicaConflict
            {
                LayerId = conflict.PublicLayerId,
                ObjectId = conflict.ObjectId,
                ConflictType = (int)conflict.ConflictType,
                Applied = conflict.Applied,
                ConflictId = conflict.ConflictId
            };
        }

        return result;
    }

    /// <summary>
    /// Reads the current server state of every uploaded update/delete target, keyed by
    /// (public layer id, object id), as a <c>{"attributes": {...}, "geometry": ...}</c> envelope. Must
    /// run before the edits are applied so the captured server side is the pre-conflict state, not the
    /// just-applied client value (#1287). The geometry is converted to GeoServices (Esri) JSON so it is
    /// comparable to the uploaded client geometry. A target the server has already deleted yields no entry.
    /// </summary>
    private static async Task<Dictionary<(int PublicLayerId, long ObjectId), string>> CaptureServerConflictStatesAsync(
        IFeatureReader featureReader,
        ImmutableArray<ReplicaUploadLayerEdits> layerEdits,
        CancellationToken cancellationToken)
    {
        var states = new Dictionary<(int, long), string>();
        foreach (var layer in layerEdits)
        {
            var edits = layer.Edits.IsDefault ? ImmutableArray<ReplicaUploadEdit>.Empty : layer.Edits;
            foreach (var edit in edits)
            {
                if (edit.Kind == FeatureEditOperationKind.Create || edit.ObjectId is not { } objectId)
                {
                    continue;
                }

                var key = (layer.PublicLayerId, objectId);
                if (states.ContainsKey(key))
                {
                    continue;
                }

                var feature = await featureReader
                    .GetAsync(layer.StorageLayerId, objectId, cancellationToken)
                    .ConfigureAwait(false);
                if (feature is { } found)
                {
                    // Match the Z/M handling ConvertFeatureToGeoServices uses elsewhere so the captured
                    // server geometry is the same shape the rest of the GeoServices surface emits.
                    var geometry = GeoServicesGeometryConverter.ConvertWkbToGeoServicesGeometry(
                        found.Geometry, srid: null, geometryLimits: null, includeZ: false, includeM: false);
                    states[key] = SerializeStateEnvelope(found.Attributes, geometry);
                }
            }
        }

        return states;
    }

    /// <summary>
    /// Updates the durable conflict records written by the sync service with the client (uploaded) and
    /// pre-apply server state snapshots, so the conflict-review detail API can compute the field- and
    /// geometry-level comparison (#1287). Now that both states are captured, the coarse detection-time
    /// classification is also refined (a geometry-only divergence becomes a geometry conflict). Returns
    /// the refined classification for each conflict whose type changed, keyed by (public layer id,
    /// object id), so the caller can mirror it onto the transient synchronize response. Conflicts whose
    /// record cannot be loaded, or for which neither side has a captured state (e.g. delete-vs-delete),
    /// are left unchanged.
    /// </summary>
    private static async Task<Dictionary<(int PublicLayerId, long ObjectId), ReplicaConflictType>> AttachConflictStatesAsync(
        IReplicaConflictRepository conflictRepository,
        ImmutableArray<ReplicaSyncConflict> conflicts,
        ImmutableArray<ReplicaUploadLayerEdits> layerEdits,
        Dictionary<(int PublicLayerId, long ObjectId), string> serverStates,
        CancellationToken cancellationToken)
    {
        var refinedTypes = new Dictionary<(int PublicLayerId, long ObjectId), ReplicaConflictType>();
        var clientStates = BuildClientConflictStates(layerEdits);
        foreach (var conflict in conflicts)
        {
            if (conflict.ConflictId is not { Length: > 0 } conflictId)
            {
                continue;
            }

            var key = (conflict.PublicLayerId, conflict.ObjectId);
            clientStates.TryGetValue(key, out var clientState);
            serverStates.TryGetValue(key, out var serverState);
            if (clientState is null && serverState is null)
            {
                continue;
            }

            var record = await conflictRepository.GetAsync(conflictId, cancellationToken).ConfigureAwait(false);
            if (record is not { } existing)
            {
                continue;
            }

            // Refine the sync service's operation-only classification now that the geometry/attribute
            // values are available: attributes-agree + geometry-differs is a geometry conflict (#1287).
            var refinedType = ReplicaConflictClassifier.Refine(existing.ConflictType, clientState, serverState);
            if (refinedType != existing.ConflictType)
            {
                refinedTypes[key] = refinedType;
            }

            await conflictRepository.UpsertAsync(
                existing with
                {
                    ConflictType = refinedType,
                    ClientStateJson = clientState,
                    ServerStateJson = serverState,
                },
                cancellationToken).ConfigureAwait(false);
        }

        return refinedTypes;
    }

    /// <summary>
    /// Builds the client (uploaded) state envelopes for update edits, keyed by (public layer id, object
    /// id). Delete edits carry no client attributes and are omitted.
    /// </summary>
    private static Dictionary<(int PublicLayerId, long ObjectId), string> BuildClientConflictStates(
        ImmutableArray<ReplicaUploadLayerEdits> layerEdits)
    {
        var states = new Dictionary<(int, long), string>();
        foreach (var layer in layerEdits)
        {
            var edits = layer.Edits.IsDefault ? ImmutableArray<ReplicaUploadEdit>.Empty : layer.Edits;
            foreach (var edit in edits)
            {
                if (edit.ObjectId is { } objectId && edit.Payload is GeoServicesFeature feature)
                {
                    states[(layer.PublicLayerId, objectId)] =
                        SerializeStateEnvelope(feature.Attributes, feature.Geometry);
                }
            }
        }

        return states;
    }

    /// <summary>
    /// Serializes a feature's attributes and (when present) geometry into the conflict-state envelope
    /// <c>{"attributes": {...}, "geometry": ...}</c> consumed by the conflict-review diff and the
    /// geometry-conflict classifier, AOT-safely via the source-generated serializers. The geometry is
    /// emitted as GeoServices (Esri) JSON on both the client and server sides so the two are directly
    /// comparable (#1287); it is omitted entirely when the feature has no geometry.
    /// </summary>
    private static string SerializeStateEnvelope(
        IReadOnlyDictionary<string, object?> attributes,
        GeoServicesGeometry? geometry)
    {
        var attributeMap = attributes as Dictionary<string, object?> ?? new Dictionary<string, object?>(attributes);
        var attributesJson = System.Text.Json.JsonSerializer.Serialize(
            attributeMap, FeatureServerJsonContext.Default.DictionaryStringObject);
        if (geometry is null)
        {
            return $"{{\"attributes\":{attributesJson}}}";
        }

        var geometryJson = System.Text.Json.JsonSerializer.Serialize(
            geometry, FeatureServerJsonContext.Default.GeoServicesGeometry);
        return $"{{\"attributes\":{attributesJson},\"geometry\":{geometryJson}}}";
    }

    /// <summary>
    /// Parses the <c>synchronizeReplica</c> <c>edits</c> parameter into canonical per-layer upload
    /// edits. Accepts the ArcGIS per-layer form (a JSON array of
    /// <c>{ id, adds, updates, deletes }</c> objects) and, for backward compatibility, the legacy flat
    /// array of features (interpreted as adds against the replica's first layer). Edits referencing a
    /// layer not in the replica are rejected.
    /// </summary>
    private static bool TryParseSynchronizeReplicaEdits(
        string editsJson,
        ReplicaLayerV2[] replicaLayers,
        out ImmutableArray<ReplicaUploadLayerEdits> layerEdits,
        out string? error)
    {
        layerEdits = ImmutableArray<ReplicaUploadLayerEdits>.Empty;
        error = null;

        var storageByPublicId = replicaLayers
            .DistinctBy(layer => layer.PublicLayerId)
            .ToDictionary(layer => layer.PublicLayerId, layer => layer.StorageLayerId);

        // BH5-014: build a per-layer resource lookup so TryReadObjectId can resolve the
        // primary OID field name from the layer's schema rather than relying on hardcoded names.
        var resourceByPublicId = replicaLayers
            .DistinctBy(layer => layer.PublicLayerId)
            .ToDictionary(layer => layer.PublicLayerId, layer => layer.Resource);

        if (!TryClassifySynchronizeReplicaEdits(editsJson, out var editsShape, out error))
        {
            return false;
        }

        var builder = ImmutableArray.CreateBuilder<ReplicaUploadLayerEdits>();

        // BH5-013: route any successfully-parsed per-layer payload through the per-layer path,
        // including no-op payloads where every layer has empty Adds/Updates/Deletes arrays (e.g.
        // a client acknowledging receipt on a read-only replica). The Any() guard that was here
        // caused all-empty per-layer payloads to fall through to the legacy flat-form branch,
        // where the per-layer JSON objects were re-deserialized as GeoServicesFeature[] and
        // submitted as phantom creates against the first replica layer.
        if (editsShape == SynchronizeReplicaEditsShape.PerLayer)
        {
            SynchronizeReplicaLayerEdits[]? perLayer;
            try
            {
                perLayer = System.Text.Json.JsonSerializer.Deserialize(
                    editsJson, FeatureServerJsonContext.Default.SynchronizeReplicaLayerEditsArray);
            }
            catch (System.Text.Json.JsonException)
            {
                error = "edits must be a valid JSON array of per-layer edit objects.";
                return false;
            }

            if (perLayer is null)
            {
                error = "edits must be a valid JSON array of per-layer edit objects.";
                return false;
            }

            foreach (var entry in perLayer)
            {
                if (!storageByPublicId.TryGetValue(entry.Id, out var storageLayerId))
                {
                    error = $"edits reference layer {entry.Id} which is not part of this replica.";
                    return false;
                }

                var edits = ImmutableArray.CreateBuilder<ReplicaUploadEdit>();
                if (entry.Adds is not null)
                {
                    foreach (var add in entry.Adds)
                    {
                        edits.Add(new ReplicaUploadEdit(FeatureEditOperationKind.Create, ObjectId: null, Payload: add));
                    }
                }

                if (entry.Updates is not null)
                {
                    // BH5-014: resolve the OID field name from the layer's schema so updates on
                    // layers with a custom ObjectId field name are correctly keyed for conflict detection.
                    var layerResource = resourceByPublicId[entry.Id];
                    foreach (var update in entry.Updates)
                    {
                        edits.Add(new ReplicaUploadEdit(
                            FeatureEditOperationKind.Update,
                            ObjectId: TryReadObjectId(update, layerResource),
                            Payload: update));
                    }
                }

                if (entry.Deletes is not null)
                {
                    foreach (var deleteId in entry.Deletes)
                    {
                        edits.Add(new ReplicaUploadEdit(FeatureEditOperationKind.Delete, ObjectId: deleteId, Payload: null));
                    }
                }

                if (edits.Count > 0)
                {
                    builder.Add(new ReplicaUploadLayerEdits(entry.Id, storageLayerId, edits.ToImmutable()));
                }
            }

            layerEdits = builder.ToImmutable();
            return true;
        }

        if (editsShape == SynchronizeReplicaEditsShape.EmptyArray)
        {
            return true;
        }

        // Legacy flat form: an array of features applied as adds to the first replica layer.
        GeoServicesFeature[]? features;
        try
        {
            features = System.Text.Json.JsonSerializer.Deserialize(
                editsJson, FeatureServerJsonContext.Default.GeoServicesFeatureArray);
        }
        catch (System.Text.Json.JsonException)
        {
            error = "edits must be a valid JSON array of features or per-layer edit objects.";
            return false;
        }

        if (features is { Length: > 0 })
        {
            var firstLayer = replicaLayers[0];
            var edits = ImmutableArray.CreateBuilder<ReplicaUploadEdit>(features.Length);
            foreach (var feature in features)
            {
                edits.Add(new ReplicaUploadEdit(FeatureEditOperationKind.Create, ObjectId: null, Payload: feature));
            }

            builder.Add(new ReplicaUploadLayerEdits(firstLayer.PublicLayerId, firstLayer.StorageLayerId, edits.ToImmutable()));
        }

        layerEdits = builder.ToImmutable();
        return true;
    }

    private enum SynchronizeReplicaEditsShape
    {
        EmptyArray,
        PerLayer,
        FlatFeatures
    }

    private static bool TryClassifySynchronizeReplicaEdits(
        string editsJson,
        out SynchronizeReplicaEditsShape shape,
        out string? error)
    {
        shape = SynchronizeReplicaEditsShape.EmptyArray;
        error = null;

        System.Text.Json.JsonDocument document;
        try
        {
            document = System.Text.Json.JsonDocument.Parse(editsJson);
        }
        catch (System.Text.Json.JsonException)
        {
            error = "edits must be a valid JSON array of features or per-layer edit objects.";
            return false;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                error = "edits must be a JSON array of features or per-layer edit objects.";
                return false;
            }

            var hasEntries = false;
            var allPerLayer = true;
            var allFlatFeatures = true;
            foreach (var entry in document.RootElement.EnumerateArray())
            {
                hasEntries = true;
                if (entry.ValueKind != System.Text.Json.JsonValueKind.Object)
                {
                    error = "edits array entries must be JSON objects.";
                    return false;
                }

                allPerLayer &= IsSynchronizeReplicaLayerEditObject(entry);
                allFlatFeatures &= IsGeoServicesFeatureObject(entry);
            }

            if (!hasEntries)
            {
                shape = SynchronizeReplicaEditsShape.EmptyArray;
                return true;
            }

            if (allPerLayer)
            {
                shape = SynchronizeReplicaEditsShape.PerLayer;
                return true;
            }

            if (allFlatFeatures)
            {
                shape = SynchronizeReplicaEditsShape.FlatFeatures;
                return true;
            }
        }

        error = "edits must be a JSON array containing either feature objects or per-layer edit objects.";
        return false;
    }

    private static bool IsSynchronizeReplicaLayerEditObject(System.Text.Json.JsonElement entry)
        => HasProperty(entry, "id")
           && (HasProperty(entry, "adds")
               || HasProperty(entry, "updates")
               || HasProperty(entry, "deletes"));

    private static bool IsGeoServicesFeatureObject(System.Text.Json.JsonElement entry)
        => HasProperty(entry, "attributes")
           || HasProperty(entry, "geometry");

    private static bool HasProperty(System.Text.Json.JsonElement entry, string propertyName)
    {
        foreach (var property in entry.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Reads the object id attribute from an Esri-JSON update feature for conflict keying. Returns
    /// null when no recognizable object id attribute is present; conflict detection then treats the
    /// update as non-conflicting (the shared edit pipeline still rejects updates without an id).
    /// </summary>
    /// <param name="feature">The uploaded update feature whose attributes are searched for an OID value.</param>
    /// <param name="resource">
    /// The layer's metadata resource used to resolve the actual OID field name via the
    /// <c>id.primary</c> semantic role. This avoids false misses on layers whose primary key
    /// column does not match the hardcoded fallback names (BH5-014).
    /// </param>
    private static long? TryReadObjectId(GeoServicesFeature feature, MetadataV2Resource resource)
    {
        if (feature.Attributes is null)
        {
            return null;
        }

        // BH5-014: resolve the canonical OID field name from the layer's schema first.
        // FindPrimaryIdField returns the field with the 'id.primary' semantic role, falling back
        // to fields named 'objectid' or 'id'. Only if the schema yields no match do we fall
        // through to the legacy hardcoded name list for backward compatibility with ad-hoc payloads.
        var schemaOidName = resource.FindPrimaryIdField()?.Name;
        if (schemaOidName is not null && feature.Attributes.TryGetValue(schemaOidName, out var schemaValue))
        {
            if (FeatureServerValueParser.TryConvertToLong(schemaValue, out var schemaObjectId))
            {
                return schemaObjectId;
            }
        }

        foreach (var (key, value) in feature.Attributes)
        {
            if (key.Equals(FieldNames.ObjectId, StringComparison.OrdinalIgnoreCase) ||
                key.Equals("objectid", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("oid", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("fid", StringComparison.OrdinalIgnoreCase))
            {
                if (FeatureServerValueParser.TryConvertToLong(value, out var objectId))
                {
                    return objectId;
                }
            }
        }

        return null;
    }

    private static async Task<IResult> HandleUnRegisterReplica(
        string serviceId,
        HttpContext context)
    {
        using var activity = HonuaTelemetry.ActivitySource.StartActivity("featureserver.unRegisterReplica");
        activity?.SetTag(HonuaTelemetry.Tags.ServiceId, serviceId);

        var entitlementGate = RequireOfflineSyncEntitlement(context);
        if (entitlementGate is not null)
        {
            return entitlementGate;
        }

        var unsupportedBackend = RequireReplicaPersistenceSupport(context);
        if (unsupportedBackend is not null)
        {
            return unsupportedBackend;
        }

        var cancellationToken = GetTimeoutAwareCancellationToken(context);
        var serviceValidationResult = await ValidateReplicationServiceV2Async(serviceId, context, cancellationToken);
        if (serviceValidationResult.ErrorResult is not null)
        {
            return serviceValidationResult.ErrorResult!;
        }

        var service = serviceValidationResult.Service!;
        var snapshot = serviceValidationResult.Snapshot!;

        var writeAccessError = await RequireAnyServiceResourceWriteAccessBeforeBodyAsync(
            context,
            service,
            snapshot,
            cancellationToken);
        if (writeAccessError != null)
        {
            return writeAccessError;
        }

        var replicaStore = context.RequestServices.GetRequiredService<IReplicaStore>();

        var (values, readError) = await TryReadRequestValuesAsync(context.Request, cancellationToken);
        if (values == null)
        {
            if (TryGetUnsupportedMediaType(readError, out var receivedContentType))
            {
                return CreateUnsupportedRequestContentTypeResult(context, receivedContentType);
            }

            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid unRegisterReplica request",
                [readError ?? "Invalid request body."]);
        }

        var replicaId = GetValueString(values, "replicaID");
        if (string.IsNullOrWhiteSpace(replicaId))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "replicaID parameter is required");
        }

        activity?.SetTag("honua.replicaId", replicaId);

        var replica = await replicaStore.GetAsync(replicaId, cancellationToken);
        if (replica == null ||
            !string.Equals(replica.ServiceId, serviceId, StringComparison.OrdinalIgnoreCase))
        {
            return StandardErrorHelpers.CreateNotFound(context,
                $"Replica '{replicaId}' not found for service '{serviceId}'.");
        }

        if (!TryResolveReplicaLayersV2(context, service, snapshot, replica, AccessScope.Write, out var replicaLayers, out var replicaLayerError))
        {
            return replicaLayerError ?? StandardErrorHelpers.CreateNotFound(
                context,
                $"Replica '{replicaId}' not found for service '{serviceId}'.");
        }

        var unregisterRbacError = await RequireReplicaWriteAccessV2Async(
            context,
            service,
            replicaLayers,
            cancellationToken);
        if (unregisterRbacError != null)
        {
            return unregisterRbacError;
        }

        var removed = await replicaStore.RemoveAsync(replicaId, cancellationToken);
        if (!removed)
        {
            return StandardErrorHelpers.CreateNotFound(context,
                $"Replica '{replicaId}' not found.");
        }

        var response = new SuccessResponse { Success = true };
        return Results.Json(response, FeatureServerJsonContext.Default.SuccessResponse, contentType: "application/json");
    }

    private readonly record struct ReplicationServiceValidationResult(
        MetadataV2Service? Service,
        MetadataV2GraphSnapshot? Snapshot,
        IResult? ErrorResult);

    private sealed record ReplicaLayerV2(
        int PublicLayerId,
        int StorageLayerId,
        MetadataV2Publication Publication,
        MetadataV2Resource Resource);

    private static async Task<ReplicationServiceValidationResult> ValidateReplicationServiceV2Async(
        string serviceId,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var resourceValidator = context.RequestServices.GetRequiredService<IResourceValidator>();
        var serviceValidationResult = await FeatureServerResourceValidationHelpers.ValidateServiceV2Async(
            resourceValidator,
            serviceId,
            context,
            logger: null,
            cancellationToken: cancellationToken);
        if (!serviceValidationResult.IsValid)
        {
            return new ReplicationServiceValidationResult(null, null, serviceValidationResult.ErrorResult);
        }

        var snapshotProvider = context.RequestServices.GetRequiredService<IMetadataV2GraphProvider>();
        var snapshot = await snapshotProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        return new ReplicationServiceValidationResult(serviceValidationResult.Service!, snapshot, null);
    }

    private static ReplicaLayerV2[] ResolveServiceReplicaLayersV2(
        MetadataV2Service service,
        MetadataV2GraphSnapshot snapshot)
    {
        return
        [
            ..snapshot.PublicationsForService(service.Metadata.Id)
                .Select(publication =>
                {
                    var resource = snapshot.ResolveResource(publication);
                    var storageLayerId = publication.LayerIndex
                        ?? snapshot.ResolveStorageLayerId(publication)
                        ?? (resource is not null ? snapshot.ResolveStorageLayerId(resource) : null);
                    var publicLayerId = publication.LayerIndex ?? storageLayerId;
                    return resource is not null && storageLayerId is not null && publicLayerId is not null
                        ? new ReplicaLayerV2(publicLayerId.Value, storageLayerId.Value, publication, resource)
                        : null;
                })
                .Where(layer => layer is not null)
                .Select(layer => layer!)
        ];
    }

    private static bool TryResolveReplicaLayerIdsV2(
        HttpContext context,
        MetadataV2Service service,
        MetadataV2GraphSnapshot snapshot,
        string? layersParam,
        out int[] layerIds,
        out IResult? error,
        AccessScope scope = AccessScope.Read)
    {
        layerIds = [];
        error = null;

        if (string.IsNullOrWhiteSpace(layersParam))
        {
            var serviceLayers = ResolveServiceReplicaLayersV2(service, snapshot);
            var accessibleLayers = serviceLayers
                .Where(layer => AccessPolicyHelpers.IsResourceAccessible(context, layer.Resource, service, scope))
                .Select(layer => layer.PublicLayerId)
                .ToArray();

            if (accessibleLayers.Length == 0)
            {
                error = AccessPolicyHelpers.RequireAnyResourceAccess(
                            context,
                            serviceLayers.Select(layer => layer.Resource),
                            service,
                            scope)
                        ?? StandardErrorHelpers.CreateForbidden(context, AccessPolicyHelpers.AccessForbiddenMessage);
                return false;
            }

            layerIds = accessibleLayers;
            return true;
        }

        var layerById = ResolveServiceReplicaLayersV2(service, snapshot)
            .ToDictionary(layer => layer.PublicLayerId);

        // Accept both the comma-separated form (layers=0 / layers=0,1) and the Esri
        // JSON-array form (layers=[0] / layers=[0,1]) that the ArcGIS API for Python
        // (FeatureLayerCollection.extract_changes / create_replica) sends.
        var tokens = StripLayerListBrackets(layersParam).Split(',', StringSplitOptions.TrimEntries);
        if (tokens.Length == 0 || tokens.Any(token => token.Length == 0))
        {
            error = StandardErrorHelpers.CreateBadRequest(
                context,
                "layers parameter must contain only numeric layer IDs.");
            return false;
        }

        var ids = new HashSet<int>();

        foreach (var token in tokens)
        {
            if (!int.TryParse(token, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var id))
            {
                error = StandardErrorHelpers.CreateBadRequest(
                    context,
                    "layers parameter must contain only numeric layer IDs.");
                return false;
            }

            if (!layerById.TryGetValue(id, out var layer))
            {
                error = StandardErrorHelpers.CreateBadRequest(
                    context,
                    "layers parameter contains one or more invalid layer IDs for this service.");
                return false;
            }

            var accessError = AccessPolicyHelpers.RequireResourceAccess(context, layer.Resource, service, scope);
            if (accessError != null)
            {
                error = accessError;
                return false;
            }

            ids.Add(id);
        }

        if (ids.Count == 0)
        {
            error = StandardErrorHelpers.CreateBadRequest(
                context,
                "layers parameter must contain at least one layer ID.");
            return false;
        }

        layerIds = ids.ToArray();
        return true;
    }

    private static bool TryResolveReplicaLayersV2(
        HttpContext context,
        MetadataV2Service service,
        MetadataV2GraphSnapshot snapshot,
        ReplicaState replica,
        AccessScope scope,
        out ReplicaLayerV2[] layers,
        out IResult? error)
    {
        layers = [];
        error = null;

        var serviceLayerById = ResolveServiceReplicaLayersV2(service, snapshot)
            .ToDictionary(layer => layer.PublicLayerId);
        var resolved = new List<ReplicaLayerV2>(replica.LayerIds.Length);

        foreach (var layerId in replica.LayerIds.Distinct())
        {
            if (!serviceLayerById.TryGetValue(layerId, out var layer))
            {
                error = StandardErrorHelpers.CreateNotFound(
                    context,
                    $"Replica '{replica.ReplicaId}' not found for service '{service.Metadata.Name}'.");
                return false;
            }

            var accessError = AccessPolicyHelpers.RequireResourceAccess(context, layer.Resource, service, scope);
            if (accessError != null)
            {
                error = accessError;
                return false;
            }

            resolved.Add(layer);
        }

        if (resolved.Count == 0)
        {
            error = StandardErrorHelpers.CreateNotFound(
                context,
                $"Replica '{replica.ReplicaId}' not found for service '{service.Metadata.Name}'.");
            return false;
        }

        layers = resolved.ToArray();
        return true;
    }

    private static bool TryParseReplicaBooleanQuery(
        HttpContext context,
        string parameterName,
        out bool value,
        out IResult? error)
    {
        value = false;
        error = null;

        if (!context.Request.Query.TryGetValue(parameterName, out var values) || values.Count == 0)
        {
            return true;
        }

        if (bool.TryParse(values[0], out value))
        {
            return true;
        }

        error = StandardErrorHelpers.CreateBadRequest(
            context,
            $"{parameterName} must be a boolean value.");
        return false;
    }

    private static ReplicaState ToReplicaState(ReplicaRecord record) => new(
        record.ReplicaId,
        record.ReplicaName,
        record.ServiceId,
        record.SyncModel,
        record.LayerIds,
        record.CreatedAt)
    {
        LastSyncTime = record.LastSyncTime,
        LastSyncGeneration = record.LastSyncGeneration,
        UploadBaseGeneration = record.UploadBaseGeneration
    };

    // Per-operation authorization (BH3-001/BH3-014) does NOT cleanly apply to replica write
    // access: this gate covers replica lifecycle operations (create / unregister — which are not
    // feature mutations) and replica synchronize (which carries a mix of insert/update/delete
    // edits governed by the replica registration and applied through the shared edit pipeline,
    // and whose edits are not yet parsed at this point). Authorization here is therefore the
    // coarse replica-scoped write capability rather than a single feature operation.
    private static async Task<IResult?> RequireReplicaWriteAccessV2Async(
        HttpContext context,
        MetadataV2Service service,
        IEnumerable<ReplicaLayerV2> layers,
        CancellationToken cancellationToken)
    {
        foreach (var layer in layers.DistinctBy(static layer => layer.PublicLayerId))
        {
            var rbacError = await ServiceDataEditorAuthorization.RequireResourceDataEditorAsync(
                context,
                layer.Resource,
                service,
                cancellationToken);
            if (rbacError != null)
            {
                return rbacError;
            }
        }

        return null;
    }

    // Pre-body OR-gate for replica operations: mirrors the FeatureServer pre-body mutating-access
    // gate. Coarse by design (it authorizes when ANY service resource is writable before the body
    // is read); the per-operation narrowing is applied by the primary single-verb edit surfaces,
    // not this replica capability check.
    private static async Task<IResult?> RequireAnyServiceResourceWriteAccessBeforeBodyAsync(
        HttpContext context,
        MetadataV2Service service,
        MetadataV2GraphSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        IResult? firstError = null;
        foreach (var layer in ResolveServiceReplicaLayersV2(service, snapshot).DistinctBy(static layer => layer.PublicLayerId))
        {
            var rbacError = await ServiceDataEditorAuthorization.RequireResourceDataEditorAsync(
                context,
                layer.Resource,
                service,
                cancellationToken);
            if (rbacError == null)
            {
                return null;
            }

            firstError ??= rbacError;
        }

        return firstError ?? await ServiceDataEditorAuthorization.RequireServiceDataEditorAsync(
            context,
            service,
            cancellationToken);
    }

    /// <summary>
    /// Converts a domain Feature to a GeoServicesFeature for replication responses.
    /// esriFieldTypeDate attributes are coerced to epoch-ms integers uniformly across rows
    /// (JSONB stores dates as ISO strings from seeds or epoch-ms longs from applyEdits) via the
    /// shared GeoServices date convention, matching the query/identify serialization.
    /// </summary>
    private static GeoServicesFeature ConvertFeatureToGeoServices(Feature feature, MetadataV2Resource resource)
    {
        var attributes = feature.Attributes
            .Where(kvp => !FeatureAttributeVisibility.IsInternalAttribute(kvp.Key))
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);
        GeoServicesFieldConventions.CoerceDateAttributes(
            attributes,
            GeoServicesFieldConventions.ResolveDateFieldNames(resource));

        return new GeoServicesFeature
        {
            Attributes = attributes,
            Geometry = GeoServicesGeometryConverter.ConvertWkbToGeoServicesGeometry(
                feature.Geometry, null, null, false, false)
        };
    }
}
