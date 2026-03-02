// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Grpc.Core;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Validation.Abstractions;
using Proto = Honua.Server.Features.Grpc.Proto;

namespace Honua.Server.Features.Grpc;

/// <summary>
/// gRPC service implementation for feature queries.
/// Delegates to existing infrastructure (IResourceValidator, IFeatureReader, IStreamingFeatureStore).
/// </summary>
internal sealed class HonuaFeatureService : Proto.FeatureService.FeatureServiceBase
{
    private const int StreamBatchSize = 1000;

    private readonly IResourceValidator _resourceValidator;
    private readonly IFeatureReader _featureReader;
    private readonly IStreamingFeatureStore _streamingFeatureStore;
    private readonly ILogger<HonuaFeatureService> _logger;

    public HonuaFeatureService(
        IResourceValidator resourceValidator,
        IFeatureReader featureReader,
        IStreamingFeatureStore streamingFeatureStore,
        ILogger<HonuaFeatureService> logger)
    {
        _resourceValidator = resourceValidator;
        _featureReader = featureReader;
        _streamingFeatureStore = streamingFeatureStore;
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
        var query = GrpcConversionHelpers.ToFeatureQuery(request);
        var pkField = layer.PrimaryKeyField?.Name ?? "objectid";

        var response = new Proto.QueryFeaturesResponse
        {
            ObjectIdFieldName = pkField,
            GeometryType = GrpcConversionHelpers.ToProtoGeometryType(layer.GeometryType),
            SpatialReference = GrpcConversionHelpers.ToProtoSpatialReference(layer.SpatialReference)
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
                response.Extent = GrpcConversionHelpers.ToProtoExtent(extent.Value, layer.SpatialReference);
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
            response.Features.Add(GrpcConversionHelpers.ToProtoFeature(feature));
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
        var query = GrpcConversionHelpers.ToFeatureQuery(request);
        var pkField = layer.PrimaryKeyField?.Name ?? "objectid";

        var isFirstPage = true;
        var batch = new List<Proto.Feature>(StreamBatchSize);

        await foreach (var feature in _streamingFeatureStore
            .StreamFeaturesAsync(request.LayerId, query, context.CancellationToken)
            .ConfigureAwait(false))
        {
            batch.Add(GrpcConversionHelpers.ToProtoFeature(feature));

            if (batch.Count >= StreamBatchSize)
            {
                var page = CreatePage(batch, layer, pkField, isFirstPage, isLastPage: false);
                await responseStream.WriteAsync(page, context.CancellationToken).ConfigureAwait(false);
                isFirstPage = false;
                batch.Clear();
            }
        }

        // Send the final page (may be empty if count was exact multiple of batch size)
        var lastPage = CreatePage(batch, layer, pkField, isFirstPage, isLastPage: true);
        await responseStream.WriteAsync(lastPage, context.CancellationToken).ConfigureAwait(false);
    }

    private static Proto.FeaturePage CreatePage(
        List<Proto.Feature> features,
        Honua.Core.Features.Catalog.Domain.LayerDefinition layer,
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
            page.SpatialReference = GrpcConversionHelpers.ToProtoSpatialReference(layer.SpatialReference);

            foreach (var field in layer.AttributeFields)
            {
                page.Fields.Add(GrpcConversionHelpers.ToProtoField(field));
            }
        }

        page.Features.AddRange(features);
        return page;
    }

    private static void EnsureGrpcEnabled(ServiceDefinition service)
    {
        if (ServiceProtocols.IsProtocolEnabled(service.Metadata, ServiceProtocols.Grpc))
        {
            return;
        }

        throw new RpcException(new Status(StatusCode.NotFound, "Grpc is not enabled for this service."));
    }
}
