// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.GeometryService.Abstractions;
using Honua.Core.Features.Infrastructure.Crs;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Models;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.Protocols.GeoServices.ImageServer.Models;
using Honua.Protocols.GeoServices.ImageServer.Services;
using Honua.ServiceDefaults;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace Honua.Protocols.GeoServices.ImageServer.Handlers;

/// <summary>
/// Handler for ImageServer geometry projection operations.
/// </summary>
internal sealed class ImageServerProjectHandler
{
    private const int MaxGeometryCount = 1_000;
    private const int MaxGeometryJsonLength = 10_000_000;

    private readonly IGeometryOperationService _operationService;
    private readonly ImageServerCoordinateProjection _projection;
    private readonly ILogger<ImageServerProjectHandler> _logger;
    private readonly IRasterStore? _rasterStore;

    public ImageServerProjectHandler(
        IGeometryOperationService operationService,
        ImageServerCoordinateProjection projection,
        ILogger<ImageServerProjectHandler> logger,
        IRasterStore? rasterStore = null)
    {
        _operationService = operationService ?? throw new ArgumentNullException(nameof(operationService));
        _projection = projection ?? throw new ArgumentNullException(nameof(projection));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _rasterStore = rasterStore;
    }

    /// <summary>
    /// Projects Esri JSON geometries between spatial references.
    /// </summary>
    public async Task<IResult> ProjectAsync(
        HttpContext context,
        int layerId,
        IReadOnlyDictionary<string, StringValues> values,
        CancellationToken cancellationToken)
    {
        using var scope = HonuaTelemetryScope.StartFeature(
            "project",
            HonuaTelemetry.Protocols.ImageServer,
            layerId.ToString(CultureInfo.InvariantCulture));
        scope.WithTag(HonuaTelemetry.Tags.Operation, "project");

        try
        {
            // The image-coordinate-system `transformation` parameter warps geometries between
            // image (pixel/sample-line) space and map space using the raster's RPC sensor model
            // (#1881). When the layer's raster carries RPC metadata, the geometries are treated as
            // image coordinates and mapped to ground (and then reprojected into outSR). When the
            // raster has no RPC metadata, the operation is genuinely unsupported and we keep the
            // 400 with a clear "no image CS metadata" message.
            var transformation = GetString(values, "transformation");
            if (!string.IsNullOrWhiteSpace(transformation))
            {
                return await ProjectImageCoordinatesAsync(context, layerId, values, scope, cancellationToken)
                    .ConfigureAwait(false);
            }

            var (inSrid, inSridError) = await ResolveRequiredSpatialReferenceAsync(values, "inSR", cancellationToken)
                .ConfigureAwait(false);
            if (inSridError is not null || !inSrid.HasValue)
            {
                var error = inSridError ?? "inSR must be a supported spatial reference.";
                ImageServerLog.InvalidProjectParameters(_logger, layerId, error);
                return StandardErrorHelpers.CreateBadRequest(context, error);
            }

            var (outSrid, outSridError) = await ResolveRequiredSpatialReferenceAsync(values, "outSR", cancellationToken)
                .ConfigureAwait(false);
            if (outSridError is not null || !outSrid.HasValue)
            {
                var error = outSridError ?? "outSR must be a supported spatial reference.";
                ImageServerLog.InvalidProjectParameters(_logger, layerId, error);
                return StandardErrorHelpers.CreateBadRequest(context, error);
            }

            if (!TryParseGeometries(GetString(values, "geometries"), out var request, out var geometryError))
            {
                ImageServerLog.InvalidProjectParameters(_logger, layerId, geometryError ?? "Invalid geometries.");
                return StandardErrorHelpers.CreateBadRequest(context, geometryError ?? "Invalid geometries.");
            }

            var sourceSrid = inSrid.Value;
            var targetSrid = outSrid.Value;

            if (!_projection.TryResolveDatumTransformation(
                    GetString(values, "datumTransformation"),
                    sourceSrid,
                    targetSrid,
                    out var datumSelection,
                    out var datumError))
            {
                ImageServerLog.InvalidProjectParameters(_logger, layerId, datumError ?? "Invalid datumTransformation.");
                return StandardErrorHelpers.CreateBadRequest(context, datumError ?? "Invalid datumTransformation.");
            }

            var projectedGeometries = new List<JsonElement>(request.GeometryJsonStrings.Count);
            foreach (var geometryJson in request.GeometryJsonStrings)
            {
                var requireEnvelope = string.Equals(
                    request.GeometryType,
                    "esriGeometryEnvelope",
                    StringComparison.OrdinalIgnoreCase);
                if (TryReadEnvelopeGeometry(geometryJson, requireEnvelope, out var envelope, out var envelopeError))
                {
                    if (!_projection.HasTransformService && sourceSrid != targetSrid)
                    {
                        return StandardErrorHelpers.CreateNotImplemented(
                            context,
                            "Envelope projection requires a configured coordinate transform service.");
                    }

                    projectedGeometries.Add(await ProjectEnvelopeAsync(
                        envelope,
                        sourceSrid,
                        targetSrid,
                        datumSelection,
                        cancellationToken).ConfigureAwait(false));
                    continue;
                }

                if (envelopeError is not null)
                {
                    ImageServerLog.InvalidProjectParameters(_logger, layerId, envelopeError);
                    return StandardErrorHelpers.CreateBadRequest(context, envelopeError);
                }

                projectedGeometries.Add(await ProjectGeometryAsync(
                    geometryJson,
                    sourceSrid,
                    targetSrid,
                    datumSelection,
                    cancellationToken).ConfigureAwait(false));
            }

            var response = new ImageServerProjectResponse
            {
                Geometries = projectedGeometries.ToArray(),
            };

            ImageServerLog.ProjectCompleted(_logger, layerId, projectedGeometries.Count, sourceSrid, targetSrid);
            scope.SetSuccess(projectedGeometries.Count);
            return Results.Json(response, ImageServerJsonContext.Default.ImageServerProjectResponse);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ArgumentException ex)
        {
            ImageServerLog.InvalidProjectParameters(_logger, layerId, ex.Message);
            scope.RecordException(ex);
            return StandardErrorHelpers.CreateBadRequest(context, "Invalid geometry input.");
        }
        // Intentionally generic: this is the top-level request handler boundary; any
        // unanticipated failure must map to a generic 500 rather than crash the request.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ImageServerLog.ProjectFailed(_logger, ex, layerId);
            scope.RecordException(ex);
            return StandardErrorHelpers.CreateInternalServerError(
                context,
                "An error occurred while projecting geometries.");
        }
    }

    /// <summary>
    /// Projects geometries between image (pixel sample/line) space and map space using the
    /// resolved raster's RPC sensor model (#1881). The supplied geometries are interpreted as
    /// image coordinates and mapped to ground (longitude/latitude, EPSG:4326), then reprojected
    /// into <c>outSR</c> when it differs and a transform service is available. The image-CS warp
    /// composes with a client-supplied <c>datumTransformation</c> on the ground -&gt; <c>outSR</c>
    /// leg (#2840); an unknown/inapplicable transformation is rejected with a precise 400 instead
    /// of being silently dropped. Returns 400 when the raster carries no RPC metadata (the image CS
    /// is genuinely unsupported for that raster).
    /// </summary>
    private async Task<IResult> ProjectImageCoordinatesAsync(
        HttpContext context,
        int layerId,
        IReadOnlyDictionary<string, StringValues> values,
        HonuaTelemetryScope scope,
        CancellationToken cancellationToken)
    {
        const int GroundSrid = 4326;

        if (_rasterStore is null)
        {
            return StandardErrorHelpers.CreateNotImplemented(
                context,
                "Image-coordinate-system projection requires a configured raster store.");
        }

        var rpc = await ResolveRpcModelAsync(layerId, cancellationToken).ConfigureAwait(false);
        if (rpc is not { } rpcModel)
        {
            var error = $"The 'transformation' (image coordinate system) parameter is not supported for layer {layerId.ToString(CultureInfo.InvariantCulture)}: the raster carries no RPC/sensor metadata.";
            ImageServerLog.InvalidProjectParameters(_logger, layerId, error);
            return StandardErrorHelpers.CreateBadRequest(context, error);
        }

        var (outSrid, outSridError) = await ResolveRequiredSpatialReferenceAsync(values, "outSR", cancellationToken)
            .ConfigureAwait(false);
        if (outSridError is not null || !outSrid.HasValue)
        {
            var error = outSridError ?? "outSR must be a supported spatial reference.";
            ImageServerLog.InvalidProjectParameters(_logger, layerId, error);
            return StandardErrorHelpers.CreateBadRequest(context, error);
        }

        if (!TryParseGeometries(GetString(values, "geometries"), out var request, out var geometryError))
        {
            ImageServerLog.InvalidProjectParameters(_logger, layerId, geometryError ?? "Invalid geometries.");
            return StandardErrorHelpers.CreateBadRequest(context, geometryError ?? "Invalid geometries.");
        }

        if (!string.Equals(request.GeometryType, "esriGeometryPoint", StringComparison.OrdinalIgnoreCase))
        {
            const string error = "Image-coordinate-system projection currently supports point geometries only.";
            ImageServerLog.InvalidProjectParameters(_logger, layerId, error);
            return StandardErrorHelpers.CreateBadRequest(context, error);
        }

        var reprojectToOut = outSrid.Value != GroundSrid;
        if (reprojectToOut && !_projection.HasTransformService)
        {
            return StandardErrorHelpers.CreateNotImplemented(
                context,
                "Image-coordinate-system projection into the requested outSR requires a configured coordinate transform service.");
        }

        // Compose the image-CS warp with the requested datum transformation: the RPC sensor model
        // maps image (sample/line) space to WGS84 ground, and the ground -> outSR reprojection then
        // honors the client-supplied datumTransformation (both warps apply when outSR sits on a
        // different datum). An unknown/inapplicable transformation is rejected with a precise 400
        // rather than silently dropped, matching the datumTransformation contract on the map path.
        DatumTransformationSelection? datumSelection = null;
        if (reprojectToOut && !_projection.TryResolveDatumTransformation(
                GetString(values, "datumTransformation"),
                GroundSrid,
                outSrid.Value,
                out datumSelection,
                out var datumTransformationError))
        {
            var error = datumTransformationError ?? "Invalid datumTransformation.";
            ImageServerLog.InvalidProjectParameters(_logger, layerId, error);
            return StandardErrorHelpers.CreateBadRequest(context, error);
        }

        // Parse each input through the projection while retaining explicit per-iteration disposal.
        var projected = new List<JsonElement>(request.GeometryJsonStrings.Count);
        foreach (var document in request.GeometryJsonStrings.Select(geometryJson => JsonDocument.Parse(geometryJson)))
        {
            using (document)
            {
                var root = document.RootElement;
                if (!TryGetGeometryDouble(root, "x", out var sample) ||
                    !TryGetGeometryDouble(root, "y", out var line))
                {
                    const string error = "Each image-space geometry must include numeric x (sample) and y (line).";
                    ImageServerLog.InvalidProjectParameters(_logger, layerId, error);
                    return StandardErrorHelpers.CreateBadRequest(context, error);
                }

                var (longitude, latitude) = rpcModel.ImageToGround(sample, line);

                var outX = longitude;
                var outY = latitude;
                if (reprojectToOut)
                {
                    var transformResult = await _projection.TransformExtentAsync(
                        longitude, latitude, longitude, latitude,
                        GroundSrid, outSrid.Value, datumSelection, cancellationToken).ConfigureAwait(false);
                    if (transformResult is null)
                    {
                        return StandardErrorHelpers.CreateBadRequest(
                            context,
                            "Image-space ground coordinate could not be projected into the requested outSR.");
                    }

                    outX = transformResult.Value.MinX;
                    outY = transformResult.Value.MinY;
                }

                var geometry = new GeoServicesGeometry
                {
                    X = outX,
                    Y = outY,
                    SpatialReference = new GeoServicesSpatialReference { Wkid = outSrid.Value, LatestWkid = outSrid.Value },
                };
                projected.Add(ToJsonElement(geometry));
            }
        }

        var response = new ImageServerProjectResponse { Geometries = projected.ToArray() };
        ImageServerLog.ProjectCompleted(_logger, layerId, projected.Count, 0, outSrid.Value);
        scope.SetSuccess(projected.Count);
        return Results.Json(response, ImageServerJsonContext.Default.ImageServerProjectResponse);
    }

    private async Task<RpcModel?> ResolveRpcModelAsync(int layerId, CancellationToken cancellationToken)
    {
        var primary = await _rasterStore!.GetPrimaryRasterInfoAsync(layerId, cancellationToken).ConfigureAwait(false);
        if (primary is not { } raster || raster.Id <= 0)
        {
            return null;
        }

        var metadata = await _rasterStore.GetSensorMetadataAsync([raster.Id], cancellationToken).ConfigureAwait(false);
        var sensor = metadata.TryGetValue(raster.Id, out var meta) ? meta : raster.SensorMetadata;
        return ImageServerSensorModel.TryReadRpc(sensor);
    }

    private static bool TryGetGeometryDouble(JsonElement root, string propertyName, out double value)
    {
        value = 0;
        return root.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.Number &&
               property.TryGetDouble(out value);
    }

    private async Task<JsonElement> ProjectGeometryAsync(
        string geometryJson,
        int inSrid,
        int outSrid,
        DatumTransformationSelection? selection,
        CancellationToken cancellationToken)
    {
        var geometry = JsonSerializer.Deserialize(
            geometryJson,
            FeatureServerJsonContext.Default.GeoServicesGeometry)
            ?? throw new ArgumentException("Invalid GeoServices JSON geometry format.");

        var wkb = GeoServicesGeometryConverter.ConvertGeoServicesGeometryToWkb(geometry, inSrid);
        var projected = await _operationService.ProjectAsync(
            wkb,
            inSrid,
            outSrid,
            selection,
            cancellationToken).ConfigureAwait(false);
        var outputGeometry = GeoServicesGeometryConverter.ConvertWkbToGeoServicesGeometry(projected, outSrid)
            ?? throw new ArgumentException("Failed to convert projected geometry.");

        return ToJsonElement(outputGeometry);
    }

    private async Task<JsonElement> ProjectEnvelopeAsync(
        ProjectEnvelope envelope,
        int inSrid,
        int outSrid,
        DatumTransformationSelection? selection,
        CancellationToken cancellationToken)
    {
        var projected = (XMin: envelope.XMin, YMin: envelope.YMin, XMax: envelope.XMax, YMax: envelope.YMax);
        if (inSrid != outSrid)
        {
            var transformResult = await _projection.TransformExtentAsync(
                envelope.XMin,
                envelope.YMin,
                envelope.XMax,
                envelope.YMax,
                inSrid,
                outSrid,
                selection,
                cancellationToken).ConfigureAwait(false);
            if (transformResult is null)
            {
                throw new ArgumentException("Envelope could not be projected between the requested spatial references.");
            }

            projected = (
                transformResult.Value.MinX,
                transformResult.Value.MinY,
                transformResult.Value.MaxX,
                transformResult.Value.MaxY);
        }

        var geometry = new GeoServicesGeometry
        {
            Xmin = projected.XMin,
            Ymin = projected.YMin,
            Xmax = projected.XMax,
            Ymax = projected.YMax,
            SpatialReference = new GeoServicesSpatialReference { Wkid = outSrid, LatestWkid = outSrid },
        };

        return ToJsonElement(geometry);
    }

    private async Task<(int? Srid, string? Error)> ResolveRequiredSpatialReferenceAsync(
        IReadOnlyDictionary<string, StringValues> values,
        string key,
        CancellationToken cancellationToken)
    {
        var raw = GetString(values, key);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return (null, $"{key} is required.");
        }

        var srid = await _projection.ResolveSridAsync(raw, cancellationToken)
            .ConfigureAwait(false);
        if (!srid.HasValue)
        {
            return (null, $"{key} must be a supported spatial reference.");
        }

        return (srid.Value, null);
    }

    private static bool TryParseGeometries(
        string? raw,
        out ProjectGeometryRequest request,
        out string? error)
    {
        request = default;
        error = null;

        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "geometries is required.";
            return false;
        }

        if (raw.Length > MaxGeometryJsonLength)
        {
            error = "geometries is too large.";
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(raw);
        }
        catch (JsonException)
        {
            error = "geometries must be valid JSON.";
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            var geometryType = "esriGeometryPoint";
            var geometriesElement = root;

            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("geometryType", out var geometryTypeElement) &&
                    geometryTypeElement.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(geometryTypeElement.GetString()))
                {
                    geometryType = geometryTypeElement.GetString()!;
                }

                if (!root.TryGetProperty("geometries", out geometriesElement))
                {
                    error = "geometries must contain a geometries array.";
                    return false;
                }
            }

            if (geometriesElement.ValueKind != JsonValueKind.Array)
            {
                error = "geometries must be an array.";
                return false;
            }

            var geometries = new List<string>();
            foreach (var geometryElement in geometriesElement.EnumerateArray())
            {
                if (geometryElement.ValueKind != JsonValueKind.Object)
                {
                    error = "each geometry must be a JSON object.";
                    return false;
                }

                geometries.Add(geometryElement.GetRawText());
                if (geometries.Count > MaxGeometryCount)
                {
                    error = $"geometries cannot contain more than {MaxGeometryCount.ToString(CultureInfo.InvariantCulture)} items.";
                    return false;
                }
            }

            if (geometries.Count == 0)
            {
                error = "geometries must contain at least one geometry.";
                return false;
            }

            request = new ProjectGeometryRequest(geometryType, geometries);
            return true;
        }
    }

    private static bool TryReadEnvelopeGeometry(
        string raw,
        bool requireEnvelope,
        out ProjectEnvelope envelope,
        out string? error)
    {
        envelope = default;
        error = null;

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;

        var hasXMin = root.TryGetProperty("xmin", out var xmin);
        var hasYMin = root.TryGetProperty("ymin", out var ymin);
        var hasXMax = root.TryGetProperty("xmax", out var xmax);
        var hasYMax = root.TryGetProperty("ymax", out var ymax);
        var hasAnyEnvelopeCoordinate = hasXMin || hasYMin || hasXMax || hasYMax;

        if (!hasAnyEnvelopeCoordinate)
        {
            if (requireEnvelope)
            {
                error = "Envelope geometries must include xmin, ymin, xmax, and ymax.";
            }

            return false;
        }

        if (!hasXMin ||
            !hasYMin ||
            !hasXMax ||
            !hasYMax ||
            !TryGetDouble(xmin, out var xMin) ||
            !TryGetDouble(ymin, out var yMin) ||
            !TryGetDouble(xmax, out var xMax) ||
            !TryGetDouble(ymax, out var yMax))
        {
            error = "Envelope geometries must include numeric xmin, ymin, xmax, and ymax.";
            return false;
        }

        envelope = new ProjectEnvelope(
            Math.Min(xMin, xMax),
            Math.Min(yMin, yMax),
            Math.Max(xMin, xMax),
            Math.Max(yMin, yMax));
        return true;
    }

    private static JsonElement ToJsonElement(GeoServicesGeometry geometry)
    {
        var json = JsonSerializer.Serialize(geometry, FeatureServerJsonContext.Default.GeoServicesGeometry);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static bool TryGetDouble(JsonElement element, out double value)
    {
        value = 0;
        return element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out value);
    }

    private static string? GetString(IReadOnlyDictionary<string, StringValues> values, string key)
        => values.TryGetValue(key, out var raw) ? raw.ToString() : null;

    private readonly record struct ProjectEnvelope(double XMin, double YMin, double XMax, double YMax);

    private readonly record struct ProjectGeometryRequest(
        string GeometryType,
        IReadOnlyList<string> GeometryJsonStrings);
}
