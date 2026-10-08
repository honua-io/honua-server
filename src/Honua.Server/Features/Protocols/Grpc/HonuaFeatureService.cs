// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Grpc.Core;
using Honua.Core.Configuration;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Core.Queries.Filters;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Events;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Services;
using Honua.Infrastructure.Validation;
using Honua.ServiceDefaults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using AccessDecision = Honua.Core.Features.Security.Domain.AccessDecision;
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
    private readonly GrpcApplyEditsIdempotencyStore _idempotencyStore;
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
        ILogger<HonuaFeatureService> logger,
        GrpcApplyEditsIdempotencyStore idempotencyStore)
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
            logger,
            idempotencyStore)
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
        ILogger<HonuaFeatureService> logger,
        GrpcApplyEditsIdempotencyStore idempotencyStore)
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
        _idempotencyStore = idempotencyStore;
    }

    public override async Task<Proto.QueryFeaturesResponse> QueryFeatures(
        Proto.QueryFeaturesRequest request,
        ServerCallContext context)
    {
        EnrichActivity("query", request);

        var layer = await ValidateGrpcLayerAsync(
            request.ServiceId, request.LayerId, context.CancellationToken).ConfigureAwait(false);
        await EnsureReadAccessAsync(context, layer.Service, layer.Resource).ConfigureAwait(false);
        var queryContext = await CreateQueryContextAsync(
            request, layer, context.GetHttpContext().RequestServices, context.CancellationToken).ConfigureAwait(false);
        var query = queryContext.Query;
        var pkField = layer.ObjectIdFieldName;
        var readOperation = request.ReturnCountOnly
            ? FeatureProviderReadOperation.Count
            : request.ReturnExtentOnly
                ? FeatureProviderReadOperation.Extent
                : FeatureProviderReadOperation.Query;
        var reader = await ResolveReaderAsync(
                layer, readOperation, context.GetHttpContext().RequestServices, context.CancellationToken)
            .ConfigureAwait(false);

        var response = new Proto.QueryFeaturesResponse
        {
            ObjectIdFieldName = pkField,
            GeometryType = GrpcConversionHelpers.ToProtoGeometryType(layer.GeometryType),
            SpatialReference = GrpcConversionHelpers.ToProtoSpatialReference(queryContext.ResponseSpatialReference)
        };

        // Count-only query
        if (request.ReturnCountOnly)
        {
            response.Count = await reader.CountAsync(
                layer.StorageLayerId, query, context.CancellationToken).ConfigureAwait(false);
            return response;
        }

        // IDs-only query
        if (request.ReturnIdsOnly)
        {
            var objectIds = await reader.QueryObjectIdsAsync(
                layer.StorageLayerId, query, context.CancellationToken).ConfigureAwait(false);
            response.ObjectIds.AddRange(objectIds);
            return response;
        }

        // Extent-only query
        if (request.ReturnExtentOnly)
        {
            var extent = await reader.GetExtentAsync(
                layer.StorageLayerId, query, context.CancellationToken).ConfigureAwait(false);
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

        var result = await reader.QueryAsync(
            layer.StorageLayerId, query, context.CancellationToken).ConfigureAwait(false);

        foreach (var feature in result.Items)
        {
            response.Features.Add(GrpcConversionHelpers.ToProtoFeature(
                feature,
                queryContext.ReturnGeometry,
                queryContext.GeometryLimits,
                query.Distinct ? null : pkField));
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
        await EnsureReadAccessAsync(context, layer.Service, layer.Resource).ConfigureAwait(false);
        var queryContext = await CreateQueryContextAsync(
            request, layer, context.GetHttpContext().RequestServices, context.CancellationToken, streaming: true).ConfigureAwait(false);
        var query = queryContext.Query;
        var pkField = layer.ObjectIdFieldName;
        var requestServices = context.GetHttpContext().RequestServices;
        var resolvedReader = await ResolveReaderAsync(
                layer, FeatureProviderReadOperation.Query, requestServices, context.CancellationToken)
            .ConfigureAwait(false);
        var streamingStore = requestServices.GetService<FeatureProviderQueryRouter>() is null
            && requestServices.GetService<IMetadataV2GraphProvider>() is null
            ? _streamingFeatureStore
            : resolvedReader as IStreamingFeatureStore;
        if (streamingStore is null)
        {
            throw new RpcException(new Status(
                StatusCode.FailedPrecondition,
                "The layer's feature provider does not support streaming queries."));
        }

        var isFirstPage = true;
        var batch = new List<Proto.Feature>(_streamBatchSize);

        await using var enumerator = streamingStore
            .StreamFeaturesAsync(layer.StorageLayerId, query, context.CancellationToken)
            .GetAsyncEnumerator(context.CancellationToken);

        var hasCurrent = await enumerator.MoveNextAsync().ConfigureAwait(false);
        while (hasCurrent)
        {
            batch.Add(GrpcConversionHelpers.ToProtoFeature(
                enumerator.Current,
                queryContext.ReturnGeometry,
                queryContext.GeometryLimits,
                query.Distinct ? null : pkField));

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

        var metadataGraphProvider = context.GetHttpContext().RequestServices.GetService<IMetadataV2GraphProvider>();
        if (metadataGraphProvider is not null)
        {
            var snapshot = await metadataGraphProvider.GetCurrentAsync(context.CancellationToken).ConfigureAwait(false);
            var storageBinding = snapshot.ResolveStorageBinding(layer.Publication)
                ?? throw new RpcException(new Status(StatusCode.FailedPrecondition, "The layer has no resolvable storage binding."));
            if (!FeatureStorageMapping.FromMetadata(layer.Resource, storageBinding).SupportsManagedWrites)
            {
                throw new RpcException(new Status(
                    StatusCode.FailedPrecondition,
                    "The layer's storage binding does not support managed writes."));
            }
        }

        // gRPC ApplyEdits is an open-protocol edit surface and remains Community (#1591).
        // Validation, authz, eventing, and telemetry still run through the shared edit pipeline.

        FeatureEditBatch editBatch;
        try
        {
            editBatch = GrpcConversionHelpers.ToFeatureEditBatch(request);
        }
        catch (ArgumentException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }

        await EnsureWriteAccessAsync(context, layer.Service, layer.Resource, editBatch).ConfigureAwait(false);

        var idempotencyKey = request.IdempotencyKey?.Trim();
        GrpcApplyEditsIdempotencyStore.Lease? idempotencyLease = null;
        if (!string.IsNullOrEmpty(idempotencyKey))
        {
            // A stored result is replayed only within the same service, layer, effective
            // tenant and scheme/issuer-qualified actor (SEC-34).
            var scope = GrpcApplyEditsIdempotencyStore.CreateScope(
                context.GetHttpContext(), request.ServiceId, request.LayerId, idempotencyKey)
                ?? throw new RpcException(new Status(
                    StatusCode.FailedPrecondition,
                    "An idempotency key requires a caller identity the server can bind retries to."));
            idempotencyLease = await _idempotencyStore.EnterAsync(scope, context.CancellationToken).ConfigureAwait(false);
        }
        await using var heldIdempotencyLease = idempotencyLease;
        if (idempotencyLease?.Response is not null)
        {
            return idempotencyLease.Response;
        }

        // RLS / permanent-filter enforcement runs only on the read path; the edit SQL
        // filters by (layer_id, objectid) with no row-level predicate. So every update/delete
        // target MUST be pre-resolved through the RLS-enforced reader and fail closed when a
        // row is hidden from the caller — otherwise a caller could mutate or delete a row RLS
        // hides from them by supplying its objectid (#2071). Adds carry no objectid and are
        // not pre-read. Mirrors the GeoServices/OData/WFS-T not-found guards (#2066).
        await EnsureEditTargetsVisibleAsync(
            layer.StorageLayerId, editBatch, context.CancellationToken).ConfigureAwait(false);

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

        // A keyed write runs only while its shared reservation is provably held, and is
        // cancelled before that reservation could lapse, so another replica can never begin
        // the same keyed edit while this one may still commit (SEC-36).
        var writeCancellation = context.CancellationToken;
        if (idempotencyLease is not null)
        {
            writeCancellation = await idempotencyLease.TryBeginWriteAsync(context.CancellationToken).ConfigureAwait(false)
                ?? throw IdempotencyReservationLost();
        }

        FeatureEditResult result;
        try
        {
            result = await _featureWriter.ApplyEditsAsync(
                layer.StorageLayerId,
                editBatch,
                writeCancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            idempotencyLease?.OwnershipLost == true && !context.CancellationToken.IsCancellationRequested)
        {
            throw IdempotencyReservationLost();
        }

        var response = GrpcConversionHelpers.ToProtoApplyEditsResponse(result);

        // Record the committed result before any later step can fail, so a retry replays it
        // instead of executing the edit again.
        if (idempotencyLease is not null)
        {
            await idempotencyLease.CompleteAsync(response).ConfigureAwait(false);
        }

        await PublishFeatureChangeEventsAsync(
            request.ServiceId ?? "unknown",
            request.LayerId,
            layer,
            editBatch,
            result,
            context).ConfigureAwait(false);

        return response;
    }

    private static RpcException IdempotencyReservationLost() => new(new Status(
        StatusCode.Aborted,
        "The edit lost its idempotency reservation before it completed; retry with the same idempotency key."));

    // The router and graph provider are resolved per request, as the FeatureServer handlers
    // do, so the service stays within the architecture collaborator ceiling.
    private async Task<IFeatureReader> ResolveReaderAsync(
        GrpcLayerContext layer,
        FeatureProviderReadOperation operation,
        IServiceProvider requestServices,
        CancellationToken cancellationToken)
    {
        var providerQueryRouter = requestServices.GetService<FeatureProviderQueryRouter>();
        var metadataGraphProvider = requestServices.GetService<IMetadataV2GraphProvider>();
        if (providerQueryRouter is null || metadataGraphProvider is null)
        {
            return _featureReader;
        }

        var snapshot = await metadataGraphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        return await providerQueryRouter.ResolveReaderAsync(
            snapshot,
            layer.Service,
            layer.Resource,
            layer.Publication,
            layer.StorageLayerId,
            operation,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Pre-reads every update and delete target through the RLS-enforced
    /// <see cref="IFeatureReader.GetAsync(int, long, CancellationToken)"/> so the gRPC edit
    /// surface enforces the same row-level visibility as the read path. A row that is hidden
    /// from the caller by a row-level-security policy or a metadata permanent filter resolves
    /// to <see langword="null"/>; the request is rejected (fail closed) rather than mutating or
    /// deleting it, because the underlying edit SQL filters only on <c>(layer_id, objectid)</c>
    /// with no RLS predicate (#2071). Adds carry no objectid and are not pre-read.
    /// </summary>
    private async Task EnsureEditTargetsVisibleAsync(
        int storageLayerId,
        FeatureEditBatch editBatch,
        CancellationToken cancellationToken)
    {
        if (editBatch.Updates.IsDefaultOrEmpty && editBatch.Deletes.IsDefaultOrEmpty)
        {
            return;
        }

        // Collect distinct target objectids across updates and deletes; a single hidden/missing
        // target fails the whole batch closed.
        var targets = new HashSet<long>();
        if (!editBatch.Updates.IsDefaultOrEmpty)
        {
            foreach (var update in editBatch.Updates)
            {
                targets.Add(update.Id);
            }
        }

        if (!editBatch.Deletes.IsDefaultOrEmpty)
        {
            foreach (var objectId in editBatch.Deletes)
            {
                targets.Add(objectId);
            }
        }

        foreach (var objectId in targets)
        {
            var existing = await _featureReader
                .GetAsync(storageLayerId, objectId, cancellationToken)
                .ConfigureAwait(false);
            if (existing is null)
            {
                throw new RpcException(new Status(
                    StatusCode.NotFound,
                    $"Feature with objectid {objectId} was not found."));
            }
        }
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

        foreach (var r in editResult.DeleteResults.Where(r => r.IsSuccess && r.ObjectId.HasValue))
        {
            // The Where clause above already guarantees ObjectId.HasValue, but the compiler
            // can't propagate that narrowing across the LINQ lambda boundary back into this loop.
            await _mutationEventService.PublishAsync(
                context.GetHttpContext(),
                layerId,
                r.ObjectId!.Value,
                "delete",
                HonuaTelemetry.Protocols.Grpc,
                CancellationToken.None,
                serviceId: serviceId,
                requestId: requestId,
                layerSrid: layerSrid).ConfigureAwait(false);
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

        // The request addresses the layer by its service-local index; the feature
        // reader/writer boundary is keyed on the storage-layer handle. Validation
        // resolves that handle from the same snapshot as the authorized resource;
        // fetching current metadata again could race a publication rebind.
        var storageLayerId = triple.StorageLayerId;
        if (!storageLayerId.HasValue)
        {
            throw new RpcException(new Status(
                StatusCode.NotFound,
                $"Layer {layerId} is not bound to feature storage."));
        }

        return CreateLayerContext(service, triple.Publication, triple.Resource, storageLayerId.Value);
    }

    private static GrpcLayerContext CreateLayerContext(
        MetadataV2Service service,
        MetadataV2Publication publication,
        MetadataV2Resource resource,
        int storageLayerId)
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
            storageLayerId,
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
        IServiceProvider requestServices,
        CancellationToken cancellationToken,
        bool streaming = false)
    {
        EnsureAggregationNotRequested(request);

        var whereValidation = _queryValidator.ValidateWhereClause(request.Where);
        if (!whereValidation.IsValid)
        {
            throw new RpcException(new Status(
                StatusCode.InvalidArgument,
                whereValidation.ErrorMessage ?? "Invalid where clause."));
        }

        var whereFilter = await BindWhereClauseAsync(
            request.Where, layer.Resource, requestServices, cancellationToken).ConfigureAwait(false);

        // 0.2.0-alpha.1 retired the int32 result_offset / result_record_count fields in favour of
        // the int64 result_offset_long / result_record_count_long; narrow here so the existing
        // pagination validator keeps owning the range and ordering diagnostics.
        var requestedOffset = request.ResultOffsetLong != 0
            ? GrpcConversionHelpers.NarrowPagination(request.ResultOffsetLong, "result_offset_long")
            : (int?)null;
        var requestedLimit = request.ResultRecordCountLong != 0
            ? GrpcConversionHelpers.NarrowPagination(request.ResultRecordCountLong, "result_record_count_long")
            : (int?)null;
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
            SqlFilter = whereFilter,
            SpatialReferenceSrid = layer.SpatialReference.ToSrid(),
            Offset = pagination.Offset,
            Limit = streaming && !requestedLimit.HasValue ? null : pagination.Limit
        };

        if (query.OrderBy is { } orderBy)
        {
            query = query with
            {
                OrderBy = GrpcConversionHelpers.WithSchemaFieldTypes(orderBy, layer.Resource.SchemaFields)
            };
        }

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

    /// <summary>
    /// Binds the request's <c>where</c> clause to the shared filter pipeline, as FeatureServer
    /// does with the same GeoServices SQL parameter: the clause is parsed by the shared parser,
    /// refused when any field it references is masked from the caller, and translated against
    /// the layer schema. The raw text stays on <see cref="FeatureQuery.Where"/> for providers
    /// that re-parse it, and the provider's own field-security check still runs on both.
    /// </summary>
    private static async Task<SqlFragment?> BindWhereClauseAsync(
        string? where,
        MetadataV2Resource resource,
        IServiceProvider requestServices,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(where))
        {
            return null;
        }

        var filterService = requestServices.GetService<IFilterExpressionService>()
            ?? throw new InvalidOperationException("A where clause requires the shared filter expression service.");

        var parse = filterService.Parse(FilterLanguage.ArcGisSql, where);
        if (!parse.IsSuccess || parse.Expression is null)
        {
            throw new RpcException(new Status(
                StatusCode.InvalidArgument,
                parse.ErrorMessage ?? "Invalid where clause."));
        }

        var expression = parse.Expression;
        if (!FilterExpressionHelpers.IsBooleanFilterExpression(expression))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid where clause."));
        }

        if (requestServices.GetService<IFieldMaskSource>() is { } fieldMaskSource)
        {
            var maskedFields = await fieldMaskSource.ResolveAsync(resource, cancellationToken).ConfigureAwait(false);
            try
            {
                FeatureQuerySecurity.ValidateFilterExpression(expression, maskedFields, "where");
            }
            catch (ArgumentException ex)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
            }
        }

        // ArcGIS clients send "1=1" for "no filter"; providers already accept it as raw text.
        if (IsConstantTrue(expression))
        {
            return null;
        }

        var translation = filterService.Translate(expression, resource);
        if (!translation.IsSuccess)
        {
            throw new RpcException(new Status(
                StatusCode.InvalidArgument,
                translation.ErrorMessage ?? "Invalid where clause."));
        }

        return translation.SqlFilter;
    }

    private static bool IsConstantTrue(FilterExpression expression)
        => expression switch
        {
            Literal { Type: LiteralType.Boolean, Value: true } => true,
            BinaryExpression { Operator: BinaryOperator.Equal, Left: Literal left, Right: Literal right }
                => left.Type == right.Type && Equals(left.Value, right.Value),
            _ => false
        };

    private static void EnsureAggregationNotRequested(Proto.QueryFeaturesRequest request)
    {
        // #5465: geospatial.v1 defines no aggregate result shape. QueryFeaturesResponse and
        // FeaturePage carry the layer's attribute field definitions and feature rows only, so
        // answering out_statistics/group_by would either invent an unpublished response shape
        // or silently return ordinary features. Refuse until the contract defines one.
        if (request.OutStatistics.Count > 0 || request.GroupBy.Count > 0)
        {
            throw new RpcException(new Status(
                StatusCode.InvalidArgument,
                "out_statistics and group_by are not supported by geospatial.v1 feature queries. Use the GeoServices FeatureServer query outStatistics and groupByFieldsForStatistics parameters for aggregates."));
        }
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
                spatialReference.LatestWkid.ToString(System.Globalization.CultureInfo.InvariantCulture),
                geometrySpatialReference: null,
                cancellationToken).ConfigureAwait(false);
        }

        if (spatialReference.Wkid > 0)
        {
            return await _spatialReferenceResolver.ResolveSridAsync(
                spatialReference.Wkid.ToString(System.Globalization.CultureInfo.InvariantCulture),
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

        activity.SetTag(HonuaTelemetry.Tags.LayerId, layerId.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static async Task EnsureWriteAccessAsync(
        ServerCallContext context,
        MetadataV2Service service,
        MetadataV2Resource resource,
        FeatureEditBatch editBatch)
    {
        var httpContext = context.GetHttpContext();

        // Match the REST ApplyEdits seam: conversion first establishes the bounded
        // set of requested edit kinds, then every present kind must independently
        // pass the canonical resource data-editor gate before reads, outbox
        // resolution, or writes begin.
        if (!editBatch.Creates.IsEmpty)
        {
            await EnsureOperationAccessAsync(AuthorizationOperation.Insert).ConfigureAwait(false);
        }

        if (!editBatch.Updates.IsEmpty)
        {
            await EnsureOperationAccessAsync(AuthorizationOperation.Update).ConfigureAwait(false);
        }

        if (!editBatch.Deletes.IsEmpty)
        {
            await EnsureOperationAccessAsync(AuthorizationOperation.Delete).ConfigureAwait(false);
        }

        // Preserve the prior authorization ceiling for an empty no-op request.
        if (editBatch.Creates.IsEmpty && editBatch.Updates.IsEmpty && editBatch.Deletes.IsEmpty)
        {
            await EnsureOperationAccessAsync(AuthorizationOperation.Update).ConfigureAwait(false);
        }

        async Task EnsureOperationAccessAsync(AuthorizationOperation operation)
        {
            var decision = await ServiceDataEditorAuthorization.EvaluateResourceDataEditorAsync(
                httpContext,
                resource,
                service,
                operation,
                context.CancellationToken).ConfigureAwait(false);

            ThrowIfAccessDenied(decision);
        }
    }

    private static async Task EnsureReadAccessAsync(
        ServerCallContext context,
        MetadataV2Service service,
        MetadataV2Resource resource)
    {
        var httpContext = context.GetHttpContext();

        // Per-operation RBAC grants are consulted first (#1376) via the shared
        // seam; the coarse AccessPolicy read evaluation is the no-grant fallback.
        var decision = await AccessPolicyHelpers.EvaluateResourceAccessAsync(
            httpContext,
            resource,
            service,
            AuthorizationOperation.Query,
            context.CancellationToken).ConfigureAwait(false);

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
        int StorageLayerId,
        SpatialReference SpatialReference,
        MetadataV2GeometryType GeometryType,
        IReadOnlyList<MetadataV2Field> AttributeFields,
        string ObjectIdFieldName);
}
