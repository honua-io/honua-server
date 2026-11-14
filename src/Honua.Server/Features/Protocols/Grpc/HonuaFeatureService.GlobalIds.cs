// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using Google.Protobuf.Collections;
using Grpc.Core;
using Honua.Core.Features.FeatureStore.Domain;
using Proto = Geospatial.V1;

namespace Honua.Server.Features.Protocols.Grpc;

/// <summary>
/// geospatial-grpc 1.2 global-ID selectors on queries and edits. Selectors become an ordinary
/// where predicate bound through the masked-field checks and the RLS-enforced reader, so a
/// global ID never reaches a row or field the caller could not address by object ID.
/// </summary>
internal sealed partial class HonuaFeatureService
{
    /// <summary>The caller's where clause, narrowed by <c>global_ids</c> when present.</summary>
    private static string? EffectiveWhere(Proto.QueryFeaturesRequest request, GrpcLayerContext layer)
    {
        if (request.GlobalIds.Count == 0)
        {
            return request.Where;
        }

        if (request.ObjectIds.Count > 0)
        {
            throw new RpcException(new Status(
                StatusCode.InvalidArgument,
                "object_ids and global_ids are mutually exclusive."));
        }

        var ids = GrpcGlobalIds.ParseSelectors(request.GlobalIds, "global_ids");
        return GrpcGlobalIds.CombineWhere(request.Where, GrpcGlobalIds.RequireField(layer.Resource), ids);
    }

    /// <summary>The layer's global-ID field when the caller may see it, otherwise <c>null</c>.</summary>
    private static string? VisibleGlobalIdField(GrpcLayerContext layer)
        => GrpcGlobalIds.ResolveField(layer.Resource) is { } field && !layer.HiddenFields.Contains(field) ? field : null;

    /// <summary>
    /// Reads the global-ID field even when <c>out_fields</c> omits it, so features carry their
    /// global IDs independently of the projection; the extra column stays out of the attributes.
    /// </summary>
    private static (FeatureQuery Query, IReadOnlySet<string> HiddenAttributes) WithGlobalIdField(
        FeatureQuery query,
        string? globalIdField,
        IReadOnlySet<string> hiddenFields)
    {
        if (globalIdField is null
            || query.Distinct
            || query.OutFields is not { } outFields
            || outFields.Contains(globalIdField, StringComparer.OrdinalIgnoreCase))
        {
            return (query, hiddenFields);
        }

        var hidden = new HashSet<string>(hiddenFields, StringComparer.OrdinalIgnoreCase) { globalIdField };
        return (query with { OutFields = outFields.Add(globalIdField) }, hidden);
    }

    /// <summary>
    /// Readdresses a <c>use_global_ids</c> batch by object ID. Every selector must name a row the
    /// caller can read; a missing or hidden target fails the whole call with NOT_FOUND before
    /// any write, as for integer selectors. Adds keep their client global IDs as attributes.
    /// </summary>
    private async Task<FeatureEditBatch> ResolveGlobalIdTargetsAsync(
        Proto.ApplyEditsRequest request,
        FeatureEditBatch batch,
        GrpcLayerContext layer,
        IServiceProvider requestServices,
        CancellationToken cancellationToken)
    {
        var addsWithIds = request.Adds.Any(static add => add.GlobalId.Length > 0);
        if (!request.UseGlobalIds && !addsWithIds)
        {
            return batch;
        }

        var field = GrpcGlobalIds.RequireField(layer.Resource);
        batch = batch with { Creates = WithClientGlobalIds(batch.Creates, request.Adds, field) };
        if (!request.UseGlobalIds)
        {
            return batch;
        }

        var updateIds = request.Updates.Select(update => GrpcGlobalIds.ParseSelector(update.GlobalId, "Feature.global_id")).ToArray();
        var deleteIds = GrpcGlobalIds.ParseSelectors(request.DeleteGlobalIds, "delete_global_ids");
        var objectIds = await ResolveObjectIdsAsync(
            layer, field, [.. updateIds.Concat(deleteIds).Distinct(StringComparer.Ordinal)], requestServices, cancellationToken)
            .ConfigureAwait(false);

        return batch with
        {
            Updates = [.. batch.Updates.Select((update, index) => update with { Id = objectIds[updateIds[index]] })],
            Deletes = [.. deleteIds.Select(id => objectIds[id])],
        };
    }

    private async Task<Dictionary<string, long>> ResolveObjectIdsAsync(
        GrpcLayerContext layer,
        string field,
        ImmutableArray<string> globalIds,
        IServiceProvider requestServices,
        CancellationToken cancellationToken)
    {
        var resolved = new Dictionary<string, long>(StringComparer.Ordinal);
        if (globalIds.IsEmpty)
        {
            return resolved;
        }

        var filter = await BindWhereClauseAsync(
            GrpcGlobalIds.CombineWhere(null, field, globalIds), layer.Resource, requestServices, cancellationToken)
            .ConfigureAwait(false);
        var rows = await _featureReader.QueryAsync(
            layer.StorageLayerId,
            new FeatureQuery { SqlFilter = filter, OutFields = [field], Limit = globalIds.Length + 1 },
            cancellationToken).ConfigureAwait(false);
        foreach (var row in rows.Items)
        {
            if (GrpcGlobalIds.Read(row.Attributes, field) is { } globalId && !resolved.TryAdd(globalId, row.Id))
            {
                throw new RpcException(new Status(
                    StatusCode.FailedPrecondition,
                    $"Global ID {globalId} identifies more than one feature."));
            }
        }

        foreach (var globalId in globalIds)
        {
            if (!resolved.ContainsKey(globalId))
            {
                throw new RpcException(new Status(StatusCode.NotFound, $"Feature with global ID {globalId} was not found."));
            }
        }

        return resolved;
    }

    private static ImmutableArray<Feature> WithClientGlobalIds(
        ImmutableArray<Feature> creates,
        RepeatedField<Proto.Feature> adds,
        string field)
    {
        if (creates.IsDefaultOrEmpty)
        {
            return creates;
        }

        return [.. creates.Select((create, index) => adds[index].GlobalId is { Length: > 0 } globalId
            ? create with { Attributes = create.Attributes.SetItem(field, GrpcGlobalIds.ParseSelector(globalId, "Feature.global_id")) }
            : create)];
    }

    /// <summary>Fills result global IDs the writer did not report from the request's selectors.</summary>
    private static void StampResultGlobalIds(Proto.ApplyEditsRequest request, Proto.ApplyEditsResponse response)
    {
        Stamp(response.AddResults, request.Adds.Select(static add => add.GlobalId).ToArray());
        if (request.UseGlobalIds)
        {
            Stamp(response.UpdateResults, request.Updates.Select(static update => update.GlobalId).ToArray());
            Stamp(response.DeleteResults, [.. GrpcGlobalIds.ParseSelectors(request.DeleteGlobalIds, "delete_global_ids")]);
        }

        static void Stamp(IList<Proto.EditResult> results, string[] selectors)
        {
            for (var index = 0; index < results.Count && index < selectors.Length; index++)
            {
                if (results[index].GlobalId.Length == 0 && selectors[index].Length > 0)
                {
                    results[index].GlobalId = GrpcGlobalIds.Normalize(selectors[index]);
                }
            }
        }
    }
}
