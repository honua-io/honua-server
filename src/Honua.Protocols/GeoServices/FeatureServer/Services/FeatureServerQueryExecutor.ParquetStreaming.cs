// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Services;
using Honua.Protocols.GeoServices.FeatureServer.Models;

namespace Honua.Protocols.GeoServices.FeatureServer.Services;

internal sealed partial class FeatureServerQueryExecutor
{
    public async Task StreamParquetQueryAsync(
        ParquetStreamQueryRequest request,
        CancellationToken cancellationToken = default)
    {
        var streamingFeatureStore = await ResolveStreamingFeatureStoreV2Async(
            request.Service,
            request.Resource,
            request.Publication,
            request.StorageLayerId,
            FeatureProviderReadOperation.Query,
            cancellationToken).ConfigureAwait(false);

        var features = streamingFeatureStore.StreamFeaturesAsync(
            request.StorageLayerId, request.Query, cancellationToken);
        var (payload, contentType) = await GeoParquetFeatureWriter.FormatAsGeoParquetStreamAsync(
            features,
            request.Resource,
            CreateParquetEncodingOptions(request),
            cancellationToken).ConfigureAwait(false);

        await using (payload.ConfigureAwait(false))
        {
            try
            {
                request.Context.Response.ContentType = contentType;
                request.Context.Response.StatusCode = StatusCodes.Status200OK;
                if (payload.CanSeek)
                {
                    request.Context.Response.ContentLength = payload.Length;
                }

                await payload.CopyToAsync(
                    request.Context.Response.Body,
                    PbfFeatureSpool.StreamChunkBytes,
                    cancellationToken).ConfigureAwait(false);
            }
            catch when (request.Context.Response.HasStarted)
            {
                request.Context.Abort();
                throw;
            }
        }

        await request.Context.Response.CompleteAsync().ConfigureAwait(false);
    }

    private GeoParquetStreamEncodingOptions CreateParquetEncodingOptions(ParquetStreamQueryRequest request)
    {
        var queryParams = request.QueryParams;
        string[]? outFields = string.IsNullOrEmpty(queryParams.OutFields)
            ? null
            : [.. queryParams.OutFields.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(f => f.Trim())
                .Where(f => f.Length > 0)];
        if (outFields is { Length: 0 })
        {
            outFields = null;
        }

        var effectiveLimits = GeometryOutputProcessor.CreateEffectiveLimits(
            _streamingFormatter.GeometryLimits,
            queryParams.GeometryPrecision,
            queryParams.MaxAllowableOffset,
            forceSimplify: queryParams.MaxAllowableOffset is > 0);

        return new GeoParquetStreamEncodingOptions
        {
            ObjectIdFieldName = GeoServicesObjectIdFieldResolver.ResolveObjectIdFieldName(request.Resource),
            ReturnGeometry = queryParams.ReturnGeometry,
            OutputSrid = request.OutputSrid,
            ReturnZ = queryParams.ReturnZ,
            ReturnM = queryParams.ReturnM,
            GeometryLimits = effectiveLimits,
            OutFields = outFields,
            Logger = request.Context.RequestServices?.GetService<ILogger<FeatureServerQueryExecutor>>(),
            EncodingLimits = _streamingFormatter.GeoParquetLimits
        };
    }

    internal sealed record ParquetStreamQueryRequest
    {
        public required MetadataV2Service Service { get; init; }
        public required MetadataV2Object Resource { get; init; }
        public required MetadataV2Layer Publication { get; init; }
        public required int StorageLayerId { get; init; }
        public required FeatureQuery Query { get; init; }
        public required QueryParameters QueryParams { get; init; }
        public int? OutputSrid { get; init; }
        public required HttpContext Context { get; init; }
    }
}
