// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Security;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Import;

/// <summary>
/// File import endpoints for uploading and processing geospatial files
/// </summary>
internal static partial class ImportEndpoints
{
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
        _ = group.Map("/formats", HandleGetSupportedFormats)
            .WithName($"GetSupportedFileFormats{nameSuffix}")
            .WithSummary("Get supported geospatial file formats")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        // Preview file before import
        _ = group.Map("/preview", HandlePreviewFile)
            .WithName($"PreviewFile{nameSuffix}")
            .WithSummary("Preview geospatial file contents")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }))
            .DisableAntiforgery(); // For file uploads

        // Import geospatial file
        _ = group.Map("/upload", HandleImportFile)
            .WithName($"ImportFile{nameSuffix}")
            .WithSummary("Import geospatial file to PostgreSQL using memory-efficient streaming")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }))
            .DisableAntiforgery(); // For file uploads

        // Get upload progress
        _ = group.Map("/uploads/{uploadId}/progress", HandleGetUploadProgress)
            .WithName($"GetUploadProgress{nameSuffix}")
            .WithSummary("Get the progress of a file upload operation")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        // Cancel upload
        _ = group.Map("/uploads/{uploadId}/cancel", HandleCancelUpload)
            .WithName($"CancelUpload{nameSuffix}")
            .WithSummary("Cancel a running file upload operation")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));

        // Get all active uploads
        _ = group.Map("/uploads", HandleGetActiveUploads)
            .WithName($"GetActiveUploads{nameSuffix}")
            .WithSummary("Get all active file upload operations")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        // Get active import jobs
        _ = group.Map("/jobs", HandleGetActiveJobs)
            .WithName($"GetActiveImportJobs{nameSuffix}")
            .WithSummary("Get all active import jobs")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        // Get import job status
        _ = group.Map("/jobs/{jobId}", HandleGetImportJobStatus)
            .WithName($"GetImportJobStatus{nameSuffix}")
            .WithSummary("Get the status of an import job")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        // Cancel import job
        _ = group.Map("/jobs/{jobId}/cancel", HandleCancelImportJob)
            .WithName($"CancelImportJob{nameSuffix}")
            .WithSummary("Cancel a running import job")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));

        // Get import limits configuration
        _ = group.Map("/limits", HandleGetLimits)
            .WithName($"GetImportLimits{nameSuffix}")
            .WithSummary("Get current import configuration limits")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));
    }

    /// <summary>
    /// Get supported file formats and extensions
    /// </summary>
    private static async Task HandleGetSupportedFormats(HttpContext context)
    {
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            await ProblemDetailsHelpers.CreateAdminProblem(
                    context,
                    StatusCodes.Status405MethodNotAllowed,
                    "Method not allowed.")
                .ExecuteAsync(context);
            return;
        }

        IFileImportService importService = context.RequestServices.GetRequiredService<IFileImportService>();
        string[] extensions = importService.GetSupportedExtensions();
        var formatDescriptions = new Dictionary<string, string>
        {
            [".geojson"] = "GeoJSON - Web-standard JSON format",
            [".json"] = "JSON - May contain GeoJSON data",
            [".shp"] = "Shapefile - vector format (requires .shx, .dbf)",
            [".gpkg"] = "GeoPackage - OGC SQLite-based format",
            [".gpx"] = "GPX - GPS Exchange format",
            [".kml"] = "KML - Keyhole Markup Language (Google Earth)",
            [".kmz"] = "KMZ - Compressed KML format",
            [".gml"] = "GML - Geography Markup Language",
            [".wkt"] = "WKT - Well-Known Text format",
            [".twkb"] = "TinyWKB - Compact binary format"
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
        if (!HttpMethods.IsPost(context.Request.Method))
        {
            await ProblemDetailsHelpers.CreateAdminProblem(
                    context,
                    StatusCodes.Status405MethodNotAllowed,
                    "Method not allowed.")
                .ExecuteAsync(context);
            return;
        }

        CancellationToken cancellationToken = GetTimeoutAwareCancellationToken(context);
        IFormCollection form = await context.Request.ReadFormAsync(cancellationToken);
        IFormFile? file = GetFormFile(form, "file", "File");

        if (file == null || file.Length == 0)
        {
            await WriteErrorAsync(context, "File is empty", StatusCodes.Status400BadRequest);
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
            await WriteErrorAsync(context, previewValidation.ErrorMessage ?? "File validation failed", StatusCodes.Status400BadRequest);
            return;
        }

        var safeFileName = FileUploadSecurity.SanitizeFileName(file.FileName);
        SupportedFileFormat? format = importService.DetectFormat(safeFileName);
        if (format == null)
        {
            await WriteErrorAsync(context, $"Unsupported file format: {Path.GetExtension(safeFileName)}",
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
            await WriteErrorAsync(context, "Failed to preview file", StatusCodes.Status400BadRequest);
        }
        catch (Exception ex)
        {
            var logger = context.RequestServices.GetRequiredService<ILogger<ImportEndpointsLog>>();
            Log.PreviewFailed(logger, file?.FileName ?? "unknown", ex);
            await WriteErrorAsync(context, "Failed to preview file", StatusCodes.Status500InternalServerError);
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
            await ProblemDetailsHelpers.CreateAdminProblem(
                    context,
                    StatusCodes.Status405MethodNotAllowed,
                    "Method not allowed.")
                .ExecuteAsync(context);
            return;
        }

        CancellationToken cancellationToken = GetTimeoutAwareCancellationToken(context);
        IFormCollection form = await context.Request.ReadFormAsync(cancellationToken);

        IFormFile? file = GetFormFile(form, "File", "file");
        if (file == null || file.Length == 0)
        {
            await WriteErrorAsync(context, "File is required", StatusCodes.Status400BadRequest);
            return;
        }

        string tableName = form["TableName"].ToString();
        if (string.IsNullOrWhiteSpace(tableName))
        {
            await WriteErrorAsync(context, "Table name is required", StatusCodes.Status400BadRequest);
            return;
        }

        // Validate table name (basic SQL injection prevention)
        if (!IsValidTableName(tableName))
        {
            await WriteErrorAsync(context, "Invalid table name. Use only letters, numbers, and underscores.",
                StatusCodes.Status400BadRequest);
            return;
        }

        IFileImportService importService = context.RequestServices.GetRequiredService<IFileImportService>();
        var securityOptions = context.RequestServices.GetRequiredService<IOptions<FileUploadSecurityOptions>>();
        var maxFileSizeBytes = Math.Max(importService.Limits.BackgroundJobThresholdBytes, importService.Limits.MaxMemoryBytes);
        var uploadValidation = await FileUploadSecurity.ValidateFileAsync(
            file,
            maxFileSizeBytes,
            securityOptions.Value.MaxSecurityScanSizeBytes,
            cancellationToken);
        if (!uploadValidation.IsValid)
        {
            await WriteErrorAsync(context, uploadValidation.ErrorMessage ?? "File validation failed", StatusCodes.Status400BadRequest);
            return;
        }

        var safeFileName = FileUploadSecurity.SanitizeFileName(file.FileName);
        SupportedFileFormat? format = importService.DetectFormat(safeFileName);
        if (format == null)
        {
            await WriteErrorAsync(context, $"Unsupported file format: {Path.GetExtension(safeFileName)}",
                StatusCodes.Status400BadRequest);
            return;
        }

        try
        {
            // Parse optional parameters
            int? sourceSrid = int.TryParse(form["SourceSrid"], out int src) ? src : (int?)null;
            int targetSrid = int.TryParse(form["TargetSrid"], out int tgt) ? tgt : 4326;
            bool overwriteExisting = bool.TryParse(form["OverwriteExisting"], out bool overwrite) && overwrite;
            bool forceBackground = bool.TryParse(form["ForceBackground"], out bool forceBg) && forceBg;
            bool trackProgress = bool.TryParse(form["TrackProgress"], out bool track) && track;

            // Check if file should be processed in background
            var limits = importService.Limits;
            var shouldQueueBackground = forceBackground || file.Length > limits.BackgroundJobThresholdBytes;

            // First, upload file to cloud storage for better handling of large files
            var cloudStorage = context.RequestServices.GetService<Honua.Core.Features.Infrastructure.Abstractions.ICloudFileStorage>();
            string? cloudFileId = null;
            string? uploadId = null;

            if (cloudStorage != null && (shouldQueueBackground || file.Length > 10 * 1024 * 1024)) // Use cloud storage for files > 10MB
            {
                using Stream uploadStream = file.OpenReadStream();
                uploadId = Guid.NewGuid().ToString();

                // Set up progress tracking if requested
                IProgress<UploadProgress>? progressReporter = null;
                if (trackProgress)
                {
                    var progressStore = context.RequestServices.GetService<IUploadProgressStore>();
                    if (progressStore != null)
                    {
                        progressReporter = new Progress<UploadProgress>(async progress =>
                        {
                            await progressStore.SetProgressAsync(uploadId, progress, TimeSpan.FromHours(1), CancellationToken.None);
                        });
                    }
                }

                var uploadRequest = new Honua.Core.Features.Infrastructure.Domain.FileUploadRequest
                {
                    Content = uploadStream,
                    FileName = safeFileName,
                    ContentType = file.ContentType ?? "application/octet-stream",
                    SizeBytes = file.Length,
                    TimeToLive = TimeSpan.FromDays(1), // Temporary storage for processing
                    Folder = "imports",
                    Progress = progressReporter,
                    UploadId = uploadId,
                    Metadata = new Dictionary<string, string>
                    {
                        ["tableName"] = tableName,
                        ["uploadedAt"] = DateTimeOffset.UtcNow.ToString("O"),
                        ["sourceSrid"] = sourceSrid?.ToString() ?? "",
                        ["targetSrid"] = targetSrid.ToString()
                    }.ToImmutableDictionary()
                };

                var uploadResult = await cloudStorage.UploadAsync(uploadRequest, cancellationToken);
                if (!uploadResult.Success)
                {
                    await WriteErrorAsync(context, $"Failed to upload file to cloud storage: {uploadResult.ErrorMessage}",
                        StatusCodes.Status500InternalServerError);
                    return;
                }
                cloudFileId = uploadResult.File!.FileId;
            }

            Stream? localStream = null;
            try
            {
                // Create import request with either cloud file reference or local stream
                ImportRequest importRequest;
                if (cloudFileId != null)
                {
                    // Create request referencing cloud-stored file
                    importRequest = new ImportRequest
                    {
                        CloudFileId = cloudFileId,
                        FileName = safeFileName,
                        TableName = tableName,
                        SourceSrid = sourceSrid,
                        TargetSrid = targetSrid,
                        OverwriteExisting = overwriteExisting
                    };
                }
                else
                {
                    // Fallback to local stream processing
                    localStream = file.OpenReadStream();
                    importRequest = new ImportRequest
                    {
                        FileStream = localStream,
                        FileName = safeFileName,
                        TableName = tableName,
                        SourceSrid = sourceSrid,
                        TargetSrid = targetSrid,
                        OverwriteExisting = overwriteExisting
                    };
                }

                if (shouldQueueBackground)
                {
                    // Queue for background processing
                    var jobService = context.RequestServices.GetService<IImportJobService>();
                    if (jobService == null)
                    {
                        await WriteErrorAsync(context,
                            "Background import service not available. File is too large for synchronous import.",
                            StatusCodes.Status503ServiceUnavailable);
                        return;
                    }

                    var jobId = await jobService.QueueImportAsync(importRequest, file.Length, cancellationToken);

                    var response = new BackgroundImportResponse
                    {
                        JobId = jobId,
                        Message = "File queued for background processing",
                        StatusUrl = $"/api/v1/admin/operations/{jobId}",
                        CancelUrl = $"/api/v1/admin/operations/{jobId}/cancel",
                        UploadId = uploadId // Include upload ID for progress tracking
                    };

                    IResult result = Results.Json(response, ImportJsonContext.Default.BackgroundImportResponse, statusCode: StatusCodes.Status202Accepted);
                    await result.ExecuteAsync(context);
                }
                else
                {
                    // Process synchronously with streaming
                    ImportResult result = await importService.ImportFileAsync(importRequest, cancellationToken);
                    IResult response = Results.Json(result, ImportJsonContext.Default.ImportResult);
                    await response.ExecuteAsync(context);
                }
            }
            finally
            {
                if (localStream != null)
                {
                    await localStream.DisposeAsync();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or NotSupportedException)
        {
            var logger = context.RequestServices.GetRequiredService<ILogger<ImportEndpointsLog>>();
            Log.ImportFailed(logger, tableName, ex);
            // Provide generic error message - details logged for debugging
            await WriteErrorAsync(context, "Import failed: invalid or unsupported file format", StatusCodes.Status400BadRequest);
        }
        catch (Exception ex)
        {
            var logger = context.RequestServices.GetRequiredService<ILogger<ImportEndpointsLog>>();
            Log.ImportFailed(logger, tableName, ex);
            await WriteErrorAsync(context, "Import failed", StatusCodes.Status500InternalServerError);
        }
    }


    /// <summary>
    /// Get current import configuration limits
    /// </summary>
    private static async Task HandleGetLimits(HttpContext context)
    {
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            await ProblemDetailsHelpers.CreateAdminProblem(
                    context,
                    StatusCodes.Status405MethodNotAllowed,
                    "Method not allowed.")
                .ExecuteAsync(context);
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
            await ProblemDetailsHelpers.CreateAdminProblem(
                    context,
                    StatusCodes.Status405MethodNotAllowed,
                    "Method not allowed.")
                .ExecuteAsync(context);
            return;
        }

        var uploadId = context.GetRouteValue("uploadId")?.ToString();
        if (string.IsNullOrEmpty(uploadId))
        {
            await WriteErrorAsync(context, "Upload ID is required", StatusCodes.Status400BadRequest);
            return;
        }

        var cloudStorage = context.RequestServices.GetService<ICloudFileStorage>();
        if (cloudStorage == null)
        {
            await WriteErrorAsync(context, "File storage service not available", StatusCodes.Status503ServiceUnavailable);
            return;
        }

        CancellationToken cancellationToken = context.RequestAborted;
        var progress = await cloudStorage.GetUploadProgressAsync(uploadId, cancellationToken);

        if (progress == null)
        {
            await WriteErrorAsync(context, "Upload not found", StatusCodes.Status404NotFound);
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
            await ProblemDetailsHelpers.CreateAdminProblem(
                    context,
                    StatusCodes.Status405MethodNotAllowed,
                    "Method not allowed.")
                .ExecuteAsync(context);
            return;
        }

        var uploadId = context.GetRouteValue("uploadId")?.ToString();
        if (string.IsNullOrEmpty(uploadId))
        {
            await WriteErrorAsync(context, "Upload ID is required", StatusCodes.Status400BadRequest);
            return;
        }

        var cloudStorage = context.RequestServices.GetService<ICloudFileStorage>();
        if (cloudStorage == null)
        {
            await WriteErrorAsync(context, "File storage service not available", StatusCodes.Status503ServiceUnavailable);
            return;
        }

        CancellationToken cancellationToken = context.RequestAborted;
        var cancelled = await cloudStorage.CancelUploadAsync(uploadId, cancellationToken);

        if (!cancelled)
        {
            await WriteErrorAsync(context, "Upload not found or already completed", StatusCodes.Status404NotFound);
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
            await ProblemDetailsHelpers.CreateAdminProblem(
                    context,
                    StatusCodes.Status405MethodNotAllowed,
                    "Method not allowed.")
                .ExecuteAsync(context);
            return;
        }

        var cloudStorage = context.RequestServices.GetService<ICloudFileStorage>();
        if (cloudStorage == null)
        {
            await WriteErrorAsync(context, "File storage service not available", StatusCodes.Status503ServiceUnavailable);
            return;
        }

        CancellationToken cancellationToken = context.RequestAborted;
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
            await ProblemDetailsHelpers.CreateAdminProblem(
                    context,
                    StatusCodes.Status405MethodNotAllowed,
                    "Method not allowed.")
                .ExecuteAsync(context);
            return;
        }

        var jobService = context.RequestServices.GetService<IImportJobService>();
        if (jobService == null)
        {
            await WriteErrorAsync(context, "Import job service not available", StatusCodes.Status503ServiceUnavailable);
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
            await WriteErrorAsync(context, "Import job service unavailable", StatusCodes.Status503ServiceUnavailable);
        }
    }

    /// <summary>
    /// Get a specific import job status
    /// </summary>
    private static async Task HandleGetImportJobStatus(HttpContext context)
    {
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            await ProblemDetailsHelpers.CreateAdminProblem(
                    context,
                    StatusCodes.Status405MethodNotAllowed,
                    "Method not allowed.")
                .ExecuteAsync(context);
            return;
        }

        var jobId = context.GetRouteValue("jobId")?.ToString();
        if (string.IsNullOrWhiteSpace(jobId))
        {
            await WriteErrorAsync(context, "Job ID is required", StatusCodes.Status400BadRequest);
            return;
        }

        var jobService = context.RequestServices.GetService<IImportJobService>();
        if (jobService == null)
        {
            await WriteErrorAsync(context, "Import job service not available", StatusCodes.Status503ServiceUnavailable);
            return;
        }

        try
        {
            CancellationToken cancellationToken = context.RequestAborted;
            var progress = await jobService.GetProgressAsync(jobId, cancellationToken);

            if (progress == null)
            {
                await WriteErrorAsync(context, "Import job not found", StatusCodes.Status404NotFound);
                return;
            }

            IResult result = Results.Json(progress, ImportJsonContext.Default.ImportProgress);
            await result.ExecuteAsync(context);
        }
        catch (Exception ex)
        {
            var logger = context.RequestServices.GetRequiredService<ILogger<ImportEndpointsLog>>();
            Log.ImportFailed(logger, jobId, ex);
            await WriteErrorAsync(context, "Import job service unavailable", StatusCodes.Status503ServiceUnavailable);
        }
    }

    /// <summary>
    /// Cancel a running import job
    /// </summary>
    private static async Task HandleCancelImportJob(HttpContext context)
    {
        if (!HttpMethods.IsPost(context.Request.Method))
        {
            await ProblemDetailsHelpers.CreateAdminProblem(
                    context,
                    StatusCodes.Status405MethodNotAllowed,
                    "Method not allowed.")
                .ExecuteAsync(context);
            return;
        }

        var jobId = context.GetRouteValue("jobId")?.ToString();
        if (string.IsNullOrWhiteSpace(jobId))
        {
            await WriteErrorAsync(context, "Job ID is required", StatusCodes.Status400BadRequest);
            return;
        }

        var jobService = context.RequestServices.GetService<IImportJobService>();
        if (jobService == null)
        {
            await WriteErrorAsync(context, "Import job service not available", StatusCodes.Status503ServiceUnavailable);
            return;
        }

        try
        {
            CancellationToken cancellationToken = context.RequestAborted;
            var cancelled = await jobService.CancelJobAsync(jobId, cancellationToken);

            if (!cancelled)
            {
                await WriteErrorAsync(context, "Import job not found", StatusCodes.Status404NotFound);
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
            await WriteErrorAsync(context, "Import job service unavailable", StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static CancellationToken GetTimeoutAwareCancellationToken(HttpContext context)
    {
        if (context.Items.TryGetValue("LimitsTimeoutToken", out var tokenObj) && tokenObj is CancellationToken timeoutToken)
        {
            return timeoutToken;
        }

        return context.RequestAborted;
    }

    /// <summary>
    /// Validate table name to prevent SQL injection
    /// </summary>
    private static bool IsValidTableName(string tableName)
    {
        if (string.IsNullOrWhiteSpace(tableName) || tableName.Length > 63) // PostgreSQL limit
            return false;

        // Allow letters, numbers, underscores; must start with letter or underscore
        return tableName.All(c => char.IsLetterOrDigit(c) || c == '_') &&
               (char.IsLetter(tableName[0]) || tableName[0] == '_');
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
    }

    private static Task WriteErrorAsync(HttpContext context, string message, int statusCode)
    {
        IResult result = ProblemDetailsHelpers.CreateAdminProblem(context, statusCode, message);
        return result.ExecuteAsync(context);
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
