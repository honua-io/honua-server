// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.GeometryService.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Services;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.Protocols.GeoServices.ImageServer.Models;
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
    private readonly SpatialReferenceResolver _spatialReferenceResolver;
    private readonly ILogger<ImageServerProjectHandler> _logger;
    private readonly ICoordinateTransformService? _transformService;

    public ImageServerProjectHandler(
        IGeometryOperationService operationService,
        SpatialReferenceResolver spatialReferenceResolver,
        ILogger<ImageServerProjectHandler> logger,
        ICoordinateTransformService? transformService = null)
    {
        _operationService = operationService ?? throw new ArgumentNullException(nameof(operationService));
        _spatialReferenceResolver = spatialReferenceResolver ?? throw new ArgumentNullException(nameof(spatialReferenceResolver));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _transformService = transformService;
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
            if (HasUnsupportedTransformation(values))
            {
                const string error = "datumTransformation and transformation are not yet supported by ImageServer project.";
                ImageServerLog.InvalidProjectParameters(_logger, layerId, error);
                return StandardErrorHelpers.CreateBadRequest(context, error);
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
            var projectedGeometries = new List<JsonElement>(request.GeometryJsonStrings.Count);
            foreach (var geometryJson in request.GeometryJsonStrings)
            {
                var requireEnvelope = string.Equals(
                    request.GeometryType,
                    "esriGeometryEnvelope",
                    StringComparison.OrdinalIgnoreCase);
                if (TryReadEnvelopeGeometry(geometryJson, requireEnvelope, out var envelope, out var envelopeError))
                {
                    if (_transformService is null && sourceSrid != targetSrid)
                    {
                        return StandardErrorHelpers.CreateNotImplemented(
                            context,
                            "Envelope projection requires a configured coordinate transform service.");
                    }

                    projectedGeometries.Add(await ProjectEnvelopeAsync(
                        envelope,
                        sourceSrid,
                        targetSrid,
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
        catch (Exception ex)
        {
            ImageServerLog.ProjectFailed(_logger, ex, layerId);
            scope.RecordException(ex);
            return StandardErrorHelpers.CreateInternalServerError(
                context,
                "An error occurred while projecting geometries.");
        }
    }

    private async Task<JsonElement> ProjectGeometryAsync(
        string geometryJson,
        int inSrid,
        int outSrid,
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
            cancellationToken).ConfigureAwait(false);
        var outputGeometry = GeoServicesGeometryConverter.ConvertWkbToGeoServicesGeometry(projected, outSrid)
            ?? throw new ArgumentException("Failed to convert projected geometry.");

        return ToJsonElement(outputGeometry);
    }

    private async Task<JsonElement> ProjectEnvelopeAsync(
        ProjectEnvelope envelope,
        int inSrid,
        int outSrid,
        CancellationToken cancellationToken)
    {
        var projected = (XMin: envelope.XMin, YMin: envelope.YMin, XMax: envelope.XMax, YMax: envelope.YMax);
        if (inSrid != outSrid)
        {
            var transformResult = await _transformService!.TransformExtentAsync(
                envelope.XMin,
                envelope.YMin,
                envelope.XMax,
                envelope.YMax,
                inSrid,
                outSrid,
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

        var srid = await _spatialReferenceResolver.ResolveSridAsync(raw, null, cancellationToken)
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

    private static bool HasUnsupportedTransformation(IReadOnlyDictionary<string, StringValues> values)
        => !string.IsNullOrWhiteSpace(GetString(values, "datumTransformation")) ||
           !string.IsNullOrWhiteSpace(GetString(values, "transformation"));

    private static string? GetString(IReadOnlyDictionary<string, StringValues> values, string key)
        => values.TryGetValue(key, out var raw) ? raw.ToString() : null;

    private readonly record struct ProjectEnvelope(double XMin, double YMin, double XMax, double YMax);

    private readonly record struct ProjectGeometryRequest(
        string GeometryType,
        IReadOnlyList<string> GeometryJsonStrings);
}
