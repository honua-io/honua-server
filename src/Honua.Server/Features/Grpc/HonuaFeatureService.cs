// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Grpc.Core;
using Honua.Core.Configuration;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Validation.Abstractions;
using Microsoft.Extensions.Options;
using Proto = Honua.Server.Features.Grpc.Proto;

namespace Honua.Server.Features.Grpc;

/// <summary>
/// gRPC service implementation for feature queries.
/// Delegates to existing infrastructure (IResourceValidator, IFeatureReader, IStreamingFeatureStore).
/// </summary>
internal sealed class HonuaFeatureService : Proto.FeatureService.FeatureServiceBase
{
    private readonly IResourceValidator _resourceValidator;
    private readonly IFeatureReader _featureReader;
    private readonly IStreamingFeatureStore _streamingFeatureStore;
    private readonly ILogger<HonuaFeatureService> _logger;
    private readonly GeometryLimits _geometryLimits;
    private readonly int _streamBatchSize;

    public HonuaFeatureService(
        IResourceValidator resourceValidator,
        IFeatureReader featureReader,
        IStreamingFeatureStore streamingFeatureStore,
        IOptions<LimitsOptions> limitsOptions,
        IOptions<GrpcOptions> grpcOptions,
        ILogger<HonuaFeatureService> logger)
    {
        _resourceValidator = resourceValidator;
        _featureReader = featureReader;
        _streamingFeatureStore = streamingFeatureStore;
        _geometryLimits = limitsOptions?.Value?.Geometry ?? new GeometryLimits();
        _streamBatchSize = grpcOptions?.Value?.StreamBatchSize ?? 1000;
        _logger = logger;
    }

    public override async Task<Proto.QueryFeaturesResponse> QueryFeatures(
        Proto.QueryFeaturesRequest request,
        ServerCallContext context)
    {
        var validation = await _resourceValidator.ValidateServiceLayerAsync(
            request.ServiceId, request.LayerId, context.CancellationToken).ConfigureAwait(false);

        if (!validation.IsValid)
        {
            throw new RpcException(new Status(
                validation.ErrorCode == ResourceValidationError.NotFound
                    ? StatusCode.NotFound
                    : StatusCode.InvalidArgument,
                validation.ErrorMessage ?? "Resource validation failed"));
        }

        var (service, layer) = validation.Resource!;
        EnsureGrpcEnabled(service);
        var queryContext = CreateQueryContext(request, layer);
        var query = queryContext.Query;
        var pkField = layer.PrimaryKeyField?.Name ?? "objectid";

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
            var idsResult = await _featureReader.QueryAsync(
                request.LayerId, query, context.CancellationToken).ConfigureAwait(false);
            response.ObjectIds.AddRange(idsResult.Items.Select(f => f.Id));
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
        var validation = await _resourceValidator.ValidateServiceLayerAsync(
            request.ServiceId, request.LayerId, context.CancellationToken).ConfigureAwait(false);

        if (!validation.IsValid)
        {
            throw new RpcException(new Status(
                validation.ErrorCode == ResourceValidationError.NotFound
                    ? StatusCode.NotFound
                    : StatusCode.InvalidArgument,
                validation.ErrorMessage ?? "Resource validation failed"));
        }

        var (service, layer) = validation.Resource!;
        EnsureGrpcEnabled(service);
        var queryContext = CreateQueryContext(request, layer);
        var query = queryContext.Query;
        var pkField = layer.PrimaryKeyField?.Name ?? "objectid";

        var isFirstPage = true;
        var batch = new List<Proto.Feature>(_streamBatchSize);

        await using var enumerator = _streamingFeatureStore
            .StreamFeaturesAsync(request.LayerId, query, context.CancellationToken)
            .GetAsyncEnumerator(context.CancellationToken);

        while (await enumerator.MoveNextAsync().ConfigureAwait(false))
        {
            batch.Add(GrpcConversionHelpers.ToProtoFeature(
                enumerator.Current,
                queryContext.ReturnGeometry,
                queryContext.GeometryLimits));

            if (batch.Count < _streamBatchSize)
            {
                continue;
            }

            var hasMore = await enumerator.MoveNextAsync().ConfigureAwait(false);
            if (!hasMore)
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

            batch.Add(GrpcConversionHelpers.ToProtoFeature(
                enumerator.Current,
                queryContext.ReturnGeometry,
                queryContext.GeometryLimits));
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

    private static Proto.FeaturePage CreatePage(
        List<Proto.Feature> features,
        LayerDefinition layer,
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

    private QueryContext CreateQueryContext(Proto.QueryFeaturesRequest request, LayerDefinition layer)
    {
        var query = GrpcConversionHelpers.ToFeatureQuery(request) with
        {
            SpatialReferenceSrid = layer.SpatialReference.ToSrid()
        };

        var outputSrid = query.OutputSrid;
        if (request.OutSr != null && !outputSrid.HasValue)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid out_sr value."));
        }

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

    private static void EnsureGrpcEnabled(ServiceDefinition service)
    {
        if (ServiceProtocols.IsProtocolEnabled(service.Metadata, ServiceProtocols.Grpc))
        {
            return;
        }

        throw new RpcException(new Status(StatusCode.NotFound, "Grpc is not enabled for this service."));
    }

    private readonly record struct QueryContext(
        FeatureQuery Query,
        SpatialReference ResponseSpatialReference,
        GeometryLimits GeometryLimits,
        bool ReturnGeometry);
}
