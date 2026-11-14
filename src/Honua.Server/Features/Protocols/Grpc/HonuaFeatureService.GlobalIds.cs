// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using Google.Protobuf.Collections;
using Grpc.Core;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Queries.Filters;
using Microsoft.Extensions.DependencyInjection;
using Proto = Geospatial.V1;

namespace Honua.Server.Features.Protocols.Grpc;

// geospatial-grpc 1.2 global-ID selectors on queries and edits. Selectors are bound as an
// ordinary predicate through the masked-field checks and read through the RLS-enforced reader,
// so a global ID never reaches a row or field the caller could not address by object ID. A
// hidden or masked global-ID field behaves as if the layer declared none.
internal sealed partial class HonuaFeatureService
{
    // One ApplyEdits call's global-ID context; Field is the caller-visible global-ID field.
    private readonly record struct GlobalIdEdit(
        Proto.ApplyEditsRequest Request,
        GrpcLayerContext Layer,
        IServiceProvider Services,
        string? Field);

    private readonly record struct QueryGlobalIds(GrpcLayerContext Layer, IServiceProvider Services, string? Field);

    // The layer's global-ID field when the caller may see it: not schema-hidden and not masked.
    private static async Task<string?> VisibleGlobalIdFieldAsync(
        GrpcLayerContext layer,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        if (GrpcGlobalIds.ResolveField(layer.Resource) is not { } field || layer.HiddenFields.Contains(field))
        {
            return null;
        }

        if (services.GetService<IFieldMaskSource>() is { } masks)
        {
            var masked = await masks.ResolveAsync(layer.Resource, cancellationToken).ConfigureAwait(false);
            if (masked.Any(name => name.Equals(field, StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }
        }

        return field;
    }

    private static string RequireGlobalIdField(string? visibleField)
        => visibleField ?? throw new RpcException(new Status(
            StatusCode.FailedPrecondition,
            "The layer does not declare a global-ID field, so global-ID selectors are unavailable."));

    // The bound global_ids predicate, or null without selectors.
    private static async Task<SqlFragment?> GlobalIdFilterAsync(
        Proto.QueryFeaturesRequest request,
        QueryGlobalIds selection,
        CancellationToken cancellationToken)
    {
        if (request.GlobalIds.Count == 0)
        {
            return null;
        }

        if (request.ObjectIds.Count > 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "object_ids and global_ids are mutually exclusive."));
        }

        ImmutableArray<string> ids;
        try
        {
            ids = GrpcGlobalIds.ParseDistinctSelectors(request.GlobalIds, "global_ids");
        }
        catch (ArgumentException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }

        var field = RequireGlobalIdField(selection.Field);
        return await BindWhereClauseAsync(
            GrpcGlobalIds.Predicate(selection.Layer.Resource, field, ids),
            selection.Layer.Resource,
            selection.Services,
            cancellationToken).ConfigureAwait(false);
    }

    // Reads the global-ID field even when out_fields omits it, so features carry their global IDs
    // independently of the projection; the extra column stays out of the attributes.
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

    private static Proto.Feature WithGlobalId(Proto.Feature proto, Feature feature, string? globalIdField)
    {
        proto.GlobalId = GrpcGlobalIds.Read(feature.Attributes, globalIdField) ?? string.Empty;
        return proto;
    }

    // Adds keep their client global IDs as attribute values; the field must be visible.
    private static FeatureEditBatch WithClientGlobalIds(GlobalIdEdit edit, FeatureEditBatch batch)
    {
        var adds = edit.Request.Adds;
        if (!adds.Any(static add => add.GlobalId.Length > 0))
        {
            return batch;
        }

        var field = RequireGlobalIdField(edit.Field);
        return batch with
        {
            Creates = [.. batch.Creates.Select((create, index) => adds[index].GlobalId is { Length: > 0 } globalId
                ? create with { Attributes = create.Attributes.SetItem(field, GrpcGlobalIds.Normalize(globalId)) }
                : create)],
        };
    }

    // Readdresses a use_global_ids batch by object ID after the idempotency replay check. Every
    // selector must name a row the caller can read (otherwise NOT_FOUND before any write), and a
    // client global ID on an add must not already exist.
    private async Task<FeatureEditBatch> ResolveGlobalIdTargetsAsync(
        GlobalIdEdit edit,
        FeatureEditBatch batch,
        CancellationToken cancellationToken)
    {
        var addIds = edit.Request.Adds.Where(static add => add.GlobalId.Length > 0)
            .Select(static add => GrpcGlobalIds.Normalize(add.GlobalId)).ToImmutableArray();
        if (!addIds.IsEmpty)
        {
            var existing = await ResolveObjectIdsAsync(edit, addIds, cancellationToken).ConfigureAwait(false);
            if (existing.Count > 0)
            {
                throw new RpcException(new Status(
                    StatusCode.InvalidArgument,
                    $"Global ID {existing.Keys.First()} already identifies a feature."));
            }
        }

        if (!edit.Request.UseGlobalIds)
        {
            return batch;
        }

        var updateIds = edit.Request.Updates.Select(static update => GrpcGlobalIds.Normalize(update.GlobalId)).ToArray();
        var deleteIds = edit.Request.DeleteGlobalIds.Select(GrpcGlobalIds.Normalize).ToArray();
        var targets = updateIds.Concat(deleteIds).ToImmutableArray();
        var objectIds = await ResolveObjectIdsAsync(edit, targets, cancellationToken).ConfigureAwait(false);
        var missing = targets.FirstOrDefault(id => !objectIds.ContainsKey(id));
        if (missing is not null)
        {
            throw new RpcException(new Status(StatusCode.NotFound, $"Feature with global ID {missing} was not found."));
        }

        return batch with
        {
            Updates = [.. batch.Updates.Select((update, index) => update with { Id = objectIds[updateIds[index]] })],
            Deletes = [.. deleteIds.Select(id => objectIds[id])],
        };
    }

    private async Task<Dictionary<string, long>> ResolveObjectIdsAsync(
        GlobalIdEdit edit,
        ImmutableArray<string> globalIds,
        CancellationToken cancellationToken)
    {
        var resolved = new Dictionary<string, long>(StringComparer.Ordinal);
        if (globalIds.IsEmpty)
        {
            return resolved;
        }

        var field = RequireGlobalIdField(edit.Field);
        var filter = await BindWhereClauseAsync(
            GrpcGlobalIds.Predicate(edit.Layer.Resource, field, globalIds), edit.Layer.Resource, edit.Services, cancellationToken)
            .ConfigureAwait(false);
        var rows = await _featureReader.QueryAsync(
            edit.Layer.StorageLayerId,
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

        return resolved;
    }

    // Fills result global IDs the writer did not report from the request's selectors, and removes
    // every global ID when the caller may not see the field. Runs before the receipt is stored,
    // so replays return exactly what was committed.
    private static Proto.ApplyEditsResponse StampResultGlobalIds(GlobalIdEdit edit, Proto.ApplyEditsResponse response)
    {
        var request = edit.Request;
        if (edit.Field is null)
        {
            ClearGlobalIds(response);
            return response;
        }

        Stamp(response.AddResults, [.. request.Adds.Select(static add => add.GlobalId)]);
        if (request.UseGlobalIds)
        {
            Stamp(response.UpdateResults, [.. request.Updates.Select(static update => update.GlobalId)]);
            Stamp(response.DeleteResults, [.. request.DeleteGlobalIds]);
        }

        return response;
    }

    private static void Stamp(RepeatedField<Proto.EditResult> results, string[] selectors)
    {
        for (var index = 0; index < results.Count && index < selectors.Length; index++)
        {
            if (results[index].GlobalId.Length == 0 && selectors[index].Length > 0)
            {
                results[index].GlobalId = GrpcGlobalIds.Normalize(selectors[index]);
            }
        }
    }

    private static void ClearGlobalIds(Proto.ApplyEditsResponse response)
    {
        foreach (var result in response.AddResults.Concat(response.UpdateResults).Concat(response.DeleteResults))
        {
            result.GlobalId = string.Empty;
        }
    }
}
