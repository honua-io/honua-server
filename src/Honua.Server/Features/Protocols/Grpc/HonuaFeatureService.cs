// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Grpc.Core;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Events;
using Honua.Infrastructure.Services;
using Honua.Infrastructure.Validation;
using Honua.ServiceDefaults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using AccessDecision = Honua.Core.Features.Security.Domain.AccessDecision;
using AccessPolicy = Honua.Core.Features.Security.Domain.AccessPolicy;
using Proto = Geospatial.V1;

namespace Honua.Server.Features.Protocols.Grpc;

/// <summary>
/// gRPC service implementation for feature queries.
/// Delegates to existing infrastructure (IResourceValidator, IFeatureReader, IStreamingFeatureStore).
/// </summary>
internal sealed class HonuaFeatureService : Proto.FeatureService.FeatureServiceBase
{
    private const string GrpcProtocolName = "Grpc";

    private static readonly PaginationValidationOptions _grpcPaginationValidation =
        new(MinOffset: 0, MinLimit: 1, OffsetParameterName: "resultOffset", LimitParameterName: "resultRecordCount");

    private readonly IResourceValidator _resourceValidator;
    private readonly IFeatureReader _featureReader;
    private readonly IFeatureWriter _featureWriter;
    private readonly IStreamingFeatureStore _streamingFeatureStore;
    private readonly ICommonQueryValidator _queryValidator;
    private readonly SpatialReferenceResolver _spatialReferenceResolver;
    private readonly FeatureMutationEventService _mutationEventService;
    private readonly ILogger<HonuaFeatureService> _logger;
    private readonly GeometryLimits _geometryLimits;
    private readonly int _streamBatchSize;

    public HonuaFeatureService(
        IResourceValidator resourceValidator,
        IFeatureReader featureReader,
        IFeatureWriter featureWriter,
        IStreamingFeatureStore streamingFeatureStore,
        ICommonQueryValidator queryValidator,
        SpatialReferenceResolver spatialReferenceResolver,
        IFeatureChangeEventPublisher featureChangeEventPublisher,
        IOptions<LimitsOptions> limitsOptions,
        IOptions<GrpcOptions> grpcOptions,
        ILogger<HonuaFeatureService> logger)
        : this(
            resourceValidator,
            featureReader,
            featureWriter,
            streamingFeatureStore,
            queryValidator,
            spatialReferenceResolver,
            new FeatureMutationEventService(featureChangeEventPublisher),
            limitsOptions,
            grpcOptions,
            logger)
    {
    }

    [ActivatorUtilitiesConstructor]
    public HonuaFeatureService(
        IResourceValidator resourceValidator,
        IFeatureReader featureReader,
        IFeatureWriter featureWriter,
        IStreamingFeatureStore streamingFeatureStore,
        ICommonQueryValidator queryValidator,
        SpatialReferenceResolver spatialReferenceResolver,
        FeatureMutationEventService mutationEventService,
        IOptions<LimitsOptions> limitsOptions,
        IOptions<GrpcOptions> grpcOptions,
        ILogger<HonuaFeatureService> logger)
    {
        _resourceValidator = resourceValidator;
        _featureReader = featureReader;
        _featureWriter = featureWriter;
        _streamingFeatureStore = streamingFeatureStore;
        _queryValidator = queryValidator;
        _spatialReferenceResolver = spatialReferenceResolver;
        _mutationEventService = mutationEventService;
        _geometryLimits = limitsOptions?.Value?.Geometry ?? new GeometryLimits();
        _streamBatchSize = Math.Max(grpcOptions?.Value?.StreamBatchSize ?? 1000, 1);
        _logger = logger;
    }

    public override async Task<Proto.QueryFeaturesResponse> QueryFeatures(
        Proto.QueryFeaturesRequest request,
        ServerCallContext context)
    {
        EnrichActivity("query", request);

        var layer = await ValidateGrpcLayerAsync(
            request.ServiceId, request.LayerId, context.CancellationToken).ConfigureAwait(false);
        EnsureReadAccess(context, layer.Service, layer.Resource);
        var queryContext = await CreateQueryContextAsync(request, layer, context.CancellationToken).ConfigureAwait(false);
        var query = queryContext.Query;
        var pkField = layer.ObjectIdFieldName;

        var response = new Proto.QueryFeaturesResponse
        {
            ObjectIdFieldName = pkField,
            GeometryType = GrpcConversionHelpers.ToProtoGeometryType(layer.GeometryType),
            SpatialReference = GrpcConversionHelpers.ToProtoSpatialReference(queryContext.ResponseSpatialReference)
        };

        // Count-only query
        if (request.ReturnCountOnly)
        {
            response.Count = await _featureReader.CountAsync(
                request.LayerId, query, context.CancellationToken).ConfigureAwait(false);
            return response;
        }

        // IDs-only query
        if (request.ReturnIdsOnly)
        {
            var objectIds = await _featureReader.QueryObjectIdsAsync(
                request.LayerId, query, context.CancellationToken).ConfigureAwait(false);
            response.ObjectIds.AddRange(objectIds);
            return response;
        }

        // Extent-only query
        if (request.ReturnExtentOnly)
        {
            var extent = await _featureReader.GetExtentAsync(
                request.LayerId, query, context.CancellationToken).ConfigureAwait(false);
            if (extent.HasValue)
            {
                response.Extent = GrpcConversionHelpers.ToProtoExtent(extent.Value, queryContext.ResponseSpatialReference);
            }
            return response;
        }

        // Standard feature query
        foreach (var field in layer.AttributeFields)
        {
            response.Fields.Add(GrpcConversionHelpers.ToProtoField(field));
        }

        var result = await _featureReader.QueryAsync(
            request.LayerId, query, context.CancellationToken).ConfigureAwait(false);

        foreach (var feature in result.Items)
        {
            response.Features.Add(GrpcConversionHelpers.ToProtoFeature(
                feature,
                queryContext.ReturnGeometry,
                queryContext.GeometryLimits));
        }

        response.ExceededTransferLimit = result.HasMoreResults;
        return response;
    }

    public override async Task QueryFeaturesStream(
        Proto.QueryFeaturesRequest request,
        IServerStreamWriter<Proto.FeaturePage> responseStream,
        ServerCallContext context)
    {
        EnrichActivity("query_stream", request);
        EnsureStreamingFlagsSupported(request);

        var layer = await ValidateGrpcLayerAsync(
            request.ServiceId, request.LayerId, context.CancellationToken).ConfigureAwait(false);
        EnsureReadAccess(context, layer.Service, layer.Resource);
        var queryContext = await CreateQueryContextAsync(request, layer, context.CancellationToken).ConfigureAwait(false);
        var query = queryContext.Query;
        var pkField = layer.ObjectIdFieldName;

        var isFirstPage = true;
        var batch = new List<Proto.Feature>(_streamBatchSize);

        await using var enumerator = _streamingFeatureStore
            .StreamFeaturesAsync(request.LayerId, query, context.CancellationToken)
            .GetAsyncEnumerator(context.CancellationToken);

        var hasCurrent = await enumerator.MoveNextAsync().ConfigureAwait(false);
        while (hasCurrent)
        {
            batch.Add(GrpcConversionHelpers.ToProtoFeature(
                enumerator.Current,
                queryContext.ReturnGeometry,
                queryContext.GeometryLimits));

            if (batch.Count < _streamBatchSize)
            {
                hasCurrent = await enumerator.MoveNextAsync().ConfigureAwait(false);
                continue;
            }

            hasCurrent = await enumerator.MoveNextAsync().ConfigureAwait(false);
            if (!hasCurrent)
            {
                var lastFullPage = CreatePage(
                    batch,
                    layer,
                    queryContext.ResponseSpatialReference,
                    pkField,
                    isFirstPage,
                    isLastPage: true);
                await responseStream.WriteAsync(lastFullPage, context.CancellationToken).ConfigureAwait(false);
                return;
            }

            var page = CreatePage(
                batch,
                layer,
                queryContext.ResponseSpatialReference,
                pkField,
                isFirstPage,
                isLastPage: false);
            await responseStream.WriteAsync(page, context.CancellationToken).ConfigureAwait(false);
            isFirstPage = false;
            batch.Clear();
        }

        var lastPage = CreatePage(
            batch,
            layer,
            queryContext.ResponseSpatialReference,
            pkField,
            isFirstPage,
            isLastPage: true);
        await responseStream.WriteAsync(lastPage, context.CancellationToken).ConfigureAwait(false);
    }

    public override async Task<Proto.ApplyEditsResponse> ApplyEdits(
        Proto.ApplyEditsRequest request,
        ServerCallContext context)
    {
        EnrichActivity("apply_edits", request.ServiceId, request.LayerId);

        var layer = await ValidateGrpcLayerAsync(
            request.ServiceId, request.LayerId, context.CancellationToken).ConfigureAwait(false);
        await EnsureWriteAccessAsync(context, layer.Service, layer.Resource).ConfigureAwait(false);

        FeatureEditBatch editBatch;
        try
        {
            editBatch = GrpcConversionHelpers.ToFeatureEditBatch(request);
        }
        catch (ArgumentException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }

        var grpcHttpContext = context.GetHttpContext()
            ?? throw new InvalidOperationException("HttpContext is required for gRPC outbox dispatch.");

        // Per-row geometry-change semantics mirror the inline publish path
        // (PublishFeatureChangeEventsAsync below): a row is considered to have changed
        // geometry when the request feature carries non-null WKB. Deletes default to
        // false. Without these queues an attribute-only update would over-report as a
        // geometry change because the post-mutation snapshot still carries the prior WKB.
        var perOperationGeometryChanged = BuildPerOperationGeometryChanged(editBatch);

        var outboxScopeData = await _mutationEventService.ResolveOutboxScopeAsync(
            grpcHttpContext,
            request.LayerId,
            HonuaTelemetry.Protocols.Grpc,
            serviceId: request.ServiceId,
            serviceProtocol: GrpcProtocolName,
            // Use ToSrid() (LatestWkid ?? Wkid) so the outbox enrichment fallback
            // emits the same SRID as the inline-publish path on layers like
            // Wkid=102100/LatestWkid=3857. Passing Wkid alone would publish the
            // deprecated WKID on outbox-active backends only.
            layerSrid: layer.SpatialReference.ToSrid(),
            perOperationGeometryChanged: perOperationGeometryChanged,
            cancellationToken: context.CancellationToken).ConfigureAwait(false);
        using var outboxScope = Honua.Core.Features.Infrastructure.Events.Outbox.FeatureMutationOutboxScope.BeginIfNotNull(outboxScopeData);

        var result = await _featureWriter.ApplyEditsAsync(
            request.LayerId,
            editBatch,
            context.CancellationToken).ConfigureAwait(false);

        await PublishFeatureChangeEventsAsync(
            request.ServiceId ?? "unknown",
            request.LayerId,
            layer,
            editBatch,
            result,
            context).ConfigureAwait(false);

        return GrpcConversionHelpers.ToProtoApplyEditsResponse(result);
    }

    private async Task PublishFeatureChangeEventsAsync(
        string serviceId,
        int layerId,
        GrpcLayerContext layer,
        FeatureEditBatch editBatch,
        FeatureEditResult editResult,
        ServerCallContext context)
    {
        var requestId = context.GetHttpContext().TraceIdentifier;
        // GrpcConversionHelpers builds WKB through a default GeometryFactory that does
        // not embed SRID metadata. Thread the validated layer SRID through so the
        // enrichment can still emit geometry/geometryCrs to streaming subscribers.
        var layerSrid = layer.SpatialReference.ToSrid();

        for (var i = 0; i < editResult.CreateResults.Length; i++)
        {
            var r = editResult.CreateResults[i];
            if (r.IsSuccess && r.ObjectId.HasValue)
            {
                var hasGeometry = i < editBatch.Creates.Length && editBatch.Creates[i].Geometry != null;
                await _mutationEventService.PublishAsync(
                    context.GetHttpContext(),
                    layerId,
                    r.ObjectId.Value,
                    "create",
                    HonuaTelemetry.Protocols.Grpc,
                    CancellationToken.None,
                    mutationFeature: i < editBatch.Creates.Length ? editBatch.Creates[i] : null,
                    serviceId: serviceId,
                    requestId: requestId,
                    geometryChanged: hasGeometry,
                    layerSrid: layerSrid).ConfigureAwait(false);
            }
        }

        for (var i = 0; i < editResult.UpdateResults.Length; i++)
        {
            var r = editResult.UpdateResults[i];
            if (r.IsSuccess && r.ObjectId.HasValue)
            {
                var hasGeometry = i < editBatch.Updates.Length && editBatch.Updates[i].Geometry != null;
                await _mutationEventService.PublishAsync(
                    context.GetHttpContext(),
                    layerId,
                    r.ObjectId.Value,
                    "update",
                    HonuaTelemetry.Protocols.Grpc,
                    CancellationToken.None,
                    mutationFeature: i < editBatch.Updates.Length ? editBatch.Updates[i] : null,
                    serviceId: serviceId,
                    requestId: requestId,
                    geometryChanged: hasGeometry,
                    layerSrid: layerSrid).ConfigureAwait(false);
            }
        }

        foreach (var r in editResult.DeleteResults)
        {
            if (r.IsSuccess && r.ObjectId.HasValue)
            {
                await _mutationEventService.PublishAsync(
                    context.GetHttpContext(),
                    layerId,
                    r.ObjectId.Value,
                    "delete",
                    HonuaTelemetry.Protocols.Grpc,
                    CancellationToken.None,
                    serviceId: serviceId,
                    requestId: requestId,
                    layerSrid: layerSrid).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Build per-operation-kind queues of geometry-change flags in the same order
    /// ApplyEditsAsync iterates rows for each kind, so the outbox payload's
    /// <c>GeometryChanged</c> field tracks the originating request's intent rather than
    /// inferring from the post-mutation snapshot. Deletes default to false (the inline
    /// publish path also defaults to false for delete events).
    /// </summary>
    private static Dictionary<string, IReadOnlyList<bool>>? BuildPerOperationGeometryChanged(FeatureEditBatch editBatch)
    {
        if (editBatch.Creates.IsDefaultOrEmpty && editBatch.Updates.IsDefaultOrEmpty && editBatch.Deletes.IsDefaultOrEmpty)
        {
            return null;
        }

        var result = new Dictionary<string, IReadOnlyList<bool>>(StringComparer.Ordinal);
        if (!editBatch.Creates.IsDefaultOrEmpty)
        {
            result["create"] = editBatch.Creates.Select(static f => f.Geometry is { Length: > 0 }).ToImmutableArray();
        }
        if (!editBatch.Updates.IsDefaultOrEmpty)
        {
            result["update"] = editBatch.Updates.Select(static f => f.Geometry is { Length: > 0 }).ToImmutableArray();
        }
        if (!editBatch.Deletes.IsDefaultOrEmpty)
        {
            result["delete"] = Enumerable.Repeat(false, editBatch.Deletes.Length).ToImmutableArray();
        }
        return result;
    }

    private async Task<GrpcLayerContext> ValidateGrpcLayerAsync(
        string serviceId,
        int layerId,
        CancellationToken cancellationToken)
    {
        var validation = await _resourceValidator.ValidateServiceLayerV2Async(
            serviceId, layerId, cancellationToken).ConfigureAwait(false);

        if (!validation.IsValid)
        {
            throw new RpcException(new Status(
                validation.ErrorCode == ResourceValidationError.NotFound
                    ? StatusCode.NotFound
                    : StatusCode.InvalidArgument,
                validation.ErrorMessage ?? "Resource validation failed"));
        }

        var triple = validation.Resource;
        var service = triple.Service;
        if (!IsGrpcEnabled(service))
        {
            throw new RpcException(new Status(StatusCode.NotFound, "Grpc is not enabled for this service."));
        }

        return CreateLayerContext(service, triple.Publication, triple.Resource);
    }

    private static GrpcLayerContext CreateLayerContext(
        MetadataV2Service service,
        MetadataV2Publication publication,
        MetadataV2Resource resource)
    {
        var spatialReference = ToSpatialReference(resource);
        var geometryType = resource.ReadGeometryType();
        var attributeFields = resource.SchemaFields
            .Where(static field => field.Type is not (MetadataV2FieldType.Geometry or MetadataV2FieldType.Geography))
            .ToArray();
        var objectIdFieldName = resource.FindPrimaryIdField()?.Name ?? "objectid";

        return new GrpcLayerContext(
            service,
            publication,
            resource,
            spatialReference,
            geometryType,
            attributeFields,
            objectIdFieldName);
    }

    private static SpatialReference ToSpatialReference(MetadataV2Resource resource)
    {
        var srid = resource.ReadSrid();
        return srid.HasValue ? SpatialReference.Create(srid.Value) : SpatialReference.WGS84;
    }

    private static Proto.FeaturePage CreatePage(
        List<Proto.Feature> features,
        GrpcLayerContext layer,
        SpatialReference responseSpatialReference,
        string pkField,
        bool isFirstPage,
        bool isLastPage)
    {
        var page = new Proto.FeaturePage
        {
            IsLastPage = isLastPage
        };

        if (isFirstPage)
        {
            page.ObjectIdFieldName = pkField;
            page.GeometryType = GrpcConversionHelpers.ToProtoGeometryType(layer.GeometryType);
            page.SpatialReference = GrpcConversionHelpers.ToProtoSpatialReference(responseSpatialReference);

            foreach (var field in layer.AttributeFields)
            {
                page.Fields.Add(GrpcConversionHelpers.ToProtoField(field));
            }
        }

        page.Features.AddRange(features);
        return page;
    }

    private async Task<QueryContext> CreateQueryContextAsync(
        Proto.QueryFeaturesRequest request,
        GrpcLayerContext layer,
        CancellationToken cancellationToken)
    {
        var whereValidation = _queryValidator.ValidateWhereClause(request.Where);
        if (!whereValidation.IsValid)
        {
            throw new RpcException(new Status(
                StatusCode.InvalidArgument,
                whereValidation.ErrorMessage ?? "Invalid where clause."));
        }

        var requestedOffset = request.ResultOffset != 0 ? request.ResultOffset : (int?)null;
        var requestedLimit = request.ResultRecordCount != 0 ? request.ResultRecordCount : (int?)null;
        if (!requestedLimit.HasValue && request.ObjectIds.Count > 0)
        {
            requestedLimit = request.ObjectIds.Count;
        }

        var paginationResult = _queryValidator.ValidateAndNormalizePagination(
            requestedOffset,
            requestedLimit,
            _grpcPaginationValidation);
        if (!paginationResult.IsValid)
        {
            throw new RpcException(new Status(
                StatusCode.InvalidArgument,
                paginationResult.ErrorMessage ?? "Invalid pagination parameters."));
        }

        var pagination = paginationResult.Value!;

        var query = GrpcConversionHelpers.ToFeatureQuery(request) with
        {
            SpatialReferenceSrid = layer.SpatialReference.ToSrid(),
            Offset = pagination.Offset,
            Limit = pagination.Limit
        };

        var outputSrid = query.OutputSrid;
        if (request.OutSr != null)
        {
            outputSrid = await ResolveSpatialReferenceSridAsync(request.OutSr, cancellationToken).ConfigureAwait(false);
            if (!outputSrid.HasValue)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid out_sr value."));
            }
        }

        if (query.SpatialFilter.HasValue)
        {
            var spatialFilterSrid = query.SpatialFilter.Value.Srid;
            if (request.SpatialFilter?.SpatialReference != null)
            {
                spatialFilterSrid = await ResolveSpatialReferenceSridAsync(
                    request.SpatialFilter.SpatialReference,
                    cancellationToken).ConfigureAwait(false);
                if (!spatialFilterSrid.HasValue)
                {
                    throw new RpcException(new Status(
                        StatusCode.InvalidArgument,
                        "Invalid spatial_filter.spatial_reference value."));
                }
            }

            query = query with
            {
                SpatialFilter = query.SpatialFilter.Value with
                {
                    Srid = spatialFilterSrid ?? layer.SpatialReference.ToSrid()
                }
            };
        }

        query = query with { OutputSrid = outputSrid };

        var responseSpatialReference = outputSrid.HasValue
            ? SpatialReference.Create(
                outputSrid.Value,
                request.OutSr?.LatestWkid > 0 ? request.OutSr.LatestWkid : null,
                vcsWkid: null,
                latestVcsWkid: null,
                wkt: string.IsNullOrWhiteSpace(request.OutSr?.Wkt) ? null : request.OutSr!.Wkt)
            : layer.SpatialReference;

        return new QueryContext(
            query,
            responseSpatialReference,
            GrpcConversionHelpers.CreateEffectiveGeometryLimits(_geometryLimits, request),
            request.ReturnGeometry);
    }

    private static void EnsureStreamingFlagsSupported(Proto.QueryFeaturesRequest request)
    {
        if (request.ReturnCountOnly || request.ReturnIdsOnly || request.ReturnExtentOnly)
        {
            throw new RpcException(new Status(
                StatusCode.InvalidArgument,
                "Streaming queries support feature payloads only. return_count_only, return_ids_only, and return_extent_only are not supported for QueryFeaturesStream."));
        }
    }

    private async Task<int?> ResolveSpatialReferenceSridAsync(Proto.SpatialReference? spatialReference, CancellationToken cancellationToken)
    {
        if (spatialReference == null)
        {
            return null;
        }

        if (spatialReference.LatestWkid > 0)
        {
            return await _spatialReferenceResolver.ResolveSridAsync(
                spatialReference.LatestWkid.ToString(),
                geometrySpatialReference: null,
                cancellationToken).ConfigureAwait(false);
        }

        if (spatialReference.Wkid > 0)
        {
            return await _spatialReferenceResolver.ResolveSridAsync(
                spatialReference.Wkid.ToString(),
                geometrySpatialReference: null,
                cancellationToken).ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(spatialReference.Wkt))
        {
            return await _spatialReferenceResolver.ResolveSridAsync(
                spatialReference.Wkt,
                geometrySpatialReference: null,
                cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    private static void EnrichActivity(string operation, Proto.QueryFeaturesRequest request)
    {
        EnrichActivity(operation, request.ServiceId, request.LayerId);
    }

    private static void EnrichActivity(string operation, string? serviceId, int layerId)
    {
        var activity = System.Diagnostics.Activity.Current;
        if (activity == null)
        {
            return;
        }

        activity.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.Grpc);
        activity.SetTag(HonuaTelemetry.Tags.Operation, operation);

        if (!string.IsNullOrWhiteSpace(serviceId))
        {
            activity.SetTag(HonuaTelemetry.Tags.ServiceId, serviceId);
        }

        activity.SetTag(HonuaTelemetry.Tags.LayerId, layerId.ToString());
    }

    private static async Task EnsureWriteAccessAsync(
        ServerCallContext context,
        MetadataV2Service service,
        MetadataV2Resource resource)
    {
        var httpContext = context.GetHttpContext();

        var decision = AccessPolicyHelpers.EvaluateAccess(
            httpContext,
            resource.AccessPolicy,
            service.AccessPolicy,
            AccessScope.Write);

        ThrowIfAccessDenied(decision);

        if (HasExplicitWritePolicy(resource.AccessPolicy) || HasExplicitWritePolicy(service.AccessPolicy))
        {
            return;
        }

        var rbacDecision = await ServiceDataEditorAuthorization.EvaluateServiceAccessAsync(
            httpContext,
            service.Metadata.Name,
            context.CancellationToken).ConfigureAwait(false);

        ThrowIfAccessDenied(rbacDecision);
    }

    private static void EnsureReadAccess(
        ServerCallContext context,
        MetadataV2Service service,
        MetadataV2Resource resource)
    {
        var httpContext = context.GetHttpContext();

        var decision = AccessPolicyHelpers.EvaluateAccess(
            httpContext,
            resource.AccessPolicy,
            service.AccessPolicy,
            AccessScope.Read);

        ThrowIfAccessDenied(decision);
    }

    private static void ThrowIfAccessDenied(AccessDecision decision)
    {
        if (decision.IsAllowed)
        {
            return;
        }

        throw new RpcException(new Status(
            decision.RequiresAuthentication ? StatusCode.Unauthenticated : StatusCode.PermissionDenied,
            decision.RequiresAuthentication
                ? AccessPolicyHelpers.AuthRequiredMessage
                : AccessPolicyHelpers.AccessForbiddenMessage));
    }

    private static bool HasExplicitWritePolicy(AccessPolicy? policy)
        => policy is not null &&
           (policy.AllowAnonymousWrite ||
            policy.AllowedWriteRoles is { Length: > 0 } ||
            policy.AllowedRoles is { Length: > 0 });

    private static bool IsGrpcEnabled(MetadataV2Service service)
        => service.Protocols.Any(enabled =>
            string.Equals(enabled, GrpcProtocolName, StringComparison.OrdinalIgnoreCase));

    private readonly record struct QueryContext(
        FeatureQuery Query,
        SpatialReference ResponseSpatialReference,
        GeometryLimits GeometryLimits,
        bool ReturnGeometry);

    private readonly record struct GrpcLayerContext(
        MetadataV2Service Service,
        MetadataV2Publication Publication,
        MetadataV2Resource Resource,
        SpatialReference SpatialReference,
        MetadataV2GeometryType GeometryType,
        IReadOnlyList<MetadataV2Field> AttributeFields,
        string ObjectIdFieldName);
}
