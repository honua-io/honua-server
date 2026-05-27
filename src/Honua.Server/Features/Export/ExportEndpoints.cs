// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Server.Features.Export.Writers;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Security;
using Honua.ServiceDefaults;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Export;

/// <summary>
/// Minimal API endpoint for exporting layer data in traditional GIS interchange formats.
/// </summary>
internal static class ExportEndpoints
{
    private const long AsyncThreshold = 50_000;

    private static readonly HashSet<string> _validFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        "csv", "shapefile", "gpkg"
    };

    /// <summary>
    /// Maps the export endpoint.
    /// </summary>
    public static void MapExportEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v{version:apiVersion}/admin/services/{serviceName}/layers/{layerId:int}/export")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Export")
            .WithDescription("Data export in traditional GIS formats")
            .RequireAdminAuthorization();

        _ = group.MapGet("", HandleExport)
            .WithName("ExportLayerData")
            .WithSummary("Export layer data as Shapefile, GeoPackage, or CSV");
    }

    private static async Task<IResult> HandleExport(
        string serviceName,
        int layerId,
        [FromQuery] string format,
        [FromQuery] string? where,
        [FromQuery] string? bbox,
        [FromQuery] string? outFields,
        [FromQuery] int? outSR,
        [FromServices] IResourceValidator resourceValidator,
        [FromServices] IFeatureReader featureReader,
        [FromServices] IStreamingFeatureStore streamingStore,
        [FromServices] ICrsRegistry crsRegistry,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var logger = httpContext.RequestServices.GetRequiredService<ILogger<ExportEndpointsLog>>();

        // Validate format
        if (string.IsNullOrEmpty(format) || !_validFormats.Contains(format))
        {
            ExportLog.FormatValidationFailed(logger, format ?? "(empty)");
            return ProblemDetailsHelpers.CreateAdminProblem(
                StatusCodes.Status400BadRequest,
                ProblemDetailsHelpers.GetTitle(StatusCodes.Status400BadRequest),
                $"Invalid export format '{format}'. Valid formats: csv, shapefile, gpkg");
        }

        // Validate service + layer against the Metadata v2 graph.
        var validation = await resourceValidator.ValidateServiceLayerV2Async(serviceName, layerId, cancellationToken);
        if (!validation.IsValid)
        {
            var statusCode = validation.ErrorCode == ResourceValidationError.NotFound
                ? StatusCodes.Status404NotFound
                : StatusCodes.Status400BadRequest;

            return ProblemDetailsHelpers.CreateAdminProblem(
                statusCode,
                ProblemDetailsHelpers.GetTitle(statusCode),
                validation.ErrorMessage ?? "Resource not found");
        }

        var triple = validation.Resource;
        var resource = triple.Resource;
        var layerName = ResolveLayerName(resource, layerId);
        var geometryType = MapGeometryType(resource.ReadGeometryType());
        var layerSrid = resource.ReadSrid() ?? SpatialReference.WGS84.Wkid;

        // Shapefile cannot handle mixed geometry types
        if (format.Equals("shapefile", StringComparison.OrdinalIgnoreCase)
            && geometryType is ExportGeometryType.GeometryCollection or ExportGeometryType.Mixed)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                StatusCodes.Status400BadRequest,
                ProblemDetailsHelpers.GetTitle(StatusCodes.Status400BadRequest),
                "Shapefile format does not support mixed geometry types. Use GeoPackage or CSV instead.");
        }

        // Validate output CRS
        if (outSR.HasValue)
        {
            var supported = await crsRegistry.IsSridSupportedAsync(outSR.Value, cancellationToken);
            if (!supported)
            {
                return ProblemDetailsHelpers.CreateAdminProblem(
                    StatusCodes.Status400BadRequest,
                    ProblemDetailsHelpers.GetTitle(StatusCodes.Status400BadRequest),
                    $"Unsupported output spatial reference: {outSR.Value}");
            }
        }

        using var activity = HonuaTelemetry.StartFeatureActivity(
            "export",
            HonuaTelemetry.Protocols.Admin,
            layerId.ToString(CultureInfo.InvariantCulture));
        activity?.SetTag(HonuaTelemetry.Tags.ServiceId, serviceName);
        activity?.SetTag("honua.export.format", format);

        var sw = Stopwatch.StartNew();

        var selectedFields = ResolveOutputFields(resource, outFields);
        if (!TryBuildQuery(where, bbox, outSR, layerSrid, out var countQuery, out var queryError))
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                StatusCodes.Status400BadRequest,
                ProblemDetailsHelpers.GetTitle(StatusCodes.Status400BadRequest),
                queryError);
        }

        var query = ApplyOutputFieldProjection(countQuery, selectedFields, outFields);
        long count;
        var countSw = Stopwatch.StartNew();
        using (var countActivity = HonuaTelemetry.StartDatabaseActivity(
            "export.count",
            layerId.ToString(CultureInfo.InvariantCulture)))
        {
            countActivity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.Admin);
            countActivity?.SetTag(HonuaTelemetry.Tags.ServiceId, serviceName);
            try
            {
                count = await featureReader.CountAsync(layerId, countQuery, cancellationToken);
                HonuaTelemetry.SetSuccess(countActivity, ToTelemetryFeatureCount(count));
            }
            catch (Exception ex)
            {
                HonuaTelemetry.RecordException(countActivity, ex);
                HonuaTelemetry.RecordException(activity, ex);
                throw;
            }
            finally
            {
                HonuaTelemetry.CategorizeLatency(countActivity, countSw.Elapsed.TotalMilliseconds);
            }
        }

        activity?.SetTag(HonuaTelemetry.Tags.FeatureCount, count);

        ExportLog.ExportRequested(logger, serviceName, layerId, format, count);

        var outputSrid = outSR ?? layerSrid;

        // Async path for large exports
        if (count > AsyncThreshold)
        {
            var exportJobService = httpContext.RequestServices.GetService<IExportJobService>();
            if (exportJobService is null)
            {
                return ProblemDetailsHelpers.CreateAdminProblem(
                    StatusCodes.Status503ServiceUnavailable,
                    ProblemDetailsHelpers.GetTitle(StatusCodes.Status503ServiceUnavailable),
                    "Export background service is not available.");
            }

            // Cloud storage is required for async exports — the scratch directory is cleaned up
            // after processing, so without cloud storage the output file would be lost.
            var cloudStorage = httpContext.RequestServices.GetService<ICloudFileStorage>();
            if (cloudStorage is null)
            {
                return ProblemDetailsHelpers.CreateAdminProblem(
                    StatusCodes.Status503ServiceUnavailable,
                    ProblemDetailsHelpers.GetTitle(StatusCodes.Status503ServiceUnavailable),
                    "Large exports require cloud storage to be configured. Configure a cloud storage provider or reduce the export scope.");
            }

            var jobId = Guid.NewGuid().ToString("N");
            var job = new ExportJob(jobId, serviceName, layerId, layerName, format,
                query, selectedFields, outputSrid, count, geometryType);

            try
            {
                await exportJobService.StartAsync(job, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex) when (string.Equals(
                ex.Message,
                "Large exports require durable background job storage to be configured.",
                StringComparison.Ordinal))
            {
                HonuaTelemetry.RecordException(activity, ex);
                HonuaTelemetry.CategorizeLatency(activity, sw.Elapsed.TotalMilliseconds);
                return ProblemDetailsHelpers.CreateAdminProblem(
                    StatusCodes.Status503ServiceUnavailable,
                    ProblemDetailsHelpers.GetTitle(StatusCodes.Status503ServiceUnavailable),
                    ex.Message);
            }
            catch (InvalidOperationException ex) when (string.Equals(
                ex.Message,
                "Export queue is full. Please retry later.",
                StringComparison.Ordinal))
            {
                HonuaTelemetry.RecordException(activity, ex);
                HonuaTelemetry.CategorizeLatency(activity, sw.Elapsed.TotalMilliseconds);
                return ProblemDetailsHelpers.CreateAdminProblem(
                    StatusCodes.Status503ServiceUnavailable,
                    ProblemDetailsHelpers.GetTitle(StatusCodes.Status503ServiceUnavailable),
                    ex.Message);
            }
            catch (Exception ex)
            {
                HonuaTelemetry.RecordException(activity, ex);
                HonuaTelemetry.CategorizeLatency(activity, sw.Elapsed.TotalMilliseconds);
                throw;
            }

            ExportLog.AsyncExportQueued(logger, jobId, serviceName, layerId, format, count);
            HonuaTelemetry.SetSuccess(activity, ToTelemetryFeatureCount(count));
            HonuaTelemetry.CategorizeLatency(activity, sw.Elapsed.TotalMilliseconds);

            var response = new ExportAcceptedResponse
            {
                OperationId = jobId,
                Message = "Export queued for background processing.",
                TotalFeatures = count,
                StatusUrl = $"/api/v1/admin/operations/{jobId}"
            };

            return Results.Json(response, ExportJsonContext.Default.ExportAcceptedResponse,
                statusCode: StatusCodes.Status202Accepted);
        }

        // Sync path — stream directly
        var features = streamingStore.StreamFeaturesAsync(layerId, query, cancellationToken);

        try
        {
            return format.ToLowerInvariant() switch
            {
                "csv" => await WriteCsvResponseAsync(httpContext, features, selectedFields, serviceName, layerId, layerName, logger, sw, activity),
                "shapefile" => await WriteShapefileResponseAsync(httpContext, features, selectedFields, layerId, layerName, geometryType, outputSrid,
                    crsRegistry, serviceName, logger, sw, activity, cancellationToken),
                "gpkg" => await WriteGeoPackageResponseAsync(httpContext, features, selectedFields, layerId, layerName, geometryType, outputSrid,
                    crsRegistry, serviceName, logger, sw, activity, cancellationToken),
                _ => ProblemDetailsHelpers.CreateAdminProblem(
                    StatusCodes.Status400BadRequest,
                    ProblemDetailsHelpers.GetTitle(StatusCodes.Status400BadRequest),
                    $"Unsupported format: {format}")
            };
        }
        catch (Exception ex)
        {
            ExportLog.ExportFailed(logger, serviceName, layerId, format, ex);
            HonuaTelemetry.RecordException(activity, ex);
            HonuaTelemetry.CategorizeLatency(activity, sw.Elapsed.TotalMilliseconds);
            throw;
        }
    }

    private static async Task<IResult> WriteCsvResponseAsync(
        HttpContext httpContext,
        IAsyncEnumerable<Feature> features,
        ExportField[] fields,
        string serviceName,
        int layerId,
        string layerName,
        ILogger logger,
        Stopwatch sw,
        Activity? activity)
    {
        var response = httpContext.Response;
        response.ContentType = "text/csv";
        response.Headers["Content-Disposition"] = $"attachment; filename=\"{SanitizeExportFilename(serviceName, layerName)}.csv\"";
        response.StatusCode = StatusCodes.Status200OK;

        var csvRowCount = await CsvExportWriter.WriteAsync(response.Body, features, fields, httpContext.RequestAborted);

        ExportLog.ExportCompleted(logger, serviceName, layerId, "csv", csvRowCount, sw.Elapsed.TotalMilliseconds);
        HonuaTelemetry.SetSuccess(activity, ToTelemetryFeatureCount(csvRowCount));
        HonuaTelemetry.CategorizeLatency(activity, sw.Elapsed.TotalMilliseconds);
        return Results.Empty;
    }

    private static async Task<IResult> WriteShapefileResponseAsync(
        HttpContext httpContext,
        IAsyncEnumerable<Feature> features,
        ExportField[] fields,
        int layerId,
        string layerName,
        ExportGeometryType geometryType,
        int outputSrid,
        ICrsRegistry crsRegistry,
        string serviceName,
        ILogger logger,
        Stopwatch sw,
        Activity? activity,
        CancellationToken cancellationToken)
    {
        // Resolve CRS WKT for .prj file from PostGIS spatial_ref_sys
        string? prjWkt = null;
        var crs = await crsRegistry.ResolveBySridAsync(outputSrid, cancellationToken);
        if (crs.HasValue)
        {
            prjWkt = crs.Value.Wkt;
        }

        var response = httpContext.Response;
        response.ContentType = "application/zip";
        response.Headers["Content-Disposition"] = $"attachment; filename=\"{SanitizeExportFilename(serviceName, layerName)}.zip\"";
        response.StatusCode = StatusCodes.Status200OK;

        var result = await ShapefileExportWriter.WriteAsync(
            response.Body, features, fields, geometryType,
            prjWkt, logger, cancellationToken);

        ExportLog.ExportCompleted(logger, serviceName, layerId, "shapefile", result.WrittenCount,
            sw.Elapsed.TotalMilliseconds);
        HonuaTelemetry.SetSuccess(activity, ToTelemetryFeatureCount(result.WrittenCount));
        HonuaTelemetry.CategorizeLatency(activity, sw.Elapsed.TotalMilliseconds);
        return Results.Empty;
    }

    private static async Task<IResult> WriteGeoPackageResponseAsync(
        HttpContext httpContext,
        IAsyncEnumerable<Feature> features,
        ExportField[] fields,
        int layerId,
        string layerName,
        ExportGeometryType geometryType,
        int outputSrid,
        ICrsRegistry crsRegistry,
        string serviceName,
        ILogger logger,
        Stopwatch sw,
        Activity? activity,
        CancellationToken cancellationToken)
    {
        // Resolve CRS info for GeoPackage metadata
        string? srsName = null;
        string? srsWkt = null;
        var crs = await crsRegistry.ResolveBySridAsync(outputSrid, cancellationToken);
        if (crs.HasValue)
        {
            srsName = $"EPSG:{outputSrid}";
            srsWkt = crs.Value.Wkt;
        }

        var scratchDir = Path.Combine(Path.GetTempPath(), "honua-export", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratchDir);
        var gpkgPath = Path.Combine(scratchDir, $"{SanitizeExportFilename(serviceName, layerName)}.gpkg");

        try
        {
            var featureCount = await GeoPackageExportWriter.WriteAsync(
                gpkgPath, features, fields, geometryType,
                outputSrid, srsName, srsWkt, cancellationToken);

            var response = httpContext.Response;
            response.ContentType = "application/geopackage+sqlite3";
            response.Headers["Content-Disposition"] = $"attachment; filename=\"{SanitizeExportFilename(serviceName, layerName)}.gpkg\"";
            response.StatusCode = StatusCodes.Status200OK;

            await using var fileStream = File.OpenRead(gpkgPath);
            await fileStream.CopyToAsync(response.Body, cancellationToken);

            ExportLog.ExportCompleted(logger, serviceName, layerId, "gpkg", featureCount,
                sw.Elapsed.TotalMilliseconds);
            HonuaTelemetry.SetSuccess(activity, ToTelemetryFeatureCount(featureCount));
            HonuaTelemetry.CategorizeLatency(activity, sw.Elapsed.TotalMilliseconds);
            return Results.Empty;
        }
        finally
        {
            try { Directory.Delete(scratchDir, recursive: true); }
            catch (Exception ex)
            {
                ExportLog.ScratchDirectoryCleanupFailed(logger, scratchDir, ex);
            }
        }
    }

    private static bool TryBuildQuery(
        string? where,
        string? bbox,
        int? outSR,
        int layerSrid,
        out FeatureQuery query,
        out string error)
    {
        query = new FeatureQuery
        {
            Where = where,
            OutputSrid = outSR,
            SpatialReferenceSrid = layerSrid
        };
        error = string.Empty;

        // Parse bbox
        if (!string.IsNullOrEmpty(bbox))
        {
            var parts = bbox.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length == 4
                && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var xmin)
                && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var ymin)
                && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var xmax)
                && double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var ymax)
                && double.IsFinite(xmin)
                && double.IsFinite(ymin)
                && double.IsFinite(xmax)
                && double.IsFinite(ymax)
                && xmin < xmax
                && ymin < ymax)
            {
                var envelope = new NetTopologySuite.Geometries.GeometryFactory()
                    .ToGeometry(new NetTopologySuite.Geometries.Envelope(xmin, xmax, ymin, ymax));

                query = query with
                {
                    SpatialFilter = SpatialFilter.Create(
                        envelope.AsBinary(),
                        SpatialRelationship.Intersects,
                        layerSrid)
                };

                return true;
            }

            error = "Invalid bbox parameter. Expected format: xmin,ymin,xmax,ymax with finite coordinates where xmin < xmax and ymin < ymax.";
            return false;
        }

        return true;
    }

    private static int ToTelemetryFeatureCount(long featureCount)
        => featureCount > int.MaxValue ? int.MaxValue : (int)Math.Max(featureCount, 0);

    internal static string SanitizeExportFilename(string serviceName, string layerName)
        => FileUploadSecurity.SanitizeFileName($"{serviceName}_{layerName}");

    private static string ResolveLayerName(MetadataV2Resource resource, int layerId)
        => string.IsNullOrWhiteSpace(resource.Metadata.Name)
            ? $"layer-{layerId.ToString(CultureInfo.InvariantCulture)}"
            : resource.Metadata.Name;

    private static ExportField[] ResolveOutputFields(MetadataV2Resource resource, string? outFields)
    {
        var attributeFields = resource.SchemaFields
            .Where(static field => field.Type is not (MetadataV2FieldType.Geometry or MetadataV2FieldType.Geography))
            .Select(static field => new ExportField(field.Name, MapFieldType(field.Type), field.Nullable))
            .ToArray();

        if (string.IsNullOrEmpty(outFields) || outFields == "*")
            return attributeFields;

        var requested = new HashSet<string>(
            outFields.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);

        return attributeFields.Where(f => requested.Contains(f.Name)).ToArray();
    }

    private static FeatureQuery ApplyOutputFieldProjection(
        FeatureQuery query,
        ExportField[] selectedFields,
        string? outFields)
    {
        if (string.IsNullOrWhiteSpace(outFields) || string.Equals(outFields, "*", StringComparison.Ordinal))
        {
            return query;
        }

        return query with
        {
            OutFields = selectedFields.Select(field => field.Name).ToImmutableArray()
        };
    }

    private static ExportGeometryType MapGeometryType(MetadataV2GeometryType geometryType) => geometryType switch
    {
        MetadataV2GeometryType.Point => ExportGeometryType.Point,
        MetadataV2GeometryType.MultiPoint => ExportGeometryType.MultiPoint,
        MetadataV2GeometryType.LineString => ExportGeometryType.LineString,
        MetadataV2GeometryType.MultiLineString => ExportGeometryType.MultiLineString,
        MetadataV2GeometryType.Polygon => ExportGeometryType.Polygon,
        MetadataV2GeometryType.MultiPolygon => ExportGeometryType.MultiPolygon,
        MetadataV2GeometryType.GeometryCollection => ExportGeometryType.GeometryCollection,
        MetadataV2GeometryType.Mixed => ExportGeometryType.Mixed,
        _ => ExportGeometryType.None
    };

    private static ExportFieldType MapFieldType(MetadataV2FieldType fieldType) => fieldType switch
    {
        MetadataV2FieldType.String => ExportFieldType.String,
        MetadataV2FieldType.Integer => ExportFieldType.Integer,
        MetadataV2FieldType.BigInteger => ExportFieldType.BigInteger,
        MetadataV2FieldType.Double => ExportFieldType.Double,
        MetadataV2FieldType.Float => ExportFieldType.Float,
        MetadataV2FieldType.Boolean => ExportFieldType.Boolean,
        MetadataV2FieldType.DateTime => ExportFieldType.DateTime,
        MetadataV2FieldType.Date => ExportFieldType.Date,
        MetadataV2FieldType.Time => ExportFieldType.Time,
        MetadataV2FieldType.Json => ExportFieldType.Json,
        MetadataV2FieldType.Binary => ExportFieldType.Binary,
        MetadataV2FieldType.Uuid => ExportFieldType.Uuid,
        _ => ExportFieldType.Unknown
    };
}

/// <summary>
/// Represents a queued export job for background processing.
/// </summary>
internal sealed record ExportJob(
    string JobId,
    string ServiceName,
    int LayerId,
    string LayerName,
    string Format,
    FeatureQuery Query,
    ExportField[] Fields,
    int OutputSrid,
    long TotalFeatures,
    ExportGeometryType GeometryType);
