// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Server.Features.Protocols.GeoServices.ImageServer.Models;
using Honua.Server.Features.Protocols.GeoServices.ImageServer.Services;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Services;
using Honua.ServiceDefaults;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace Honua.Server.Features.Protocols.GeoServices.ImageServer.Handlers;

/// <summary>
/// Handler for the Esri Image Server <c>query</c> endpoint, which exposes the
/// raster catalog as Esri-compatible features. Mirrors the FeatureServer query
/// envelope so the Esri client SDKs can drive both protocols with shared code.
/// </summary>
internal sealed class ImageServerCatalogQueryHandler
{
    /// <summary>Maximum number of records the catalog query will ever return per page.</summary>
    private const int MaxRecordCount = 1000;

    /// <summary>Default page size when the caller does not specify <c>resultRecordCount</c>.</summary>
    private const int DefaultRecordCount = MaxRecordCount;

    private readonly ILayerCatalog _layerCatalog;
    private readonly IImageServerCatalogReader _catalogReader;
    private readonly ILogger<ImageServerCatalogQueryHandler> _logger;

    public ImageServerCatalogQueryHandler(
        ILayerCatalog layerCatalog,
        IImageServerCatalogReader catalogReader,
        ILogger<ImageServerCatalogQueryHandler> logger)
    {
        _layerCatalog = layerCatalog ?? throw new ArgumentNullException(nameof(layerCatalog));
        _catalogReader = catalogReader ?? throw new ArgumentNullException(nameof(catalogReader));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Queries the raster catalog for the specified layer using the supplied request values.
    /// </summary>
    public async Task<IResult> QueryCatalogAsync(
        HttpContext context,
        int layerId,
        IReadOnlyDictionary<string, StringValues> values,
        CancellationToken cancellationToken)
    {
        using var scope = HonuaTelemetryScope.StartFeature(
            "query-catalog",
            HonuaTelemetry.Protocols.ImageServer,
            layerId.ToString(CultureInfo.InvariantCulture));
        scope.WithTag(HonuaTelemetry.Tags.Operation, "query-catalog");

        try
        {
            var layer = await _layerCatalog.GetLayerAsync(layerId, cancellationToken);
            if (layer is null)
            {
                ImageServerLog.LayerNotFound(_logger, layerId);
                return StandardErrorHelpers.CreateNotFound(context, "Layer not found.");
            }

            if (!TryParseQuery(values, out var query, out var parseError))
            {
                ImageServerLog.InvalidCatalogQueryParameters(_logger, layerId, parseError ?? "Invalid query parameter.");
                return StandardErrorHelpers.CreateBadRequest(
                    context,
                    parseError ?? "Invalid query parameter.");
            }

            var editionError = ImageServerMosaicHelpers.RequireTemporalMosaicAccess(context, query.Time);
            if (editionError != null)
            {
                return editionError;
            }

            ImageServerCatalogPage page;
            try
            {
                page = await _catalogReader.ReadAsync(layerId, query, cancellationToken);
            }
            catch (ImageServerCatalogFilterException ex)
            {
                ImageServerLog.InvalidCatalogQueryParameters(_logger, layerId, ex.Message);
                return StandardErrorHelpers.CreateBadRequest(context, ex.Message);
            }

            ImageServerLog.CatalogQueryCompleted(_logger, layerId, page.Items.Count, page.TotalCount);
            scope.SetSuccess(page.Items.Count);

            return BuildResponse(page, query);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            ImageServerLog.CatalogQueryFailed(_logger, ex, layerId);
            scope.RecordException(ex);
            return StandardErrorHelpers.CreateInternalServerError(
                context,
                "An error occurred while querying the raster catalog.");
        }
    }

    private static bool TryParseQuery(
        IReadOnlyDictionary<string, StringValues> values,
        out ImageServerCatalogQuery query,
        out string? error)
    {
        query = new ImageServerCatalogQuery();
        error = null;

        var format = GetString(values, "f");
        if (!IsSupportedFormat(format))
        {
            error = "Only JSON format is supported. Use f=json or f=pjson.";
            return false;
        }

        var where = GetString(values, "where");
        if (where is { Length: > 2000 })
        {
            error = "where clause exceeds the maximum length of 2000 characters.";
            return false;
        }

        var objectIds = ParseObjectIds(GetString(values, "objectIds"));

        var outSr = GetString(values, "outSR");
        int? outputSrid = null;
        if (!string.IsNullOrWhiteSpace(outSr))
        {
            outputSrid = SpatialReferenceHelpers.TryParseSrid(outSr);
            if (!outputSrid.HasValue)
            {
                error = $"Invalid outSR '{outSr}'.";
                return false;
            }
        }

        if (!ImageServerMosaicHelpers.TryParseTime(GetString(values, "time"), out var time, out var timeError))
        {
            error = timeError;
            return false;
        }

        if (!TryParseInt(GetString(values, "resultOffset"), out var offset, defaultValue: 0))
        {
            error = "resultOffset must be a non-negative integer.";
            return false;
        }
        if (offset < 0)
        {
            error = "resultOffset must be a non-negative integer.";
            return false;
        }

        if (!TryParseInt(GetString(values, "resultRecordCount"), out var limit, defaultValue: DefaultRecordCount))
        {
            error = "resultRecordCount must be a positive integer.";
            return false;
        }
        if (limit <= 0)
        {
            error = "resultRecordCount must be a positive integer.";
            return false;
        }

        if (limit > MaxRecordCount)
        {
            limit = MaxRecordCount;
        }

        var returnGeometry = ParseBool(GetString(values, "returnGeometry"), defaultValue: true);
        var returnIdsOnly = ParseBool(GetString(values, "returnIdsOnly"), defaultValue: false);
        var returnCountOnly = ParseBool(GetString(values, "returnCountOnly"), defaultValue: false);
        var returnExtentOnly = ParseBool(GetString(values, "returnExtentOnly"), defaultValue: false);

        query = new ImageServerCatalogQuery
        {
            Where = where,
            ObjectIds = objectIds,
            OutputSrid = outputSrid,
            Time = time,
            Offset = offset,
            Limit = limit,
            ReturnGeometry = returnGeometry,
            ReturnIdsOnly = returnIdsOnly,
            ReturnCountOnly = returnCountOnly,
            ReturnExtentOnly = returnExtentOnly,
        };
        return true;
    }

    private static IResult BuildResponse(ImageServerCatalogPage page, ImageServerCatalogQuery query)
    {
        if (query.ReturnCountOnly)
        {
            var countResponse = new CatalogCountResponse { Count = page.TotalCount };
            return Results.Json(countResponse, ImageServerJsonContext.Default.CatalogCountResponse);
        }

        if (query.ReturnIdsOnly)
        {
            var idsResponse = new CatalogObjectIdsResponse
            {
                ObjectIdFieldName = "OBJECTID",
                ObjectIds = page.Items.Select(item => item.ObjectId).OrderBy(id => id).ToArray(),
            };
            return Results.Json(idsResponse, ImageServerJsonContext.Default.CatalogObjectIdsResponse);
        }

        if (query.ReturnExtentOnly)
        {
            var extentSrid = page.AggregateExtent?.Srid ?? page.NativeSrid ?? 4326;
            var spatialReference = new SpatialReference
            {
                Wkid = extentSrid,
                LatestWkid = extentSrid,
            };

            var extentResponse = new CatalogExtentResponse
            {
                Extent = new ImageServerExtent
                {
                    XMin = page.AggregateExtent?.XMin ?? 0,
                    YMin = page.AggregateExtent?.YMin ?? 0,
                    XMax = page.AggregateExtent?.XMax ?? 0,
                    YMax = page.AggregateExtent?.YMax ?? 0,
                    SpatialReference = spatialReference,
                },
            };
            return Results.Json(extentResponse, ImageServerJsonContext.Default.CatalogExtentResponse);
        }

        // Keep the response envelope in the native SRID because the MVP does not
        // reproject footprints. Stamping the envelope with the requested outSR while
        // leaving geometries in the native SRID would mislead clients that project
        // by the envelope's declared spatial reference.
        var responseSrid = page.AggregateExtent?.Srid
            ?? page.NativeSrid
            ?? 4326;

        var response = new CatalogQueryResponse
        {
            ObjectIdFieldName = "OBJECTID",
            GlobalIdFieldName = string.Empty,
            GeometryType = "esriGeometryPolygon",
            SpatialReference = new SpatialReference
            {
                Wkid = responseSrid,
                LatestWkid = responseSrid,
            },
            Fields = BuildCatalogFields(),
            Features = page.Items.Select(item => BuildFeature(item, query)).ToArray(),
            ExceededTransferLimit = page.ExceededTransferLimit,
        };

        return Results.Json(response, ImageServerJsonContext.Default.CatalogQueryResponse);
    }

    private static CatalogQueryFeature BuildFeature(ImageServerCatalogItem item, ImageServerCatalogQuery query)
    {
        var attributes = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["OBJECTID"] = item.ObjectId,
            ["Name"] = item.Name,
            ["MinPS"] = item.MinPixelSize,
            ["MaxPS"] = item.MaxPixelSize,
            ["LowPS"] = item.LowPixelSize,
            ["HighPS"] = item.HighPixelSize,
            ["CenterX"] = item.CenterX,
            ["CenterY"] = item.CenterY,
            ["ZOrder"] = item.ZOrder,
            ["Shape_Length"] = item.ShapeLength,
            ["Shape_Area"] = item.ShapeArea,
            ["BandCount"] = item.BandCount,
            ["PixelType"] = item.PixelType,
            ["AcquisitionDate"] = item.AcquisitionDate?.ToUnixTimeMilliseconds(),
            ["CreatedAt"] = item.CreatedAt.ToUnixTimeMilliseconds(),
        };

        CatalogQueryGeometry? geometry = null;
        if (query.ReturnGeometry && item.FootprintRings is { Length: > 0 })
        {
            // Stamp the geometry with the actual coordinate SRID. The MVP does not
            // reproject footprints, so the rings are still in the raster's native SRID
            // even when the caller passes outSR.
            var geometrySrid = item.FootprintSrid ?? 4326;
            geometry = new CatalogQueryGeometry
            {
                Rings = item.FootprintRings,
                SpatialReference = new SpatialReference
                {
                    Wkid = geometrySrid,
                    LatestWkid = geometrySrid,
                },
            };
        }

        return new CatalogQueryFeature
        {
            Attributes = attributes,
            Geometry = geometry,
        };
    }

    private static Field[] BuildCatalogFields()
    {
        return
        [
            new Field { Name = "OBJECTID", Type = "esriFieldTypeOID", Alias = "OBJECTID" },
            new Field { Name = "Name", Type = "esriFieldTypeString", Alias = "Name", Nullable = true },
            new Field { Name = "MinPS", Type = "esriFieldTypeDouble", Alias = "MinPS" },
            new Field { Name = "MaxPS", Type = "esriFieldTypeDouble", Alias = "MaxPS" },
            new Field { Name = "LowPS", Type = "esriFieldTypeDouble", Alias = "LowPS" },
            new Field { Name = "HighPS", Type = "esriFieldTypeDouble", Alias = "HighPS" },
            new Field { Name = "CenterX", Type = "esriFieldTypeDouble", Alias = "CenterX" },
            new Field { Name = "CenterY", Type = "esriFieldTypeDouble", Alias = "CenterY" },
            new Field { Name = "ZOrder", Type = "esriFieldTypeInteger", Alias = "ZOrder" },
            new Field { Name = "Shape_Length", Type = "esriFieldTypeDouble", Alias = "Shape_Length" },
            new Field { Name = "Shape_Area", Type = "esriFieldTypeDouble", Alias = "Shape_Area" },
            new Field { Name = "BandCount", Type = "esriFieldTypeInteger", Alias = "BandCount" },
            new Field { Name = "PixelType", Type = "esriFieldTypeString", Alias = "PixelType", Nullable = true },
            new Field { Name = "AcquisitionDate", Type = "esriFieldTypeDate", Alias = "AcquisitionDate", Nullable = true },
            new Field { Name = "CreatedAt", Type = "esriFieldTypeDate", Alias = "CreatedAt" },
        ];
    }

    private static List<long>? ParseObjectIds(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var trimmed = raw.Trim();
        if (trimmed.StartsWith('['))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                {
                    return null;
                }

                var ids = new List<long>(doc.RootElement.GetArrayLength());
                foreach (var element in doc.RootElement.EnumerateArray())
                {
                    if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var id))
                    {
                        ids.Add(id);
                    }
                    else if (element.ValueKind == JsonValueKind.String &&
                             long.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var stringId))
                    {
                        ids.Add(stringId);
                    }
                }
                return ids.Count == 0 ? null : ids;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        var parts = trimmed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return null;
        }

        var parsed = new List<long>(parts.Length);
        foreach (var part in parts)
        {
            if (long.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            {
                parsed.Add(id);
            }
        }
        return parsed.Count == 0 ? null : parsed;
    }

    private static bool IsSupportedFormat(string? format)
        => string.IsNullOrWhiteSpace(format) ||
           string.Equals(format, "json", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(format, "pjson", StringComparison.OrdinalIgnoreCase);

    private static bool TryParseInt(string? value, out int parsed, int defaultValue)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            parsed = defaultValue;
            return true;
        }

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed);
    }

    private static bool ParseBool(string? value, bool defaultValue)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        return value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("1", StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetString(IReadOnlyDictionary<string, StringValues> values, string key)
        => values.TryGetValue(key, out var raw) ? raw.ToString() : null;
}
