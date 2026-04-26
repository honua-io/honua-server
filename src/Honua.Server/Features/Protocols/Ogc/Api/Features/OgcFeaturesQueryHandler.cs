// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Buffers;
using System.Text.Json;
using Honua.Core.Features.Caching;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Caching;
using Honua.Core.Features.Query;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Caching;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Validation;
using Honua.Server.Features.Protocols.Ogc.Common;
using Honua.Server.Features.Protocols.Ogc.Api.Features.Models;
using Honua.Server.Features.Protocols.Ogc.Api.Features.Services;
using Honua.ServiceDefaults;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Features.Protocols.Ogc.Api.Features;

/// <summary>
/// Handler for OGC Features query operations with filtering, pagination, and spatial/temporal queries.
/// </summary>
internal sealed partial class OgcFeaturesQueryHandler(
    OgcFeaturesQueryDependencies dependencies,
    ILogger<OgcFeaturesQueryHandler> logger)
{
    private readonly IFeatureReader _featureReader = dependencies?.FeatureReader
        ?? throw new ArgumentNullException(nameof(dependencies));
    private readonly IStreamingFeatureStore _streamingFeatureStore = dependencies.StreamingFeatureStore;
    private readonly ICrsRegistry _crsRegistry = dependencies.CrsRegistry;
    private readonly OgcFeaturesGeometryServices _geometryServices = dependencies.GeometryServices;
    private readonly IQueryParameterAdapter<OgcFeaturesQueryParameters> _queryParameterAdapter = dependencies.QueryParameterAdapter;
    private readonly IQueryProcessor _queryProcessor = dependencies.QueryProcessor;
    private readonly IResponseCache _responseCache = dependencies.ResponseCache;
    private readonly IETagService _etagService = dependencies.ETagService;
    private readonly CacheOptions _cacheOptions = dependencies.CacheOptions;
    private readonly OgcFeaturesOptions _ogcFeaturesOptions = dependencies.OgcFeaturesOptions;
    private readonly ILogger<OgcFeaturesQueryHandler> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private const int StreamingThreshold = 200;
    private const int StreamingFlushInterval = 128;
    /// <summary>
    /// Handles GetItems request with comprehensive filtering and pagination.
    /// </summary>
    public async Task<IResult> HandleGetItemsAsync(
        string collectionId,
        HttpContext context,
        string? f,
        int? limit,
        int? offset,
        string? bbox,
        string? datetime,
        string? filter,
        string? ids,
        string? properties,
        string? sortby,
        string? crs,
        CancellationToken cancellationToken)
    {
        Activity? featureActivity = null;
        var request = context.Request;

        try
        {
            var routeValidator = context.RequestServices.GetRequiredService<IRouteParameterValidator>();
            var collectionValidation = routeValidator.ValidateCollectionId(context);
            if (!collectionValidation.IsValid || string.IsNullOrWhiteSpace(collectionValidation.Value))
            {
                return StandardErrorHelpers.CreateBadRequest(
                    context,
                    collectionValidation.ErrorMessage ?? "Collection ID is required.");
            }

            collectionId = collectionValidation.Value!;

            var layerValidation = await LayerValidationHelpers.ValidateCollectionWithAccessAsync(
                context,
                collectionId,
                cancellationToken: cancellationToken,
                requiredProtocol: ServiceProtocols.OgcFeatures);
            if (!layerValidation.IsValid)
            {
                return layerValidation.ErrorResult!;
            }

            var layer = layerValidation.Layer!;
            var layerId = layer.Id;
            var activity = Activity.Current;
            activity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.OgcFeatures);
            activity?.SetTag(HonuaTelemetry.Tags.CollectionId, collectionId);
            activity?.SetTag(HonuaTelemetry.Tags.LayerId, layerId.ToString(CultureInfo.InvariantCulture));

            featureActivity = HonuaTelemetry.StartFeatureActivity(
                "query",
                HonuaTelemetry.Protocols.OgcFeatures,
                layerId.ToString(CultureInfo.InvariantCulture),
                context.TraceIdentifier);
            featureActivity?.SetTag(HonuaTelemetry.Tags.CollectionId, collectionId);

            OgcFeaturesLog.ItemsRequested(_logger, collectionId, limit, offset);

            var validationError = OgcFeaturesUtilities.ValidateItemsQueryParameters(request, layer);
            if (validationError is not null)
            {
                return StandardErrorHelpers.CreateBadRequest(context, validationError.Value ?? "Invalid query parameters.");
            }

            if (!OgcCommonUtilities.TryGetOutputFormat(f, context, isFeatureContent: true, out var outputFormat, out var formatError))
            {
                return OgcCommonUtilities.CreateFormatError(context, formatError);
            }

            var queryAdapterResult = await _queryParameterAdapter.ConvertAsync(
                new OgcFeaturesQueryParameters
                {
                    Request = request,
                    Filter = filter,
                    Limit = limit,
                    Offset = offset,
                    Bbox = bbox,
                    Datetime = datetime,
                    Ids = ids,
                    Properties = properties,
                    Sortby = sortby,
                    Crs = crs,
                    F = f
                },
                layer,
                cancellationToken);
            if (!queryAdapterResult.IsSuccess || queryAdapterResult.Query == null)
            {
                return StandardErrorHelpers.CreateBadRequest(
                    context,
                    queryAdapterResult.ErrorMessage ?? "Invalid query parameters.");
            }

            var unifiedQuery = _queryProcessor.OptimizeQuery(queryAdapterResult.Query.Value, layer);
            var unifiedQueryValidation = _queryProcessor.ValidateQuery(unifiedQuery, layer);
            if (!unifiedQueryValidation.IsValid)
            {
                return StandardErrorHelpers.CreateBadRequest(
                    context,
                    unifiedQueryValidation.ErrorMessage ?? "Invalid query parameters.");
            }

            var query = _queryProcessor.ToFeatureQuery(unifiedQuery, layer);

            var effectiveLimit = query.Limit ?? 0;
            var effectiveOffset = query.Offset ?? 0;
            var projectedProperties = query.OutFields?.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
            var outputAxisOrder = unifiedQuery.OutputCrs?.AxisOrder ?? AxisOrder.EastNorth;
            var outputCrsUri = unifiedQuery.OutputCrs?.Uri ?? "http://www.opengis.net/def/crs/OGC/1.3/CRS84";

            var allowStreaming = string.Equals(outputFormat, MediaTypes.GeoJson, StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(outputFormat, MediaTypes.Gml, StringComparison.OrdinalIgnoreCase);
            var omitExactNumberMatched = _ogcFeaturesOptions.NumberMatchedPolicy == OgcFeaturesNumberMatchedPolicy.OmitWhenExpensive;
            var useStreaming = allowStreaming &&
                               !omitExactNumberMatched &&
                               projectedProperties == null &&
                               effectiveLimit > StreamingThreshold &&
                               !string.Equals(outputFormat, MediaTypes.Html, StringComparison.OrdinalIgnoreCase);

            var cacheableFormat = string.Equals(outputFormat, MediaTypes.Json, StringComparison.OrdinalIgnoreCase) ||
                                  string.Equals(outputFormat, MediaTypes.GeoJson, StringComparison.OrdinalIgnoreCase);
            var canCache = !useStreaming &&
                           cacheableFormat &&
                           !ResponseCacheUtilities.ShouldBypassAdHocSpatialResponseCache(query, filter) &&
                           ResponseCacheUtilities.ShouldCache(context, _cacheOptions);
            var cacheTtl = canCache ? _cacheOptions.GetQueryTtlWithJitter() : TimeSpan.Zero;
            if (canCache && cacheTtl <= TimeSpan.Zero)
            {
                canCache = false;
            }

            var cacheKey = canCache
                ? ResponseCacheUtilities.BuildOgcCollectionKey(collectionId, request)
                : null;

            if (canCache && cacheKey != null)
            {
                var cached = await _responseCache.GetAsync<CachedResponse>(cacheKey, cancellationToken);
                if (cached != null)
                {
                    context.Response.Headers["Content-Crs"] = FormatContentCrs(outputCrsUri);
                    return ResponseCacheUtilities.CreateResultFromCachedResponse(context, cached, _etagService);
                }
            }

            OgcFeaturesLog.ItemsQueryStarted(_logger, collectionId, effectiveLimit, effectiveOffset);
            var stopwatch = Stopwatch.StartNew();

            if (useStreaming)
            {
                var totalCount = await _featureReader.CountAsync(layerId, query, cancellationToken);

                // Avoid streaming for small result sets even when the requested limit is large.
                if (totalCount > StreamingThreshold)
                {
                    var hasMoreResults = totalCount > (effectiveOffset + effectiveLimit);
                    stopwatch.Stop();
                    var estimatedReturned = (int)Math.Min(effectiveLimit, Math.Max(0, totalCount - effectiveOffset));
                    OgcFeaturesLog.ItemsQueryCompleted(_logger, collectionId, estimatedReturned, totalCount, stopwatch.Elapsed.TotalMilliseconds);
                    HonuaTelemetry.SetSuccess(featureActivity, estimatedReturned);

                    var streamBaseUrl = BaseUrlResolver.GetBaseUrl(context);
                    var streamCollectionSegment = Uri.EscapeDataString(collectionId);
                    var streamBasePath = $"{streamBaseUrl}/ogc/features/collections/{streamCollectionSegment}/items";
                    var streamLinks = BuildItemsLinks(
                        request,
                        collectionId,
                        streamBasePath,
                        outputFormat,
                        effectiveLimit,
                        effectiveOffset,
                        hasMoreResults);

                    if (string.Equals(outputFormat, MediaTypes.Gml, StringComparison.OrdinalIgnoreCase))
                    {
                        return new StreamingGmlItemsResult(
                            _streamingFeatureStore,
                            layer,
                            query,
                            totalCount,
                            estimatedReturned,
                            outputCrsUri,
                            streamLinks,
                            cancellationToken);
                    }

                    return new StreamingItemsResult(
                        _streamingFeatureStore,
                        layer,
                        query,
                        collectionId,
                        outputAxisOrder,
                        _geometryServices,
                        projectedProperties,
                        outputFormat,
                        streamLinks,
                        _ogcFeaturesOptions.IncludeFeatureLinks,
                        totalCount,
                        outputCrsUri,
                        cancellationToken);
                }
            }

            var useNativeGeoJson = string.Equals(outputFormat, MediaTypes.GeoJson, StringComparison.OrdinalIgnoreCase) ||
                                   string.Equals(outputFormat, MediaTypes.Json, StringComparison.OrdinalIgnoreCase);
            var includeFeatureLinks = _ogcFeaturesOptions.IncludeFeatureLinks;
            QueryResult<GmlFeature>? gmlResult = null;
            QueryResult<Feature>? featureResult = null;
            QueryResult<EncodedGeoJsonFeature>? encodedResult = null;
            PagedQueryResult<Feature>? pagedFeatureResult = null;
            PagedQueryResult<EncodedGeoJsonFeature>? pagedEncodedResult = null;
            PagedQueryResult<RawGeoJsonFeature>? pagedRawResult = null;
            PagedQueryResult<RawGeoServicesFeature>? pagedRawPointResult = null;
            GeoJsonFeature[] features = [];
            var canUseRawGeoJsonFastPath = omitExactNumberMatched &&
                                           useNativeGeoJson &&
                                           outputAxisOrder == AxisOrder.EastNorth &&
                                           projectedProperties == null &&
                                           !includeFeatureLinks &&
                                           _geometryServices.CanUsePreformattedGeoJson;
            var canUseRawPointGeoJsonFastPath = canUseRawGeoJsonFastPath &&
                                                layer.GeometryType == GeometryType.Point &&
                                                query.SpatialFilter?.IsSimpleEnvelope == true;

            if (canUseRawPointGeoJsonFastPath &&
                _featureReader is IPagedRawGeoServicesFeatureStore rawPointFeatureStore)
            {
                pagedRawPointResult = await rawPointFeatureStore.QueryGeoServicesRawPointPageAsync(layerId, query, cancellationToken);
            }
            else if (canUseRawGeoJsonFastPath &&
                _featureReader is IPagedRawGeoJsonFeatureStore rawGeoJsonFeatureStore)
            {
                pagedRawResult = await rawGeoJsonFeatureStore.QueryGeoJsonRawPageAsync(layerId, query, cancellationToken);
            }
            else if (string.Equals(outputFormat, MediaTypes.Gml, StringComparison.OrdinalIgnoreCase) &&
                _featureReader is IGmlFeatureStore gmlFeatureStore)
            {
                gmlResult = await gmlFeatureStore.QueryGmlAsync(layerId, query, cancellationToken);
            }
            else if (omitExactNumberMatched && useNativeGeoJson && _featureReader is IPagedGeoJsonFeatureStore pagedGeoJsonFeatureStore)
            {
                pagedEncodedResult = await pagedGeoJsonFeatureStore.QueryGeoJsonPageAsync(layerId, query, cancellationToken);
                features = pagedEncodedResult.Value.Items
                    .Select(feature =>
                    {
                        ImmutableArray<Link>? links = includeFeatureLinks
                            ? OgcFeaturesUtilities.BuildFeatureLinks(
                                request,
                                collectionId,
                                OgcFeatureIdentifierResolver.FormatPublicId(feature, layer),
                                outputFormat)
                            : null;
                        return ToOgcFeature(
                            feature,
                            layer,
                            outputAxisOrder,
                            _geometryServices,
                            projectedProperties,
                            links);
                    })
                    .ToArray();
            }
            else if (omitExactNumberMatched && _featureReader is IPagedFeatureReader pagedFeatureReader)
            {
                pagedFeatureResult = await pagedFeatureReader.QueryPageAsync(layerId, query, cancellationToken);
                features = pagedFeatureResult.Value.Items
                    .Select(feature =>
                    {
                        ImmutableArray<Link>? links = includeFeatureLinks
                            ? OgcFeaturesUtilities.BuildFeatureLinks(
                                request,
                                collectionId,
                                OgcFeatureIdentifierResolver.FormatPublicId(feature, layer),
                                outputFormat)
                            : null;
                        return ToOgcFeature(
                            feature,
                            layer,
                            outputAxisOrder,
                            _geometryServices,
                            projectedProperties,
                            links);
                    })
                    .ToArray();
            }
            else if (useNativeGeoJson && _featureReader is IGeoJsonFeatureStore geoJsonFeatureStore)
            {
                encodedResult = await geoJsonFeatureStore.QueryGeoJsonAsync(layerId, query, cancellationToken);
                features = encodedResult.Value.Items
                    .Select(feature =>
                    {
                        ImmutableArray<Link>? links = includeFeatureLinks
                            ? OgcFeaturesUtilities.BuildFeatureLinks(
                                request,
                                collectionId,
                                OgcFeatureIdentifierResolver.FormatPublicId(feature, layer),
                                outputFormat)
                            : null;
                        return ToOgcFeature(
                            feature,
                            layer,
                            outputAxisOrder,
                            _geometryServices,
                            projectedProperties,
                            links);
                    })
                    .ToArray();
            }
            else
            {
                featureResult = await _featureReader.QueryAsync(layerId, query, cancellationToken);
                features = featureResult.Value.Items
                    .Select(feature =>
                    {
                        ImmutableArray<Link>? links = includeFeatureLinks
                            ? OgcFeaturesUtilities.BuildFeatureLinks(
                                request,
                                collectionId,
                                OgcFeatureIdentifierResolver.FormatPublicId(feature, layer),
                                outputFormat)
                            : null;
                        return ToOgcFeature(
                            feature,
                            layer,
                            outputAxisOrder,
                            _geometryServices,
                            projectedProperties,
                            links);
                    })
                    .ToArray();
            }
            stopwatch.Stop();
            var queryTotalCount = pagedRawPointResult?.TotalCount
                                  ?? pagedRawResult?.TotalCount
                                  ?? pagedEncodedResult?.TotalCount
                                  ?? pagedFeatureResult?.TotalCount
                                  ?? encodedResult?.TotalCount
                                  ?? featureResult?.TotalCount;
            var queryHasMoreResults = pagedRawPointResult?.HasMoreResults
                                      ?? pagedRawResult?.HasMoreResults
                                      ?? pagedEncodedResult?.HasMoreResults
                                      ?? pagedFeatureResult?.HasMoreResults
                                      ?? encodedResult?.HasMoreResults
                                      ?? featureResult?.HasMoreResults
                                      ?? false;
            var numberReturned = pagedRawPointResult?.Items.Length ?? pagedRawResult?.Items.Length ?? gmlResult?.Items.Length ?? features.Length;
            if (gmlResult.HasValue)
            {
                queryTotalCount = gmlResult.Value.TotalCount;
                queryHasMoreResults = gmlResult.Value.HasMoreResults;
            }

            OgcFeaturesLog.ItemsQueryCompleted(_logger, collectionId, numberReturned, queryTotalCount, stopwatch.Elapsed.TotalMilliseconds);
            HonuaTelemetry.SetSuccess(featureActivity, numberReturned);

            var baseUrl = BaseUrlResolver.GetBaseUrl(context);
            var collectionSegment = Uri.EscapeDataString(collectionId);
            var basePath = $"{baseUrl}/ogc/features/collections/{collectionSegment}/items";

            var links = BuildItemsLinks(
                request,
                collectionId,
                basePath,
                outputFormat,
                effectiveLimit,
                effectiveOffset,
                queryHasMoreResults);

            context.Response.Headers["Content-Crs"] = FormatContentCrs(outputCrsUri);

            if (pagedRawPointResult.HasValue)
            {
                var contentType = string.Equals(outputFormat, MediaTypes.GeoJson, StringComparison.OrdinalIgnoreCase)
                    ? MediaTypes.GeoJson
                    : MediaTypes.Json;
                var payload = CreateRawPointFeatureCollectionPayload(pagedRawPointResult.Value.Items, layer, links, queryTotalCount);
                if (canCache && cacheKey != null)
                {
                    var cachedResponse = ResponseCacheUtilities.CreateCachedResponse(payload.ToArray(), contentType, _etagService);
                    await _responseCache.SetAsync(cacheKey, cachedResponse, cacheTtl, cancellationToken);
                    return ResponseCacheUtilities.CreateResultFromCachedResponse(context, cachedResponse, _etagService);
                }

                return Results.Bytes(payload, contentType);
            }

            if (pagedRawResult.HasValue)
            {
                var contentType = string.Equals(outputFormat, MediaTypes.GeoJson, StringComparison.OrdinalIgnoreCase)
                    ? MediaTypes.GeoJson
                    : MediaTypes.Json;
                var payload = CreateRawFeatureCollectionPayload(pagedRawResult.Value.Items, layer, links, queryTotalCount);
                if (canCache && cacheKey != null)
                {
                    var cachedResponse = ResponseCacheUtilities.CreateCachedResponse(payload.ToArray(), contentType, _etagService);
                    await _responseCache.SetAsync(cacheKey, cachedResponse, cacheTtl, cancellationToken);
                    return ResponseCacheUtilities.CreateResultFromCachedResponse(context, cachedResponse, _etagService);
                }

                return Results.Bytes(payload, contentType);
            }

            var response = OgcGeoJsonFeatureBuilder.CreateCollection(features, queryTotalCount, links);

            if (string.Equals(outputFormat, MediaTypes.Gml, StringComparison.OrdinalIgnoreCase))
            {
                var gml = gmlResult.HasValue
                    ? OgcResponseFormatter.BuildGmlFeatureCollection(gmlResult.Value.Items, queryTotalCount, gmlResult.Value.Items.Length, DateTimeOffset.UtcNow)
                    : OgcResponseFormatter.BuildGmlFeatureCollection(features, queryTotalCount, features.Length, DateTimeOffset.UtcNow);
                return Results.Text(gml, MediaTypes.Gml);
            }

            if (string.Equals(outputFormat, MediaTypes.Csv, StringComparison.OrdinalIgnoreCase))
            {
                var fieldNames = ResolveCsvFieldNames(layer, projectedProperties);
                var csv = OgcResponseFormatter.BuildCsvResponse(features, fieldNames);
                return Results.Text(csv, MediaTypes.Csv);
            }

            if (canCache && cacheKey != null)
            {
                var contentType = string.Equals(outputFormat, MediaTypes.GeoJson, StringComparison.OrdinalIgnoreCase)
                    ? MediaTypes.GeoJson
                    : MediaTypes.Json;
                var payload = JsonSerializer.SerializeToUtf8Bytes(response, OgcJsonContext.Default.FeatureCollection);
                var cachedResponse = ResponseCacheUtilities.CreateCachedResponse(payload, contentType, _etagService);
                await _responseCache.SetAsync(cacheKey, cachedResponse, cacheTtl, cancellationToken);
                return ResponseCacheUtilities.CreateResultFromCachedResponse(context, cachedResponse, _etagService);
            }

            return OgcResponseFormatter.FormatFeatureResponse(response, OgcJsonContext.Default.FeatureCollection, outputFormat, "Features");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            OgcFeaturesLog.ItemsQueryFailed(_logger, collectionId, ex);
            HonuaTelemetry.RecordException(featureActivity, ex);
            return StandardErrorHelpers.CreateInternalServerError(context, "An error occurred while retrieving features.");
        }
        finally
        {
            featureActivity?.Dispose();
        }
    }

    /// <summary>
    /// Handles GetItem request for a single feature by ID.
    /// </summary>
    public async Task<IResult> HandleGetItemAsync(
        string collectionId,
        string featureId,
        HttpContext context,
        string? f,
        string? crs,
        CancellationToken cancellationToken)
    {
        Activity? featureActivity = null;
        var request = context.Request;

        try
        {
            var routeValidator = context.RequestServices.GetRequiredService<IRouteParameterValidator>();
            var collectionValidation = routeValidator.ValidateCollectionId(context);
            if (!collectionValidation.IsValid || string.IsNullOrWhiteSpace(collectionValidation.Value))
            {
                return StandardErrorHelpers.CreateBadRequest(
                    context,
                    collectionValidation.ErrorMessage ?? "Collection ID is required.");
            }

            var featureValidation = routeValidator.ValidateFeatureId(context);
            if (!featureValidation.IsValid || string.IsNullOrWhiteSpace(featureValidation.Value))
            {
                return StandardErrorHelpers.CreateBadRequest(
                    context,
                    featureValidation.ErrorMessage ?? "Feature ID is required.");
            }

            collectionId = collectionValidation.Value!;
            featureId = featureValidation.Value!;

            var layerValidation = await LayerValidationHelpers.ValidateCollectionWithAccessAsync(
                context,
                collectionId,
                cancellationToken: cancellationToken,
                requiredProtocol: ServiceProtocols.OgcFeatures);
            if (!layerValidation.IsValid)
            {
                return layerValidation.ErrorResult!;
            }

            var layer = layerValidation.Layer!;
            var layerId = layer.Id;
            var activity = Activity.Current;
            activity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.OgcFeatures);
            activity?.SetTag(HonuaTelemetry.Tags.CollectionId, collectionId);
            activity?.SetTag(HonuaTelemetry.Tags.LayerId, layerId.ToString(CultureInfo.InvariantCulture));

            featureActivity = HonuaTelemetry.StartFeatureActivity(
                "feature",
                HonuaTelemetry.Protocols.OgcFeatures,
                layerId.ToString(CultureInfo.InvariantCulture),
                context.TraceIdentifier);
            featureActivity?.SetTag(HonuaTelemetry.Tags.CollectionId, collectionId);

            OgcFeaturesLog.ItemRequested(_logger, collectionId, featureId);

            var resolvedFeature = await OgcFeatureIdentifierResolver.ResolveAsync(
                _featureReader,
                _queryProcessor,
                layer,
                featureId,
                cancellationToken).ConfigureAwait(false);
            if (!resolvedFeature.HasValue)
            {
                return StandardErrorHelpers.CreateNotFound(context, $"Feature '{featureId}' not found.");
            }

            var objectId = resolvedFeature.Value.ObjectId;

            if (!OgcCommonUtilities.TryGetOutputFormat(f, context, isFeatureContent: true, out var outputFormat, out var formatError))
            {
                return OgcCommonUtilities.CreateFormatError(context, formatError);
            }

            var supportedCrs = await OgcFeaturesUtilities.GetSupportedCrsDefinitionsAsync(
                layer,
                _crsRegistry,
                cancellationToken);
            var crsResolution = await OgcFeaturesUtilities.TryResolveCrsAsync(
                crs,
                supportedCrs,
                _crsRegistry,
                cancellationToken);
            if (!crsResolution.IsSuccess)
            {
                return StandardErrorHelpers.CreateBadRequest(context, crsResolution.Error!);
            }

            var crsDefinition = crsResolution.Definition;

            var cacheableFormat = string.Equals(outputFormat, MediaTypes.Json, StringComparison.OrdinalIgnoreCase) ||
                                  string.Equals(outputFormat, MediaTypes.GeoJson, StringComparison.OrdinalIgnoreCase);
            var canCache = cacheableFormat && ResponseCacheUtilities.ShouldCache(context, _cacheOptions);
            var cacheTtl = canCache ? _cacheOptions.GetQueryTtlWithJitter() : TimeSpan.Zero;
            if (canCache && cacheTtl <= TimeSpan.Zero)
            {
                canCache = false;
            }

            var cacheKey = canCache
                ? ResponseCacheUtilities.BuildOgcCollectionKey(collectionId, request)
                : null;

            if (canCache && cacheKey != null)
            {
                var cached = await _responseCache.GetAsync<CachedResponse>(cacheKey, cancellationToken);
                if (cached != null)
                {
                    context.Response.Headers["Content-Crs"] = FormatContentCrs(crsDefinition.Uri);
                    return ResponseCacheUtilities.CreateResultFromCachedResponse(context, cached, _etagService);
                }
            }

            OgcFeaturesLog.ItemQueryStarted(_logger, collectionId, featureId);
            var stopwatch = Stopwatch.StartNew();
            var storedFeature = resolvedFeature.Value.Feature;

            var query = new FeatureQuery
            {
                ObjectIds = ImmutableArray.Create(objectId),
                Limit = 1,
                SpatialReferenceSrid = layer.SpatialReference.ToSrid(),
                OutputSrid = crsDefinition.Srid,
                OutputAxisOrder = crsDefinition.AxisOrder
            };

            var entityETag = OgcFeatureEntityTag.Compute(storedFeature, _etagService);
            context.Response.Headers["Content-Crs"] = FormatContentCrs(crsDefinition.Uri);

            if (string.Equals(outputFormat, MediaTypes.Gml, StringComparison.OrdinalIgnoreCase) &&
                _featureReader is IGmlFeatureStore gmlFeatureStore)
            {
                var gmlResult = await gmlFeatureStore.QueryGmlAsync(layerId, query, cancellationToken);
                stopwatch.Stop();
                OgcFeaturesLog.ItemQueryCompleted(_logger, collectionId, featureId, stopwatch.Elapsed.TotalMilliseconds);
                if (gmlResult.Items.IsDefaultOrEmpty)
                {
                    return StandardErrorHelpers.CreateNotFound(context, $"Feature '{featureId}' not found.");
                }

                HonuaTelemetry.SetSuccess(featureActivity, 1);
                var gml = OgcResponseFormatter.BuildGmlSingleFeature(gmlResult.Items[0]);
                return Results.Text(gml, MediaTypes.Gml);
            }

            var responseFeature = await OgcFeaturesResponseHelpers.LoadFeatureForResponseAsync(
                _featureReader,
                layerId,
                layer,
                objectId,
                crsDefinition,
                cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            OgcFeaturesLog.ItemQueryCompleted(_logger, collectionId, featureId, stopwatch.Elapsed.TotalMilliseconds);
            if (!responseFeature.HasValue)
            {
                return StandardErrorHelpers.CreateInternalServerError(context, "Feature response could not be projected.");
            }

            var responseFeatureId = OgcFeatureIdentifierResolver.FormatPublicId(responseFeature.Value, layer);
            ImmutableArray<Link>? featureLinks = _ogcFeaturesOptions.IncludeFeatureLinks
                ? OgcFeaturesUtilities.BuildFeatureLinks(request, collectionId, responseFeatureId, outputFormat)
                : null;
            var ogcFeature = ToOgcFeature(responseFeature.Value, layer, crsDefinition.AxisOrder, _geometryServices, null, featureLinks);

            if (string.Equals(outputFormat, MediaTypes.Gml, StringComparison.OrdinalIgnoreCase))
            {
                var gml = OgcResponseFormatter.BuildGmlSingleFeature(ogcFeature);
                return Results.Text(gml, MediaTypes.Gml);
            }

            if (string.Equals(outputFormat, MediaTypes.Csv, StringComparison.OrdinalIgnoreCase))
            {
                var fieldNames = ResolveCsvFieldNames(layer, null);
                var csv = OgcResponseFormatter.BuildCsvResponse([ogcFeature], fieldNames);
                return Results.Text(csv, MediaTypes.Csv);
            }

            if (cacheableFormat)
            {
                var contentType = string.Equals(outputFormat, MediaTypes.GeoJson, StringComparison.OrdinalIgnoreCase)
                    ? MediaTypes.GeoJson
                    : MediaTypes.Json;
                var payload = JsonSerializer.SerializeToUtf8Bytes(ogcFeature, OgcJsonContext.Default.GeoJsonFeature);
                var representationETag = OgcFeatureEntityTag.ComputeRepresentation(payload, entityETag, _etagService);
                var cachedResponse = new CachedResponse(payload, contentType, representationETag);
                if (canCache && cacheKey != null)
                {
                    await _responseCache.SetAsync(cacheKey, cachedResponse, cacheTtl, cancellationToken);
                }

                HonuaTelemetry.SetSuccess(featureActivity, 1);
                return ResponseCacheUtilities.CreateResultFromCachedResponse(context, cachedResponse, _etagService);
            }

            HonuaTelemetry.SetSuccess(featureActivity, 1);
            return OgcResponseFormatter.FormatFeatureResponse(ogcFeature, OgcJsonContext.Default.GeoJsonFeature, outputFormat, "Feature");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            OgcFeaturesLog.ItemQueryFailed(_logger, collectionId, ex);
            HonuaTelemetry.RecordException(featureActivity, ex);
            return StandardErrorHelpers.CreateInternalServerError(context, "An error occurred while retrieving the feature.");
        }
        finally
        {
            featureActivity?.Dispose();
        }
    }

    private static GeoJsonFeature ToOgcFeature(
        Feature feature,
        LayerDefinition layer,
        AxisOrder axisOrder,
        OgcFeaturesGeometryServices geometryServices,
        ImmutableHashSet<string>? projectedProperties = null,
        ImmutableArray<Link>? links = null)
        => OgcGeoJsonFeatureBuilder.Create(
            feature,
            layer,
            axisOrder,
            geometryServices,
            projectedProperties,
            links: links);

    private static GeoJsonFeature ToOgcFeature(
        EncodedGeoJsonFeature feature,
        LayerDefinition layer,
        AxisOrder axisOrder,
        OgcFeaturesGeometryServices geometryServices,
        ImmutableHashSet<string>? projectedProperties = null,
        ImmutableArray<Link>? links = null)
        => OgcGeoJsonFeatureBuilder.Create(
            feature,
            layer,
            axisOrder,
            geometryServices,
            projectedProperties,
            links: links);

    private static string[] ResolveCsvFieldNames(
        LayerDefinition layer,
        ImmutableHashSet<string>? projectedProperties)
    {
        IEnumerable<string> fieldNames = layer.AttributeFields.Select(field => field.Name);
        if (projectedProperties != null)
        {
            fieldNames = fieldNames.Where(projectedProperties.Contains);
        }

        return fieldNames.ToArray();
    }

    private static ImmutableArray<Link> BuildItemsLinks(
        HttpRequest request,
        string collectionId,
        string basePath,
        string outputFormat,
        int limit,
        int? offset,
        bool hasMoreResults)
    {
        var links = OgcCommonUtilities.BuildFormatLinks(
            request,
            basePath,
            outputFormat,
            OgcFeaturesUtilities.FeatureFormats,
            "Items").ToBuilder();

        if (offset.HasValue && offset.Value > 0)
        {
            var prevOffset = Math.Max(0, offset.Value - limit);
            links.Add(Link.Create(
                href: BuildPagedUrl(request, basePath, outputFormat, limit, prevOffset),
                rel: RelationTypes.Prev,
                type: outputFormat,
                title: "Previous page"));
        }

        if (hasMoreResults)
        {
            var nextOffset = (offset ?? 0) + limit;
            links.Add(Link.Create(
                href: BuildPagedUrl(request, basePath, outputFormat, limit, nextOffset),
                rel: RelationTypes.Next,
                type: outputFormat,
                title: "Next page"));
        }

        var baseUrl = BaseUrlResolver.GetBaseUrl(request);
        links.Add(Link.Create(
            href: $"{baseUrl}/ogc/features/collections/{Uri.EscapeDataString(collectionId)}/queryables",
            rel: RelationTypes.Queryables,
            type: MediaTypes.SchemaJson,
            title: "Queryables schema"));

        return links.ToImmutable();
    }

    private static string BuildPagedUrl(
        HttpRequest request,
        string basePath,
        string outputFormat,
        int limit,
        int offset)
    {
        var queryParts = new List<string>();

        foreach (var (key, value) in request.Query)
        {
            if (string.Equals(key, "offset", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "limit", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "f", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(value))
            {
                queryParts.Add($"{key}={Uri.EscapeDataString(value.ToString())}");
            }
        }

        queryParts.Add(FormattableString.Invariant($"limit={limit}"));
        queryParts.Add(FormattableString.Invariant($"offset={offset}"));

        var formatValue = outputFormat switch
        {
            var format when string.Equals(format, MediaTypes.Json, StringComparison.OrdinalIgnoreCase) => "json",
            var format when string.Equals(format, MediaTypes.GeoJson, StringComparison.OrdinalIgnoreCase) => "geojson",
            var format when string.Equals(format, MediaTypes.Html, StringComparison.OrdinalIgnoreCase) => "html",
            var format when string.Equals(format, MediaTypes.Gml, StringComparison.OrdinalIgnoreCase) => "gml",
            var format when string.Equals(format, MediaTypes.Csv, StringComparison.OrdinalIgnoreCase) => "csv",
            _ => null
        };

        if (!string.IsNullOrWhiteSpace(formatValue))
        {
            queryParts.Add($"f={formatValue}");
        }

        return queryParts.Count > 0
            ? $"{basePath}?{string.Join("&", queryParts)}"
            : basePath;
    }

    private static string FormatContentCrs(string crsUri) => $"<{crsUri}>";

    internal static ReadOnlyMemory<byte> CreateRawFeatureCollectionPayload(
        ImmutableArray<RawGeoJsonFeature> features,
        LayerDefinition layer,
        ImmutableArray<Link> links,
        long? numberMatched)
    {
        var propertyFields = layer.AttributeFields
            .Where(field => !field.Name.Equals(layer.ObjectIdFieldName, StringComparison.OrdinalIgnoreCase))
            .Select(field => field.Name)
            .ToArray();
        var publicIdPropertyName = ResolveRawPublicIdPropertyName(layer);
        var buffer = new ArrayBufferWriter<byte>(EstimateRawFeatureCollectionPayloadCapacity(features, propertyFields, links));
        using var writer = new Utf8JsonWriter(buffer);

        writer.WriteStartObject();
        writer.WriteString("type", "FeatureCollection");
        writer.WriteStartArray("features");

        foreach (var feature in features)
        {
            using var propertiesDocument = TryParseJsonObject(feature.PropertiesJson);
            var propertiesElement = propertiesDocument?.RootElement;

            writer.WriteStartObject();
            writer.WriteString("type", "Feature");
            writer.WritePropertyName("id");
            WriteRawFeatureId(writer, feature.Id, feature.PublicIdJson, propertiesElement, publicIdPropertyName);
            writer.WritePropertyName("geometry");
            WriteValidatedRawGeometry(writer, feature.GeometryGeoJson);

            writer.WritePropertyName("properties");
            WriteSchemaProperties(writer, propertiesElement, propertyFields);

            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        if (numberMatched.HasValue)
        {
            writer.WriteNumber("numberMatched", numberMatched.Value);
        }

        writer.WriteNumber("numberReturned", features.Length);
        writer.WritePropertyName("links");
        JsonSerializer.Serialize(writer, links, OgcJsonContext.Default.ImmutableArrayLink);
        writer.WriteString("timeStamp", DateTimeOffset.UtcNow);
        writer.WriteEndObject();
        writer.Flush();

        return buffer.WrittenMemory;
    }

    internal static ReadOnlyMemory<byte> CreateRawPointFeatureCollectionPayload(
        ImmutableArray<RawGeoServicesFeature> features,
        LayerDefinition layer,
        ImmutableArray<Link> links,
        long? numberMatched)
    {
        var propertyFields = layer.AttributeFields
            .Where(field => !field.Name.Equals(layer.ObjectIdFieldName, StringComparison.OrdinalIgnoreCase))
            .Select(field => field.Name)
            .ToArray();
        var publicIdPropertyName = ResolveRawPublicIdPropertyName(layer);
        var buffer = new ArrayBufferWriter<byte>(EstimateRawPointFeatureCollectionPayloadCapacity(features, propertyFields, links));
        using var writer = new Utf8JsonWriter(buffer);

        writer.WriteStartObject();
        writer.WriteString("type", "FeatureCollection");
        writer.WriteStartArray("features");

        foreach (var feature in features)
        {
            using var propertiesDocument = TryParseJsonObject(feature.AttributesJson);
            var propertiesElement = propertiesDocument?.RootElement;

            writer.WriteStartObject();
            writer.WriteString("type", "Feature");
            writer.WritePropertyName("id");
            WriteRawFeatureId(writer, feature.Id, feature.PublicIdJson, propertiesElement, publicIdPropertyName);
            writer.WritePropertyName("geometry");
            WriteRawPointGeometry(writer, feature);

            writer.WritePropertyName("properties");
            WriteSchemaProperties(writer, propertiesElement, propertyFields);

            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        if (numberMatched.HasValue)
        {
            writer.WriteNumber("numberMatched", numberMatched.Value);
        }

        writer.WriteNumber("numberReturned", features.Length);
        writer.WritePropertyName("links");
        JsonSerializer.Serialize(writer, links, OgcJsonContext.Default.ImmutableArrayLink);
        writer.WriteString("timeStamp", DateTimeOffset.UtcNow);
        writer.WriteEndObject();
        writer.Flush();

        return buffer.WrittenMemory;
    }

    private static string? ResolveRawPublicIdPropertyName(LayerDefinition layer)
    {
        var configuredField = layer.AttributeFields.FirstOrDefault(
            field => field.Name.Equals(layer.ObjectIdFieldName, StringComparison.OrdinalIgnoreCase));
        if (configuredField?.Name is { Length: > 0 })
        {
            return configuredField.Name;
        }

        if (!layer.ObjectIdFieldName.Equals("id", StringComparison.OrdinalIgnoreCase))
        {
            var idField = layer.AttributeFields.FirstOrDefault(
                field => field.Name.Equals("id", StringComparison.OrdinalIgnoreCase));
            if (idField?.Name is { Length: > 0 })
            {
                return idField.Name;
            }
        }

        return null;
    }

    private static int EstimateRawFeatureCollectionPayloadCapacity(
        ImmutableArray<RawGeoJsonFeature> features,
        string[] propertyFields,
        ImmutableArray<Link> links)
    {
        const int MinimumCapacity = 8 * 1024;
        const int FixedPayloadOverhead = 512;
        const int PropertyFieldOverhead = 48;
        const int LinkOverhead = 256;
        const int FeatureOverhead = 96;

        long estimated = FixedPayloadOverhead +
                         (propertyFields.Length * PropertyFieldOverhead) +
                         (links.Length * LinkOverhead);
        foreach (var feature in features)
        {
            estimated += (feature.GeometryGeoJson?.Length ?? 4) +
                         (feature.PropertiesJson?.Length ?? 2) +
                         FeatureOverhead;
        }

        return estimated >= int.MaxValue
            ? int.MaxValue
            : Math.Max(MinimumCapacity, (int)estimated);
    }

    private static int EstimateRawPointFeatureCollectionPayloadCapacity(
        ImmutableArray<RawGeoServicesFeature> features,
        string[] propertyFields,
        ImmutableArray<Link> links)
    {
        const int MinimumCapacity = 8 * 1024;
        const int FixedPayloadOverhead = 512;
        const int PropertyFieldOverhead = 48;
        const int LinkOverhead = 256;
        const int FeatureOverhead = 128;

        long estimated = FixedPayloadOverhead +
                         (propertyFields.Length * PropertyFieldOverhead) +
                         (links.Length * LinkOverhead);
        foreach (var feature in features)
        {
            estimated += (feature.AttributesJson?.Length ?? 2) + FeatureOverhead;
        }

        return estimated >= int.MaxValue
            ? int.MaxValue
            : Math.Max(MinimumCapacity, (int)estimated);
    }

    private static void WriteRawPointGeometry(Utf8JsonWriter writer, RawGeoServicesFeature feature)
    {
        if (!feature.X.HasValue || !feature.Y.HasValue)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("type", "Point");
        writer.WriteStartArray("coordinates");
        writer.WriteNumberValue(feature.X.Value);
        writer.WriteNumberValue(feature.Y.Value);
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteValidatedRawGeometry(Utf8JsonWriter writer, string? geometryGeoJson)
    {
        if (string.IsNullOrWhiteSpace(geometryGeoJson))
        {
            writer.WriteNullValue();
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(geometryGeoJson);
            if (OgcGeoJsonGeometryShapeValidator.IsKnownValidGeometry(document.RootElement))
            {
                document.RootElement.WriteTo(writer);
                return;
            }
        }
        catch (JsonException)
        {
        }

        writer.WriteNullValue();
    }

    private static JsonDocument? TryParseJsonObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                return document;
            }

            document.Dispose();
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private static void WriteRawFeatureId(
        Utf8JsonWriter writer,
        long objectId,
        string? publicIdJson,
        JsonElement? propertiesElement,
        string? publicIdPropertyName)
    {
        if (TryWriteRawJsonId(writer, publicIdJson))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(publicIdPropertyName) &&
            propertiesElement.HasValue &&
            TryGetJsonPropertyIgnoreCase(propertiesElement.Value, publicIdPropertyName, out var property) &&
            TryWriteJsonElementId(writer, property.Value))
        {
            return;
        }

        writer.WriteNumberValue(objectId);
    }

    private static bool TryWriteRawJsonId(Utf8JsonWriter writer, string? publicIdJson)
    {
        if (string.IsNullOrWhiteSpace(publicIdJson))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(publicIdJson);
            return TryWriteJsonElementId(writer, document.RootElement);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryWriteJsonElementId(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
                if (value.TryGetInt64(out var longValue))
                {
                    writer.WriteNumberValue(longValue);
                    return true;
                }

                if (value.TryGetDouble(out var doubleValue))
                {
                    writer.WriteNumberValue(doubleValue);
                    return true;
                }

                return false;

            case JsonValueKind.String:
                writer.WriteStringValue(value.GetString());
                return true;

            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                return true;

            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                return true;

            default:
                return false;
        }
    }

    private static void WriteSchemaProperties(Utf8JsonWriter writer, JsonElement? propertiesElement, string[] propertyFields)
    {
        writer.WriteStartObject();
        if (!propertiesElement.HasValue || propertyFields.Length == 0)
        {
            writer.WriteEndObject();
            return;
        }

        foreach (var fieldName in propertyFields)
        {
            if (TryGetJsonPropertyIgnoreCase(propertiesElement.Value, fieldName, out var property))
            {
                writer.WritePropertyName(fieldName);
                property.Value.WriteTo(writer);
            }
        }

        writer.WriteEndObject();
    }

    private static bool TryGetJsonPropertyIgnoreCase(JsonElement element, string propertyName, out JsonProperty property)
    {
        foreach (var candidate in element.EnumerateObject())
        {
            if (candidate.NameEquals(propertyName) ||
                string.Equals(candidate.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                property = candidate;
                return true;
            }
        }

        property = default;
        return false;
    }

    private static async Task StreamFeatureCollectionAsync(
        HttpContext context,
        IAsyncEnumerable<Feature> features,
        LayerDefinition layer,
        string collectionId,
        AxisOrder axisOrder,
        OgcFeaturesGeometryServices geometryServices,
        ImmutableHashSet<string>? projectedProperties,
        string outputFormat,
        ImmutableArray<Link> links,
        bool includeFeatureLinks,
        long numberMatched,
        CancellationToken cancellationToken)
    {
        using var writer = new Utf8JsonWriter(context.Response.BodyWriter, new JsonWriterOptions
        {
            Indented = false,
            SkipValidation = true
        });

        writer.WriteStartObject();
        writer.WriteString("type", "FeatureCollection");
        writer.WriteStartArray("features");

        var numberReturned = 0;
        var featuresSinceFlush = 0;
        await foreach (var feature in features.WithCancellation(cancellationToken))
        {
            ImmutableArray<Link>? featureLinks = includeFeatureLinks
                ? OgcFeaturesUtilities.BuildFeatureLinks(
                    context.Request,
                    collectionId,
                    OgcFeatureIdentifierResolver.FormatPublicId(feature, layer),
                    outputFormat)
                : null;
            var ogcFeature = ToOgcFeature(feature, layer, axisOrder, geometryServices, projectedProperties, featureLinks);
            JsonSerializer.Serialize(writer, ogcFeature, OgcJsonContext.Default.GeoJsonFeature);

            numberReturned++;
            if (++featuresSinceFlush >= StreamingFlushInterval)
            {
                await writer.FlushAsync(cancellationToken);
                featuresSinceFlush = 0;
            }
        }

        writer.WriteEndArray();
        writer.WriteNumber("numberMatched", numberMatched);
        writer.WriteNumber("numberReturned", numberReturned);
        writer.WritePropertyName("links");
        JsonSerializer.Serialize(writer, links, OgcJsonContext.Default.ImmutableArrayLink);

        writer.WriteString("timeStamp", DateTimeOffset.UtcNow);
        writer.WriteEndObject();

        await writer.FlushAsync(cancellationToken);
        await context.Response.BodyWriter.CompleteAsync();
    }

    private static void EnableChunkedEncodingIfHttp1(HttpContext context)
    {
        if (context.Request.Protocol.StartsWith("HTTP/1.", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.Headers.ContentLength = null;
            context.Response.Headers.TransferEncoding = "chunked";
        }
    }

    private sealed class StreamingItemsResult : IResult
    {
        private readonly IStreamingFeatureStore _streamingFeatureStore;
        private readonly LayerDefinition _layer;
        private readonly FeatureQuery _query;
        private readonly string _collectionId;
        private readonly AxisOrder _axisOrder;
        private readonly string _outputFormat;
        private readonly ImmutableArray<Link> _links;
        private readonly bool _includeFeatureLinks;
        private readonly ImmutableHashSet<string>? _projectedProperties;
        private readonly long _numberMatched;
        private readonly string _crsUri;
        private readonly OgcFeaturesGeometryServices _geometryServices;
        private readonly CancellationToken _requestCancellationToken;

        public StreamingItemsResult(
            IStreamingFeatureStore streamingFeatureStore,
            LayerDefinition layer,
            FeatureQuery query,
            string collectionId,
            AxisOrder axisOrder,
            OgcFeaturesGeometryServices geometryServices,
            ImmutableHashSet<string>? projectedProperties,
            string outputFormat,
            ImmutableArray<Link> links,
            bool includeFeatureLinks,
            long numberMatched,
            string crsUri,
            CancellationToken requestCancellationToken)
        {
            _streamingFeatureStore = streamingFeatureStore;
            _layer = layer;
            _query = query;
            _collectionId = collectionId;
            _axisOrder = axisOrder;
            _geometryServices = geometryServices;
            _projectedProperties = projectedProperties;
            _outputFormat = outputFormat;
            _links = links;
            _includeFeatureLinks = includeFeatureLinks;
            _numberMatched = numberMatched;
            _crsUri = crsUri;
            _requestCancellationToken = requestCancellationToken;
        }

        public async Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.ContentType = _outputFormat;
            httpContext.Response.Headers["Content-Crs"] = FormatContentCrs(_crsUri);
            httpContext.Response.Headers["OGC-NumberMatched"] = _numberMatched.ToString(CultureInfo.InvariantCulture);
            EnableChunkedEncodingIfHttp1(httpContext);

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                httpContext.RequestAborted,
                _requestCancellationToken);
            var cancellationToken = linkedCts.Token;

            var stream = _streamingFeatureStore.StreamFeaturesAsync(
                _layer.Id,
                _query,
                cancellationToken);

            await StreamFeatureCollectionAsync(
                httpContext,
                stream,
                _layer,
                _collectionId,
                _axisOrder,
                _geometryServices,
                _projectedProperties,
                _outputFormat,
                _links,
                _includeFeatureLinks,
                _numberMatched,
                cancellationToken);
        }
    }

    private sealed class StreamingGmlItemsResult : IResult
    {
        private readonly IStreamingFeatureStore _streamingFeatureStore;
        private readonly LayerDefinition _layer;
        private readonly FeatureQuery _query;
        private readonly long _numberMatched;
        private readonly int _numberReturned;
        private readonly string _crsUri;
        private readonly ImmutableArray<Link> _links;
        private readonly CancellationToken _requestCancellationToken;

        public StreamingGmlItemsResult(
            IStreamingFeatureStore streamingFeatureStore,
            LayerDefinition layer,
            FeatureQuery query,
            long numberMatched,
            int numberReturned,
            string crsUri,
            ImmutableArray<Link> links,
            CancellationToken requestCancellationToken)
        {
            _streamingFeatureStore = streamingFeatureStore;
            _layer = layer;
            _query = query;
            _numberMatched = numberMatched;
            _numberReturned = numberReturned;
            _crsUri = crsUri;
            _links = links;
            _requestCancellationToken = requestCancellationToken;
        }

        public async Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.ContentType = MediaTypes.Gml;
            httpContext.Response.Headers["Content-Crs"] = FormatContentCrs(_crsUri);
            httpContext.Response.Headers["OGC-NumberMatched"] = _numberMatched.ToString(CultureInfo.InvariantCulture);

            // Emit pagination links as HTTP Link headers (standard for non-JSON formats)
            foreach (var link in _links)
            {
                if (!string.IsNullOrEmpty(link.Href))
                {
                    httpContext.Response.Headers.Append("Link", $"<{link.Href}>; rel=\"{link.Rel}\"");
                }
            }

            EnableChunkedEncodingIfHttp1(httpContext);

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                httpContext.RequestAborted,
                _requestCancellationToken);
            var cancellationToken = linkedCts.Token;

            var stream = _streamingFeatureStore.StreamGmlFeaturesAsync(
                _layer.Id,
                _query,
                cancellationToken);

            await OgcResponseFormatter.StreamGmlFeatureCollectionAsync(
                stream,
                httpContext.Response.BodyWriter,
                _numberMatched,
                _numberReturned,
                DateTimeOffset.UtcNow,
                cancellationToken);

            await httpContext.Response.BodyWriter.CompleteAsync();
        }
    }

}
