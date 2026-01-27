// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Honua.Core.Features.Caching;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Caching;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Core.Queries.Filters;
using Honua.Core.Queries.Filters.Cql2;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Caching;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Validation;
using Honua.Server.Features.Ogc.Common;
using Honua.Server.Features.OgcFeatures.Models;
using Honua.Server.Features.OgcFeatures.Services;
using Honua.ServiceDefaults;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Features.OgcFeatures;

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
    private readonly IResourceValidator _resourceValidator = dependencies.ResourceValidator;
    private readonly ICommonQueryValidator _queryValidator = dependencies.QueryValidator;
    private readonly ICrsRegistry _crsRegistry = dependencies.CrsRegistry;
    private readonly OgcFilterProcessor _filterProcessor = dependencies.FilterProcessor;
    private readonly OgcFeaturesGeometryServices _geometryServices = dependencies.GeometryServices;
    private readonly IResponseCache _responseCache = dependencies.ResponseCache;
    private readonly IETagService _etagService = dependencies.ETagService;
    private readonly CacheOptions _cacheOptions = dependencies.CacheOptions;
    private readonly ILogger<OgcFeaturesQueryHandler> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private const int StreamingThreshold = 1000;

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

            // Use centralized resource validation
            var collectionResult = await _resourceValidator.ValidateCollectionAsync(collectionId, cancellationToken);
            if (!collectionResult.IsValid)
            {
                return StandardErrorHelpers.CreateNotFound(context, collectionResult.ErrorMessage ?? $"Collection '{collectionId}' not found.");
            }
            var layer = collectionResult.Resource!;
            var layerId = layer.Id;
            var activity = Activity.Current;
            activity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.OgcFeatures);
            activity?.SetTag(HonuaTelemetry.Tags.CollectionId, collectionId);
            activity?.SetTag(HonuaTelemetry.Tags.LayerId, layerId.ToString(CultureInfo.InvariantCulture));
            var accessError = AccessPolicyHelpers.RequireLayerAccess(context, layer);
            if (accessError != null)
            {
                return accessError;
            }

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

            // Use filter processor for comprehensive filter handling
            var filterResult = await _filterProcessor.ProcessFiltersAsync(
                request, layer, filter, bbox, datetime, crs, cancellationToken);
            if (!filterResult.IsSuccess)
            {
                return StandardErrorHelpers.CreateBadRequest(context, filterResult.ErrorMessage!);
            }

            // Use centralized pagination validation
            var paginationResult = _queryValidator.ValidateAndNormalizePagination(offset, limit);
            if (!paginationResult.IsValid)
            {
                return StandardErrorHelpers.CreateBadRequest(context, paginationResult.ErrorMessage ?? "Invalid paging parameters.");
            }
            var effectiveLimit = paginationResult.Value!.Limit;
            var effectiveOffset = paginationResult.Value.Offset;

            var query = new FeatureQuery
            {
                Where = filterResult.CombinedFilter,
                SqlFilter = filterResult.SqlFilter,
                Offset = effectiveOffset,
                Limit = effectiveLimit,
                SpatialReferenceSrid = layer.SpatialReference.ToSrid(),
                OutputSrid = filterResult.CrsDefinition.Srid,
                SpatialFilter = filterResult.SpatialFilter,
                TemporalFilter = filterResult.TemporalFilter,
                IncludeNullGeometry = filterResult.IncludeNullGeometry
            };

            var useStreaming = effectiveLimit > StreamingThreshold &&
                               !string.Equals(outputFormat, MediaTypes.Html, StringComparison.OrdinalIgnoreCase);

            var cacheableFormat = string.Equals(outputFormat, MediaTypes.Json, StringComparison.OrdinalIgnoreCase) ||
                                  string.Equals(outputFormat, MediaTypes.GeoJson, StringComparison.OrdinalIgnoreCase);
            var canCache = !useStreaming && cacheableFormat && ResponseCacheUtilities.ShouldCache(context, _cacheOptions);
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
                    context.Response.Headers["Content-Crs"] = FormatContentCrs(filterResult.CrsDefinition.Uri);
                    return ResponseCacheUtilities.CreateResultFromCachedResponse(context, cached, _etagService);
                }
            }

            OgcFeaturesLog.ItemsQueryStarted(_logger, collectionId, effectiveLimit, effectiveOffset);
            var stopwatch = Stopwatch.StartNew();

            if (useStreaming)
            {
                var totalCount = await _featureReader.CountAsync(layerId, query, cancellationToken);
                var hasMoreResults = totalCount > (effectiveOffset + effectiveLimit);
                stopwatch.Stop();
                var estimatedReturned = (int)Math.Min(effectiveLimit, Math.Max(0, totalCount - effectiveOffset));
                OgcFeaturesLog.ItemsQueryCompleted(_logger, collectionId, estimatedReturned, totalCount, stopwatch.Elapsed.TotalMilliseconds);
                HonuaTelemetry.SetSuccess(featureActivity, estimatedReturned);

                var streamBaseUrl = BaseUrlResolver.GetBaseUrl(context);
                var streamBasePath = $"{streamBaseUrl}/ogc/features/collections/{collectionId}/items";
                var streamLinks = BuildItemsLinks(request, streamBasePath, outputFormat, effectiveLimit, effectiveOffset, hasMoreResults);

                if (string.Equals(outputFormat, MediaTypes.Gml, StringComparison.OrdinalIgnoreCase))
                {
                    return new StreamingGmlItemsResult(
                        _streamingFeatureStore,
                        layer,
                        query,
                        filterResult.CrsDefinition.Uri);
                }

                return new StreamingItemsResult(
                    _streamingFeatureStore,
                    layer,
                    query,
                    collectionId,
                    filterResult.CrsDefinition.AxisOrder,
                    _geometryServices,
                    outputFormat,
                    streamLinks,
                    totalCount,
                    filterResult.CrsDefinition.Uri);
            }

            var result = await _featureReader.QueryAsync(layerId, query, cancellationToken);
            var features = result.Items
                .Select(feature =>
                {
                    var links = BuildFeatureLinks(
                        request,
                        collectionId,
                        FormattableString.Invariant($"{feature.Id}"),
                        outputFormat);
                    return ToOgcFeature(feature, filterResult.CrsDefinition.AxisOrder, _geometryServices, links);
                })
                .ToArray();
            stopwatch.Stop();
            OgcFeaturesLog.ItemsQueryCompleted(_logger, collectionId, features.Length, result.TotalCount, stopwatch.Elapsed.TotalMilliseconds);
            HonuaTelemetry.SetSuccess(featureActivity, features.Length);

            var baseUrl = BaseUrlResolver.GetBaseUrl(context);
            var basePath = $"{baseUrl}/ogc/features/collections/{collectionId}/items";

            var links = BuildItemsLinks(request, basePath, outputFormat, effectiveLimit, effectiveOffset, result.HasMoreResults);

            var response = new FeatureCollection
            {
                Features = features,
                NumberMatched = result.TotalCount,
                NumberReturned = features.Length,
                Links = links,
                TimeStamp = DateTimeOffset.UtcNow
            };

            context.Response.Headers["Content-Crs"] = FormatContentCrs(filterResult.CrsDefinition.Uri);

            if (string.Equals(outputFormat, MediaTypes.Gml, StringComparison.OrdinalIgnoreCase))
            {
                var gml = BuildGmlFeatureCollection(features);
                return Results.Text(gml, MediaTypes.Gml);
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

            return FormatFeatureResponse(response, OgcJsonContext.Default.FeatureCollection, outputFormat, "Features");
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

            // Use centralized resource validation
            var collectionResult = await _resourceValidator.ValidateCollectionAsync(collectionId, cancellationToken);
            if (!collectionResult.IsValid)
            {
                return StandardErrorHelpers.CreateNotFound(context, collectionResult.ErrorMessage ?? $"Collection '{collectionId}' not found.");
            }
            var layer = collectionResult.Resource!;
            var layerId = layer.Id;
            var activity = Activity.Current;
            activity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.OgcFeatures);
            activity?.SetTag(HonuaTelemetry.Tags.CollectionId, collectionId);
            activity?.SetTag(HonuaTelemetry.Tags.LayerId, layerId.ToString(CultureInfo.InvariantCulture));
            var accessError = AccessPolicyHelpers.RequireLayerAccess(context, layer);
            if (accessError != null)
            {
                return accessError;
            }

            featureActivity = HonuaTelemetry.StartFeatureActivity(
                "feature",
                HonuaTelemetry.Protocols.OgcFeatures,
                layerId.ToString(CultureInfo.InvariantCulture),
                context.TraceIdentifier);
            featureActivity?.SetTag(HonuaTelemetry.Tags.CollectionId, collectionId);

            OgcFeaturesLog.ItemRequested(_logger, collectionId, featureId);

            if (!long.TryParse(featureId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var objectId))
            {
                return StandardErrorHelpers.CreateNotFound(context, $"Feature '{featureId}' not found.");
            }

            if (!OgcCommonUtilities.TryGetOutputFormat(f, context, isFeatureContent: true, out var outputFormat, out var formatError))
            {
                return OgcCommonUtilities.CreateFormatError(context, formatError);
            }

            var supportedCrs = await OgcFeaturesUtilities.GetSupportedCrsDefinitionsAsync(
                layer,
                _crsRegistry,
                cancellationToken);
            if (!OgcFeaturesUtilities.TryResolveCrs(crs, supportedCrs, out var crsDefinition, out var crsError))
            {
                return StandardErrorHelpers.CreateBadRequest(context, crsError!);
            }

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
            var feature = await _featureReader.GetAsync(layerId, objectId, cancellationToken);
            stopwatch.Stop();
            OgcFeaturesLog.ItemQueryCompleted(_logger, collectionId, featureId, stopwatch.Elapsed.TotalMilliseconds);
            if (feature == null)
            {
                return StandardErrorHelpers.CreateNotFound(context, $"Feature '{featureId}' not found.");
            }

            var featureLinks = BuildFeatureLinks(request, collectionId, featureId, outputFormat);
            var ogcFeature = ToOgcFeature(feature.Value, crsDefinition.AxisOrder, _geometryServices, featureLinks);

            context.Response.Headers["Content-Crs"] = FormatContentCrs(crsDefinition.Uri);

            if (string.Equals(outputFormat, MediaTypes.Gml, StringComparison.OrdinalIgnoreCase))
            {
                var gml = BuildGmlSingleFeature(ogcFeature);
                return Results.Text(gml, MediaTypes.Gml);
            }

            if (canCache && cacheKey != null)
            {
                var contentType = string.Equals(outputFormat, MediaTypes.GeoJson, StringComparison.OrdinalIgnoreCase)
                    ? MediaTypes.GeoJson
                    : MediaTypes.Json;
                var payload = JsonSerializer.SerializeToUtf8Bytes(ogcFeature, OgcJsonContext.Default.GeoJsonFeature);
                var cachedResponse = ResponseCacheUtilities.CreateCachedResponse(payload, contentType, _etagService);
                await _responseCache.SetAsync(cacheKey, cachedResponse, cacheTtl, cancellationToken);
                HonuaTelemetry.SetSuccess(featureActivity, 1);
                return ResponseCacheUtilities.CreateResultFromCachedResponse(context, cachedResponse, _etagService);
            }

            HonuaTelemetry.SetSuccess(featureActivity, 1);
            return FormatFeatureResponse(ogcFeature, OgcJsonContext.Default.GeoJsonFeature, outputFormat, "Feature");
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
        AxisOrder axisOrder,
        OgcFeaturesGeometryServices geometryServices,
        ImmutableArray<Link>? links = null)
    {
        var geometry = geometryServices.ConvertWkbToSimpleGeometry(feature.Geometry, axisOrder);
        return feature.ToGeoJsonBase().ToOgcGeoJsonFeature(geometry, links);
    }

    private static ImmutableArray<Link> BuildItemsLinks(
        HttpRequest request,
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

        return links.ToImmutable();
    }

    private static ImmutableArray<Link> BuildFeatureLinks(
        HttpRequest request,
        string collectionId,
        string featureId,
        string outputFormat)
    {
        var baseUrl = BaseUrlResolver.GetBaseUrl(request);
        var basePath = $"{baseUrl}/ogc/features/collections/{collectionId}/items/{featureId}";

        var links = new List<Link>
        {
            Link.Create(
                href: basePath,
                rel: RelationTypes.Self,
                type: outputFormat,
                title: "Feature")
        };

        foreach (var format in OgcFeaturesUtilities.FeatureFormats)
        {
            if (string.Equals(format.MediaType, outputFormat, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            links.Add(Link.Create(
                href: $"{basePath}?f={Uri.EscapeDataString(format.QueryValue)}",
                rel: RelationTypes.Alternate,
                type: format.MediaType,
                title: format.Title));
        }

        links.Add(Link.Create(
            href: $"{baseUrl}/ogc/features/collections/{collectionId}",
            rel: RelationTypes.Collection,
            type: MediaTypes.Json,
            title: "Collection"));

        return links.ToImmutableArray();
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

    private static IResult FormatFeatureResponse<T>(
        T payload,
        JsonTypeInfo<T> typeInfo,
        string outputFormat,
        string title)
    {
        return OgcResponseFormatter.FormatFeatureResponse(payload, typeInfo, outputFormat, title);
    }

    private static string FormatContentCrs(string crsUri) => $"<{crsUri}>";

    private static string BuildGmlFeatureCollection(IEnumerable<GeoJsonFeature> features)
    {
        return OgcResponseFormatter.BuildGmlFeatureCollection(features);
    }

    private static string BuildGmlSingleFeature(GeoJsonFeature feature)
    {
        return OgcResponseFormatter.BuildGmlSingleFeature(feature);
    }

    private static async Task StreamFeatureCollectionAsync(
        HttpContext context,
        IAsyncEnumerable<Feature> features,
        string collectionId,
        AxisOrder axisOrder,
        OgcFeaturesGeometryServices geometryServices,
        string outputFormat,
        ImmutableArray<Link> links,
        long numberMatched,
        CancellationToken cancellationToken)
    {
        using var writer = new Utf8JsonWriter(context.Response.BodyWriter, new JsonWriterOptions
        {
            Indented = false,
            SkipValidation = false
        });

        writer.WriteStartObject();
        writer.WriteString("type", "FeatureCollection");
        writer.WriteStartArray("features");

        var numberReturned = 0;
        await foreach (var feature in features.WithCancellation(cancellationToken))
        {
            var featureLinks = BuildFeatureLinks(
                context.Request,
                collectionId,
                FormattableString.Invariant($"{feature.Id}"),
                outputFormat);
            var ogcFeature = ToOgcFeature(feature, axisOrder, geometryServices, featureLinks);
            JsonSerializer.Serialize(writer, ogcFeature, OgcJsonContext.Default.GeoJsonFeature);

            numberReturned++;
            await writer.FlushAsync(cancellationToken);
        }

        writer.WriteEndArray();
        writer.WriteNumber("numberMatched", numberMatched);
        writer.WriteNumber("numberReturned", numberReturned);
        writer.WritePropertyName("links");

        var linksTypeInfo = OgcJsonContext.Default.GetTypeInfo(typeof(ImmutableArray<Link>));
        if (linksTypeInfo is not null)
        {
            JsonSerializer.Serialize(writer, links, linksTypeInfo);
        }
        else
        {
            JsonSerializer.Serialize(writer, links);
        }

        writer.WriteString("timeStamp", DateTimeOffset.UtcNow);
        writer.WriteEndObject();

        await writer.FlushAsync(cancellationToken);
        await context.Response.BodyWriter.CompleteAsync();
    }

    private static void EnableChunkedEncodingIfHttp1(HttpContext context)
    {
        if (string.IsNullOrEmpty(context.Request.Protocol)
            || context.Request.Protocol.StartsWith("HTTP/1", StringComparison.OrdinalIgnoreCase))
        {
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
        private readonly long _numberMatched;
        private readonly string _crsUri;
        private readonly OgcFeaturesGeometryServices _geometryServices;

        public StreamingItemsResult(
            IStreamingFeatureStore streamingFeatureStore,
            LayerDefinition layer,
            FeatureQuery query,
            string collectionId,
            AxisOrder axisOrder,
            OgcFeaturesGeometryServices geometryServices,
            string outputFormat,
            ImmutableArray<Link> links,
            long numberMatched,
            string crsUri)
        {
            _streamingFeatureStore = streamingFeatureStore;
            _layer = layer;
            _query = query;
            _collectionId = collectionId;
            _axisOrder = axisOrder;
            _geometryServices = geometryServices;
            _outputFormat = outputFormat;
            _links = links;
            _numberMatched = numberMatched;
            _crsUri = crsUri;
        }

        public async Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.ContentType = _outputFormat;
            httpContext.Response.Headers["Content-Crs"] = FormatContentCrs(_crsUri);
            EnableChunkedEncodingIfHttp1(httpContext);

            var stream = _streamingFeatureStore.StreamFeaturesAsync(
                _layer.Id,
                _query,
                httpContext.RequestAborted);

            await StreamFeatureCollectionAsync(
                httpContext,
                stream,
                _collectionId,
                _axisOrder,
                _geometryServices,
                _outputFormat,
                _links,
                _numberMatched,
                httpContext.RequestAborted);
        }
    }

    private sealed class StreamingGmlItemsResult : IResult
    {
        private readonly IStreamingFeatureStore _streamingFeatureStore;
        private readonly LayerDefinition _layer;
        private readonly FeatureQuery _query;
        private readonly string _crsUri;

        public StreamingGmlItemsResult(
            IStreamingFeatureStore streamingFeatureStore,
            LayerDefinition layer,
            FeatureQuery query,
            string crsUri)
        {
            _streamingFeatureStore = streamingFeatureStore;
            _layer = layer;
            _query = query;
            _crsUri = crsUri;
        }

        public async Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.ContentType = MediaTypes.Gml;
            httpContext.Response.Headers["Content-Crs"] = FormatContentCrs(_crsUri);
            EnableChunkedEncodingIfHttp1(httpContext);

            var stream = _streamingFeatureStore.StreamGmlFeaturesAsync(
                _layer.Id,
                _query,
                httpContext.RequestAborted);

            await OgcResponseFormatter.StreamGmlFeatureCollectionAsync(
                stream,
                httpContext.Response.BodyWriter,
                httpContext.RequestAborted);

            await httpContext.Response.BodyWriter.CompleteAsync();
        }
    }

}
