// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Grpc.Core;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Metadata.Domain.V2;
using Microsoft.AspNetCore.DataProtection;
using Proto = Geospatial.V1;

namespace Honua.Server.Features.Protocols.Grpc;

internal sealed partial class HonuaFeatureService
{
    public override async Task<Proto.QueryRelatedRecordsResponse> QueryRelatedRecords(
        Proto.QueryRelatedRecordsRequest request, ServerCallContext context)
    {
        EnrichActivity("query_related_records", request.ServiceId, request.LayerId);
        ValidateRelatedRequest(request);
        var plan = await PrepareRelatedReadAsync(request, context).ConfigureAwait(false);
        var services = context.GetHttpContext().RequestServices;
        var store = await ResolveRelatedStoreAsync(plan.Origin, plan.Related, context).ConfigureAwait(false);
        var tokens = new GrpcRelatedPageTokens(services.GetRequiredService<IDataProtectionProvider>(),
            RelatedTokenBinding(request, plan.Origin, context));
        var cursor = string.IsNullOrEmpty(request.PageToken) ? (RelatedPageCursor?)null :
            tokens.Decode(request.PageToken, DateTimeOffset.UtcNow);
        var relationship = RelatedQuery.ForObjects(request.ObjectIds.Order().ToArray(), plan.Related.StorageLayerId,
            plan.Relationship.OriginKeyField, plan.Relationship.DestinationKeyField) with
        {
            SqlFilter = plan.Query.Query.SqlFilter,
            OutFields = plan.Query.Query.OutFields
        };
        var pageQuery = new RelatedPageQuery(relationship,
            request.PageSize == 0 ? Math.Min(_maxRecordCount, RelatedPageQuery.MaximumPositions) : request.PageSize, cursor)
        {
            OutputSrid = plan.Query.Query.OutputSrid
        };
        try
        {
            pageQuery.Validate();
        }
        catch (ArgumentException)
        {
            throw MetadataArgument("Invalid related-record page cursor.");
        }
        var page = await store.QueryRelatedPageAsync(plan.Origin.StorageLayerId, pageQuery, context.CancellationToken)
            .ConfigureAwait(false);
        return BuildRelatedResponse(plan, page, tokens);
    }

    private void ValidateRelatedRequest(Proto.QueryRelatedRecordsRequest request)
    {
        if (request.ObjectIds.Count == 0 || request.ObjectIds.Count > Math.Min(_maxRecordCount, RelatedPageQuery.MaximumOrigins) ||
            request.ObjectIds.Distinct().Count() != request.ObjectIds.Count || request.PageSize < 0 ||
            request.PageSize > Math.Min(_maxRecordCount, RelatedPageQuery.MaximumPositions))
        {
            throw MetadataArgument("Related queries require distinct, bounded object_ids and page_size.");
        }
    }

    private async Task<RelatedReadPlan> PrepareRelatedReadAsync(Proto.QueryRelatedRecordsRequest request,
        ServerCallContext context)
    {
        var origin = await ValidateGrpcLayerAsync(request.ServiceId, request.LayerId, context.CancellationToken)
            .ConfigureAwait(false);
        await EnsureReadAccessAsync(context, origin.Service, origin.Resource).ConfigureAwait(false);
        var (related, descriptor) = await ResolveRelatedLayerAsync(origin, request.RelationshipId, context).ConfigureAwait(false);
        await EnsureReadAccessAsync(context, related.Service, related.Resource).ConfigureAwait(false);
        var fields = (await VisibleMetadataFieldsAsync(related, context).ConfigureAwait(false))
            .Where(field => field.Type is not (MetadataV2FieldType.Geometry or MetadataV2FieldType.Geography)).ToArray();
        var visible = fields.Select(field => field.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (request.OutFields.Any(field => field != "*" && !visible.Contains(field)))
        {
            throw MetadataArgument("out_fields contains an unavailable field.");
        }
        var queryRequest = new Proto.QueryFeaturesRequest
        {
            Where = request.Where, ReturnGeometry = request.ReturnGeometry, OutSr = request.OutSr
        };
        queryRequest.OutFields.AddRange(fields.Where(field => request.OutFields.Count == 0 ||
            request.OutFields.Contains("*") || request.OutFields.Contains(field.Name, StringComparer.OrdinalIgnoreCase))
            .Select(field => field.Name));
        var query = await CreateQueryContextAsync(queryRequest, related,
            context.GetHttpContext().RequestServices, context.CancellationToken).ConfigureAwait(false);
        return new RelatedReadPlan
        {
            Request = request, Origin = origin, Related = related, Relationship = descriptor, Query = query, Fields = fields
        };
    }

    private static async Task<(GrpcLayerContext Layer, Proto.RelationshipDescriptor Descriptor)> ResolveRelatedLayerAsync(
        GrpcLayerContext origin, int relationshipId, ServerCallContext context)
    {
        if (origin.Snapshot is not null)
        {
            var visible = (await VisibleMetadataFieldsAsync(origin, context).ConfigureAwait(false))
                .Select(field => field.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var relationship in origin.Resource.Relationships)
            {
                var descriptor = await MetadataRelationshipAsync(new MetadataVisibility(origin, visible), relationship, context)
                    .ConfigureAwait(false);
                if (descriptor?.RelationshipId != relationshipId)
                {
                    continue;
                }
                var publication = origin.Snapshot.PublicationsForService(origin.Service.Metadata.Id)
                    .Single(publication => publication.LayerIndex == descriptor.RelatedLayerId &&
                        publication.ResourceId == relationship.RelatedResourceId && origin.Snapshot.IsRoutable(publication));
                var resource = origin.Snapshot.ResolveResource(publication)!;
                var storageId = origin.Snapshot.ResolveStorageLayerId(publication, resource)!.Value;
                return (CreateLayerContext(origin.Service, publication, resource, storageId, origin.Snapshot), descriptor);
            }
        }
        throw new RpcException(new Status(StatusCode.NotFound, "Relationship is unavailable."));
    }

    private async Task<IRelationshipStore> ResolveRelatedStoreAsync(GrpcLayerContext origin, GrpcLayerContext related,
        ServerCallContext context)
    {
        var services = context.GetHttpContext().RequestServices;
        var cancellationToken = context.CancellationToken;
        var router = services.GetService<FeatureProviderQueryRouter>();
        IRelationshipStore? store = _featureReader as IRelationshipStore;
        if (router is not null && origin.Snapshot is not null)
        {
            var left = await router.ResolveBindingAsync(origin.Snapshot, origin.Service, origin.Resource,
                origin.Publication, origin.StorageLayerId, FeatureProviderReadOperation.Query, cancellationToken).ConfigureAwait(false);
            var right = await router.ResolveBindingAsync(origin.Snapshot, related.Service, related.Resource,
                related.Publication, related.StorageLayerId, FeatureProviderReadOperation.Query, cancellationToken).ConfigureAwait(false);
            // The managed pair-page capability joins one provider's managed table. Other storage
            // paths must supply their own bounded implementation rather than fall back to that table.
            store = ReferenceEquals(left.Provider, right.Provider) && left.Connection is null && right.Connection is null &&
                left.StorageMapping.SupportsManagedWrites && right.StorageMapping.SupportsManagedWrites
                ? left.Provider.Reader as IRelationshipStore : null;
        }
        if (store?.SupportsRelatedPages != true)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "The provider does not support bounded relationship pages."));
        }
        return store;
    }

    private sealed record RelatedReadPlan
    {
        public required Proto.QueryRelatedRecordsRequest Request { get; init; }
        public required GrpcLayerContext Origin { get; init; }
        public required GrpcLayerContext Related { get; init; }
        public required Proto.RelationshipDescriptor Relationship { get; init; }
        public required QueryContext Query { get; init; }
        public required MetadataV2Field[] Fields { get; init; }
    }

}
