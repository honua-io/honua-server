// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Threading.Channels;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Server.Features.Export.Writers;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Progress;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Export;

/// <summary>
/// Minimal API endpoint for exporting layer data in traditional GIS interchange formats.
/// </summary>
internal static class ExportEndpoints
{
    private static readonly ActivitySource _activitySource = new("Honua.Server.Export");

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

        // Validate service + layer
        var validation = await resourceValidator.ValidateServiceLayerAsync(serviceName, layerId, cancellationToken);
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

        var (service, layer) = validation.Resource;

        // Shapefile cannot handle mixed geometry types
        if (format.Equals("shapefile", StringComparison.OrdinalIgnoreCase)
            && layer.GeometryType == GeometryType.GeometryCollection)
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

        // Build query
        var query = BuildQuery(where, bbox, outFields, outSR, layer);

        // Count features
        var count = await featureReader.CountAsync(layerId, query, cancellationToken);

        using var activity = _activitySource.StartActivity("ExportLayer");
        activity?.SetTag("service", serviceName);
        activity?.SetTag("layer", layerId);
        activity?.SetTag("format", format);
        activity?.SetTag("feature_count", count);

        ExportLog.ExportRequested(logger, serviceName, layerId, format, count);

        // Resolve fields for output
        var selectedFields = ResolveOutputFields(layer, outFields);
        var outputSrid = outSR ?? layer.SpatialReference.Wkid;

        // Async path for large exports
        if (count > AsyncThreshold)
        {
            var channel = httpContext.RequestServices.GetService<Channel<ExportJob>>();
            if (channel is null)
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
            var job = new ExportJob(jobId, serviceName, layerId, layer.Name, format,
                query, selectedFields, outputSrid, count, layer.GeometryType);

            if (!channel.Writer.TryWrite(job))
            {
                return ProblemDetailsHelpers.CreateAdminProblem(
                    StatusCodes.Status503ServiceUnavailable,
                    ProblemDetailsHelpers.GetTitle(StatusCodes.Status503ServiceUnavailable),
                    "Export queue is full. Please retry later.");
            }

            var progressStore = httpContext.RequestServices.GetRequiredService<IUniversalProgressStore>();
            var progress = ExportProgress.CreateInitial(jobId, format, serviceName, layerId, count);
            await progressStore.SetProgressAsync(jobId, progress, TimeSpan.FromHours(24), cancellationToken);

            ExportLog.AsyncExportQueued(logger, jobId, serviceName, layerId, format, count);

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
        var sw = Stopwatch.StartNew();
        var features = streamingStore.StreamFeaturesAsync(layerId, query, cancellationToken);

        return format.ToLowerInvariant() switch
        {
            "csv" => await WriteCsvResponseAsync(httpContext, features, selectedFields, serviceName, layer, logger, sw),
            "shapefile" => await WriteShapefileResponseAsync(httpContext, features, selectedFields, layer, outputSrid,
                crsRegistry, serviceName, logger, sw, cancellationToken),
            "gpkg" => await WriteGeoPackageResponseAsync(httpContext, features, selectedFields, layer, outputSrid,
                crsRegistry, serviceName, logger, sw, cancellationToken),
            _ => ProblemDetailsHelpers.CreateAdminProblem(
                StatusCodes.Status400BadRequest,
                ProblemDetailsHelpers.GetTitle(StatusCodes.Status400BadRequest),
                $"Unsupported format: {format}")
        };
    }

    private static async Task<IResult> WriteCsvResponseAsync(
        HttpContext httpContext,
        IAsyncEnumerable<Feature> features,
        FieldDefinition[] fields,
        string serviceName,
        LayerDefinition layer,
        ILogger logger,
        Stopwatch sw)
    {
        var response = httpContext.Response;
        response.ContentType = "text/csv";
        response.Headers["Content-Disposition"] = $"attachment; filename=\"{SanitizeExportFilename(serviceName, layer.Name)}.csv\"";
        response.StatusCode = StatusCodes.Status200OK;

        var csvRowCount = await CsvExportWriter.WriteAsync(response.Body, features, fields, httpContext.RequestAborted);

        ExportLog.ExportCompleted(logger, serviceName, layer.Id, "csv", csvRowCount, sw.Elapsed.TotalMilliseconds);
        return Results.Empty;
    }

    private static async Task<IResult> WriteShapefileResponseAsync(
        HttpContext httpContext,
        IAsyncEnumerable<Feature> features,
        FieldDefinition[] fields,
        LayerDefinition layer,
        int outputSrid,
        ICrsRegistry crsRegistry,
        string serviceName,
        ILogger logger,
        Stopwatch sw,
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
        response.Headers["Content-Disposition"] = $"attachment; filename=\"{SanitizeExportFilename(serviceName, layer.Name)}.zip\"";
        response.StatusCode = StatusCodes.Status200OK;

        var result = await ShapefileExportWriter.WriteAsync(
            response.Body, features, fields, layer.GeometryType,
            prjWkt, logger, cancellationToken);

        ExportLog.ExportCompleted(logger, serviceName, layer.Id, "shapefile", result.WrittenCount,
            sw.Elapsed.TotalMilliseconds);
        return Results.Empty;
    }

    private static async Task<IResult> WriteGeoPackageResponseAsync(
        HttpContext httpContext,
        IAsyncEnumerable<Feature> features,
        FieldDefinition[] fields,
        LayerDefinition layer,
        int outputSrid,
        ICrsRegistry crsRegistry,
        string serviceName,
        ILogger logger,
        Stopwatch sw,
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
        var gpkgPath = Path.Combine(scratchDir, $"{SanitizeExportFilename(serviceName, layer.Name)}.gpkg");

        try
        {
            var featureCount = await GeoPackageExportWriter.WriteAsync(
                gpkgPath, features, fields, layer.GeometryType,
                outputSrid, srsName, srsWkt, cancellationToken);

            var response = httpContext.Response;
            response.ContentType = "application/geopackage+sqlite3";
            response.Headers["Content-Disposition"] = $"attachment; filename=\"{SanitizeExportFilename(serviceName, layer.Name)}.gpkg\"";
            response.StatusCode = StatusCodes.Status200OK;

            await using var fileStream = File.OpenRead(gpkgPath);
            await fileStream.CopyToAsync(response.Body, cancellationToken);

            ExportLog.ExportCompleted(logger, serviceName, layer.Id, "gpkg", featureCount,
                sw.Elapsed.TotalMilliseconds);
            return Results.Empty;
        }
        finally
        {
            try { Directory.Delete(scratchDir, recursive: true); }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to clean up GeoPackage scratch directory: {Path}", scratchDir);
            }
        }
    }

    private static FeatureQuery BuildQuery(
        string? where,
        string? bbox,
        string? outFields,
        int? outSR,
        LayerDefinition layer)
    {
        var query = new FeatureQuery
        {
            Where = where,
            OutputSrid = outSR,
            SpatialReferenceSrid = layer.SpatialReference.Wkid
        };

        // Parse bbox
        if (!string.IsNullOrEmpty(bbox))
        {
            var parts = bbox.Split(',');
            if (parts.Length == 4
                && double.TryParse(parts[0], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var xmin)
                && double.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var ymin)
                && double.TryParse(parts[2], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var xmax)
                && double.TryParse(parts[3], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var ymax))
            {
                var envelope = new NetTopologySuite.Geometries.GeometryFactory()
                    .ToGeometry(new NetTopologySuite.Geometries.Envelope(xmin, xmax, ymin, ymax));

                query = query with
                {
                    SpatialFilter = SpatialFilter.Create(
                        envelope.AsBinary(),
                        SpatialRelationship.Intersects,
                        layer.SpatialReference.Wkid)
                };
            }
        }

        return query;
    }

    internal static string SanitizeExportFilename(string serviceName, string layerName)
        => FileUploadSecurity.SanitizeFileName($"{serviceName}_{layerName}");

    private static FieldDefinition[] ResolveOutputFields(LayerDefinition layer, string? outFields)
    {
        var attributeFields = layer.AttributeFields;

        if (string.IsNullOrEmpty(outFields) || outFields == "*")
            return attributeFields;

        var requested = new HashSet<string>(
            outFields.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);

        return attributeFields.Where(f => requested.Contains(f.Name)).ToArray();
    }
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
    FieldDefinition[] Fields,
    int OutputSrid,
    long TotalFeatures,
    GeometryType GeometryType);
