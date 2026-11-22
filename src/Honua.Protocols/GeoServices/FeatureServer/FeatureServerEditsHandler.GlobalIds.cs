// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.GeoServices.FeatureServer.Models;

namespace Honua.Protocols.GeoServices.FeatureServer;

internal sealed partial class FeatureServerEditsHandler
{
    private sealed class UpdateSlotRequest
    {
        public required ApplyEditsRequest Request { get; init; }
        public required MetadataV2Object Resource { get; init; }
        public MetadataV2Field? GlobalIdField { get; init; }
        public required int StorageLayerId { get; init; }
        public VersionContext? VersionContext { get; init; }
        public required EditOperationContext Context { get; init; }
        public required long?[] SlotObjectIds { get; init; }
        public required string?[] SlotGlobalIds { get; init; }
    }

    private sealed class DeleteSlotRequest
    {
        public required ApplyEditsRequest Request { get; init; }
        public required EditTarget Target { get; init; }
        public MetadataV2Field? GlobalIdField { get; init; }
        public required EditOperationContext Context { get; init; }
        public required long?[] SlotObjectIds { get; init; }
        public required string?[] SlotGlobalIds { get; init; }
    }

    private async Task<IReadOnlyDictionary<long, Feature>?> ResolveUpdateFeaturesAsync(
        UpdateSlotRequest slotRequest,
        CancellationToken cancellationToken)
    {
        var updates = slotRequest.Request.Updates!;
        if (slotRequest.Request.UseGlobalIds && slotRequest.GlobalIdField is not null)
        {
            var resolution = new GlobalIdSlotResolution
            {
                Reader = _featureReader,
                FilterExpressionService = _filterExpressionService,
                Resource = slotRequest.Resource,
                GlobalIdField = slotRequest.GlobalIdField,
                StorageLayerId = slotRequest.StorageLayerId,
                VersionContext = slotRequest.VersionContext,
                Context = slotRequest.Context,
                SlotObjectIds = slotRequest.SlotObjectIds,
                SlotGlobalIds = slotRequest.SlotGlobalIds,
                Results = slotRequest.Context.UpdateResults!,
                NotFoundCode = GeoServicesEditErrorCodes.NotFound
            };
            return await FeatureServerGlobalIdEdits.ResolveUpdateSlotsAsync(updates, resolution, cancellationToken)
                .ConfigureAwait(false);
        }

        return await ResolveUpdateFeaturesByObjectIdsAsync(slotRequest, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyDictionary<long, Feature>?> ResolveUpdateFeaturesByObjectIdsAsync(
        UpdateSlotRequest slotRequest,
        CancellationToken cancellationToken)
    {
        var updates = slotRequest.Request.Updates!;
        for (var i = 0; i < updates.Length; i++)
        {
            var hasObjectId = TryGetObjectId(updates[i].Attributes, slotRequest.Resource, out var parsedObjectId);
            if (!hasObjectId || parsedObjectId <= 0)
            {
                slotRequest.Context.HasValidationErrors = true;
                var description = hasObjectId ? "Invalid Object ID for update operation" : "ObjectId is required for update operations";
                slotRequest.Context.UpdateResults![i] = CreateFailureResult(GeoServicesEditErrorCodes.InvalidObjectId, description, hasObjectId ? parsedObjectId : null);
                continue;
            }

            slotRequest.SlotObjectIds[i] = parsedObjectId;
        }

        try
        {
            return await ResolveFeaturesByGeoServicesObjectIdsAsync(
                slotRequest.Resource, slotRequest.StorageLayerId, slotRequest.SlotObjectIds, slotRequest.VersionContext, cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            FailObjectIdSlots(slotRequest.SlotObjectIds, slotRequest.Context, SanitizeEditErrorMessage(ex.Message, InvalidFeatureDataMessage));
            return null;
        }
    }

    private async Task<IReadOnlyDictionary<long, Feature>?> ResolveDeleteFeaturesAsync(
        DeleteSlotRequest slotRequest,
        CancellationToken cancellationToken)
    {
        var deletes = slotRequest.Request.Deletes!;
        if (slotRequest.Request.UseGlobalIds && slotRequest.GlobalIdField is not null)
        {
            var resolution = new GlobalIdSlotResolution
            {
                Reader = _featureReader,
                FilterExpressionService = _filterExpressionService,
                Resource = slotRequest.Target.Resource,
                GlobalIdField = slotRequest.GlobalIdField,
                StorageLayerId = slotRequest.Target.StorageLayerId,
                VersionContext = slotRequest.Target.VersionContext,
                Context = slotRequest.Context,
                SlotObjectIds = slotRequest.SlotObjectIds,
                SlotGlobalIds = slotRequest.SlotGlobalIds,
                Results = slotRequest.Context.DeleteResults!,
                NotFoundCode = GeoServicesEditErrorCodes.DeleteNotFound
            };
            return await FeatureServerGlobalIdEdits.ResolveDeleteSlotsAsync(deletes, resolution, cancellationToken)
                .ConfigureAwait(false);
        }

        return await ResolveDeleteFeaturesByObjectIdsAsync(slotRequest, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyDictionary<long, Feature>?> ResolveDeleteFeaturesByObjectIdsAsync(
        DeleteSlotRequest slotRequest,
        CancellationToken cancellationToken)
    {
        var deletes = slotRequest.Request.Deletes!;
        for (var i = 0; i < deletes.Length; i++)
        {
            if (!FeatureServerValueParser.TryConvertToLong(deletes[i], out var parsedObjectId) || parsedObjectId <= 0)
            {
                slotRequest.Context.HasValidationErrors = true;
                slotRequest.Context.DeleteResults![i] = CreateFailureResult(
                    code: GeoServicesEditErrorCodes.InvalidObjectId,
                    description: "Invalid Object ID for delete operation",
                    objectId: parsedObjectId > 0 ? parsedObjectId : null);
                continue;
            }

            slotRequest.SlotObjectIds[i] = parsedObjectId;
        }

        try
        {
            return await ResolveFeaturesByGeoServicesObjectIdsAsync(
                slotRequest.Target.Resource,
                slotRequest.Target.StorageLayerId,
                slotRequest.SlotObjectIds,
                slotRequest.Target.VersionContext,
                cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            FailDeleteObjectIdSlots(
                slotRequest.SlotObjectIds,
                slotRequest.Context,
                SanitizeEditErrorMessage(ex.Message, InvalidFeatureDataMessage));
            return null;
        }
    }

    private static void FailObjectIdSlots(
        long?[] slotObjectIds,
        EditOperationContext context,
        string description)
    {
        for (var i = 0; i < slotObjectIds.Length; i++)
        {
            if (slotObjectIds[i] is { } failedObjectId)
            {
                context.HasValidationErrors = true;
                context.UpdateResults![i] = CreateFailureResult(
                    code: GeoServicesEditErrorCodes.InvalidObjectId,
                    description: description,
                    objectId: failedObjectId);
            }
        }
    }

    private static void FailDeleteObjectIdSlots(
        long?[] slotObjectIds,
        EditOperationContext context,
        string description)
    {
        for (var i = 0; i < slotObjectIds.Length; i++)
        {
            if (slotObjectIds[i] is { } failedObjectId)
            {
                context.HasValidationErrors = true;
                context.DeleteResults![i] = CreateFailureResult(
                    code: GeoServicesEditErrorCodes.InvalidObjectId,
                    description: description,
                    objectId: failedObjectId);
            }
        }
    }
}
