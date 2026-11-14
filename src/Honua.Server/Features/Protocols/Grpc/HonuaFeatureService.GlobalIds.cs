// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using Google.Protobuf.Collections;
using Grpc.Core;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Edit;
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
    // ClientAddIds are the IDs adds supplied; AssignedAddIds the IDs every add was given.
    private readonly record struct GlobalIdEdit(
        Proto.ApplyEditsRequest Request,
        GrpcLayerContext Layer,
        IServiceProvider Services)
    {
        public string? Field { get; init; }

        public ImmutableArray<string> ClientAddIds { get; init; } = [];

        public ImmutableArray<string> AssignedAddIds { get; init; } = [];
    }

    private readonly record struct QueryGlobalIds(GrpcLayerContext Layer, IServiceProvider Services, string? Field);

    // Wire edits to a domain batch: lexically invalid input is INVALID_ARGUMENT, a valid value the
    // server cannot hold is FAILED_PRECONDITION; both before any write.
    private static FeatureEditBatch ToEditBatch(Proto.ApplyEditsRequest request)
    {
        try
        {
            return GrpcConversionHelpers.ToFeatureEditBatch(request);
        }
        catch (UnstorableValueException ex)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, ex.Message));
        }
        catch (ArgumentException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
    }

    // After the authorization, capability and limit gates: storability, the caller-visible
    // global-ID field and insert global IDs.
    private static async Task<(FeatureEditBatch Batch, GlobalIdEdit Edit)> PrepareGlobalIdsAsync(
        EditCall call,
        FeatureEditBatch batch)
    {
        GrpcTypedValues.EnsureStorable(call.Layer.Resource, batch);
        var services = call.Context.GetHttpContext().RequestServices;
        var field = await VisibleGlobalIdFieldAsync(call.Layer, services, call.Context.CancellationToken).ConfigureAwait(false);
        return AssignInsertGlobalIds(new GlobalIdEdit(call.Request, call.Layer, services) { Field = field }, batch);
    }

    private readonly record struct EditCall(Proto.ApplyEditsRequest Request, GrpcLayerContext Layer, ServerCallContext Context);

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
        var cap = GrpcGlobalIds.MaxSelectorsPerPredicate(selection.Layer.Resource, field);
        if (ids.Length > cap)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, $"global_ids accepts at most {cap} IDs per request."));
        }

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

    // Every add gets a global ID, as on GeoServices: the client's (Feature.global_id or the raw
    // attribute, validated and checked for duplicates like any selector) or a new one. Writers
    // generate none. Updates never change a stored global ID, so raw values on updates are dropped.
    private static (FeatureEditBatch Batch, GlobalIdEdit Edit) AssignInsertGlobalIds(GlobalIdEdit edit, FeatureEditBatch batch)
    {
        if (GrpcGlobalIds.ResolveField(edit.Layer.Resource) is not { } field)
        {
            return edit.Request.Adds.Any(static add => add.GlobalId.Length > 0)
                ? throw new RpcException(new Status(StatusCode.FailedPrecondition, "The layer does not declare a global-ID field."))
                : (batch, edit);
        }

        string?[] supplied;
        ImmutableArray<string> clientIds;
        try
        {
            supplied = batch.Creates.Select((create, index) => ClientGlobalId(edit.Request.Adds[index], create, field)).ToArray();
            clientIds = GrpcGlobalIds.ParseDistinctSelectors(supplied.OfType<string>(), "add global IDs");
        }
        catch (ArgumentException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }

        if (!clientIds.IsEmpty)
        {
            RequireGlobalIdField(edit.Field);
        }

        var assigned = ImmutableArray.CreateBuilder<string>(batch.Creates.Length);
        var creates = batch.Creates.Select((create, index) =>
        {
            var attributes = new Dictionary<string, object?>(WithoutField(create.Attributes, field), StringComparer.OrdinalIgnoreCase);
            var id = InsertGlobalIds.Resolve(supplied[index]);
            attributes[field] = id;
            assigned.Add(id);
            return create with { Attributes = attributes.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase) };
        }).ToImmutableArray();
        var updates = batch.Updates.Select(update => update with { Attributes = WithoutField(update.Attributes, field) }).ToImmutableArray();
        return (batch with { Creates = creates, Updates = updates },
            edit with { ClientAddIds = clientIds, AssignedAddIds = assigned.MoveToImmutable() });
    }

    private static string? ClientGlobalId(Proto.Feature add, Feature create, string field)
    {
        foreach (var (key, value) in create.Attributes)
        {
            if (key.Equals(field, StringComparison.OrdinalIgnoreCase) && value is not null)
            {
                if (value is not (Guid or string { Length: > 0 }))
                {
                    throw new ArgumentException($"{field} must contain a UUID.");
                }

                var raw = GrpcGlobalIds.ParseSelector(InsertGlobalIds.Normalize(value), field);
                if (add.GlobalId.Length > 0 && !raw.Equals(add.GlobalId, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException($"{field} and Feature.global_id must identify the same UUID.");
                }

                return raw;
            }
        }

        return add.GlobalId.Length > 0 ? add.GlobalId : null;
    }

    private static ImmutableDictionary<string, object?> WithoutField(ImmutableDictionary<string, object?> attributes, string field)
        => attributes.Keys.Where(key => key.Equals(field, StringComparison.OrdinalIgnoreCase)).Aggregate(
            attributes, static (kept, key) => kept.Remove(key));

    // Readdresses a use_global_ids batch by object ID after the idempotency replay check. Every
    // selector must name a row the caller can read (otherwise NOT_FOUND before any write), and a
    // client global ID on an add must not already exist.
    private async Task<FeatureEditBatch> ResolveGlobalIdTargetsAsync(
        GlobalIdEdit edit,
        FeatureEditBatch batch,
        CancellationToken cancellationToken)
    {
        await EnsureClientAddIdsAreNewAsync(edit, cancellationToken).ConfigureAwait(false);
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

    private async Task EnsureClientAddIdsAreNewAsync(GlobalIdEdit edit, CancellationToken cancellationToken)
    {
        // Best effort: the check sees only rows the caller may read (it must not reveal hidden
        // rows) and runs outside the write transaction. Managed storage has no unique index on
        // the global-ID attribute, so concurrent or RLS-hidden duplicates are not excluded yet.
        var addIds = edit.ClientAddIds;
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
        var rows = new List<Feature>();
        foreach (var chunk in globalIds.Chunk(GrpcGlobalIds.MaxSelectorsPerPredicate(edit.Layer.Resource, field)))
        {
            var filter = await BindWhereClauseAsync(
                GrpcGlobalIds.Predicate(edit.Layer.Resource, field, [.. chunk]), edit.Layer.Resource, edit.Services, cancellationToken)
                .ConfigureAwait(false);
            var page = await _featureReader.QueryAsync(
                edit.Layer.StorageLayerId,
                new FeatureQuery { SqlFilter = filter, OutFields = [field], Limit = chunk.Length + 1 },
                cancellationToken).ConfigureAwait(false);
            rows.AddRange(page.Items);
        }

        foreach (var row in rows)
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
    // so receipts retain the committed identity. Replay applies the current field visibility.
    private static Proto.ApplyEditsResponse StampResultGlobalIds(GlobalIdEdit edit, Proto.ApplyEditsResponse response)
    {
        var request = edit.Request;
        if (edit.Field is null)
        {
            ClearGlobalIds(response);
            return response;
        }

        StampAdds(response.AddResults, edit.AssignedAddIds);
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

    // An add's identity is the ID this call assigned (the writer reads a fixed attribute name);
    // a failed add stored nothing, so it reports none.
    private static void StampAdds(RepeatedField<Proto.EditResult> results, ImmutableArray<string> assigned)
    {
        for (var index = 0; index < results.Count; index++)
        {
            results[index].GlobalId = results[index].Success && index < assigned.Length ? assigned[index] : string.Empty;
        }
    }

    // Receipt identities remain tied to the committed attempt, but access is evaluated anew.
    // Never restamp a replay with this retry's newly generated insert IDs.
    private static Proto.ApplyEditsResponse FilterResultGlobalIds(GlobalIdEdit edit, Proto.ApplyEditsResponse response)
    {
        if (edit.Field is null)
        {
            ClearGlobalIds(response);
        }

        return response;
    }

    private static void ClearGlobalIds(Proto.ApplyEditsResponse response)
    {
        foreach (var result in response.AddResults.Concat(response.UpdateResults).Concat(response.DeleteResults))
        {
            result.GlobalId = string.Empty;
        }
    }
}
