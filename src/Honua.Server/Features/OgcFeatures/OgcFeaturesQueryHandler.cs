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
    private static readonly ImmutableHashSet<string> _sortByCoreFields = ImmutableHashSet.Create(
        StringComparer.OrdinalIgnoreCase,
        FieldNames.ObjectId,
        "object_id",
        "id",
        "created_at",
        "updated_at");

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

            if (!TryParseIds(ids, out var objectIds, out var idsError))
            {
                return StandardErrorHelpers.CreateBadRequest(context, idsError ?? "Invalid ids parameter.");
            }

            if (!TryParseProperties(properties, layer, out var selectedProperties, out var propertiesError))
            {
                return StandardErrorHelpers.CreateBadRequest(context, propertiesError ?? "Invalid properties parameter.");
            }

            if (!TryParseSortBy(sortby, layer, out var orderBy, out var sortByError))
            {
                return StandardErrorHelpers.CreateBadRequest(context, sortByError ?? "Invalid sortby parameter.");
            }

            var projectedProperties = selectedProperties?.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);

            var query = new FeatureQuery
            {
                Where = filterResult.CombinedFilter,
                SqlFilter = filterResult.SqlFilter,
                ObjectIds = objectIds,
                OutFields = selectedProperties,
                Offset = effectiveOffset,
                Limit = effectiveLimit,
                OrderBy = orderBy,
                SpatialReferenceSrid = layer.SpatialReference.ToSrid(),
                OutputSrid = filterResult.CrsDefinition.Srid,
                SpatialFilter = filterResult.SpatialFilter,
                TemporalFilter = filterResult.TemporalFilter,
                IncludeNullGeometry = filterResult.IncludeNullGeometry
            };

            var allowStreaming = string.Equals(outputFormat, MediaTypes.GeoJson, StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(outputFormat, MediaTypes.Gml, StringComparison.OrdinalIgnoreCase);
            var useStreaming = allowStreaming &&
                               projectedProperties == null &&
                               effectiveLimit > StreamingThreshold &&
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

                // Avoid streaming for small result sets even when the requested limit is large.
                if (totalCount > StreamingThreshold)
                {
                    var hasMoreResults = totalCount > (effectiveOffset + effectiveLimit);
                    stopwatch.Stop();
                    var estimatedReturned = (int)Math.Min(effectiveLimit, Math.Max(0, totalCount - effectiveOffset));
                    OgcFeaturesLog.ItemsQueryCompleted(_logger, collectionId, estimatedReturned, totalCount, stopwatch.Elapsed.TotalMilliseconds);
                    HonuaTelemetry.SetSuccess(featureActivity, estimatedReturned);

                    var streamBaseUrl = BaseUrlResolver.GetBaseUrl(context);
                    var streamBasePath = $"{streamBaseUrl}/ogc/features/collections/{collectionId}/items";
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
                            filterResult.CrsDefinition.Uri,
                            cancellationToken);
                    }

                    return new StreamingItemsResult(
                        _streamingFeatureStore,
                        layer,
                        query,
                        collectionId,
                        filterResult.CrsDefinition.AxisOrder,
                        _geometryServices,
                        projectedProperties,
                        outputFormat,
                        streamLinks,
                        totalCount,
                        filterResult.CrsDefinition.Uri,
                        cancellationToken);
                }
            }

            var result = await _featureReader.QueryAsync(layerId, query, cancellationToken);
            var features = result.Items
                .Select(feature =>
                {
                    var links = OgcFeaturesUtilities.BuildFeatureLinks(
                        request,
                        collectionId,
                        FormattableString.Invariant($"{feature.Id}"),
                        outputFormat);
                    return ToOgcFeature(
                        feature,
                        filterResult.CrsDefinition.AxisOrder,
                        _geometryServices,
                        projectedProperties,
                        links);
                })
                .ToArray();
            stopwatch.Stop();
            OgcFeaturesLog.ItemsQueryCompleted(_logger, collectionId, features.Length, result.TotalCount, stopwatch.Elapsed.TotalMilliseconds);
            HonuaTelemetry.SetSuccess(featureActivity, features.Length);

            var baseUrl = BaseUrlResolver.GetBaseUrl(context);
            var basePath = $"{baseUrl}/ogc/features/collections/{collectionId}/items";

            var links = BuildItemsLinks(
                request,
                collectionId,
                basePath,
                outputFormat,
                effectiveLimit,
                effectiveOffset,
                result.HasMoreResults);

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
            var query = new FeatureQuery
            {
                ObjectIds = ImmutableArray.Create(objectId),
                Limit = 1,
                SpatialReferenceSrid = layer.SpatialReference.ToSrid(),
                OutputSrid = crsDefinition.Srid
            };
            var queryResult = await _featureReader.QueryAsync(layerId, query, cancellationToken);
            stopwatch.Stop();
            OgcFeaturesLog.ItemQueryCompleted(_logger, collectionId, featureId, stopwatch.Elapsed.TotalMilliseconds);
            if (queryResult.Items.IsDefaultOrEmpty)
            {
                return StandardErrorHelpers.CreateNotFound(context, $"Feature '{featureId}' not found.");
            }

            var feature = queryResult.Items[0];
            var featureLinks = OgcFeaturesUtilities.BuildFeatureLinks(request, collectionId, featureId, outputFormat);
            var ogcFeature = ToOgcFeature(feature, crsDefinition.AxisOrder, _geometryServices, null, featureLinks);

            context.Response.Headers["Content-Crs"] = FormatContentCrs(crsDefinition.Uri);

            // Compute ETag for the feature so clients can use If-Match on subsequent PUT/PATCH
            var featureETag = _etagService.ComputeETag(ogcFeature, OgcJsonContext.Default.GeoJsonFeature);
            context.Response.Headers.ETag = featureETag;

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
        ImmutableHashSet<string>? projectedProperties = null,
        ImmutableArray<Link>? links = null)
    {
        var geometry = geometryServices.ConvertWkbToSimpleGeometry(feature.Geometry, axisOrder);
        Dictionary<string, object?> properties;
        if (projectedProperties == null)
        {
            properties = feature.Attributes.ToDictionary(
                static kvp => kvp.Key,
                static kvp => kvp.Value,
                StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            properties = feature.Attributes
                .Where(kvp => projectedProperties.Contains(kvp.Key))
                .ToDictionary(
                    static kvp => kvp.Key,
                    static kvp => kvp.Value,
                    StringComparer.OrdinalIgnoreCase);
        }

        return new GeoJsonFeature
        {
            Type = "Feature",
            Id = feature.Id,
            Geometry = geometry,
            Properties = properties,
            Links = links
        };
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
            href: $"{baseUrl}/ogc/features/collections/{collectionId}/queryables",
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

    private static bool TryParseIds(
        string? rawIds,
        out ImmutableArray<long>? objectIds,
        out string? error)
    {
        objectIds = null;
        error = null;

        if (string.IsNullOrWhiteSpace(rawIds))
        {
            return true;
        }

        var tokens = rawIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            error = "Parameter 'ids' must contain at least one ID value.";
            return false;
        }

        var ids = ImmutableArray.CreateBuilder<long>(tokens.Length);
        var seen = new HashSet<long>();
        foreach (var token in tokens)
        {
            if (!long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || id <= 0)
            {
                error = $"Invalid ids value '{token}'.";
                return false;
            }

            if (seen.Add(id))
            {
                ids.Add(id);
            }
        }

        objectIds = ids.ToImmutable();
        return true;
    }

    private static bool TryParseProperties(
        string? rawProperties,
        LayerDefinition layer,
        out ImmutableArray<string>? properties,
        out string? error)
    {
        properties = null;
        error = null;

        if (string.IsNullOrWhiteSpace(rawProperties))
        {
            return true;
        }

        if (string.Equals(rawProperties.Trim(), "*", StringComparison.Ordinal))
        {
            return true;
        }

        var tokens = rawProperties.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            error = "Parameter 'properties' must contain at least one field name.";
            return false;
        }

        var fieldsByName = layer.AttributeFields
            .ToDictionary(field => field.Name, StringComparer.OrdinalIgnoreCase);

        var selected = ImmutableArray.CreateBuilder<string>(tokens.Length);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var token in tokens)
        {
            if (!IsSimpleFieldName(token))
            {
                error = $"Invalid properties field '{token}'.";
                return false;
            }

            if (!fieldsByName.TryGetValue(token, out var field))
            {
                error = $"Unknown properties field '{token}'.";
                return false;
            }

            if (seen.Add(field.Name))
            {
                selected.Add(field.Name);
            }
        }

        properties = selected.ToImmutable();
        return true;
    }

    private static bool TryParseSortBy(
        string? rawSortBy,
        LayerDefinition layer,
        out ImmutableArray<OrderByClause>? orderBy,
        out string? error)
    {
        orderBy = null;
        error = null;

        if (string.IsNullOrWhiteSpace(rawSortBy))
        {
            return true;
        }

        var tokens = rawSortBy.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            error = "Parameter 'sortby' must contain at least one field expression.";
            return false;
        }

        var normalized = new List<string>(tokens.Length);
        foreach (var rawToken in tokens)
        {
            var token = rawToken.Trim();
            if (token.Length == 0)
            {
                continue;
            }

            var ascending = true;
            if (token[0] is '+' or '-')
            {
                ascending = token[0] != '-';
                token = token[1..].Trim();
            }

            if (token.Length == 0)
            {
                error = "Invalid sortby expression.";
                return false;
            }

            var parts = token.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 2)
            {
                error = $"Invalid sortby expression '{rawToken}'.";
                return false;
            }

            var field = parts[0];
            if (!IsSimpleFieldName(field))
            {
                error = $"Invalid sortby field '{field}'.";
                return false;
            }

            if (parts.Length == 2)
            {
                if (parts[1].Equals("DESC", StringComparison.OrdinalIgnoreCase))
                {
                    ascending = false;
                }
                else if (parts[1].Equals("ASC", StringComparison.OrdinalIgnoreCase))
                {
                    ascending = true;
                }
                else
                {
                    error = $"Invalid sort direction '{parts[1]}' in sortby.";
                    return false;
                }
            }

            normalized.Add($"{field} {(ascending ? "ASC" : "DESC")}");
        }

        if (normalized.Count == 0)
        {
            error = "Parameter 'sortby' must contain at least one field expression.";
            return false;
        }

        try
        {
            orderBy = OrderByParsing.ParseFeatureServerOrderBy(
                string.Join(",", normalized),
                layer,
                _sortByCoreFields);
            return true;
        }
        catch (InvalidOperationException ex)
        {
            error = ex.Message.Replace("orderByFields", "sortby", StringComparison.OrdinalIgnoreCase);
            return false;
        }
    }

    private static bool IsSimpleFieldName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        foreach (var ch in value)
        {
            if (!(char.IsLetterOrDigit(ch) || ch == '_'))
            {
                return false;
            }
        }

        return true;
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
        ImmutableHashSet<string>? projectedProperties,
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
            var featureLinks = OgcFeaturesUtilities.BuildFeatureLinks(
                context.Request,
                collectionId,
                FormattableString.Invariant($"{feature.Id}"),
                outputFormat);
            var ogcFeature = ToOgcFeature(feature, axisOrder, geometryServices, projectedProperties, featureLinks);
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
                _collectionId,
                _axisOrder,
                _geometryServices,
                _projectedProperties,
                _outputFormat,
                _links,
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
        private readonly string _crsUri;
        private readonly CancellationToken _requestCancellationToken;

        public StreamingGmlItemsResult(
            IStreamingFeatureStore streamingFeatureStore,
            LayerDefinition layer,
            FeatureQuery query,
            long numberMatched,
            string crsUri,
            CancellationToken requestCancellationToken)
        {
            _streamingFeatureStore = streamingFeatureStore;
            _layer = layer;
            _query = query;
            _numberMatched = numberMatched;
            _crsUri = crsUri;
            _requestCancellationToken = requestCancellationToken;
        }

        public async Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.ContentType = MediaTypes.Gml;
            httpContext.Response.Headers["Content-Crs"] = FormatContentCrs(_crsUri);
            httpContext.Response.Headers["OGC-NumberMatched"] = _numberMatched.ToString(CultureInfo.InvariantCulture);
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
                cancellationToken);

            await httpContext.Response.BodyWriter.CompleteAsync();
        }
    }

}
