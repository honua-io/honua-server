// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Honua.Core.Configuration;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Tiles;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Core.Queries.Filters;
using Honua.Server.Features.Protocols.GeoServices.FeatureServer.Models;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Validation;
using Honua.ServiceDefaults;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Honua.Server.Features.Protocols.GeoServices.FeatureServer;

internal static partial class FeatureServerEndpoints
{
    private static async Task<IResult> HandleQueryH3Get(
        string serviceId,
        int layerId,
        HttpContext context)
    {
        var queryValidator = context.RequestServices.GetRequiredService<ICommonQueryValidator>();
        if (!TryValidateAllowedParameters(context.Request.Query, queryValidator, AllowedQueryParameters.QueryH3, out var error))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid query parameters",
                [error ?? "Invalid query parameter."]);
        }

        var values = ToCaseInsensitiveDictionary(context.Request.Query);
        return await HandleQueryH3Core(serviceId, layerId, values, context);
    }

    private static async Task<IResult> HandleQueryH3Post(
        string serviceId,
        int layerId,
        HttpContext context)
    {
        var queryValidator = context.RequestServices.GetRequiredService<ICommonQueryValidator>();
        if (!TryValidateAllowedParameters(context.Request.Query, queryValidator, AllowedQueryParameters.QueryH3, out var error))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid query parameters",
                [error ?? "Invalid query parameter."]);
        }

        var cancellationToken = GetTimeoutAwareCancellationToken(context);
        var (values, readError) = await TryReadRequestValuesAsync(context.Request, cancellationToken);
        if (values == null)
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid request body",
                [readError ?? "Invalid request body."]);
        }

        if (!TryValidateAllowedParameters(values, queryValidator, AllowedQueryParameters.QueryH3, out error))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid query parameters",
                [error ?? "Invalid query parameter."]);
        }

        return await HandleQueryH3Core(serviceId, layerId, values, context);
    }

    private static async Task<IResult> HandleQueryH3Core(
        string serviceId,
        int layerId,
        IReadOnlyDictionary<string, StringValues> values,
        HttpContext context)
    {
        using var activity = HonuaTelemetry.ActivitySource.StartActivity("featureserver.queryH3");
        activity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.FeatureServer);
        activity?.SetTag(HonuaTelemetry.Tags.Operation, "queryH3");
        activity?.SetTag(HonuaTelemetry.Tags.ServiceId, serviceId);
        activity?.SetTag(HonuaTelemetry.Tags.LayerId, layerId);

        var cancellationToken = GetTimeoutAwareCancellationToken(context);

        // Parse resolution (required) — validate input before checking server capabilities
        var resolutionStr = GetValueString(values, "resolution");
        if (string.IsNullOrWhiteSpace(resolutionStr) ||
            !int.TryParse(resolutionStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var resolution))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "resolution parameter is required",
                [$"resolution must be an integer between {H3AggregationQuery.MinResolution} and {H3AggregationQuery.MaxResolution}."]);
        }

        if (resolution < H3AggregationQuery.MinResolution || resolution > H3AggregationQuery.MaxResolution)
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid resolution",
                [$"resolution must be between {H3AggregationQuery.MinResolution} and {H3AggregationQuery.MaxResolution}."]);
        }

        activity?.SetTag("h3.resolution", resolution);

        // Parse optional kRingDistance — validate all client inputs before checking
        // server capabilities so clients get actionable 400 errors first
        int? kRingDistance = null;
        var kRingStr = GetValueString(values, "kRingDistance");
        if (!string.IsNullOrWhiteSpace(kRingStr))
        {
            if (!int.TryParse(kRingStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var kRing) || kRing < 0)
            {
                return StandardErrorHelpers.CreateBadRequest(context,
                    "Invalid kRingDistance",
                    ["kRingDistance must be a non-negative integer."]);
            }

            if (kRing > H3AggregationQuery.MaxKRingDistance)
            {
                return StandardErrorHelpers.CreateBadRequest(context,
                    "Invalid kRingDistance",
                    [$"kRingDistance must not exceed {H3AggregationQuery.MaxKRingDistance}."]);
            }

            kRingDistance = kRing;
        }

        // Parse optional outStatistics
        ImmutableArray<StatisticDefinition>? outStatistics = null;
        var outStatsJson = GetValueString(values, "outStatistics");
        if (!string.IsNullOrWhiteSpace(outStatsJson))
        {
            // POST bodies with native JSON arrays are flattened by TryReadRequestValuesAsync
            // into comma-separated StringValues; re-wrap as a JSON array for TryParseOutStatistics
            if (!outStatsJson.TrimStart().StartsWith('['))
            {
                outStatsJson = $"[{outStatsJson}]";
            }

            if (!TryParseOutStatistics(outStatsJson, out var outStats, out var statsError))
            {
                return StandardErrorHelpers.CreateBadRequest(context,
                    "Invalid outStatistics parameter",
                    [statsError ?? "outStatistics must be valid JSON."]);
            }

            outStatistics = outStats;
        }

        // Validate service/layer access
        var resourceValidator = context.RequestServices.GetRequiredService<IResourceValidator>();
        var validationResult = await FeatureServerResourceValidationHelpers.ValidateServiceLayerAsync(
            resourceValidator,
            serviceId,
            layerId,
            context,
            logger: null,
            cancellationToken: cancellationToken);
        if (!validationResult.IsValid)
        {
            return validationResult.ErrorResult!;
        }

        var service = validationResult.Service!;
        var layer = validationResult.Layer!;
        var accessError = AccessPolicyHelpers.RequireLayerAccess(context, layer, service);
        if (accessError != null)
        {
            return accessError;
        }

        // Check h3-pg extension availability
        var h3Error = await H3CapabilityHelpers.ValidateH3AvailabilityAsync(context, cancellationToken);
        if (h3Error is not null)
        {
            return h3Error;
        }

        var queryLimits = context.RequestServices.GetRequiredService<IOptions<LimitsOptions>>().Value.Query;
        var h3Query = new H3AggregationQuery
        {
            Resolution = resolution,
            KRingDistance = kRingDistance,
            OutStatistics = outStatistics,
            MaxCells = queryLimits.MaxH3CellsPerQuery
        };

        var where = GetValueString(values, "where");
        SqlFragment? sqlFilter = null;
        if (!string.IsNullOrWhiteSpace(where))
        {
            var filterService = context.RequestServices.GetRequiredService<IFilterExpressionService>();
            var parseResult = filterService.Parse(FilterLanguage.ArcGisSql, where);
            if (!parseResult.IsSuccess)
            {
                return StandardErrorHelpers.CreateBadRequest(context,
                    ErrorMessages.Validation.InvalidParameter,
                    [parseResult.ErrorMessage ?? "Invalid filter syntax."]);
            }

            if (parseResult.Expression != null)
            {
                var translationResult = filterService.Translate(parseResult.Expression, layer);
                if (!translationResult.IsSuccess)
                {
                    return StandardErrorHelpers.CreateBadRequest(context,
                        ErrorMessages.Validation.InvalidParameter,
                        [translationResult.ErrorMessage ?? "Invalid filter syntax."]);
                }

                sqlFilter = translationResult.SqlFilter;
            }
        }

        var featureQuery = new FeatureQuery
        {
            Where = where,
            SqlFilter = sqlFilter,
            SpatialReferenceSrid = layer.SpatialReference.ToSrid()
        };

        var featureReader = context.RequestServices.GetRequiredService<IFeatureReader>();
        var rows = await featureReader.QueryH3Async(layer.Id, featureQuery, h3Query, cancellationToken);

        var responseFeatures = rows.Select(row =>
        {
            var attributes = new Dictionary<string, object?>(row);
            GeoServicesGeometry? geometry = null;

            // Extract cellGeometry (GeoJSON Polygon) and convert to Esri Rings format.
            // H3 boundaries are always WGS 84 Polygons, so coordinates map directly.
            if (attributes.Remove("cellGeometry", out var cellGeoJson) && cellGeoJson is string geoJsonStr)
            {
                geometry = TryParseH3CellGeometry(geoJsonStr);
                if (geometry == null)
                {
                    // Parsing failed — preserve as attribute so the data is not silently lost
                    attributes["cellGeometry"] = cellGeoJson;
                }
            }

            return new GeoServicesFeature
            {
                Attributes = attributes,
                Geometry = geometry,
                IncludeGeometry = true
            };
        }).ToArray();

        var response = new QueryResponse
        {
            Features = responseFeatures
        };

        return Results.Json(response, FeatureServerJsonContext.Default.QueryResponse, contentType: "application/json");
    }

    private static async Task<IResult> HandleH3Tile(
        int layerId,
        int z,
        int x,
        int y,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        using var activity = HonuaTelemetry.ActivitySource.StartActivity("featureserver.h3Tile");
        activity?.SetTag(HonuaTelemetry.Tags.LayerId, layerId);
        activity?.SetTag("tile.z", z);
        activity?.SetTag("tile.x", x);
        activity?.SetTag("tile.y", y);

        var queryValidator = context.RequestServices.GetRequiredService<ICommonQueryValidator>();
        if (!TryValidateAllowedParameters(context.Request.Query, queryValidator, AllowedQueryParameters.H3Tiles, out var error))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid query parameters",
                [error ?? "Invalid query parameter."]);
        }

        var tileOptions = context.RequestServices.GetRequiredService<IOptions<TileOptions>>().Value;
        var tileLimits = context.RequestServices.GetRequiredService<IOptions<LimitsOptions>>().Value.Tiles;
        if (z < tileLimits.MinTileZoom || z > tileLimits.MaxTileZoom)
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                $"Zoom level {z} is outside supported range",
                [$"Supported zoom range is {tileLimits.MinTileZoom}-{tileLimits.MaxTileZoom}"]);
        }

        var maxIndex = 1 << z;
        if (x < 0 || y < 0 || x >= maxIndex || y >= maxIndex)
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                $"Invalid tile coordinates: x={x}, y={y}, z={z}");
        }

        // Parse resolution (default from zoom level)
        var queryValues = ToCaseInsensitiveDictionary(context.Request.Query);
        var resolutionStr = GetValueString(queryValues, "resolution");
        int resolution;
        if (!string.IsNullOrWhiteSpace(resolutionStr))
        {
            if (!int.TryParse(resolutionStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out resolution) ||
                resolution < H3AggregationQuery.MinResolution || resolution > H3AggregationQuery.MaxResolution)
            {
                return StandardErrorHelpers.CreateBadRequest(context,
                    "Invalid resolution",
                    [$"resolution must be between {H3AggregationQuery.MinResolution} and {H3AggregationQuery.MaxResolution}."]);
            }
        }
        else
        {
            // Default zoom-to-resolution mapping
            resolution = ZoomToH3Resolution(z);
        }

        activity?.SetTag("h3.resolution", resolution);

        // Validate layer access before checking server capabilities
        var layerValidation = await LayerValidationHelpers.ValidateLayerWithAccessAsync(
            context,
            layerId,
            requiredProtocol: ServiceProtocols.FeatureServer,
            cancellationToken: cancellationToken);
        if (!layerValidation.IsValid)
        {
            return layerValidation.ErrorResult!;
        }
        var layer = layerValidation.Layer!;

        // Check h3-pg extension availability (after auth, consistent with HandleQueryH3Core)
        var h3Error = await H3CapabilityHelpers.ValidateH3AvailabilityAsync(context, cancellationToken);
        if (h3Error is not null)
        {
            return h3Error;
        }

        var where = GetValueString(queryValues, "where");
        SqlFragment? sqlFilter = null;
        if (!string.IsNullOrWhiteSpace(where))
        {
            var filterService = context.RequestServices.GetRequiredService<IFilterExpressionService>();
            var parseResult = filterService.Parse(FilterLanguage.ArcGisSql, where);
            if (!parseResult.IsSuccess)
            {
                return StandardErrorHelpers.CreateBadRequest(context,
                    ErrorMessages.Validation.InvalidParameter,
                    [parseResult.ErrorMessage ?? "Invalid filter syntax."]);
            }

            if (parseResult.Expression != null)
            {
                var translationResult = filterService.Translate(parseResult.Expression, layer);
                if (!translationResult.IsSuccess)
                {
                    return StandardErrorHelpers.CreateBadRequest(context,
                        ErrorMessages.Validation.InvalidParameter,
                        [translationResult.ErrorMessage ?? "Invalid filter syntax."]);
                }

                sqlFilter = translationResult.SqlFilter;
            }
        }

        var query = new FeatureQuery
        {
            Where = where,
            SqlFilter = sqlFilter,
            SpatialReferenceSrid = layer.SpatialReference.ToSrid()
        };

        var tileProvider = context.RequestServices.GetRequiredService<ITileProvider>();
        var tileData = await tileProvider.GetH3MvtTileAsync(
            layerId,
            x,
            y,
            z,
            resolution,
            query,
            tileOptions,
            tileLimits,
            cancellationToken);

        if (tileData == null || tileData.Length == 0)
        {
            return Results.NoContent();
        }

        context.Response.Headers["Cache-Control"] = $"public, max-age={tileOptions.CacheMaxAge}";
        return Results.Bytes(tileData, "application/vnd.mapbox-vector-tile");
    }

    /// <summary>
    /// Maps zoom level to a reasonable default H3 resolution.
    /// Lower zoom = coarser cells, higher zoom = finer cells.
    /// </summary>
    private static int ZoomToH3Resolution(int zoom) => zoom switch
    {
        <= 2 => 1,
        3 => 2,
        4 => 3,
        5 => 3,
        6 => 4,
        7 => 4,
        8 => 5,
        9 => 5,
        10 => 6,
        11 => 7,
        12 => 7,
        13 => 8,
        14 => 9,
        15 => 9,
        16 => 10,
        17 => 11,
        18 => 11,
        _ => 12
    };

    /// <summary>
    /// Parses a GeoJSON Polygon string (H3 cell boundary) into an Esri-compatible
    /// <see cref="GeoServicesGeometry"/> with Rings. Returns null on parse failure.
    /// </summary>
    private static GeoServicesGeometry? TryParseH3CellGeometry(string geoJsonStr)
    {
        try
        {
            using var doc = JsonDocument.Parse(geoJsonStr);
            var root = doc.RootElement;

            if (!root.TryGetProperty("coordinates", out var coordinates))
            {
                return null;
            }

            // GeoJSON Polygon coordinates: [[[lng, lat], ...], ...] → Esri Rings: double[][][]
            var ringsCount = coordinates.GetArrayLength();
            var rings = new double[ringsCount][][];
            for (var r = 0; r < ringsCount; r++)
            {
                var ring = coordinates[r];
                var pointCount = ring.GetArrayLength();
                var points = new double[pointCount][];
                for (var p = 0; p < pointCount; p++)
                {
                    var point = ring[p];
                    points[p] = point.GetArrayLength() switch
                    {
                        >= 3 => [point[0].GetDouble(), point[1].GetDouble(), point[2].GetDouble()],
                        _ => [point[0].GetDouble(), point[1].GetDouble()]
                    };
                }
                rings[r] = points;
            }

            return new GeoServicesGeometry
            {
                Rings = rings,
                SpatialReference = new GeoServicesSpatialReference { Wkid = 4326 }
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
