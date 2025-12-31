// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Security;

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
    /// Map file import endpoints to the web application
    /// </summary>
    public static void MapImportEndpoints(this WebApplication app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/import")
            .WithTags("Import")
            .RequireAdminAuthorization();

        // Get supported file formats
        _ = group.Map("/formats", HandleGetSupportedFormats)
            .WithName("GetSupportedFileFormats")
            .WithSummary("Get supported geospatial file formats")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));
        // .Produces<FileFormatsResponse>();

        // Preview file before import
        _ = group.Map("/preview", HandlePreviewFile)
            .WithName("PreviewFile")
            .WithSummary("Preview geospatial file contents")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }))
            // .Produces<FilePreview>()
            .DisableAntiforgery(); // For file uploads

        // Import geospatial file
        _ = group.Map("/upload", HandleImportFile)
            .WithName("ImportFile")
            .WithSummary("Import geospatial file to PostgreSQL using memory-efficient streaming")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }))
            // .Produces<ImportResult>()
            .DisableAntiforgery(); // For file uploads

        // Get import job status
        _ = group.Map("/jobs/{jobId}", HandleGetJobStatus)
            .WithName("GetImportJobStatus")
            .WithSummary("Get the status of a background import job")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        // Cancel import job
        _ = group.Map("/jobs/{jobId}/cancel", HandleCancelJob)
            .WithName("CancelImportJob")
            .WithSummary("Cancel a running background import job")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));

        // Get all active import jobs
        _ = group.Map("/jobs", HandleGetActiveJobs)
            .WithName("GetActiveImportJobs")
            .WithSummary("Get all active background import jobs")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        // Get import limits configuration
        _ = group.Map("/limits", HandleGetLimits)
            .WithName("GetImportLimits")
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
        var previewValidation = await FileUploadSecurity.ValidateFileAsync(
            file,
            importService.Limits.MaxPreviewSizeBytes,
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
        var maxFileSizeBytes = Math.Max(importService.Limits.BackgroundJobThresholdBytes, importService.Limits.MaxMemoryBytes);
        var uploadValidation = await FileUploadSecurity.ValidateFileAsync(
            file,
            maxFileSizeBytes,
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

            // Check if file should be processed in background
            var limits = importService.Limits;
            var shouldQueueBackground = forceBackground || file.Length > limits.BackgroundJobThresholdBytes;

            using Stream stream = file.OpenReadStream();

            var importRequest = new ImportRequest
            {
                FileStream = stream,
                FileName = safeFileName,
                TableName = tableName,
                SourceSrid = sourceSrid,
                TargetSrid = targetSrid,
                OverwriteExisting = overwriteExisting
            };

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
                    StatusUrl = $"/api/import/jobs/{jobId}",
                    CancelUrl = $"/api/import/jobs/{jobId}/cancel"
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
    /// Get the status of a background import job
    /// </summary>
    private static async Task HandleGetJobStatus(HttpContext context)
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
        if (string.IsNullOrEmpty(jobId))
        {
            await WriteErrorAsync(context, "Job ID is required", StatusCodes.Status400BadRequest);
            return;
        }

        var jobService = context.RequestServices.GetService<IImportJobService>();
        if (jobService == null)
        {
            await WriteErrorAsync(context, "Background import service not available", StatusCodes.Status503ServiceUnavailable);
            return;
        }

        CancellationToken cancellationToken = context.RequestAborted;
        var progress = await jobService.GetProgressAsync(jobId, cancellationToken);

        if (progress == null)
        {
            await WriteErrorAsync(context, "Job not found", StatusCodes.Status404NotFound);
            return;
        }

        IResult result = Results.Json(progress, ImportJsonContext.Default.ImportProgress);
        await result.ExecuteAsync(context);
    }

    /// <summary>
    /// Cancel a running background import job
    /// </summary>
    private static async Task HandleCancelJob(HttpContext context)
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
        if (string.IsNullOrEmpty(jobId))
        {
            await WriteErrorAsync(context, "Job ID is required", StatusCodes.Status400BadRequest);
            return;
        }

        var jobService = context.RequestServices.GetService<IImportJobService>();
        if (jobService == null)
        {
            await WriteErrorAsync(context, "Background import service not available", StatusCodes.Status503ServiceUnavailable);
            return;
        }

        CancellationToken cancellationToken = context.RequestAborted;
        var cancelled = await jobService.CancelJobAsync(jobId, cancellationToken);

        if (!cancelled)
        {
            await WriteErrorAsync(context, "Job not found or already completed", StatusCodes.Status404NotFound);
            return;
        }

        var response = new CancelJobResponse { JobId = jobId, Message = "Job cancelled" };
        IResult result = Results.Json(response, ImportJsonContext.Default.CancelJobResponse);
        await result.ExecuteAsync(context);
    }

    /// <summary>
    /// Get all active background import jobs
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
            await WriteErrorAsync(context, "Background import service not available", StatusCodes.Status503ServiceUnavailable);
            return;
        }

        CancellationToken cancellationToken = context.RequestAborted;
        var jobs = await jobService.GetActiveJobsAsync(cancellationToken);

        var response = new ActiveJobsResponse { Jobs = jobs.ToArray() };
        IResult result = Results.Json(response, ImportJsonContext.Default.ActiveJobsResponse);
        await result.ExecuteAsync(context);
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
}

/// <summary>
/// Response for cancel job endpoint
/// </summary>
internal sealed record CancelJobResponse
{
    /// <summary>
    /// The job ID that was cancelled
    /// </summary>
    public required string JobId { get; init; }

    /// <summary>
    /// Confirmation message
    /// </summary>
    public required string Message { get; init; }
}

/// <summary>
/// Response containing list of active import jobs
/// </summary>
internal sealed record ActiveJobsResponse
{
    /// <summary>
    /// List of active import job progress records
    /// </summary>
    public required ImportProgress[] Jobs { get; init; }
}
