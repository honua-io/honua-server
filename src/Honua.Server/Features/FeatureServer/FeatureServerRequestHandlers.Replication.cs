// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Server.Features.FeatureServer.Models;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Honua.ServiceDefaults;

namespace Honua.Server.Features.FeatureServer;

internal static partial class FeatureServerEndpoints
{
    private static async Task<IResult> HandleCreateReplica(
        string serviceId,
        HttpContext context)
    {
        using var activity = HonuaTelemetry.ActivitySource.StartActivity("featureserver.createReplica");
        activity?.SetTag(HonuaTelemetry.Tags.ServiceId, serviceId);

        var resourceValidator = context.RequestServices.GetRequiredService<IResourceValidator>();
        var cancellationToken = GetTimeoutAwareCancellationToken(context);
        var serviceResult = await resourceValidator.ValidateServiceAsync(serviceId, cancellationToken);
        if (!serviceResult.IsValid)
        {
            return StandardErrorHelpers.CreateNotFound(context,
                serviceResult.ErrorMessage ?? "Service not found.");
        }

        var service = serviceResult.Resource!;
        var accessError = AccessPolicyHelpers.RequireServiceAccess(context, service);
        if (accessError != null)
        {
            return accessError;
        }

        var replicaStore = context.RequestServices.GetRequiredService<IReplicaStore>();

        var (values, readError) = await TryReadRequestValuesAsync(context.Request, cancellationToken);
        if (values == null)
        {
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

        var layerIds = ParseLayerIds(layersParam, service.Layers);

        var replicaId = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;

        var record = new ReplicaState(
            replicaId,
            replicaName,
            serviceId,
            syncModel,
            layerIds,
            now);
        await replicaStore.SetAsync(record, cancellationToken: cancellationToken);

        var response = new CreateReplicaResponse
        {
            ReplicaId = replicaId,
            ReplicaName = replicaName,
            SyncModel = syncModel,
            Layers = layerIds.Select(id => new ReplicaLayerInfo
            {
                Id = id,
                ServerGen = now.ToUnixTimeMilliseconds()
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
        var serviceResult = await resourceValidator.ValidateServiceAsync(serviceId, cancellationToken);
        if (!serviceResult.IsValid)
        {
            return StandardErrorHelpers.CreateNotFound(context,
                serviceResult.ErrorMessage ?? "Service not found.");
        }

        var service = serviceResult.Resource!;
        var accessError = AccessPolicyHelpers.RequireServiceAccess(context, service);
        if (accessError != null)
        {
            return accessError;
        }

        var replicaStore = context.RequestServices.GetRequiredService<IReplicaStore>();

        var (values, readError) = await TryReadRequestValuesAsync(context.Request, cancellationToken);
        if (values == null)
        {
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

        // Query real feature counts from the database for each layer in the replica.
        // For the first sync (LastSyncTime == CreatedAt), all features are reported as adds.
        // For subsequent syncs, without dedicated change tracking tables, report zero changes.
        var isFirstSync = replica.LastSyncTime == replica.CreatedAt;
        var featureReader = context.RequestServices.GetRequiredService<IFeatureReader>();
        var layerChanges = new List<LayerChanges>();

        foreach (var layerIdInReplica in replica.LayerIds)
        {
            var adds = 0L;
            if (isFirstSync)
            {
                adds = await featureReader.CountAsync(layerIdInReplica, new FeatureQuery(), cancellationToken);
            }

            layerChanges.Add(new LayerChanges
            {
                Id = layerIdInReplica,
                Adds = (int)Math.Min(adds, int.MaxValue),
                Updates = 0,
                Deletes = 0
            });
        }

        var response = new ExtractChangesResponse
        {
            Success = true,
            ReplicaId = replicaId,
            LayerChanges = layerChanges.ToArray()
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
        var serviceResult = await resourceValidator.ValidateServiceAsync(serviceId, cancellationToken);
        if (!serviceResult.IsValid)
        {
            return StandardErrorHelpers.CreateNotFound(context,
                serviceResult.ErrorMessage ?? "Service not found.");
        }

        var service = serviceResult.Resource!;
        var accessError = AccessPolicyHelpers.RequireServiceAccess(context, service);
        if (accessError != null)
        {
            return accessError;
        }

        var replicaStore = context.RequestServices.GetRequiredService<IReplicaStore>();

        var (values, readError) = await TryReadRequestValuesAsync(context.Request, cancellationToken);
        if (values == null)
        {
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
                var targetLayerId = replica.LayerIds.Length > 0 ? replica.LayerIds[0] : 0;
                var editsHandler = context.RequestServices.GetRequiredService<FeatureServerEditsHandler>();
                var limitsOptions = context.RequestServices
                    .GetRequiredService<Microsoft.Extensions.Options.IOptions<Honua.Core.Configuration.LimitsOptions>>();

                var editRequest = new ApplyEditsRequest { Adds = features, RollbackOnFailure = false };
                var editResult = await editsHandler.HandleApplyEditsAsync(
                    serviceId, targetLayerId, editRequest, limitsOptions.Value.Edits, cancellationToken);

                // If the edit handler returned an error, pass it through
                if (editResult is not Microsoft.AspNetCore.Http.HttpResults.JsonHttpResult<ApplyEditsResponse>)
                {
                    return editResult;
                }
            }
        }

        // Update the last sync time in distributed store.
        var updated = replica with { LastSyncTime = DateTimeOffset.UtcNow };
        await replicaStore.SetAsync(updated, cancellationToken: cancellationToken);

        var response = new SynchronizeReplicaResponse
        {
            Success = true,
            ReplicaId = replicaId,
            SyncDirection = syncDirection
        };

        return Results.Json(response, FeatureServerJsonContext.Default.SynchronizeReplicaResponse, contentType: "application/json");
    }

    private static async Task<IResult> HandleUnRegisterReplica(
        string serviceId,
        HttpContext context)
    {
        using var activity = HonuaTelemetry.ActivitySource.StartActivity("featureserver.unRegisterReplica");
        activity?.SetTag(HonuaTelemetry.Tags.ServiceId, serviceId);

        var resourceValidator = context.RequestServices.GetRequiredService<IResourceValidator>();
        var cancellationToken = GetTimeoutAwareCancellationToken(context);
        var serviceResult = await resourceValidator.ValidateServiceAsync(serviceId, cancellationToken);
        if (!serviceResult.IsValid)
        {
            return StandardErrorHelpers.CreateNotFound(context,
                serviceResult.ErrorMessage ?? "Service not found.");
        }

        var service = serviceResult.Resource!;
        var accessError = AccessPolicyHelpers.RequireServiceAccess(context, service);
        if (accessError != null)
        {
            return accessError;
        }

        var replicaStore = context.RequestServices.GetRequiredService<IReplicaStore>();

        var (values, readError) = await TryReadRequestValuesAsync(context.Request, cancellationToken);
        if (values == null)
        {
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

        var removed = await replicaStore.RemoveAsync(replicaId, cancellationToken);
        if (!removed)
        {
            return StandardErrorHelpers.CreateNotFound(context,
                $"Replica '{replicaId}' not found.");
        }

        var response = new SuccessResponse { Success = true };
        return Results.Json(response, FeatureServerJsonContext.Default.SuccessResponse, contentType: "application/json");
    }

    private static int[] ParseLayerIds(string? layersParam, IReadOnlyList<Honua.Core.Features.Catalog.Domain.LayerDefinition> serviceLayers)
    {
        if (string.IsNullOrWhiteSpace(layersParam))
        {
            return serviceLayers.Select(l => l.Id).ToArray();
        }

        var tokens = layersParam.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var ids = new List<int>();
        foreach (var token in tokens)
        {
            if (int.TryParse(token, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var id))
            {
                ids.Add(id);
            }
        }

        return ids.Count > 0 ? ids.ToArray() : serviceLayers.Select(l => l.Id).ToArray();
    }
}
