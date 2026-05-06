// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Core.Configuration;
using System.Collections.Immutable;
using Honua.Server.Features.Protocols.GeoServices;
using Honua.Server.Features.Protocols.GeoServices.FeatureServer.Models;
using Honua.Server.Features.Protocols.GeoServices.FeatureServer.Services;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Validation;
using Honua.ServiceDefaults;
using Microsoft.Extensions.Options;

// Behavior reference: Replication durability (#383)
// Uses IChangeTracker for monotonic generation counters and incremental delta extraction

namespace Honua.Server.Features.Protocols.GeoServices.FeatureServer;

internal static partial class FeatureServerEndpoints
{
    private static async Task<IResult> HandleReplicas(
        string serviceId,
        HttpContext context)
    {
        using var activity = HonuaTelemetry.ActivitySource.StartActivity("featureserver.replicas");
        activity?.SetTag(HonuaTelemetry.Tags.ServiceId, serviceId);

        var resourceValidator = context.RequestServices.GetRequiredService<IResourceValidator>();
        var cancellationToken = GetTimeoutAwareCancellationToken(context);
        var serviceValidationResult = await FeatureServerResourceValidationHelpers.ValidateServiceAsync(
            resourceValidator,
            serviceId,
            context,
            logger: null,
            cancellationToken: cancellationToken);
        if (!serviceValidationResult.IsValid)
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
        var accessError = AccessPolicyHelpers.RequireAnyLayerAccess(context, service.Layers, service);
        if (accessError != null)
        {
            return accessError;
        }

        var accessibleLayerIds = service.Layers
            .Where(layer => AccessPolicyHelpers.EvaluateAccess(
                context,
                layer.Metadata?.AccessPolicy,
                service.Metadata?.AccessPolicy,
                AccessScope.Read).IsAllowed)
            .Select(layer => layer.Id)
            .ToHashSet();

        var replicaRepository = context.RequestServices.GetRequiredService<IReplicaRepository>();
        var replicas = await replicaRepository.ListByServiceAsync(serviceId, cancellationToken).ConfigureAwait(false);

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

        var resourceValidator = context.RequestServices.GetRequiredService<IResourceValidator>();
        var cancellationToken = GetTimeoutAwareCancellationToken(context);
        var serviceValidationResult = await FeatureServerResourceValidationHelpers.ValidateServiceAsync(
            resourceValidator,
            serviceId,
            context,
            logger: null,
            cancellationToken: cancellationToken);
        if (!serviceValidationResult.IsValid)
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
        var replicaRepository = context.RequestServices.GetRequiredService<IReplicaRepository>();
        var record = await replicaRepository.GetAsync(replicaId, cancellationToken).ConfigureAwait(false);
        if (record == null || !string.Equals(record.Value.ServiceId, serviceId, StringComparison.OrdinalIgnoreCase))
        {
            return StandardErrorHelpers.CreateNotFound(context,
                $"Replica '{replicaId}' not found for service '{serviceId}'.");
        }

        var replica = ToReplicaState(record.Value);
        if (!TryResolveReplicaLayers(context, service, replica, AccessScope.Read, out var replicaLayers, out var replicaLayerError))
        {
            return replicaLayerError ?? StandardErrorHelpers.CreateNotFound(
                context,
                $"Replica '{replicaId}' not found for service '{serviceId}'.");
        }

        var layerServerGens = record.Value.SyncModel.Equals("perLayer", StringComparison.OrdinalIgnoreCase)
            ? System.Text.Json.JsonSerializer.Serialize(
                record.Value.LayerIds
                    .Distinct()
                    .Select(id => new ReplicaInfoLayerServerGeneration
                    {
                        Id = id,
                        ServerGen = record.Value.LastSyncGeneration,
                        ServerSibGen = record.Value.LastSyncGeneration
                    })
                    .ToArray(),
                FeatureServerJsonContext.Default.ReplicaInfoLayerServerGenerationArray)
            : null;

        var response = new ReplicaInfoResponse
        {
            ReplicaName = record.Value.ReplicaName,
            ReplicaId = record.Value.ReplicaId,
            SyncModel = record.Value.SyncModel,
            ReplicaServerGen = record.Value.SyncModel.Equals("perReplica", StringComparison.OrdinalIgnoreCase)
                ? record.Value.LastSyncGeneration
                : null,
            LayerServerGens = layerServerGens,
            CreationDate = record.Value.CreatedAt.ToUnixTimeMilliseconds(),
            LastSyncDate = record.Value.LastSyncTime.ToUnixTimeMilliseconds(),
            Layers = replicaLayers.Select(layer => new ReplicaInfoLayer
            {
                Id = layer.Id
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

        var resourceValidator = context.RequestServices.GetRequiredService<IResourceValidator>();
        var cancellationToken = GetTimeoutAwareCancellationToken(context);
        var serviceValidationResult = await FeatureServerResourceValidationHelpers.ValidateServiceAsync(
            resourceValidator,
            serviceId,
            context,
            logger: null,
            cancellationToken: cancellationToken);
        if (!serviceValidationResult.IsValid)
        {
            return serviceValidationResult.ErrorResult!;
        }

        var service = serviceValidationResult.Service!;

        var writeAccessError = await RequireAnyServiceLayerWriteAccessBeforeBodyAsync(
            context,
            service,
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

        if (!TryResolveReplicaLayerIds(
                context,
                service,
                layersParam,
                out var layerIds,
                out var layerError,
                AccessScope.Write))
        {
            return layerError ?? StandardErrorHelpers.CreateBadRequest(
                context,
                "layers parameter contains one or more invalid layer IDs for this service.");
        }

        var createLayers = service.Layers
            .Where(layer => layerIds.Contains(layer.Id))
            .ToArray();
        var createRbacError = await RequireReplicaWriteAccessAsync(
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

        var resourceValidator = context.RequestServices.GetRequiredService<IResourceValidator>();
        var cancellationToken = GetTimeoutAwareCancellationToken(context);
        var serviceValidationResult = await FeatureServerResourceValidationHelpers.ValidateServiceAsync(
            resourceValidator,
            serviceId,
            context,
            logger: null,
            cancellationToken: cancellationToken);
        if (!serviceValidationResult.IsValid)
        {
            return serviceValidationResult.ErrorResult!;
        }

        var service = serviceValidationResult.Service!;
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

        if (!TryResolveReplicaLayers(context, service, replica, AccessScope.Read, out var replicaLayers, out var replicaLayerError))
        {
            return replicaLayerError ?? StandardErrorHelpers.CreateNotFound(
                context,
                $"Replica '{replicaId}' not found for service '{serviceId}'.");
        }

        var changeTracker = context.RequestServices.GetRequiredService<IChangeTracker>();
        var currentGen = await changeTracker.GetCurrentGenerationAsync(cancellationToken);
        var layerChanges = new List<LayerChanges>();

        var featureReader = context.RequestServices.GetRequiredService<IFeatureReader>();
        var queryLimits = context.RequestServices.GetRequiredService<IOptions<LimitsOptions>>().Value.Query;

        // Special case: LastSyncGeneration == 0 means pre-migration data or first sync;
        // fall back to "all features as adds" for backward compatibility.
        if (replica.LastSyncGeneration == 0)
        {
            foreach (var layer in replicaLayers)
            {
                var result = await featureReader.QueryAsync(
                    layer.Id,
                    new FeatureQuery { Limit = queryLimits.MaxRecordCount + 1 },
                    cancellationToken);
                if (result.HasMoreResults || result.Items.Length > queryLimits.MaxRecordCount)
                {
                    return StandardErrorHelpers.CreateBadRequest(
                        context,
                        $"Replica '{replicaId}' initial extract exceeds the configured per-layer record limit.",
                        [$"Layer {layer.Id} returned more than {queryLimits.MaxRecordCount} features."]);
                }

                var addFeatures = result.Items
                    .Select(f => ConvertFeatureToGeoServices(f))
                    .ToArray();

                layerChanges.Add(new LayerChanges
                {
                    Id = layer.Id,
                    Adds = addFeatures.Length,
                    Updates = 0,
                    Deletes = 0,
                    AddFeatures = addFeatures,
                    UpdateFeatures = null,
                    DeleteIds = null
                });
            }
        }
        else
        {
            // Query real incremental deltas from the change log
            var changes = await changeTracker.GetChangesSinceAsync(
                replica.LastSyncGeneration,
                replica.LayerIds,
                cancellationToken);

            // Group collapsed changes by layer
            var changesByLayer = changes
                .GroupBy(c => c.LayerId)
                .ToDictionary(g => g.Key, g => g.ToList());

            foreach (var layer in replicaLayers)
            {
                if (changesByLayer.TryGetValue(layer.Id, out var layerChangeList))
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
                        return StandardErrorHelpers.CreateBadRequest(
                            context,
                            $"Replica '{replicaId}' extract exceeds the configured per-layer change limit.",
                            [$"Layer {layer.Id} exceeded {queryLimits.MaxRecordCount} adds, updates, or deletes in a single extract."]);
                    }

                    // Query actual features for inserts and updates
                    GeoServicesFeature[]? addFeatures = null;
                    if (insertIds.Length > 0)
                    {
                        var query = new FeatureQuery { ObjectIds = ImmutableArray.Create(insertIds) };
                        var result = await featureReader.QueryAsync(layer.Id, query, cancellationToken);
                        addFeatures = result.Items
                            .Select(f => ConvertFeatureToGeoServices(f))
                            .ToArray();
                    }

                    GeoServicesFeature[]? updateFeatures = null;
                    if (updateIds.Length > 0)
                    {
                        var query = new FeatureQuery { ObjectIds = ImmutableArray.Create(updateIds) };
                        var result = await featureReader.QueryAsync(layer.Id, query, cancellationToken);
                        updateFeatures = result.Items
                            .Select(f => ConvertFeatureToGeoServices(f))
                            .ToArray();
                    }

                    layerChanges.Add(new LayerChanges
                    {
                        Id = layer.Id,
                        Adds = insertIds.Length,
                        Updates = updateIds.Length,
                        Deletes = deleteIds.Length,
                        AddFeatures = addFeatures,
                        UpdateFeatures = updateFeatures,
                        DeleteIds = deleteIds.Length > 0 ? deleteIds : null
                    });
                }
                else
                {
                    layerChanges.Add(new LayerChanges
                    {
                        Id = layer.Id,
                        Adds = 0,
                        Updates = 0,
                        Deletes = 0
                    });
                }
            }
        }

        var minGen = replica.LastSyncGeneration;
        var maxGen = currentGen;

        var response = new ExtractChangesResponse
        {
            Success = true,
            ReplicaId = replicaId,
            LayerChanges = layerChanges.ToArray(),
            ServerGen = currentGen,
            MinServerGen = minGen,
            MaxServerGen = maxGen
        };

        return Results.Json(response, FeatureServerJsonContext.Default.ExtractChangesResponse, contentType: "application/json");
    }

    private static async Task<IResult> HandleSynchronizeReplica(
        string serviceId,
        HttpContext context)
    {
        using var activity = HonuaTelemetry.ActivitySource.StartActivity("featureserver.synchronizeReplica");
        activity?.SetTag(HonuaTelemetry.Tags.ServiceId, serviceId);

        var resourceValidator = context.RequestServices.GetRequiredService<IResourceValidator>();
        var cancellationToken = GetTimeoutAwareCancellationToken(context);
        var serviceValidationResult = await FeatureServerResourceValidationHelpers.ValidateServiceAsync(
            resourceValidator,
            serviceId,
            context,
            logger: null,
            cancellationToken: cancellationToken);
        if (!serviceValidationResult.IsValid)
        {
            return serviceValidationResult.ErrorResult!;
        }

        var service = serviceValidationResult.Service!;

        var writeAccessError = await RequireAnyServiceLayerWriteAccessBeforeBodyAsync(
            context,
            service,
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

        if (!TryResolveReplicaLayers(context, service, replica, AccessScope.Write, out var replicaLayers, out var replicaLayerError))
        {
            return replicaLayerError ?? StandardErrorHelpers.CreateNotFound(
                context,
                $"Replica '{replicaId}' not found for service '{serviceId}'.");
        }

        var synchronizeRbacError = await RequireReplicaWriteAccessAsync(
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

        // If upload or bidirectional sync includes edits, apply them
        if (!string.IsNullOrWhiteSpace(editsJson) &&
            !string.Equals(syncDirection, "download", StringComparison.OrdinalIgnoreCase))
        {
            GeoServicesFeature[]? features;
            try
            {
                features = System.Text.Json.JsonSerializer.Deserialize(
                    editsJson, FeatureServerJsonContext.Default.GeoServicesFeatureArray);
            }
            catch (System.Text.Json.JsonException)
            {
                return StandardErrorHelpers.CreateBadRequest(context,
                    "Invalid edits parameter",
                    ["edits must be a valid JSON array of features."]);
            }

            if (features is { Length: > 0 })
            {
                // Apply the incoming edits to the first replica layer
                var targetLayerId = replicaLayers[0].Id;
                var editsHandler = context.RequestServices.GetRequiredService<FeatureServerEditsHandler>();
                var limitsOptions = context.RequestServices
                    .GetRequiredService<Microsoft.Extensions.Options.IOptions<Honua.Core.Configuration.LimitsOptions>>();

                var editRequest = new ApplyEditsRequest { Adds = features, RollbackOnFailure = false };
                var editResult = await editsHandler.HandleApplyEditsAsync(
                    serviceId, targetLayerId, editRequest, limitsOptions.Value.Edits, cancellationToken);

                // If the edit handler returned an error, pass it through
                if (editResult is not Microsoft.AspNetCore.Http.HttpResults.JsonHttpResult<ApplyEditsResponse> jsonResult)
                {
                    return editResult;
                }

                if (jsonResult.Value is not { } applyResponse ||
                    !applyResponse.Success ||
                    HasFailedEditResult(applyResponse.AddResults) ||
                    HasFailedEditResult(applyResponse.UpdateResults) ||
                    HasFailedEditResult(applyResponse.DeleteResults))
                {
                    return StandardErrorHelpers.CreateBadRequest(
                        context,
                        "Uploaded replica edits failed to apply.");
                }
            }
        }

        // Update the last sync time and generation in distributed store.
        var changeTracker = context.RequestServices.GetRequiredService<IChangeTracker>();
        var currentGen = await changeTracker.GetCurrentGenerationAsync(cancellationToken);
        var updated = replica with
        {
            LastSyncTime = DateTimeOffset.UtcNow,
            LastSyncGeneration = currentGen
        };
        await replicaStore.SetAsync(updated, cancellationToken: cancellationToken);

        var response = new SynchronizeReplicaResponse
        {
            Success = true,
            ReplicaId = replicaId,
            SyncDirection = syncDirection,
            ServerGen = currentGen
        };

        return Results.Json(response, FeatureServerJsonContext.Default.SynchronizeReplicaResponse, contentType: "application/json");
    }

    private static bool HasFailedEditResult(EditResult[]? results)
    {
        return results is not null && Array.Exists(results, static result => !result.Success);
    }

    private static async Task<IResult> HandleUnRegisterReplica(
        string serviceId,
        HttpContext context)
    {
        using var activity = HonuaTelemetry.ActivitySource.StartActivity("featureserver.unRegisterReplica");
        activity?.SetTag(HonuaTelemetry.Tags.ServiceId, serviceId);

        var resourceValidator = context.RequestServices.GetRequiredService<IResourceValidator>();
        var cancellationToken = GetTimeoutAwareCancellationToken(context);
        var serviceValidationResult = await FeatureServerResourceValidationHelpers.ValidateServiceAsync(
            resourceValidator,
            serviceId,
            context,
            logger: null,
            cancellationToken: cancellationToken);
        if (!serviceValidationResult.IsValid)
        {
            return serviceValidationResult.ErrorResult!;
        }

        var service = serviceValidationResult.Service!;

        var writeAccessError = await RequireAnyServiceLayerWriteAccessBeforeBodyAsync(
            context,
            service,
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

        if (!TryResolveReplicaLayers(context, service, replica, AccessScope.Write, out var replicaLayers, out var replicaLayerError))
        {
            return replicaLayerError ?? StandardErrorHelpers.CreateNotFound(
                context,
                $"Replica '{replicaId}' not found for service '{serviceId}'.");
        }

        var unregisterRbacError = await RequireReplicaWriteAccessAsync(
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

    private static bool TryResolveReplicaLayerIds(
        HttpContext context,
        ServiceDefinition service,
        string? layersParam,
        out int[] layerIds,
        out IResult? error,
        AccessScope scope = AccessScope.Read)
    {
        layerIds = [];
        error = null;

        if (string.IsNullOrWhiteSpace(layersParam))
        {
            var accessibleLayers = service.Layers
                .Where(layer => AccessPolicyHelpers.EvaluateAccess(
                    context,
                    layer.Metadata?.AccessPolicy,
                    service.Metadata?.AccessPolicy,
                    scope).IsAllowed)
                .Select(layer => layer.Id)
                .ToArray();

            if (accessibleLayers.Length == 0)
            {
                error = AccessPolicyHelpers.RequireAnyLayerAccess(context, service.Layers, service, scope)
                        ?? StandardErrorHelpers.CreateForbidden(context, AccessPolicyHelpers.AccessForbiddenMessage);
                return false;
            }

            layerIds = accessibleLayers;
            return true;
        }

        var layerById = service.Layers.ToDictionary(layer => layer.Id);
        var tokens = layersParam.Split(',', StringSplitOptions.TrimEntries);
        if (tokens.Any(token => token.Length == 0))
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

            var accessError = AccessPolicyHelpers.RequireLayerAccess(context, layer, service, scope);
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

    private static bool TryResolveReplicaLayers(
        HttpContext context,
        ServiceDefinition service,
        ReplicaState replica,
        AccessScope scope,
        out LayerDefinition[] layers,
        out IResult? error)
    {
        layers = [];
        error = null;

        var serviceLayerById = service.Layers.ToDictionary(layer => layer.Id);
        var resolved = new List<LayerDefinition>(replica.LayerIds.Length);

        foreach (var layerId in replica.LayerIds.Distinct())
        {
            if (!serviceLayerById.TryGetValue(layerId, out var layer))
            {
                error = StandardErrorHelpers.CreateNotFound(
                    context,
                    $"Replica '{replica.ReplicaId}' not found for service '{service.Name}'.");
                return false;
            }

            var accessError = AccessPolicyHelpers.RequireLayerAccess(context, layer, service, scope);
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
                $"Replica '{replica.ReplicaId}' not found for service '{service.Name}'.");
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
        LastSyncGeneration = record.LastSyncGeneration
    };

    private static async Task<IResult?> RequireReplicaWriteAccessAsync(
        HttpContext context,
        ServiceDefinition service,
        IEnumerable<LayerDefinition> layers,
        CancellationToken cancellationToken)
    {
        foreach (var layer in layers.DistinctBy(static layer => layer.Id))
        {
            var rbacError = await ServiceDataEditorAuthorization.RequireLayerDataEditorAsync(
                context,
                layer,
                service,
                cancellationToken);
            if (rbacError != null)
            {
                return rbacError;
            }
        }

        return null;
    }

    private static async Task<IResult?> RequireAnyServiceLayerWriteAccessBeforeBodyAsync(
        HttpContext context,
        ServiceDefinition service,
        CancellationToken cancellationToken)
    {
        IResult? firstError = null;
        foreach (var layer in service.Layers.DistinctBy(static layer => layer.Id))
        {
            var rbacError = await ServiceDataEditorAuthorization.RequireLayerDataEditorAsync(
                context,
                layer,
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
    /// </summary>
    private static GeoServicesFeature ConvertFeatureToGeoServices(Feature feature)
    {
        return new GeoServicesFeature
        {
            Attributes = feature.Attributes
                .Where(kvp => !FeatureAttributeVisibility.IsInternalAttribute(kvp.Key))
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
            Geometry = GeoServicesGeometryConverter.ConvertWkbToGeoServicesGeometry(
                feature.Geometry, null, null, false, false)
        };
    }
}
