// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Queries.Filters;
using Honua.Protocols.GeoServices.FeatureServer.Models;

namespace Honua.Protocols.GeoServices.FeatureServer;

internal sealed class GlobalIdSlotResolution
{
    public required IFeatureReader Reader { get; init; }
    public required IFilterExpressionService FilterExpressionService { get; init; }
    public required MetadataV2Object Resource { get; init; }
    public required MetadataV2Field GlobalIdField { get; init; }
    public required int StorageLayerId { get; init; }
    public VersionContext? VersionContext { get; init; }
    public required FeatureServerEditsHandler.EditOperationContext Context { get; init; }
    public required long?[] SlotObjectIds { get; init; }
    public required string?[] SlotGlobalIds { get; init; }
    public required EditResult?[] Results { get; init; }
    public required int NotFoundCode { get; init; }
}

internal static class FeatureServerGlobalIdEdits
{
    internal static Task<IReadOnlyDictionary<long, Feature>> ResolveUpdateSlotsAsync(
        GeoServicesFeature[] updates,
        GlobalIdSlotResolution resolution,
        CancellationToken cancellationToken)
    {
        var lookupIds = new List<string>(updates.Length);
        for (var i = 0; i < updates.Length; i++)
        {
            var (hasRaw, rawValue) = FeatureServerGlobalIds.FindRawGlobalId(updates[i].Attributes, resolution.GlobalIdField.Name);
            var normalized = hasRaw ? FeatureServerGlobalIds.ParseGlobalId(rawValue) : null;
            if (normalized is null)
            {
                resolution.Context.HasValidationErrors = true;
                resolution.Results[i] = CreateSlotFailure(
                    GeoServicesEditErrorCodes.ValidationFailed,
                    hasRaw
                        ? "Invalid GlobalID for update operation"
                        : "GlobalID is required for update operations when useGlobalIds is true");
                continue;
            }

            resolution.SlotGlobalIds[i] = normalized;
            lookupIds.Add(normalized);
        }

        return MapSlotsAsync(resolution, lookupIds, cancellationToken);
    }

    internal static Task<IReadOnlyDictionary<long, Feature>> ResolveDeleteSlotsAsync(
        object[] deletes,
        GlobalIdSlotResolution resolution,
        CancellationToken cancellationToken)
    {
        var lookupIds = new List<string>(deletes.Length);
        for (var i = 0; i < deletes.Length; i++)
        {
            var normalized = FeatureServerGlobalIds.ParseGlobalId(deletes[i]);
            if (normalized is null)
            {
                resolution.Context.HasValidationErrors = true;
                resolution.Results[i] = CreateSlotFailure(
                    GeoServicesEditErrorCodes.ValidationFailed,
                    "Invalid GlobalID for delete operation");
                continue;
            }

            resolution.SlotGlobalIds[i] = normalized;
            lookupIds.Add(normalized);
        }

        return MapSlotsAsync(resolution, lookupIds, cancellationToken);
    }

    internal static void ApplyResponseGlobalIds(
        EditResult?[]? results,
        List<int> indexes,
        List<string?> globalIds)
    {
        if (results is null)
        {
            return;
        }

        var count = Math.Min(indexes.Count, globalIds.Count);
        for (var i = 0; i < count; i++)
        {
            var globalId = globalIds[i];
            if (globalId is not null && results[indexes[i]] is { GlobalId: null } result)
            {
                result.GlobalId = globalId;
            }
        }
    }

    private static async Task<IReadOnlyDictionary<long, Feature>> MapSlotsAsync(
        GlobalIdSlotResolution resolution,
        List<string> lookupIds,
        CancellationToken cancellationToken)
    {
        var resolved = new Dictionary<long, Feature>(lookupIds.Count);
        if (lookupIds.Count == 0)
        {
            return resolved;
        }

        var byGlobalId = await QuerySlotsAsync(resolution, lookupIds, cancellationToken).ConfigureAwait(false);
        if (byGlobalId is null)
        {
            return resolved;
        }

        PopulateMatchedSlots(resolution, byGlobalId, resolved);
        return resolved;
    }

    private static async Task<IReadOnlyDictionary<string, Feature>?> QuerySlotsAsync(
        GlobalIdSlotResolution resolution,
        List<string> lookupIds,
        CancellationToken cancellationToken)
    {
        var lookupContext = new GlobalIdLookupContext
        {
            Reader = resolution.Reader,
            FilterExpressionService = resolution.FilterExpressionService,
            Resource = resolution.Resource,
            Field = resolution.GlobalIdField,
            StorageLayerId = resolution.StorageLayerId,
            VersionContext = resolution.VersionContext
        };
        try
        {
            return await FeatureServerGlobalIds.ResolveFeaturesByGlobalIdsAsync(
                lookupContext, lookupIds, cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            FailAllSlots(resolution, "Invalid GlobalID field.");
            return null;
        }
    }

    private static void PopulateMatchedSlots(
        GlobalIdSlotResolution resolution,
        IReadOnlyDictionary<string, Feature> byGlobalId,
        Dictionary<long, Feature> resolved)
    {
        for (var i = 0; i < resolution.SlotGlobalIds.Length; i++)
        {
            if (resolution.SlotGlobalIds[i] is not { } normalizedGlobalId)
            {
                continue;
            }

            if (!byGlobalId.TryGetValue(normalizedGlobalId, out var feature))
            {
                resolution.Context.HasValidationErrors = true;
                resolution.Results[i] = CreateSlotFailure(resolution.NotFoundCode, "Feature not found", normalizedGlobalId);
                continue;
            }

            var publicObjectId = FeatureServerGlobalIds.ResolvePublicObjectId(feature, resolution.Resource);
            resolution.SlotObjectIds[i] = publicObjectId;
            resolved[publicObjectId] = feature;
        }
    }

    private static void FailAllSlots(GlobalIdSlotResolution resolution, string description)
    {
        for (var i = 0; i < resolution.SlotGlobalIds.Length; i++)
        {
            if (resolution.SlotGlobalIds[i] is { } failedGlobalId)
            {
                resolution.Context.HasValidationErrors = true;
                resolution.Results[i] = CreateSlotFailure(GeoServicesEditErrorCodes.ValidationFailed, description, failedGlobalId);
            }
        }
    }

    private static EditResult CreateSlotFailure(int code, string description, string? globalId = null)
        => new()
        {
            GlobalId = globalId,
            Success = false,
            Error = new EditError { Code = code, Description = description }
        };
}
