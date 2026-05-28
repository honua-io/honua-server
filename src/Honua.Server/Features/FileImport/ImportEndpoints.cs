// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Security;
using Honua.Server.Features.Infrastructure.Validation;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
using Honua.Core.Features.FileImport.Services.FileGdb;
using Honua.Server.Features.Import;
using Honua.Server.Features.Migration;
using Honua.Server.Features.FileImport;
using Honua.Server.Features.RasterImport;

namespace Honua.Server.Features.FileImport;

/// <summary>
/// File import endpoints for uploading and processing geospatial files
/// </summary>
internal static partial class ImportEndpoints
{
    private const string InvalidMultipartImportRequestMessage = "Multipart import request is invalid.";
    private static readonly string[] NonGetMethods =
    [
        HttpMethods.Post,
        HttpMethods.Put,
        HttpMethods.Delete,
        HttpMethods.Patch
    ];
    private static readonly string[] NonPostMethods =
    [
        HttpMethods.Get,
        HttpMethods.Put,
        HttpMethods.Delete,
        HttpMethods.Patch
    ];
    internal sealed class ImportEndpointsLog
    {
    }

    /// <summary>
    /// Map file import endpoints to the web application with formal API versioning
    /// </summary>
    public static void MapImportEndpoints(this WebApplication app)
    {
        // Primary v1 routes at /api/v1/admin/import with formal versioning
        var v1Group = app.MapGroup("/api/v{version:apiVersion}/admin/import")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Import")
            .RequireAdminAuthorization();

        MapImportRoutes(v1Group, isV1: true);
    }

    /// <summary>
    /// Map import routes to a route group
    /// </summary>
    private static void MapImportRoutes(IEndpointRouteBuilder group, bool isV1)
    {
        var nameSuffix = isV1 ? "V1" : "";

        // Get supported file formats
        _ = group.MapMethods("/formats", [HttpMethods.Get], HandleGetSupportedFormats)
            .WithName($"GetSupportedFileFormats{nameSuffix}")
            .WithSummary("Get supported geospatial file formats")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));
        _ = group.MapMethods("/formats", NonGetMethods, HandleGetMethodNotAllowed)
            .WithName($"GetSupportedFileFormats{nameSuffix}MethodNotAllowed")
            .WithSummary("Get supported geospatial file formats method not allowed");

        // Preview file before import
        _ = group.MapMethods("/preview", [HttpMethods.Post], HandlePreviewFile)
            .WithName($"PreviewFile{nameSuffix}")
            .WithSummary("Preview geospatial file contents")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }))
            .DisableAntiforgery(); // For file uploads
        _ = group.MapMethods("/preview", NonPostMethods, HandlePostMethodNotAllowed)
            .WithName($"PreviewFile{nameSuffix}MethodNotAllowed")
            .WithSummary("Preview geospatial file contents method not allowed");

        _ = group.MapMethods("/preview-url", [HttpMethods.Post], HandlePreviewFileFromUrl)
            .WithName($"PreviewFileFromUrl{nameSuffix}")
            .WithSummary("Preview geospatial file contents from a public object URL")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));
        _ = group.MapMethods("/preview-url", NonPostMethods, HandlePostMethodNotAllowed)
            .WithName($"PreviewFileFromUrl{nameSuffix}MethodNotAllowed")
            .WithSummary("Preview geospatial file contents from a public object URL method not allowed");

        // Import geospatial file
        _ = group.MapMethods("/upload", [HttpMethods.Post], HandleImportFile)
            .WithName($"ImportFile{nameSuffix}")
            .WithSummary("Import geospatial file to PostgreSQL using streamed multipart ingest")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }))
            .DisableAntiforgery(); // For file uploads
        _ = group.MapMethods("/upload", NonPostMethods, HandlePostMethodNotAllowed)
            .WithName($"ImportFile{nameSuffix}MethodNotAllowed")
            .WithSummary("Import geospatial file to PostgreSQL using streamed multipart ingest method not allowed");

        _ = group.MapMethods("/upload-url", [HttpMethods.Post], HandleImportFileFromUrl)
            .WithName($"ImportFileFromUrl{nameSuffix}")
            .WithSummary("Import geospatial data from a public object URL")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));
        _ = group.MapMethods("/upload-url", NonPostMethods, HandlePostMethodNotAllowed)
            .WithName($"ImportFileFromUrl{nameSuffix}MethodNotAllowed")
            .WithSummary("Import geospatial data from a public object URL method not allowed");

        // Get upload progress
        _ = group.MapMethods("/uploads/{uploadId}/progress", [HttpMethods.Get], HandleGetUploadProgress)
            .WithName($"GetUploadProgress{nameSuffix}")
            .WithSummary("Get the progress of a file upload operation")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));
        _ = group.MapMethods("/uploads/{uploadId}/progress", NonGetMethods, HandleGetMethodNotAllowed)
            .WithName($"GetUploadProgress{nameSuffix}MethodNotAllowed")
            .WithSummary("Get the progress of a file upload operation method not allowed");

        // Cancel upload
        _ = group.MapMethods("/uploads/{uploadId}/cancel", [HttpMethods.Post], HandleCancelUpload)
            .WithName($"CancelUpload{nameSuffix}")
            .WithSummary("Cancel a running file upload operation")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));
        _ = group.MapMethods("/uploads/{uploadId}/cancel", NonPostMethods, HandlePostMethodNotAllowed)
            .WithName($"CancelUpload{nameSuffix}MethodNotAllowed")
            .WithSummary("Cancel a running file upload operation method not allowed");

        // Get all active uploads
        _ = group.MapMethods("/uploads", [HttpMethods.Get], HandleGetActiveUploads)
            .WithName($"GetActiveUploads{nameSuffix}")
            .WithSummary("Get all active file upload operations")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));
        _ = group.MapMethods("/uploads", NonGetMethods, HandleGetMethodNotAllowed)
            .WithName($"GetActiveUploads{nameSuffix}MethodNotAllowed")
            .WithSummary("Get all active file upload operations method not allowed");

        // Get active import jobs
        _ = group.MapMethods("/jobs", [HttpMethods.Get], HandleGetActiveJobs)
            .WithName($"GetActiveImportJobs{nameSuffix}")
            .WithSummary("Get all active import jobs")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));
        _ = group.MapMethods("/jobs", NonGetMethods, HandleGetMethodNotAllowed)
            .WithName($"GetActiveImportJobs{nameSuffix}MethodNotAllowed")
            .WithSummary("Get all active import jobs method not allowed");

        // Get import job status
        _ = group.MapMethods("/jobs/{jobId}", [HttpMethods.Get], HandleGetImportJobStatus)
            .WithName($"GetImportJobStatus{nameSuffix}")
            .WithSummary("Get the status of an import job")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));
        _ = group.MapMethods("/jobs/{jobId}", NonGetMethods, HandleGetMethodNotAllowed)
            .WithName($"GetImportJobStatus{nameSuffix}MethodNotAllowed")
            .WithSummary("Get the status of an import job method not allowed");

        // Cancel import job
        _ = group.MapMethods("/jobs/{jobId}/cancel", [HttpMethods.Post], HandleCancelImportJob)
            .WithName($"CancelImportJob{nameSuffix}")
            .WithSummary("Cancel a running import job")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));
        _ = group.MapMethods("/jobs/{jobId}/cancel", NonPostMethods, HandlePostMethodNotAllowed)
            .WithName($"CancelImportJob{nameSuffix}MethodNotAllowed")
            .WithSummary("Cancel a running import job method not allowed");

        // Get import limits configuration
        _ = group.MapMethods("/limits", [HttpMethods.Get], HandleGetLimits)
            .WithName($"GetImportLimits{nameSuffix}")
            .WithSummary("Get current import configuration limits")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));
        _ = group.MapMethods("/limits", NonGetMethods, HandleGetMethodNotAllowed)
            .WithName($"GetImportLimits{nameSuffix}MethodNotAllowed")
            .WithSummary("Get current import configuration limits method not allowed");
    }

    /// <summary>
    /// Get supported file formats and extensions
    /// </summary>
    private static async Task HandleGetSupportedFormats(HttpContext context)
    {
        IFileImportService importService = context.RequestServices.GetRequiredService<IFileImportService>();
        string[] extensions = importService.GetSupportedExtensions();
        var formatDescriptions = new Dictionary<string, string>
        {
            [".geojson"] = "GeoJSON - Web-standard JSON format",
            [".json"] = "JSON - May contain GeoJSON data",
            [".zip"] = "Zipped Shapefile - contains .shp/.dbf/.shx/.prj components",
            [".gpkg"] = "GeoPackage - OGC SQLite-based format",
            [".gpx"] = "GPX - GPS Exchange format",
            [".kml"] = "KML - Keyhole Markup Language (Google Earth)",
            [".kmz"] = "KMZ - Compressed KML format",
            [".gml"] = "GML - Geography Markup Language",
            [".wkt"] = "WKT - Well-Known Text format",
            [".csv"] = "CSV - Comma-separated values with lon/lat or WKT geometry columns",
            [".twkb"] = "TinyWKB - Compact binary format",
            [".fgb"] = "FlatGeobuf - Compact binary geospatial format",
            [".gdb.zip"] = "Zipped File Geodatabase - compressed .gdb archive",
            [".parquet"] = "GeoParquet - Apache Parquet with WKB geometry encoding",
            [".geoparquet"] = "GeoParquet - Apache Parquet with WKB geometry encoding"
        };

        var response = new FileFormatsResponse
        {
            SupportedExtensions = extensions,
            FormatDescriptions = formatDescriptions.Where(kv => extensions.Contains(kv.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value)
        };

        IResult result = Results.Json(response, ImportJsonContext.Default.FileFormatsResponse);
        await result.ExecuteAsync(context);
    }

    /// <summary>
    /// Preview file contents without importing
    /// </summary>
    private static async Task HandlePreviewFile(HttpContext context)
    {
        CancellationToken cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        IFormCollection form = await context.Request.ReadFormAsync(cancellationToken);
        IFormFile? file = GetFormFile(form, "file", "File");

        if (file == null || file.Length == 0)
        {
            await AdminResponseWriter.WriteErrorAsync(context, "File is empty", StatusCodes.Status400BadRequest);
            return;
        }

        IFileImportService importService = context.RequestServices.GetRequiredService<IFileImportService>();
        var securityOptions = context.RequestServices.GetRequiredService<IOptions<FileUploadSecurityOptions>>();
        var previewValidation = await FileUploadSecurity.ValidateFileAsync(
            file,
            importService.Limits.MaxPreviewSizeBytes,
            securityOptions.Value.MaxSecurityScanSizeBytes,
            cancellationToken);
        if (!previewValidation.IsValid)
        {
            await AdminResponseWriter.WriteErrorAsync(context, previewValidation.ErrorMessage ?? "File validation failed", StatusCodes.Status400BadRequest);
            return;
        }

        var safeFileName = FileUploadSecurity.SanitizeFileName(file.FileName);
        var extension = Path.GetExtension(safeFileName);
        if (string.Equals(extension, ".shp", StringComparison.OrdinalIgnoreCase))
        {
            await AdminResponseWriter.WriteErrorAsync(context, "Shapefile uploads must be a .zip containing .shp and .dbf files.",
                StatusCodes.Status400BadRequest);
            return;
        }

        SupportedFileFormat? format = importService.DetectFormat(safeFileName);
        if (format == null)
        {
            await AdminResponseWriter.WriteErrorAsync(context, $"Unsupported file format: {Path.GetExtension(safeFileName)}",
                StatusCodes.Status400BadRequest);
            return;
        }

        try
        {
            using Stream stream = file.OpenReadStream();
            FilePreview preview = await importService.PreviewFileAsync(stream, safeFileName, cancellationToken);
            IResult result = Results.Json(preview, ImportJsonContext.Default.FilePreview);
            await result.ExecuteAsync(context);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or NotSupportedException)
        {
            var logger = context.RequestServices.GetRequiredService<ILogger<ImportEndpointsLog>>();
            Log.PreviewFailed(logger, file?.FileName ?? "unknown", ex);
            await AdminResponseWriter.WriteErrorAsync(context, "Failed to preview file", StatusCodes.Status400BadRequest);
        }
        catch (Exception ex)
        {
            var logger = context.RequestServices.GetRequiredService<ILogger<ImportEndpointsLog>>();
            Log.PreviewFailed(logger, file?.FileName ?? "unknown", ex);
            await AdminResponseWriter.WriteErrorAsync(context, "Failed to preview file", StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>
    /// Import geospatial file to PostgreSQL using memory-efficient streaming.
    /// Large files are automatically queued for background processing.
    /// </summary>
    private static async Task HandleImportFile(HttpContext context)
    {
        if (!HttpMethods.IsPost(context.Request.Method))
        {
            await HandlePostMethodNotAllowed(context);
            return;
        }

        CancellationToken cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        IFileImportService importService = context.RequestServices.GetRequiredService<IFileImportService>();
        var securityOptions = context.RequestServices.GetRequiredService<IOptions<FileUploadSecurityOptions>>();
        var maxFileSizeBytes = Math.Max(importService.Limits.BackgroundJobThresholdBytes, importService.Limits.MaxMemoryBytes);
        var parseResult = await ParseMultipartImportUploadAsync(
            context,
            importService,
            maxFileSizeBytes,
            securityOptions.Value.MaxSecurityScanSizeBytes,
            cancellationToken);

        if (parseResult.ErrorMessage != null)
        {
            await AdminResponseWriter.WriteErrorAsync(
                context,
                parseResult.ErrorMessage,
                parseResult.StatusCode ?? StatusCodes.Status400BadRequest);
            return;
        }

        try
        {
            await ExecuteStagedImportAsync(context, parseResult.Request!, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or NotSupportedException)
        {
            var logger = context.RequestServices.GetRequiredService<ILogger<ImportEndpointsLog>>();
            Log.ImportFailed(logger, parseResult.Request?.TableName ?? "unknown", ex);
            // Provide generic error message - details logged for debugging
            await AdminResponseWriter.WriteErrorAsync(context, "Import failed: invalid or unsupported file format", StatusCodes.Status400BadRequest);
        }
        catch (Exception ex)
        {
            var logger = context.RequestServices.GetRequiredService<ILogger<ImportEndpointsLog>>();
            Log.ImportFailed(logger, parseResult.Request?.TableName ?? "unknown", ex);
            await AdminResponseWriter.WriteErrorAsync(context, "Import failed", StatusCodes.Status500InternalServerError);
        }
    }

    private static Task HandleGetMethodNotAllowed(HttpContext context)
    {
        var allowedMethods = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { HttpMethods.Get };
        return ValidationErrorHelpers.WriteAdminMethodNotAllowedAsync(context, allowedMethods, context.RequestAborted);
    }

    private static Task HandlePostMethodNotAllowed(HttpContext context)
    {
        var allowedMethods = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { HttpMethods.Post };
        return ValidationErrorHelpers.WriteAdminMethodNotAllowedAsync(context, allowedMethods, context.RequestAborted);
    }

    private static async Task HandlePreviewFileFromUrl(HttpContext context)
    {
        if (!HttpMethods.IsPost(context.Request.Method))
        {
            await HandlePostMethodNotAllowed(context);
            return;
        }

        CancellationToken cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        var request = await JsonSerializer.DeserializeAsync(
            context.Request.Body,
            ImportJsonContext.Default.PreviewUrlImportRequest,
            cancellationToken);

        if (request == null || string.IsNullOrWhiteSpace(request.SourceUrl))
        {
            await AdminResponseWriter.WriteErrorAsync(context, "SourceUrl is required", StatusCodes.Status400BadRequest);
            return;
        }

        IFileImportService importService = context.RequestServices.GetRequiredService<IFileImportService>();
        var securityOptions = context.RequestServices.GetRequiredService<IOptions<FileUploadSecurityOptions>>();
        var staged = await DownloadRemoteImportSourceAsync(
            context,
            request.SourceUrl,
            request.FileName,
            importService,
            importService.Limits.MaxPreviewSizeBytes,
            securityOptions.Value.MaxSecurityScanSizeBytes,
            cancellationToken);

        if (staged.ErrorMessage != null)
        {
            await AdminResponseWriter.WriteErrorAsync(
                context,
                staged.ErrorMessage,
                staged.StatusCode ?? StatusCodes.Status400BadRequest);
            return;
        }

        try
        {
            await using var previewStream = OpenSequentialReadStream(
                staged.File!.LocalFilePath,
                importService.Limits.StreamBufferSize);
            FilePreview preview = await importService.PreviewFileAsync(
                previewStream,
                staged.File.FileName,
                cancellationToken);
            IResult result = Results.Json(preview, ImportJsonContext.Default.FilePreview);
            await result.ExecuteAsync(context);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or NotSupportedException)
        {
            var logger = context.RequestServices.GetRequiredService<ILogger<ImportEndpointsLog>>();
            Log.PreviewFailed(logger, staged.File?.FileName ?? "unknown", ex);
            await AdminResponseWriter.WriteErrorAsync(context, "Failed to preview file", StatusCodes.Status400BadRequest);
        }
        catch (Exception ex)
        {
            var logger = context.RequestServices.GetRequiredService<ILogger<ImportEndpointsLog>>();
            Log.PreviewFailed(logger, staged.File?.FileName ?? "unknown", ex);
            await AdminResponseWriter.WriteErrorAsync(context, "Failed to preview file", StatusCodes.Status500InternalServerError);
        }
        finally
        {
            MultipartParsingHelpers.TryDeleteFile(staged.File?.LocalFilePath);
        }
    }

    private static async Task HandleImportFileFromUrl(HttpContext context)
    {
        if (!HttpMethods.IsPost(context.Request.Method))
        {
            await HandlePostMethodNotAllowed(context);
            return;
        }

        CancellationToken cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        var request = await JsonSerializer.DeserializeAsync(
            context.Request.Body,
            ImportJsonContext.Default.ImportUrlImportRequest,
            cancellationToken);

        if (request == null)
        {
            await AdminResponseWriter.WriteErrorAsync(context, "Request body is required", StatusCodes.Status400BadRequest);
            return;
        }

        if (string.IsNullOrWhiteSpace(request.SourceUrl))
        {
            await AdminResponseWriter.WriteErrorAsync(context, "SourceUrl is required", StatusCodes.Status400BadRequest);
            return;
        }

        if (string.IsNullOrWhiteSpace(request.TableName))
        {
            await AdminResponseWriter.WriteErrorAsync(context, "Table name is required", StatusCodes.Status400BadRequest);
            return;
        }

        if (!ImportValidationHelpers.IsValidTableName(request.TableName))
        {
            await AdminResponseWriter.WriteErrorAsync(
                context,
                "Invalid table name. Use only letters, numbers, and underscores.",
                StatusCodes.Status400BadRequest);
            return;
        }

        if (!string.IsNullOrWhiteSpace(request.TargetSchema) &&
            !ImportValidationHelpers.IsValidSchemaName(request.TargetSchema))
        {
            await AdminResponseWriter.WriteErrorAsync(
                context,
                "Invalid target schema. Use only letters, numbers, and underscores.",
                StatusCodes.Status400BadRequest);
            return;
        }

        IFileImportService importService = context.RequestServices.GetRequiredService<IFileImportService>();
        var securityOptions = context.RequestServices.GetRequiredService<IOptions<FileUploadSecurityOptions>>();
        var maxFileSizeBytes = Math.Max(importService.Limits.BackgroundJobThresholdBytes, importService.Limits.MaxMemoryBytes);
        var staged = await DownloadRemoteImportSourceAsync(
            context,
            request.SourceUrl,
            request.FileName,
            importService,
            maxFileSizeBytes,
            securityOptions.Value.MaxSecurityScanSizeBytes,
            cancellationToken);

        if (staged.ErrorMessage != null)
        {
            await AdminResponseWriter.WriteErrorAsync(
                context,
                staged.ErrorMessage,
                staged.StatusCode ?? StatusCodes.Status400BadRequest);
            return;
        }

        var executionRequest = new ImportExecutionRequest
        {
            File = staged.File!,
            TableName = request.TableName,
            SourceKind = "url",
            SourceUrl = request.SourceUrl,
            SourceSrid = request.SourceSrid,
            TargetSrid = request.TargetSrid,
            TargetSchema = request.TargetSchema,
            OverwriteExisting = request.OverwriteExisting,
            ForceBackground = request.ForceBackground,
            TrackProgress = request.TrackProgress
        };

        try
        {
            await ExecuteStagedImportAsync(context, executionRequest, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or NotSupportedException)
        {
            var logger = context.RequestServices.GetRequiredService<ILogger<ImportEndpointsLog>>();
            Log.ImportFailed(logger, executionRequest.TableName, ex);
            await AdminResponseWriter.WriteErrorAsync(context, "Import failed: invalid or unsupported file format", StatusCodes.Status400BadRequest);
        }
        catch (Exception ex)
        {
            var logger = context.RequestServices.GetRequiredService<ILogger<ImportEndpointsLog>>();
            Log.ImportFailed(logger, executionRequest.TableName, ex);
            await AdminResponseWriter.WriteErrorAsync(context, "Import failed", StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task ExecuteStagedImportAsync(
        HttpContext context,
        ImportExecutionRequest request,
        CancellationToken cancellationToken)
    {
        IFileImportService importService = context.RequestServices.GetRequiredService<IFileImportService>();
        var limits = importService.Limits;
        var shouldQueueBackground = request.ForceBackground || request.File.SizeBytes > limits.BackgroundJobThresholdBytes;
        var cloudStorage = context.RequestServices.GetService<ICloudFileStorage>();
        var deleteLocalFile = true;
        string? cloudFileId = null;
        string? uploadId = request.UploadId;

        try
        {
            if (cloudStorage != null && (shouldQueueBackground || request.File.SizeBytes > 10 * 1024 * 1024))
            {
                var cloudUploadResult = await UploadStagedFileToCloudAsync(context, request, cloudStorage, cancellationToken);
                if (!cloudUploadResult.Success)
                {
                    await AdminResponseWriter.WriteErrorAsync(
                        context,
                        cloudUploadResult.ErrorMessage ?? "Failed to upload file to cloud storage.",
                        StatusCodes.Status500InternalServerError);
                    return;
                }

                cloudFileId = cloudUploadResult.CloudFileId;
                uploadId = cloudUploadResult.UploadId;
            }
            else if (!string.IsNullOrWhiteSpace(uploadId))
            {
                // Server-side multipart parsing can only expose progress after the
                // upload has been accepted because form fields may arrive after the
                // file section. The import job progress is authoritative from here.
                await ReportAcceptedUploadAsync(context, uploadId, request, cancellationToken).ConfigureAwait(false);
            }

            var importRequest = new ImportRequest
            {
                CloudFileId = cloudFileId,
                LocalFilePath = cloudFileId == null ? request.File.LocalFilePath : null,
                FileName = request.File.FileName,
                SourceKind = cloudFileId != null ? "cloud-file" : request.SourceKind,
                SourceUrl = request.SourceUrl,
                UploadId = uploadId,
                TableName = request.TableName,
                TargetSchema = request.TargetSchema,
                SourceSrid = request.SourceSrid,
                TargetSrid = request.TargetSrid,
                OverwriteExisting = request.OverwriteExisting
            };

            if (shouldQueueBackground)
            {
                var jobService = context.RequestServices.GetService<IImportJobService>();
                if (jobService == null)
                {
                    await AdminResponseWriter.WriteErrorAsync(
                        context,
                        "Background import service not available. File is too large for synchronous import.",
                        StatusCodes.Status503ServiceUnavailable);
                    return;
                }

                var jobId = await jobService.QueueImportAsync(importRequest, request.File.SizeBytes, cancellationToken);
                deleteLocalFile = cloudFileId != null;

                var response = new BackgroundImportResponse
                {
                    JobId = jobId,
                    Message = "File queued for background processing",
                    StatusUrl = $"/api/v1/admin/import/jobs/{jobId}",
                    CancelUrl = $"/api/v1/admin/import/jobs/{jobId}/cancel",
                    UploadId = uploadId
                };

                IResult result = Results.Json(
                    response,
                    ImportJsonContext.Default.BackgroundImportResponse,
                    statusCode: StatusCodes.Status202Accepted);
                await result.ExecuteAsync(context);
                return;
            }

            ImportResult importResult = await importService.ImportFileAsync(importRequest, cancellationToken);
            importResult = importResult with
            {
                SourceKind = importRequest.SourceKind,
                SourceUrl = importRequest.SourceUrl,
                UploadId = importRequest.UploadId,
                CloudFileId = importRequest.CloudFileId
            };
            IResult syncResult = Results.Json(importResult, ImportJsonContext.Default.ImportResult);
            await syncResult.ExecuteAsync(context);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (cloudStorage != null && !string.IsNullOrEmpty(uploadId))
            {
                try
                {
                    _ = await cloudStorage.CancelUploadAsync(uploadId, CancellationToken.None);
                }
                catch (Exception cleanupEx)
                {
                    var logger = context.RequestServices.GetRequiredService<ILogger<ImportEndpointsLog>>();
                    Log.CleanupFailed(logger, uploadId, cleanupEx);
                }
            }

            throw;
        }
        catch
        {
            if (cloudStorage != null && !string.IsNullOrWhiteSpace(cloudFileId))
            {
                try
                {
                    _ = await cloudStorage.DeleteAsync(cloudFileId, CancellationToken.None);
                }
                catch (Exception cleanupEx)
                {
                    var logger = context.RequestServices.GetRequiredService<ILogger<ImportEndpointsLog>>();
                    Log.CleanupFailed(logger, cloudFileId, cleanupEx);
                }
            }

            throw;
        }
        finally
        {
            if (deleteLocalFile)
            {
                MultipartParsingHelpers.TryDeleteFile(request.File.LocalFilePath);
            }
        }
    }

    private static async Task<CloudUploadStageResult> UploadStagedFileToCloudAsync(
        HttpContext context,
        ImportExecutionRequest request,
        ICloudFileStorage cloudStorage,
        CancellationToken cancellationToken)
    {
        var uploadId = string.IsNullOrWhiteSpace(request.UploadId) ? Guid.NewGuid().ToString() : request.UploadId!;
        var progressReporter = request.TrackProgress || !string.IsNullOrWhiteSpace(request.UploadId)
            ? CreateUploadProgressReporter(context, uploadId)
            : null;
        var contentType = string.IsNullOrWhiteSpace(request.File.ContentType)
            ? "application/octet-stream"
            : request.File.ContentType!;

        await using var uploadStream = OpenSequentialReadStream(
            request.File.LocalFilePath,
            context.RequestServices.GetRequiredService<IFileImportService>().Limits.StreamBufferSize);

        var uploadRequest = new FileUploadRequest
        {
            Content = uploadStream,
            FileName = request.File.FileName,
            ContentType = contentType,
            SizeBytes = request.File.SizeBytes,
            TimeToLive = TimeSpan.FromDays(1),
            Folder = "imports",
            Progress = progressReporter,
            UploadId = uploadId,
            Metadata = new Dictionary<string, string>
            {
                ["tableName"] = request.TableName,
                ["uploadedAt"] = DateTimeOffset.UtcNow.ToString("O"),
                ["sourceKind"] = request.SourceKind,
                ["sourceUrl"] = request.SourceUrl ?? string.Empty,
                ["sourceSrid"] = request.SourceSrid?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                ["targetSrid"] = request.TargetSrid.ToString(CultureInfo.InvariantCulture)
            }.ToImmutableDictionary()
        };

        var uploadResult = await cloudStorage.UploadAsync(uploadRequest, cancellationToken);
        if (!uploadResult.Success || uploadResult.File == null)
        {
            var logger = context.RequestServices.GetRequiredService<ILogger<ImportEndpointsLog>>();
            Log.CloudUploadFailed(
                logger,
                request.File.FileName,
                uploadResult.ErrorMessage ?? "Unknown cloud upload failure");
            return new CloudUploadStageResult
            {
                Success = false,
                ErrorMessage = "Failed to upload file to cloud storage."
            };
        }

        return new CloudUploadStageResult
        {
            Success = true,
            CloudFileId = uploadResult.File.FileId,
            UploadId = uploadId
        };
    }

    private static Progress<UploadProgress>? CreateUploadProgressReporter(HttpContext context, string uploadId)
    {
        var progressStore = context.RequestServices.GetService<IUploadProgressStore>();
        if (progressStore == null)
        {
            return null;
        }

        var progressLogger = context.RequestServices.GetRequiredService<ILogger<ImportEndpointsLog>>();
        return new Progress<UploadProgress>(progress => _ = ReportProgressAsync(progress));

        async Task ReportProgressAsync(UploadProgress progress)
        {
            try
            {
                await progressStore.SetProgressAsync(uploadId, progress, TimeSpan.FromHours(1), CancellationToken.None);
            }
            catch (Exception ex)
            {
                Log.ProgressUpdateFailed(progressLogger, uploadId, ex);
            }
        }
    }

    private static async Task ReportAcceptedUploadAsync(
        HttpContext context,
        string uploadId,
        ImportExecutionRequest request,
        CancellationToken cancellationToken)
    {
        var progressStore = context.RequestServices.GetService<IUploadProgressStore>();
        if (progressStore == null)
        {
            return;
        }

        var acceptedAt = DateTimeOffset.UtcNow;
        var progress = new UploadProgress
        {
            UploadId = uploadId,
            Status = OperationStatus.Completed,
            BytesUploaded = request.File.SizeBytes,
            TotalBytes = request.File.SizeBytes,
            FileName = request.File.FileName,
            ContentType = request.File.ContentType,
            StartedAt = acceptedAt,
            CompletedAt = acceptedAt,
            CurrentPhase = "Upload accepted; import job progress is available"
        };

        await progressStore.SetProgressAsync(uploadId, progress, TimeSpan.FromHours(1), cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<MultipartImportParseResult> ParseMultipartImportUploadAsync(
        HttpContext context,
        IFileImportService importService,
        long maxFileSizeBytes,
        long maxScanSizeBytes,
        CancellationToken cancellationToken)
    {
        if (!MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var mediaType))
        {
            return MultipartImportParseResult.Failure("Invalid multipart content type.", StatusCodes.Status400BadRequest);
        }

        var boundary = HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value;
        if (string.IsNullOrWhiteSpace(boundary))
        {
            return MultipartImportParseResult.Failure("Multipart request is missing a boundary.", StatusCodes.Status400BadRequest);
        }

        var reader = new MultipartReader(boundary, context.Request.Body);
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        StagedImportFile? stagedFile = null;

        try
        {
            MultipartSection? section;
            while ((section = await reader.ReadNextSectionAsync(cancellationToken)) != null)
            {
                if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var contentDisposition))
                {
                    continue;
                }

                if (MultipartParsingHelpers.HasFileContentDisposition(contentDisposition))
                {
                    if (stagedFile != null)
                    {
                        return MultipartImportParseResult.Failure("Only one file upload is supported per request.", StatusCodes.Status400BadRequest);
                    }

                    var fileName = MultipartParsingHelpers.ResolveFileName(contentDisposition);
                    if (string.IsNullOrWhiteSpace(fileName))
                    {
                        return MultipartImportParseResult.Failure("File name is required", StatusCodes.Status400BadRequest);
                    }

                    stagedFile = await StageImportSourceAsync(
                        section.Body,
                        fileName,
                        section.ContentType,
                        importService,
                        maxFileSizeBytes,
                        maxScanSizeBytes,
                        cancellationToken);
                    continue;
                }

                if (MultipartParsingHelpers.HasFormDataContentDisposition(contentDisposition))
                {
                    var fieldName = HeaderUtilities.RemoveQuotes(contentDisposition.Name).Value;
                    if (!string.IsNullOrWhiteSpace(fieldName))
                    {
                        fields[fieldName] = await MultipartParsingHelpers.ReadSectionStringAsync(section, cancellationToken);
                    }
                }
            }

            if (stagedFile == null)
            {
                return MultipartImportParseResult.Failure("File is required", StatusCodes.Status400BadRequest);
            }

            if (!fields.TryGetValue("TableName", out var tableName) || string.IsNullOrWhiteSpace(tableName))
            {
                MultipartParsingHelpers.TryDeleteFile(stagedFile.LocalFilePath);
                return MultipartImportParseResult.Failure("Table name is required", StatusCodes.Status400BadRequest);
            }

            if (!ImportValidationHelpers.IsValidTableName(tableName))
            {
                MultipartParsingHelpers.TryDeleteFile(stagedFile.LocalFilePath);
                return MultipartImportParseResult.Failure(
                    "Invalid table name. Use only letters, numbers, and underscores.",
                    StatusCodes.Status400BadRequest);
            }

            var targetSchema = NormalizeOptionalField(fields, "TargetSchema");
            if (!string.IsNullOrWhiteSpace(targetSchema) &&
                !ImportValidationHelpers.IsValidSchemaName(targetSchema))
            {
                MultipartParsingHelpers.TryDeleteFile(stagedFile.LocalFilePath);
                return MultipartImportParseResult.Failure(
                    "Invalid target schema. Use only letters, numbers, and underscores.",
                    StatusCodes.Status400BadRequest);
            }

            var request = new ImportExecutionRequest
            {
                File = stagedFile,
                TableName = tableName,
                UploadId = NormalizeOptionalOperationId(fields, "UploadId"),
                SourceSrid = TryParseNullableInt(fields, "SourceSrid"),
                TargetSrid = TryParseNullableInt(fields, "TargetSrid") ?? 4326,
                TargetSchema = targetSchema,
                OverwriteExisting = TryParseBool(fields, "OverwriteExisting"),
                ForceBackground = TryParseBool(fields, "ForceBackground"),
                TrackProgress = TryParseBool(fields, "TrackProgress")
            };

            return MultipartImportParseResult.Success(request);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or ArgumentException)
        {
            MultipartParsingHelpers.TryDeleteFile(stagedFile?.LocalFilePath);
            return MultipartImportParseResult.Failure(InvalidMultipartImportRequestMessage, StatusCodes.Status400BadRequest);
        }
    }

    private static async Task<RemoteImportDownloadResult> DownloadRemoteImportSourceAsync(
        HttpContext context,
        string sourceUrl,
        string? fileNameOverride,
        IFileImportService importService,
        long maxFileSizeBytes,
        long maxScanSizeBytes,
        CancellationToken cancellationToken)
    {
        var validation = await ImportSourceUrlValidation.ValidateAsync(sourceUrl, cancellationToken);
        if (!validation.IsValid)
        {
            return RemoteImportDownloadResult.Failure(
                validation.ErrorMessage ?? "SourceUrl is invalid.",
                StatusCodes.Status400BadRequest);
        }

        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var sourceUri))
        {
            return RemoteImportDownloadResult.Failure("SourceUrl is invalid.", StatusCodes.Status400BadRequest);
        }

        var httpClientFactory = context.RequestServices.GetRequiredService<IHttpClientFactory>();
        using var requestMessage = new HttpRequestMessage(HttpMethod.Get, sourceUri);
        var httpClient = httpClientFactory.CreateClient("import-source");
        using var response = await httpClient
            .SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        // If the server responds with a redirect, reject it rather than following
        // to an unvalidated destination (SSRF defense).
        if ((int)response.StatusCode is >= 300 and < 400)
        {
            return RemoteImportDownloadResult.Failure(
                "Source URL returned a redirect, which is not permitted.",
                StatusCodes.Status400BadRequest);
        }

        if (!response.IsSuccessStatusCode)
        {
            return RemoteImportDownloadResult.Failure(
                $"Failed to fetch source object: {(int)response.StatusCode} {response.ReasonPhrase}",
                StatusCodes.Status502BadGateway);
        }

        var fileName = ResolveRemoteFileName(
            sourceUri,
            response.Content.Headers.ContentDisposition?.FileNameStar,
            response.Content.Headers.ContentDisposition?.FileName,
            fileNameOverride);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return RemoteImportDownloadResult.Failure(
                "Unable to determine file name for remote import source.",
                StatusCodes.Status400BadRequest);
        }

        try
        {
            await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var stagedFile = await StageImportSourceAsync(
                responseStream,
                fileName,
                response.Content.Headers.ContentType?.MediaType,
                importService,
                maxFileSizeBytes,
                maxScanSizeBytes,
                cancellationToken);
            return RemoteImportDownloadResult.Success(stagedFile);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or ArgumentException)
        {
            return RemoteImportDownloadResult.Failure(
                "Invalid remote import source.",
                StatusCodes.Status400BadRequest);
        }
    }

    private static async Task<StagedImportFile> StageImportSourceAsync(
        Stream sourceStream,
        string originalFileName,
        string? contentType,
        IFileImportService importService,
        long maxFileSizeBytes,
        long maxScanSizeBytes,
        CancellationToken cancellationToken)
    {
        var safeFileName = FileUploadSecurity.SanitizeFileName(originalFileName);
        var extension = Path.GetExtension(safeFileName);
        if (string.Equals(extension, ".shp", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Shapefile uploads must be a .zip containing .shp and .dbf files.");
        }

        var tempFilePath = CreateStagedImportFilePath(safeFileName);
        try
        {
            var sizeBytes = await MultipartParsingHelpers.CopyStreamToTempFileAsync(sourceStream, tempFilePath, maxFileSizeBytes, cancellationToken);
            var validation = await FileUploadSecurity.ValidateFileAsync(
                safeFileName,
                string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType,
                sizeBytes,
                () => OpenSequentialReadStream(tempFilePath, importService.Limits.StreamBufferSize),
                maxFileSizeBytes,
                maxScanSizeBytes,
                cancellationToken);
            if (!validation.IsValid)
            {
                throw new InvalidDataException(validation.ErrorMessage ?? "File validation failed.");
            }

            SupportedFileFormat? format = importService.DetectFormat(safeFileName);
            if (format == null)
            {
                throw new NotSupportedException($"Unsupported file format: {Path.GetExtension(safeFileName)}");
            }

            return new StagedImportFile
            {
                FileName = safeFileName,
                ContentType = contentType,
                LocalFilePath = tempFilePath,
                SizeBytes = sizeBytes
            };
        }
        catch
        {
            MultipartParsingHelpers.TryDeleteFile(tempFilePath);
            throw;
        }
    }

    private static string? ResolveRemoteFileName(
        Uri sourceUri,
        string? contentDispositionFileNameStar,
        string? contentDispositionFileName,
        string? fileNameOverride)
    {
        if (!string.IsNullOrWhiteSpace(fileNameOverride))
        {
            return fileNameOverride;
        }

        if (!string.IsNullOrWhiteSpace(contentDispositionFileNameStar))
        {
            return HeaderUtilities.RemoveQuotes(contentDispositionFileNameStar).Value;
        }

        if (!string.IsNullOrWhiteSpace(contentDispositionFileName))
        {
            return HeaderUtilities.RemoveQuotes(contentDispositionFileName).Value;
        }

        var fileName = Path.GetFileName(sourceUri.AbsolutePath);
        return string.IsNullOrWhiteSpace(fileName) ? null : WebUtility.UrlDecode(fileName);
    }

    private static int? TryParseNullableInt(Dictionary<string, string> fields, string fieldName)
        => fields.TryGetValue(fieldName, out var value) &&
           int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    private static string? NormalizeOptionalOperationId(Dictionary<string, string> fields, string fieldName)
    {
        if (!fields.TryGetValue(fieldName, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.Length > 128 || trimmed.Any(static ch => !(char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' or ':')))
        {
            throw new ArgumentException("UploadId contains invalid characters.");
        }

        return trimmed;
    }

    private static string? NormalizeOptionalField(Dictionary<string, string> fields, string fieldName)
        => fields.TryGetValue(fieldName, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;

    private static bool TryParseBool(Dictionary<string, string> fields, string fieldName)
        => fields.TryGetValue(fieldName, out var value) &&
           bool.TryParse(value, out var parsed) &&
           parsed;

    private static string CreateStagedImportFilePath(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return Path.Combine(
            Path.GetTempPath(),
            "honua-import-staging",
            $"{Guid.NewGuid():N}{extension}");
    }

    private static FileStream OpenSequentialReadStream(string path, int bufferSize)
        => new(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            BufferSize = bufferSize,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        });


    /// <summary>
    /// Get current import configuration limits
    /// </summary>
    private static async Task HandleGetLimits(HttpContext context)
    {
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            await HandleGetMethodNotAllowed(context);
            return;
        }

        IFileImportService importService = context.RequestServices.GetRequiredService<IFileImportService>();
        var limits = importService.Limits;

        IResult result = Results.Json(limits, ImportJsonContext.Default.ImportLimits);
        await result.ExecuteAsync(context);
    }

    /// <summary>
    /// Get the progress of a file upload operation
    /// </summary>
    private static async Task HandleGetUploadProgress(HttpContext context)
    {
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            await HandleGetMethodNotAllowed(context);
            return;
        }

        var uploadId = context.GetRouteValue("uploadId")?.ToString();
        if (string.IsNullOrEmpty(uploadId))
        {
            await AdminResponseWriter.WriteErrorAsync(context, "Upload ID is required", StatusCodes.Status400BadRequest);
            return;
        }

        CancellationToken cancellationToken = context.RequestAborted;
        var progressStore = context.RequestServices.GetService<IUploadProgressStore>();
        if (progressStore != null)
        {
            var storedProgress = await progressStore.GetProgressAsync(uploadId, cancellationToken);
            if (storedProgress != null)
            {
                IResult storedResult = Results.Json(storedProgress, ImportJsonContext.Default.UploadProgress);
                await storedResult.ExecuteAsync(context);
                return;
            }
        }

        var cloudStorage = context.RequestServices.GetService<ICloudFileStorage>();
        if (cloudStorage == null)
        {
            await AdminResponseWriter.WriteErrorAsync(context, "File storage service not available", StatusCodes.Status503ServiceUnavailable);
            return;
        }

        var progress = await cloudStorage.GetUploadProgressAsync(uploadId, cancellationToken);

        if (progress == null)
        {
            await AdminResponseWriter.WriteErrorAsync(context, "Upload not found", StatusCodes.Status404NotFound);
            return;
        }

        IResult result = Results.Json(progress, ImportJsonContext.Default.UploadProgress);
        await result.ExecuteAsync(context);
    }

    /// <summary>
    /// Cancel a running file upload operation
    /// </summary>
    private static async Task HandleCancelUpload(HttpContext context)
    {
        if (!HttpMethods.IsPost(context.Request.Method))
        {
            await HandlePostMethodNotAllowed(context);
            return;
        }

        var uploadId = context.GetRouteValue("uploadId")?.ToString();
        if (string.IsNullOrEmpty(uploadId))
        {
            await AdminResponseWriter.WriteErrorAsync(context, "Upload ID is required", StatusCodes.Status400BadRequest);
            return;
        }

        var cloudStorage = context.RequestServices.GetService<ICloudFileStorage>();
        if (cloudStorage == null)
        {
            await AdminResponseWriter.WriteErrorAsync(context, "File storage service not available", StatusCodes.Status503ServiceUnavailable);
            return;
        }

        CancellationToken cancellationToken = context.RequestAborted;
        var cancelled = await cloudStorage.CancelUploadAsync(uploadId, cancellationToken);

        if (!cancelled)
        {
            await AdminResponseWriter.WriteErrorAsync(context, "Upload not found or already completed", StatusCodes.Status404NotFound);
            return;
        }

        var response = new CancelUploadResponse { UploadId = uploadId, Message = "Upload cancelled" };
        IResult result = Results.Json(response, ImportJsonContext.Default.CancelUploadResponse);
        await result.ExecuteAsync(context);
    }

    /// <summary>
    /// Get all active file upload operations
    /// </summary>
    private static async Task HandleGetActiveUploads(HttpContext context)
    {
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            await HandleGetMethodNotAllowed(context);
            return;
        }

        CancellationToken cancellationToken = context.RequestAborted;
        var progressStore = context.RequestServices.GetService<IUploadProgressStore>();
        var cloudStorage = context.RequestServices.GetService<ICloudFileStorage>();
        if (cloudStorage == null && progressStore != null)
        {
            var uploadsFromStore = await progressStore.GetActiveUploadsAsync(cancellationToken);
            var storeResponse = new ActiveUploadsResponse { Uploads = uploadsFromStore.ToArray() };
            IResult storeResult = Results.Json(storeResponse, ImportJsonContext.Default.ActiveUploadsResponse);
            await storeResult.ExecuteAsync(context);
            return;
        }

        if (cloudStorage == null)
        {
            await AdminResponseWriter.WriteErrorAsync(context, "File storage service not available", StatusCodes.Status503ServiceUnavailable);
            return;
        }

        var uploads = await cloudStorage.GetActiveUploadsAsync(cancellationToken);

        var response = new ActiveUploadsResponse { Uploads = uploads.ToArray() };
        IResult result = Results.Json(response, ImportJsonContext.Default.ActiveUploadsResponse);
        await result.ExecuteAsync(context);
    }

    /// <summary>
    /// Get all active import jobs
    /// </summary>
    private static async Task HandleGetActiveJobs(HttpContext context)
    {
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            await HandleGetMethodNotAllowed(context);
            return;
        }

        var jobService = context.RequestServices.GetService<IImportJobService>();
        if (jobService == null)
        {
            await AdminResponseWriter.WriteErrorAsync(context, "Import job service not available", StatusCodes.Status503ServiceUnavailable);
            return;
        }

        try
        {
            CancellationToken cancellationToken = context.RequestAborted;
            var jobs = await jobService.GetActiveJobsAsync(cancellationToken);

            var response = new ActiveImportJobsResponse { Jobs = jobs.ToArray() };
            IResult result = Results.Json(response, ImportJsonContext.Default.ActiveImportJobsResponse);
            await result.ExecuteAsync(context);
        }
        catch (Exception ex)
        {
            var logger = context.RequestServices.GetRequiredService<ILogger<ImportEndpointsLog>>();
            Log.ImportFailed(logger, "jobs", ex);
            await AdminResponseWriter.WriteErrorAsync(context, "Import job service unavailable", StatusCodes.Status503ServiceUnavailable);
        }
    }

    /// <summary>
    /// Get a specific import job status
    /// </summary>
    private static async Task HandleGetImportJobStatus(HttpContext context)
    {
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            await HandleGetMethodNotAllowed(context);
            return;
        }

        var jobId = context.GetRouteValue("jobId")?.ToString();
        if (string.IsNullOrWhiteSpace(jobId))
        {
            await AdminResponseWriter.WriteErrorAsync(context, "Job ID is required", StatusCodes.Status400BadRequest);
            return;
        }

        var jobService = context.RequestServices.GetService<IImportJobService>();
        if (jobService == null)
        {
            await AdminResponseWriter.WriteErrorAsync(context, "Import job service not available", StatusCodes.Status503ServiceUnavailable);
            return;
        }

        try
        {
            CancellationToken cancellationToken = context.RequestAborted;
            var progress = await jobService.GetProgressAsync(jobId, cancellationToken);

            if (progress == null)
            {
                await AdminResponseWriter.WriteErrorAsync(context, "Import job not found", StatusCodes.Status404NotFound);
                return;
            }

            IResult result = Results.Json(progress, ImportJsonContext.Default.ImportProgress);
            await result.ExecuteAsync(context);
        }
        catch (Exception ex)
        {
            var logger = context.RequestServices.GetRequiredService<ILogger<ImportEndpointsLog>>();
            Log.ImportFailed(logger, jobId, ex);
            await AdminResponseWriter.WriteErrorAsync(context, "Import job service unavailable", StatusCodes.Status503ServiceUnavailable);
        }
    }

    /// <summary>
    /// Cancel a running import job
    /// </summary>
    private static async Task HandleCancelImportJob(HttpContext context)
    {
        if (!HttpMethods.IsPost(context.Request.Method))
        {
            await HandlePostMethodNotAllowed(context);
            return;
        }

        var jobId = context.GetRouteValue("jobId")?.ToString();
        if (string.IsNullOrWhiteSpace(jobId))
        {
            await AdminResponseWriter.WriteErrorAsync(context, "Job ID is required", StatusCodes.Status400BadRequest);
            return;
        }

        var jobService = context.RequestServices.GetService<IImportJobService>();
        if (jobService == null)
        {
            await AdminResponseWriter.WriteErrorAsync(context, "Import job service not available", StatusCodes.Status503ServiceUnavailable);
            return;
        }

        try
        {
            CancellationToken cancellationToken = context.RequestAborted;
            var cancelled = await jobService.CancelJobAsync(jobId, cancellationToken);

            if (!cancelled)
            {
                await AdminResponseWriter.WriteErrorAsync(context, "Import job not found", StatusCodes.Status404NotFound);
                return;
            }

            var response = new CancelImportJobResponse
            {
                JobId = jobId,
                Message = "Import job cancellation requested"
            };

            IResult result = Results.Json(response, ImportJsonContext.Default.CancelImportJobResponse);
            await result.ExecuteAsync(context);
        }
        catch (Exception ex)
        {
            var logger = context.RequestServices.GetRequiredService<ILogger<ImportEndpointsLog>>();
            Log.ImportFailed(logger, jobId, ex);
            await AdminResponseWriter.WriteErrorAsync(context, "Import job service unavailable", StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static IFormFile? GetFormFile(IFormCollection form, string primaryName, string fallbackName) => form.Files.GetFile(primaryName) ?? form.Files.GetFile(fallbackName);

    private static partial class Log
    {
        /// <summary>
        /// Logs when importing a file preview fails.
        /// </summary>
        /// <param name="logger">The logger instance.</param>
        /// <param name="fileName">The name of the file that failed to preview.</param>
        /// <param name="exception">The exception that caused the failure.</param>
        [LoggerMessage(EventId = 3300, Level = LogLevel.Error, Message = "Failed to preview import file {FileName}")]
        public static partial void PreviewFailed(ILogger logger, string fileName, Exception exception);

        /// <summary>
        /// Logs when importing data to a table fails.
        /// </summary>
        /// <param name="logger">The logger instance.</param>
        /// <param name="tableName">The name of the table where import failed.</param>
        /// <param name="exception">The exception that caused the failure.</param>
        [LoggerMessage(EventId = 3301, Level = LogLevel.Error, Message = "Import failed for table {TableName}")]
        public static partial void ImportFailed(ILogger logger, string tableName, Exception exception);

        /// <summary>
        /// Logs when upload progress fails to update.
        /// </summary>
        /// <param name="logger">The logger instance.</param>
        /// <param name="uploadId">The upload identifier.</param>
        /// <param name="exception">The exception that caused the failure.</param>
        [LoggerMessage(EventId = 3302, Level = LogLevel.Warning, Message = "Failed to update upload progress for {UploadId}")]
        public static partial void ProgressUpdateFailed(ILogger logger, string uploadId, Exception exception);

        /// <summary>
        /// Logs when cleanup of cancelled import resources fails.
        /// </summary>
        /// <param name="logger">The logger instance.</param>
        /// <param name="uploadId">The upload identifier.</param>
        /// <param name="exception">The exception that caused the failure.</param>
        [LoggerMessage(EventId = 3303, Level = LogLevel.Warning, Message = "Failed to cleanup cancelled import resources for {UploadId}")]
        public static partial void CleanupFailed(ILogger logger, string uploadId, Exception exception);

        /// <summary>
        /// Logs when cloud storage upload fails during import staging.
        /// </summary>
        /// <param name="logger">The logger instance.</param>
        /// <param name="fileName">The sanitized file name.</param>
        /// <param name="failureReason">Provider-specific failure reason.</param>
        [LoggerMessage(EventId = 3304, Level = LogLevel.Warning, Message = "Cloud upload failed for import file {FileName}: {FailureReason}")]
        public static partial void CloudUploadFailed(ILogger logger, string fileName, string failureReason);
    }

}

/// <summary>
/// Response containing supported file formats and their descriptions for the import API
/// </summary>
internal sealed record FileFormatsResponse
{
    public required string[] SupportedExtensions { get; init; }
    public required Dictionary<string, string> FormatDescriptions { get; init; }
}

/// <summary>
/// Response when a file is queued for background import processing
/// </summary>
internal sealed record BackgroundImportResponse
{
    /// <summary>
    /// Unique identifier for tracking the import job
    /// </summary>
    public required string JobId { get; init; }

    /// <summary>
    /// Human-readable status message
    /// </summary>
    public required string Message { get; init; }

    /// <summary>
    /// URL to check the status of the import job
    /// </summary>
    public required string StatusUrl { get; init; }

    /// <summary>
    /// URL to cancel the import job
    /// </summary>
    public required string CancelUrl { get; init; }

    /// <summary>
    /// Upload ID for tracking file upload progress (optional)
    /// </summary>
    public string? UploadId { get; init; }
}

internal sealed record PreviewUrlImportRequest
{
    public required string SourceUrl { get; init; }
    public string? FileName { get; init; }
}

internal sealed record ImportUrlImportRequest
{
    public required string SourceUrl { get; init; }
    public string? FileName { get; init; }
    public required string TableName { get; init; }
    public string? TargetSchema { get; init; }
    public int? SourceSrid { get; init; }
    public int TargetSrid { get; init; } = 4326;
    public bool OverwriteExisting { get; init; }
    public bool ForceBackground { get; init; }
    public bool TrackProgress { get; init; }
}

internal sealed record StagedImportFile
{
    public required string FileName { get; init; }
    public string? ContentType { get; init; }
    public required string LocalFilePath { get; init; }
    public long SizeBytes { get; init; }
}

internal sealed record ImportExecutionRequest
{
    public required StagedImportFile File { get; init; }
    public required string TableName { get; init; }
    public string SourceKind { get; init; } = "file";
    public string? SourceUrl { get; init; }
    public string? UploadId { get; init; }
    public string? TargetSchema { get; init; }
    public int? SourceSrid { get; init; }
    public int TargetSrid { get; init; } = 4326;
    public bool OverwriteExisting { get; init; }
    public bool ForceBackground { get; init; }
    public bool TrackProgress { get; init; }
}

internal sealed record MultipartImportParseResult
{
    public ImportExecutionRequest? Request { get; init; }
    public string? ErrorMessage { get; init; }
    public int? StatusCode { get; init; }

    public static MultipartImportParseResult Success(ImportExecutionRequest request)
        => new() { Request = request };

    public static MultipartImportParseResult Failure(string errorMessage, int statusCode)
        => new() { ErrorMessage = errorMessage, StatusCode = statusCode };
}

internal sealed record RemoteImportDownloadResult
{
    public StagedImportFile? File { get; init; }
    public string? ErrorMessage { get; init; }
    public int? StatusCode { get; init; }

    public static RemoteImportDownloadResult Success(StagedImportFile file)
        => new() { File = file };

    public static RemoteImportDownloadResult Failure(string errorMessage, int statusCode)
        => new() { ErrorMessage = errorMessage, StatusCode = statusCode };
}

internal sealed record CloudUploadStageResult
{
    public bool Success { get; init; }
    public string? CloudFileId { get; init; }
    public string? UploadId { get; init; }
    public string? ErrorMessage { get; init; }
}


/// <summary>
/// Response for cancel upload request
/// </summary>
internal sealed record CancelUploadResponse
{
    /// <summary>
    /// Upload identifier that was cancelled
    /// </summary>
    public required string UploadId { get; init; }

    /// <summary>
    /// Confirmation message
    /// </summary>
    public required string Message { get; init; }
}

/// <summary>
/// Response containing all active uploads
/// </summary>
internal sealed record ActiveUploadsResponse
{
    /// <summary>
    /// List of active upload operations
    /// </summary>
    public required UploadProgress[] Uploads { get; init; }
}

/// <summary>
/// Response containing all active import jobs
/// </summary>
internal sealed record ActiveImportJobsResponse
{
    /// <summary>
    /// List of active import jobs
    /// </summary>
    public required ImportProgress[] Jobs { get; init; }
}

/// <summary>
/// Response for cancel import job request
/// </summary>
internal sealed record CancelImportJobResponse
{
    /// <summary>
    /// Job identifier that was cancelled
    /// </summary>
    public required string JobId { get; init; }

    /// <summary>
    /// Confirmation message
    /// </summary>
    public required string Message { get; init; }
}
