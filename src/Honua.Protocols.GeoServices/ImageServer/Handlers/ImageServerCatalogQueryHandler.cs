// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.Protocols.GeoServices.ImageServer.Models;
using Honua.Protocols.GeoServices.ImageServer.Services;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Services;
using Honua.ServiceDefaults;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace Honua.Protocols.GeoServices.ImageServer.Handlers;

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

    private readonly IMetadataV2GraphProvider _graphProvider;
    private readonly IImageServerCatalogReader _catalogReader;
    private readonly IFieldMaskSource _fieldMaskSource;
    private readonly ILogger<ImageServerCatalogQueryHandler> _logger;

    public ImageServerCatalogQueryHandler(
        IMetadataV2GraphProvider graphProvider,
        IImageServerCatalogReader catalogReader,
        IFieldMaskSource fieldMaskSource,
        ILogger<ImageServerCatalogQueryHandler> logger)
    {
        _graphProvider = graphProvider ?? throw new ArgumentNullException(nameof(graphProvider));
        _catalogReader = catalogReader ?? throw new ArgumentNullException(nameof(catalogReader));
        _fieldMaskSource = fieldMaskSource ?? throw new ArgumentNullException(nameof(fieldMaskSource));
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
            var snapshot = await _graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            if (ImageServerV2Lookups.FindByLayerIndex(snapshot, layerId) is not { } resolved)
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
                return StandardErrorHelpers.CreateBadRequest(context, "Invalid ImageServer catalog query filter.");
            }

            // RBAC field-level masking (#2159): drop attribute (field) names the
            // principal's role is not permitted to see. This is the ImageServer
            // companion to the FeatureServer column masking (#2108): the same policy
            // source (IFieldMaskSource) and the same masked representation (the field
            // is absent), enforced server-side before serialization so a masked column
            // never leaks — even when the caller explicitly requests it via outFields.
            var maskedFields = resolved.Resource is { } maskResource
                ? await _fieldMaskSource.ResolveAsync(maskResource, cancellationToken).ConfigureAwait(false)
                : ImmutableArray<string>.Empty;

            ImageServerLog.CatalogQueryCompleted(_logger, layerId, page.Items.Count, page.TotalCount);

            // Emit read-bounding + provider-duration telemetry (no SQL is ever attached), so the
            // catalog pushdown's rows-scanned vs rows-returned ratio is observable per request.
            scope.WithTag(HonuaTelemetry.Tags.RowsScanned, page.RowsScanned);
            scope.WithTag(HonuaTelemetry.Tags.RowsReturned, page.RowsReturned);
            scope.WithTag(HonuaTelemetry.Tags.ProviderDurationMs, page.ProviderDuration.TotalMilliseconds);
            scope.SetSuccess(page.Items.Count);

            return BuildResponse(page, query, maskedFields);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        // Intentionally generic: this is a top-level protocol request handler; any
        // unexpected failure (parsing bugs, provider errors, etc.) must map to a
        // generic 500 rather than crash the host or leak internals to the client.
        catch (Exception ex)
        {
            ImageServerLog.CatalogQueryFailed(_logger, ex, layerId);
            scope.RecordException(ex);
            return StandardErrorHelpers.CreateInternalServerError(
                context,
                "An error occurred while querying the raster catalog.");
        }
    }

    /// <summary>
    /// Returns one raster catalog item as an Esri feature resource.
    /// </summary>
    public async Task<IResult> GetCatalogItemAsync(
        HttpContext context,
        int layerId,
        long rasterId,
        CancellationToken cancellationToken)
    {
        using var scope = HonuaTelemetryScope.StartFeature(
            "get-catalog-item",
            HonuaTelemetry.Protocols.ImageServer,
            layerId.ToString(CultureInfo.InvariantCulture));
        scope.WithTag(HonuaTelemetry.Tags.Operation, "get-catalog-item");

        try
        {
            var snapshot = await _graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            if (ImageServerV2Lookups.FindByLayerIndex(snapshot, layerId) is not { } resolved)
            {
                ImageServerLog.LayerNotFound(_logger, layerId);
                return StandardErrorHelpers.CreateNotFound(context, "Layer not found.");
            }

            var query = new ImageServerCatalogQuery
            {
                ObjectIds = [rasterId],
                Limit = 1,
                ReturnGeometry = true,
            };
            var page = await _catalogReader.ReadAsync(layerId, query, cancellationToken).ConfigureAwait(false);
            var item = page.Items.SingleOrDefault();
            if (item is null)
            {
                return StandardErrorHelpers.CreateNotFound(context, $"Raster catalog item {rasterId} not found.");
            }

            var maskedFields = resolved.Resource is { } resource
                ? await _fieldMaskSource.ResolveAsync(resource, cancellationToken).ConfigureAwait(false)
                : ImmutableArray<string>.Empty;
            var feature = BuildFeature(item, query, maskedFields);

            scope.SetSuccess(1);
            return Results.Json(feature, ImageServerJsonContext.Default.CatalogQueryFeature);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        // Intentionally generic: this is a top-level protocol request handler; any
        // unexpected failure (parsing bugs, provider errors, etc.) must map to a
        // generic 500 rather than crash the host or leak internals to the client.
        catch (Exception ex)
        {
            ImageServerLog.CatalogQueryFailed(_logger, ex, layerId);
            scope.RecordException(ex);
            return StandardErrorHelpers.CreateInternalServerError(
                context,
                "An error occurred while retrieving the raster catalog item.");
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

        if (!TryParseSpatialFilter(values, out var spatialFilter, out error))
        {
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

        if (!TryParseOrderByFields(GetString(values, "orderByFields"), out var orderBy, out error))
        {
            return false;
        }

        if (!TryParseOutFields(GetString(values, "outFields"), out var outFields, out error))
        {
            return false;
        }

        query = new ImageServerCatalogQuery
        {
            Where = where,
            ObjectIds = objectIds,
            OutputSrid = outputSrid,
            SpatialFilter = spatialFilter,
            Time = time,
            Offset = offset,
            Limit = limit,
            OrderBy = orderBy,
            OutFields = outFields,
            ReturnGeometry = returnGeometry,
            ReturnIdsOnly = returnIdsOnly,
            ReturnCountOnly = returnCountOnly,
            ReturnExtentOnly = returnExtentOnly,
        };
        return true;
    }

    /// <summary>
    /// Parses the Esri spatial query parameters (<c>geometry</c>, <c>geometryType</c>,
    /// <c>inSR</c>, <c>spatialRel</c>) into the canonical raster-catalog spatial filter. The
    /// geometry's axis-aligned envelope is always retained as the index-eligible pre-filter box;
    /// per the repository's spatial-semantics rule, <c>bbox</c>/envelope windowing
    /// (<c>esriGeometryEnvelope</c> or <c>esriSpatialRelEnvelopeIntersects</c>) uses the
    /// envelope-only test, while explicit relationships (<c>Intersects</c>/<c>Contains</c>/
    /// <c>Within</c>/<c>Overlaps</c>) on a non-envelope geometry additionally carry the exact
    /// geometry (WKB) so the provider evaluates the exact predicate. <c>inSR</c> sets the filter
    /// SRID; when omitted, the geometry's embedded spatial reference (or the catalog's native SRID)
    /// is assumed.
    /// </summary>
    private static bool TryParseSpatialFilter(
        IReadOnlyDictionary<string, StringValues> values,
        out ImageServerCatalogSpatialFilter? spatialFilter,
        out string? error)
    {
        spatialFilter = null;
        error = null;

        var geometry = GetString(values, "geometry");
        if (string.IsNullOrWhiteSpace(geometry))
        {
            // geometryType/inSR/spatialRel without a geometry are no-ops, mirroring ArcGIS.
            return true;
        }

        var geometryType = GetString(values, "geometryType");
        if (!string.IsNullOrWhiteSpace(geometryType) && !IsSupportedSpatialFilterGeometryType(geometryType))
        {
            error = $"Unsupported geometryType '{geometryType}'. Supported: esriGeometryEnvelope, esriGeometryPolygon, esriGeometryPoint, esriGeometryMultipoint, esriGeometryPolyline.";
            return false;
        }

        var spatialRelRaw = GetString(values, "spatialRel");
        if (!IsSupportedSpatialRelationship(spatialRelRaw, out var spatialRelError))
        {
            error = spatialRelError;
            return false;
        }

        var normalizedGeometry = NormalizeGeometryInput(geometry, geometryType);
        if (!ImageServerGeometryHelpers.TryGetEnvelope(normalizedGeometry, out var envelope, out var geometryError))
        {
            error = geometryError ?? "Invalid geometry.";
            return false;
        }

        var inSrRaw = GetString(values, "inSR");
        int? inSrid = null;
        if (!string.IsNullOrWhiteSpace(inSrRaw))
        {
            inSrid = SpatialReferenceHelpers.TryParseSrid(inSrRaw);
            if (!inSrid.HasValue)
            {
                error = $"Invalid inSR '{inSrRaw}'.";
                return false;
            }
        }

        // Precedence: explicit inSR, then the geometry's embedded spatialReference.
        var filterSrid = inSrid ?? envelope.Srid;

        // Decide envelope-only vs exact. An envelope-shaped geometry (or an explicit
        // envelope-intersects relationship) is a windowing request that reduces to the bbox test, so
        // it stays envelope-only. Any other geometry with an explicit relationship carries the exact
        // WKB so the provider applies ST_Intersects/ST_Contains/ST_Within/ST_Overlaps.
        var relation = MapSpatialRelation(spatialRelRaw);
        byte[]? exactGeometry = null;
        if (relation != RasterCatalogSpatialRelation.EnvelopeIntersects &&
            !IsEnvelopeShaped(normalizedGeometry))
        {
            if (!TryConvertGeometryToWkb(normalizedGeometry, filterSrid, out exactGeometry, out var wkbError))
            {
                error = wkbError;
                return false;
            }
        }
        else
        {
            relation = RasterCatalogSpatialRelation.EnvelopeIntersects;
        }

        spatialFilter = new ImageServerCatalogSpatialFilter(
            envelope.XMin, envelope.YMin, envelope.XMax, envelope.YMax, filterSrid, exactGeometry, relation);
        return true;
    }

    /// <summary>
    /// Maps the Esri <c>spatialRel</c> token to the canonical raster-catalog relation. A null or
    /// unspecified relationship defaults to <c>Intersects</c> (ArcGIS default); the value has already
    /// been validated by <see cref="IsSupportedSpatialRelationship"/>.
    /// </summary>
    private static RasterCatalogSpatialRelation MapSpatialRelation(string? spatialRel)
        => spatialRel?.ToLowerInvariant() switch
        {
            "esrispatialrelenvelopeintersects" => RasterCatalogSpatialRelation.EnvelopeIntersects,
            "esrispatialrelcontains" => RasterCatalogSpatialRelation.Contains,
            "esrispatialrelwithin" => RasterCatalogSpatialRelation.Within,
            "esrispatialreloverlaps" => RasterCatalogSpatialRelation.Overlaps,
            _ => RasterCatalogSpatialRelation.Intersects,
        };

    /// <summary>
    /// True when the supplied geometry JSON is an axis-aligned envelope (<c>xmin/ymin/xmax/ymax</c>),
    /// whose exact predicate is identical to the bounding-box test, so it is kept envelope-only.
    /// </summary>
    private static bool IsEnvelopeShaped(string geometryJson)
    {
        try
        {
            using var document = JsonDocument.Parse(geometryJson);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object &&
                   root.TryGetProperty("xmin", out _) &&
                   root.TryGetProperty("ymin", out _) &&
                   root.TryGetProperty("xmax", out _) &&
                   root.TryGetProperty("ymax", out _);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Converts the normalized Esri geometry JSON into WKB via the shared GeoServices geometry
    /// converter (the same path FeatureServer spatial filters use), so no geometry/geodesy logic is
    /// reimplemented here. Returns a 400-mappable error string on malformed geometry.
    /// </summary>
    private static bool TryConvertGeometryToWkb(
        string geometryJson,
        int? filterSrid,
        out byte[]? wkb,
        out string? error)
    {
        wkb = null;
        error = null;

        GeoServicesGeometry? geometry;
        try
        {
            geometry = JsonSerializer.Deserialize(geometryJson, FeatureServerJsonContext.Default.GeoServicesGeometry);
        }
        catch (JsonException)
        {
            error = "geometry must be valid JSON.";
            return false;
        }

        if (geometry is null)
        {
            error = "Invalid geometry.";
            return false;
        }

        try
        {
            wkb = GeoServicesGeometryConverter.ConvertGeoServicesGeometryToWkb(geometry, filterSrid);
            return true;
        }
        catch (ArgumentException)
        {
            error = "Invalid geometry.";
            return false;
        }
    }

    /// <summary>
    /// Accepts a bare <c>x,y</c>/<c>xmin,ymin,xmax,ymax</c> string for point/envelope
    /// geometryTypes (the ArcGIS bbox shorthand) and otherwise passes the JSON through.
    /// </summary>
    private static string NormalizeGeometryInput(string geometry, string? geometryType)
    {
        var trimmed = geometry.Trim();
        if (trimmed.StartsWith('{'))
        {
            return trimmed;
        }

        var parts = trimmed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 4 &&
            parts.All(p => double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
        {
            return $"{{\"xmin\":{parts[0]},\"ymin\":{parts[1]},\"xmax\":{parts[2]},\"ymax\":{parts[3]}}}";
        }

        if (parts.Length == 2 &&
            parts.All(p => double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
        {
            return $"{{\"x\":{parts[0]},\"y\":{parts[1]}}}";
        }

        return trimmed;
    }

    private static bool IsSupportedSpatialFilterGeometryType(string geometryType)
        => geometryType.Equals("esriGeometryEnvelope", StringComparison.OrdinalIgnoreCase) ||
           geometryType.Equals("esriGeometryPolygon", StringComparison.OrdinalIgnoreCase) ||
           geometryType.Equals("esriGeometryPoint", StringComparison.OrdinalIgnoreCase) ||
           geometryType.Equals("esriGeometryMultipoint", StringComparison.OrdinalIgnoreCase) ||
           geometryType.Equals("esriGeometryPolyline", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Validates <c>spatialRel</c>. At footprint-extent granularity the supported
    /// relationships all reduce to an envelope-overlap test, so this guards against
    /// callers expecting an unsupported exact predicate rather than silently mis-filtering.
    /// </summary>
    private static bool IsSupportedSpatialRelationship(string? spatialRel, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(spatialRel))
        {
            return true;
        }

        switch (spatialRel.ToLowerInvariant())
        {
            case "esrispatialrelintersects":
            case "esrispatialrelenvelopeintersects":
            case "esrispatialrelcontains":
            case "esrispatialrelwithin":
            case "esrispatialreloverlaps":
                return true;
            default:
                error = $"Unsupported spatialRel '{spatialRel}'. Supported: esriSpatialRelIntersects, esriSpatialRelEnvelopeIntersects, esriSpatialRelContains, esriSpatialRelWithin, esriSpatialRelOverlaps.";
                return false;
        }
    }

    /// <summary>
    /// Parses the Esri <c>orderByFields</c> parameter: a comma-separated list of
    /// <c>field [ASC|DESC]</c> terms. Unknown field names are rejected with a 400.
    /// </summary>
    private static bool TryParseOrderByFields(
        string? raw,
        out IReadOnlyList<ImageServerCatalogOrderBy> orderBy,
        out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            orderBy = [];
            return true;
        }

        var terms = new List<ImageServerCatalogOrderBy>();
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var tokens = part.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var fieldName = tokens.Length > 0 ? tokens[0] : string.Empty;
            var canonical = ImageServerCatalogFields.ResolveCanonicalName(fieldName);
            if (canonical is null)
            {
                error = $"Unknown orderByFields field '{fieldName}'.";
                orderBy = [];
                return false;
            }

            var descending = false;
            if (tokens.Length > 1)
            {
                if (tokens[1].Equals("DESC", StringComparison.OrdinalIgnoreCase))
                {
                    descending = true;
                }
                else if (!tokens[1].Equals("ASC", StringComparison.OrdinalIgnoreCase))
                {
                    error = $"Invalid sort direction '{tokens[1]}' in orderByFields. Use ASC or DESC.";
                    orderBy = [];
                    return false;
                }
            }

            terms.Add(new ImageServerCatalogOrderBy(canonical, descending));
        }

        orderBy = terms;
        return true;
    }

    /// <summary>
    /// Parses the Esri <c>outFields</c> parameter into a canonical field set.
    /// <c>*</c> (or omitted) selects all fields. <c>OBJECTID</c> is always
    /// retained. Unknown field names are rejected with a 400.
    /// </summary>
    private static bool TryParseOutFields(
        string? raw,
        out IReadOnlyList<string>? outFields,
        out string? error)
    {
        error = null;
        outFields = null;
        if (string.IsNullOrWhiteSpace(raw) || raw.Trim() == "*")
        {
            return true;
        }

        var selected = new List<string> { "OBJECTID" };
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var canonical = ImageServerCatalogFields.ResolveCanonicalName(part);
            if (canonical is null)
            {
                error = $"Unknown outFields field '{part}'.";
                return false;
            }

            if (!selected.Contains(canonical, StringComparer.Ordinal))
            {
                selected.Add(canonical);
            }
        }

        outFields = selected;
        return true;
    }

    private static IResult BuildResponse(
        ImageServerCatalogPage page,
        ImageServerCatalogQuery query,
        ImmutableArray<string> maskedFields)
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

        // The aggregate extent is already reprojected into outSR by the catalog reader
        // when a transform is available, so its SRID is authoritative for the response
        // envelope. Falling back to the native SRID keeps the envelope honest when no
        // reprojection occurred (transform unavailable or outSR omitted).
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
            Fields = BuildCatalogFields(query.OutFields, maskedFields),
            Features = page.Items.Select(item => BuildFeature(item, query, maskedFields)).ToArray(),
            ExceededTransferLimit = page.ExceededTransferLimit,
        };

        return Results.Json(response, ImageServerJsonContext.Default.CatalogQueryResponse);
    }

    private static CatalogQueryFeature BuildFeature(
        ImageServerCatalogItem item,
        ImageServerCatalogQuery query,
        ImmutableArray<string> maskedFields)
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

        // Project to the requested outFields when supplied. OBJECTID is always
        // retained by TryParseOutFields so the projected set is never empty.
        if (query.OutFields is { Count: > 0 })
        {
            var allowed = new HashSet<string>(query.OutFields, StringComparer.OrdinalIgnoreCase);
            attributes = attributes
                .Where(pair => allowed.Contains(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        }

        // Fail-secure field masking (#2159): drop masked attributes AFTER the
        // outFields projection so a masked column is absent regardless of whether the
        // caller explicitly requested it (matching the FeatureServer jsonb-key masking
        // semantics from #2108).
        if (!maskedFields.IsDefaultOrEmpty)
        {
            foreach (var masked in maskedFields)
            {
                attributes.Remove(masked);
            }
        }

        CatalogQueryGeometry? geometry = null;
        if (query.ReturnGeometry && item.FootprintRings is { Length: > 0 })
        {
            // Stamp the geometry with the actual coordinate SRID. The catalog reader
            // reprojects footprint rings into outSR when a transform is available and
            // updates FootprintSrid accordingly, so this SR always matches the rings.
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

    internal static Field[] BuildCatalogFields(
        IReadOnlyList<string>? outFields = null,
        ImmutableArray<string> maskedFields = default)
    {
        Field[] allFields =
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

        var masked = maskedFields.IsDefaultOrEmpty
            ? null
            : new HashSet<string>(maskedFields, StringComparer.OrdinalIgnoreCase);

        if (outFields is not { Count: > 0 })
        {
            // Drop masked fields from the advertised schema so the response field set
            // matches the masked attribute set (#2159).
            return masked is null
                ? allFields
                : allFields.Where(field => !masked.Contains(field.Name)).ToArray();
        }

        // Preserve the canonical field order rather than the caller's order so the
        // schema stays stable and matches the attribute emit order.
        var allowed = new HashSet<string>(outFields, StringComparer.OrdinalIgnoreCase);
        return allFields
            .Where(field => allowed.Contains(field.Name) && (masked is null || !masked.Contains(field.Name)))
            .ToArray();
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
