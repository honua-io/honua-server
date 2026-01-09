// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Serialization.Metadata;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Core.Queries.Filters;
using Honua.Core.Queries.Filters.Cql2;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Validation;
using Honua.Server.Features.Ogc.Common;
using Honua.Server.Features.OgcFeatures.Models;
using Honua.Server.Features.OgcFeatures.Services;
using Microsoft.AspNetCore.Http.HttpResults;

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
    private readonly IResourceValidator _resourceValidator = dependencies.ResourceValidator;
    private readonly ICommonQueryValidator _queryValidator = dependencies.QueryValidator;
    private readonly OgcFilterProcessor _filterProcessor = dependencies.FilterProcessor;
    private readonly ILogger<OgcFeaturesQueryHandler> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

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
        var request = context.Request;

        try
        {
            // Use centralized resource validation
            var collectionResult = await _resourceValidator.ValidateCollectionAsync(collectionId, cancellationToken);
            if (!collectionResult.IsValid)
            {
                return StandardErrorHelpers.CreateNotFound(context, collectionResult.ErrorMessage ?? $"Collection '{collectionId}' not found.");
            }
            var layer = collectionResult.Resource!;
            var layerId = layer.Id;
            var accessError = AccessPolicyHelpers.RequireLayerAccess(context, layer);
            if (accessError != null)
            {
                return accessError;
            }

            var validationError = OgcFeaturesUtilities.ValidateItemsQueryParameters(request, layer);
            if (validationError is not null)
            {
                return StandardErrorHelpers.CreateBadRequest(context, validationError.Value ?? "Invalid query parameters.");
            }

            if (!OgcCommonUtilities.TryGetOutputFormat(f, context, isFeatureContent: true, out var outputFormat, out var formatError))
            {
                return CreateFormatError(context, formatError);
            }

            // Use filter processor for comprehensive filter handling
            var filterResult = await _filterProcessor.ProcessFiltersAsync(
                request, layer, filter, bbox, datetime, crs);
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
                TemporalFilter = filterResult.TemporalFilter
            };

            var result = await _featureReader.QueryAsync(layerId, query, cancellationToken);
            var features = result.Items
                .Select(feature =>
                {
                    var links = BuildFeatureLinks(
                        request,
                        collectionId,
                        FormattableString.Invariant($"{feature.Id}"),
                        outputFormat);
                    return ToOgcFeature(feature, filterResult.CrsDefinition.AxisOrder, links);
                })
                .ToArray();

            var baseUrl = $"{request.Scheme}://{request.Host}";
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

            return FormatFeatureResponse(response, OgcJsonContext.Default.FeatureCollection, outputFormat, "Features");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.ItemsQueryFailed(_logger, collectionId, ex);
            return ProtocolErrorWriter.CreateErrorResult(context, 500, "Internal server error", "An error occurred while retrieving features.");
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
        var request = context.Request;

        try
        {
            // Use centralized resource validation
            var collectionResult = await _resourceValidator.ValidateCollectionAsync(collectionId, cancellationToken);
            if (!collectionResult.IsValid)
            {
                return StandardErrorHelpers.CreateNotFound(context, collectionResult.ErrorMessage ?? $"Collection '{collectionId}' not found.");
            }
            var layer = collectionResult.Resource!;
            var layerId = layer.Id;
            var accessError = AccessPolicyHelpers.RequireLayerAccess(context, layer);
            if (accessError != null)
            {
                return accessError;
            }

            if (!long.TryParse(featureId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var objectId))
            {
                return StandardErrorHelpers.CreateNotFound(context, $"Feature '{featureId}' not found.");
            }

            if (!OgcCommonUtilities.TryGetOutputFormat(f, context, isFeatureContent: true, out var outputFormat, out var formatError))
            {
                return CreateFormatError(context, formatError);
            }

            var crsResult = OgcCrsResolver.TryResolveCrs(crs);
            if (!crsResult.IsSuccess)
            {
                return StandardErrorHelpers.CreateBadRequest(context, crsResult.ErrorMessage!);
            }

            var feature = await _featureReader.GetAsync(layerId, objectId, cancellationToken);
            if (feature == null)
            {
                return StandardErrorHelpers.CreateNotFound(context, $"Feature '{featureId}' not found.");
            }

            var featureLinks = BuildFeatureLinks(request, collectionId, featureId, outputFormat);
            var ogcFeature = ToOgcFeature(feature.Value, crsResult.CrsDefinition.AxisOrder, featureLinks);

            context.Response.Headers["Content-Crs"] = FormatContentCrs(crsResult.CrsDefinition.Uri);

            if (string.Equals(outputFormat, MediaTypes.Gml, StringComparison.OrdinalIgnoreCase))
            {
                var gml = BuildGmlSingleFeature(ogcFeature);
                return Results.Text(gml, MediaTypes.Gml);
            }

            return FormatFeatureResponse(ogcFeature, OgcJsonContext.Default.GeoJsonFeature, outputFormat, "Feature");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.ItemQueryFailed(_logger, collectionId, ex);
            return ProtocolErrorWriter.CreateErrorResult(context, 500, "Internal server error", "An error occurred while retrieving the feature.");
        }
    }

    private static GeoJsonFeature ToOgcFeature(
        Feature feature,
        OgcFeaturesUtilities.AxisOrder axisOrder,
        ImmutableArray<Link>? links = null)
    {
        var geometry = OgcFeaturesGeometryServices.ConvertWkbToSimpleGeometry(feature.Geometry, axisOrder);
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
        var baseUrl = $"{request.Scheme}://{request.Host}";
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

    private static IResult CreateFormatError(HttpContext context, IResult? formatError)
    {
        if (formatError is BadRequest<string> badRequest)
        {
            return StandardErrorHelpers.CreateBadRequest(context, badRequest.Value ?? "Invalid format.");
        }

        if (formatError is IStatusCodeHttpResult statusCodeResult && statusCodeResult.StatusCode.HasValue)
        {
            return ProtocolErrorWriter.CreateErrorResult(
                context,
                statusCodeResult.StatusCode.Value,
                "Not Acceptable",
                "Requested format is not acceptable.");
        }

        return StandardErrorHelpers.CreateBadRequest(context, "Invalid format.");
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

    private static partial class Log
    {
        [LoggerMessage(EventId = 5210, Level = LogLevel.Error, Message = "OGC items query failed for collection {CollectionId}")]
        public static partial void ItemsQueryFailed(ILogger logger, string collectionId, Exception exception);

        [LoggerMessage(EventId = 5211, Level = LogLevel.Error, Message = "OGC item query failed for collection {CollectionId}")]
        public static partial void ItemQueryFailed(ILogger logger, string collectionId, Exception exception);
    }
}
