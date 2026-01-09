// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Models;

namespace Honua.Server.Features.Import;

/// <summary>
/// Endpoints for importing data from ArcGIS Server services.
/// </summary>
internal static partial class EsriImportEndpoints
{
    /// <summary>
    /// Map Esri import endpoints to the web application with formal API versioning.
    /// </summary>
    public static void MapEsriImportEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v{version:apiVersion}/admin/import/esri")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("EsriImport")
            .RequireAdminAuthorization();

        // Discover layers from an ArcGIS service URL
        _ = group.Map("/discover", HandleDiscoverService)
            .WithName("DiscoverEsriService")
            .WithSummary("Discover available layers from an ArcGIS Server service URL")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Post]));

        // Start a background import job
        _ = group.Map("/start", HandleStartImport)
            .WithName("StartEsriImport")
            .WithSummary("Start importing a layer from an ArcGIS Server service")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Post]));

        // List active jobs
        _ = group.Map("/jobs", HandleListJobs)
            .WithName("ListEsriImportJobs")
            .WithSummary("List active Esri import jobs")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Get]));

        // Get job status
        _ = group.Map("/jobs/{jobId}", HandleGetJobStatus)
            .WithName("GetEsriImportJobStatus")
            .WithSummary("Get Esri import job status")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Get]));

        // Cancel job
        _ = group.Map("/jobs/{jobId}/cancel", HandleCancelJob)
            .WithName("CancelEsriImportJob")
            .WithSummary("Cancel an Esri import job")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Post]));
    }

    /// <summary>
    /// Discover layers from an ArcGIS Server service URL.
    /// </summary>
    private static async Task HandleDiscoverService(HttpContext context)
    {
        if (!HttpMethods.IsPost(context.Request.Method))
        {
            await WriteMethodNotAllowedAsync(context);
            return;
        }

        var cancellationToken = context.RequestAborted;

        EsriDiscoverRequest? request;
        try
        {
            request = await context.Request.ReadFromJsonAsync(
                EsriImportApiJsonContext.Default.EsriDiscoverRequest,
                cancellationToken);
        }
        catch (Exception ex)
        {
            Log.RequestDeserializationFailed(GetLogger(context), ex);
            await WriteErrorAsync(context, "Invalid request body", StatusCodes.Status400BadRequest);
            return;
        }

        if (request == null || string.IsNullOrWhiteSpace(request.ServiceUrl))
        {
            await WriteErrorAsync(context, "ServiceUrl is required", StatusCodes.Status400BadRequest);
            return;
        }

        if (!Uri.TryCreate(request.ServiceUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            await WriteErrorAsync(context, "ServiceUrl must be a valid HTTP(S) URL", StatusCodes.Status400BadRequest);
            return;
        }

        try
        {
            var importService = context.RequestServices.GetRequiredService<IEsriImportService>();
            var discoveryRequest = new EsriDiscoveryRequest
            {
                ServiceUrl = request.ServiceUrl,
                TimeoutSeconds = request.TimeoutSeconds ?? 30
            };

            var serviceInfo = await importService.DiscoverServiceAsync(discoveryRequest, cancellationToken);

            var response = new EsriDiscoverResponse
            {
                ServiceUrl = serviceInfo.ServiceUrl,
                ServiceName = serviceInfo.ServiceName,
                Description = serviceInfo.Description,
                SpatialReferenceWkid = serviceInfo.SpatialReferenceWkid,
                MaxRecordCount = serviceInfo.MaxRecordCount,
                Layers = serviceInfo.Layers.Select(l => new EsriLayerSummary
                {
                    Id = l.Id,
                    Name = l.Name,
                    Description = l.Description,
                    GeometryType = l.GeometryType,
                    FeatureCount = l.FeatureCount,
                    HasAttachments = l.HasAttachments
                }).ToArray()
            };

            await Results.Json(response, EsriImportApiJsonContext.Default.EsriDiscoverResponse)
                .ExecuteAsync(context);
        }
        catch (HttpRequestException ex)
        {
            Log.ServiceDiscoveryFailed(GetLogger(context), request.ServiceUrl, ex);
            await WriteErrorAsync(context,
                "Failed to connect to ArcGIS service.",
                StatusCodes.Status502BadGateway);
        }
        catch (Exception ex)
        {
            Log.ServiceDiscoveryFailed(GetLogger(context), request.ServiceUrl, ex);
            await WriteErrorAsync(context,
                "Failed to discover service",
                StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>
    /// Start a background import job.
    /// </summary>
    private static async Task HandleStartImport(HttpContext context)
    {
        if (!HttpMethods.IsPost(context.Request.Method))
        {
            await WriteMethodNotAllowedAsync(context);
            return;
        }

        var cancellationToken = context.RequestAborted;

        EsriStartImportRequest? request;
        try
        {
            request = await context.Request.ReadFromJsonAsync(
                EsriImportApiJsonContext.Default.EsriStartImportRequest,
                cancellationToken);
        }
        catch (Exception ex)
        {
            Log.RequestDeserializationFailed(GetLogger(context), ex);
            await WriteErrorAsync(context, "Invalid request body", StatusCodes.Status400BadRequest);
            return;
        }

        if (request == null)
        {
            await WriteErrorAsync(context, "Request body is required", StatusCodes.Status400BadRequest);
            return;
        }

        // Validate required fields
        if (string.IsNullOrWhiteSpace(request.ServiceUrl))
        {
            await WriteErrorAsync(context, "ServiceUrl is required", StatusCodes.Status400BadRequest);
            return;
        }

        if (string.IsNullOrWhiteSpace(request.TableName))
        {
            await WriteErrorAsync(context, "TableName is required", StatusCodes.Status400BadRequest);
            return;
        }

        if (!IsValidTableName(request.TableName))
        {
            await WriteErrorAsync(context,
                "Invalid table name. Use only letters, numbers, and underscores.",
                StatusCodes.Status400BadRequest);
            return;
        }

        if (!Uri.TryCreate(request.ServiceUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            await WriteErrorAsync(context, "ServiceUrl must be a valid HTTP(S) URL", StatusCodes.Status400BadRequest);
            return;
        }

        try
        {
            var jobManager = context.RequestServices.GetRequiredService<IDistributedImportJobManager>();

            // Generate job ID
            var jobId = Guid.NewGuid().ToString("N")[..8];

            // Create import request
            var importRequest = new EsriImportRequest
            {
                ServiceUrl = request.ServiceUrl,
                LayerId = request.LayerId,
                TableName = request.TableName,
                TargetSrid = request.TargetSrid ?? 4326,
                OverwriteExisting = request.OverwriteExisting ?? false,
                WhereClause = request.WhereClause,
                OutputFields = request.OutputFields,
                BatchSize = request.BatchSize,
                RequestTimeoutSeconds = request.RequestTimeoutSeconds ?? 120,
                MaxRetries = request.MaxRetries ?? 3,
                AutoPublish = request.AutoPublish ?? true
            };

            // Store the request
            await jobManager.RequestStore.SetProgressAsync(jobId, importRequest,
                TimeSpan.FromHours(24), cancellationToken);

            // Create initial progress
            var progress = EsriImportProgress.CreateInitial(
                jobId,
                request.ServiceUrl,
                request.LayerId,
                request.TableName);

            await jobManager.ProgressStore.SetProgressAsync(jobId, progress,
                TimeSpan.FromHours(24), cancellationToken);

            // Queue the job
            await jobManager.JobQueue.EnqueueAsync(jobId, cancellationToken);

            Log.ImportJobQueued(GetLogger(context), jobId, request.ServiceUrl, request.LayerId, request.TableName);

            var response = new EsriImportJobResponse
            {
                JobId = jobId,
                Message = "Import job queued for processing",
                StatusUrl = $"/api/v1/admin/operations/{jobId}",
                CancelUrl = $"/api/v1/admin/operations/{jobId}/cancel"
            };

            await Results.Json(response, EsriImportApiJsonContext.Default.EsriImportJobResponse,
                    statusCode: StatusCodes.Status202Accepted)
                .ExecuteAsync(context);
        }
        catch (Exception ex)
        {
            Log.ImportStartFailed(GetLogger(context), request.ServiceUrl, request.LayerId, ex);
            await WriteErrorAsync(context, "Failed to queue import job", StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>
    /// Get Esri import job status.
    /// </summary>
    private static async Task HandleGetJobStatus(HttpContext context)
    {
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            await WriteMethodNotAllowedAsync(context);
            return;
        }

        var jobId = context.GetRouteValue("jobId")?.ToString();
        if (string.IsNullOrWhiteSpace(jobId))
        {
            await WriteErrorAsync(context, "Job ID is required", StatusCodes.Status400BadRequest);
            return;
        }

        var cancellationToken = context.RequestAborted;
        var jobManager = context.RequestServices.GetRequiredService<IDistributedImportJobManager>();

        var progress = await jobManager.ProgressStore.GetProgressAsync(jobId, cancellationToken);
        if (progress == null)
        {
            await WriteErrorAsync(context, "Import job not found", StatusCodes.Status404NotFound);
            return;
        }

        await Results.Json(progress, EsriImportApiJsonContext.Default.EsriImportProgress)
            .ExecuteAsync(context);
    }

    /// <summary>
    /// Cancel an Esri import job.
    /// </summary>
    private static async Task HandleCancelJob(HttpContext context)
    {
        if (!HttpMethods.IsPost(context.Request.Method))
        {
            await WriteMethodNotAllowedAsync(context);
            return;
        }

        var jobId = context.GetRouteValue("jobId")?.ToString();
        if (string.IsNullOrWhiteSpace(jobId))
        {
            await WriteErrorAsync(context, "Job ID is required", StatusCodes.Status400BadRequest);
            return;
        }

        var cancellationToken = context.RequestAborted;
        var jobManager = context.RequestServices.GetRequiredService<IDistributedImportJobManager>();

        var progress = await jobManager.ProgressStore.GetProgressAsync(jobId, cancellationToken);
        if (progress == null)
        {
            await WriteErrorAsync(context, "Import job not found", StatusCodes.Status404NotFound);
            return;
        }

        var cancelledProgress = progress with
        {
            Status = EsriImportStatus.Cancelled,
            CompletedAt = DateTimeOffset.UtcNow,
            CurrentPhase = "Cancellation requested"
        };

        await jobManager.ProgressStore.SetProgressAsync(jobId, cancelledProgress, TimeSpan.FromHours(24), cancellationToken);
        await jobManager.RequestStore.DeleteProgressAsync(jobId, cancellationToken);

        Log.ImportJobCancelled(GetLogger(context), jobId);

        var response = new EsriImportCancelResponse
        {
            JobId = jobId,
            Message = "Import job cancellation requested"
        };

        await Results.Json(response, EsriImportApiJsonContext.Default.EsriImportCancelResponse)
            .ExecuteAsync(context);
    }

    /// <summary>
    /// List active Esri import jobs.
    /// </summary>
    private static async Task HandleListJobs(HttpContext context)
    {
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            await WriteMethodNotAllowedAsync(context);
            return;
        }

        var cancellationToken = context.RequestAborted;
        var jobManager = context.RequestServices.GetRequiredService<IDistributedImportJobManager>();

        var jobIds = await jobManager.ProgressStore.GetActiveJobIdsAsync(cancellationToken);
        var jobs = new List<EsriImportProgress>(jobIds.Count);

        foreach (var jobId in jobIds)
        {
            var progress = await jobManager.ProgressStore.GetProgressAsync(jobId, cancellationToken);
            if (progress != null)
            {
                jobs.Add(progress);
            }
        }

        var response = new EsriImportJobsResponse { Jobs = jobs.ToArray() };
        await Results.Json(response, EsriImportApiJsonContext.Default.EsriImportJobsResponse)
            .ExecuteAsync(context);
    }

    private static bool IsValidTableName(string tableName)
    {
        if (string.IsNullOrWhiteSpace(tableName) || tableName.Length > 63)
            return false;

        return tableName.All(c => char.IsLetterOrDigit(c) || c == '_') &&
               (char.IsLetter(tableName[0]) || tableName[0] == '_');
    }

    private static ILogger<EsriImportEndpointsLog> GetLogger(HttpContext context) =>
        context.RequestServices.GetRequiredService<ILogger<EsriImportEndpointsLog>>();

    private static Task WriteErrorAsync(HttpContext context, string message, int statusCode) =>
        ProblemDetailsHelpers.CreateAdminProblem(context, statusCode, message).ExecuteAsync(context);

    private static Task WriteMethodNotAllowedAsync(HttpContext context) =>
        ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status405MethodNotAllowed, "Method not allowed")
            .ExecuteAsync(context);

    internal sealed class EsriImportEndpointsLog;

    private static partial class Log
    {
        [LoggerMessage(7900, LogLevel.Warning, "Failed to deserialize request")]
        public static partial void RequestDeserializationFailed(ILogger logger, Exception exception);

        [LoggerMessage(7901, LogLevel.Warning, "Service discovery failed for {ServiceUrl}")]
        public static partial void ServiceDiscoveryFailed(ILogger logger, string serviceUrl, Exception exception);

        [LoggerMessage(7902, LogLevel.Information,
            "Import job {JobId} queued: {ServiceUrl} layer {LayerId} to {TableName}")]
        public static partial void ImportJobQueued(
            ILogger logger, string jobId, string serviceUrl, int layerId, string tableName);

        [LoggerMessage(7903, LogLevel.Warning, "Failed to start import for {ServiceUrl} layer {LayerId}")]
        public static partial void ImportStartFailed(ILogger logger, string serviceUrl, int layerId, Exception exception);

        [LoggerMessage(7904, LogLevel.Information, "Import job {JobId} cancelled by user")]
        public static partial void ImportJobCancelled(ILogger logger, string jobId);
    }
}

// Request/Response DTOs for the API

/// <summary>
/// Request to discover an ArcGIS service.
/// </summary>
internal sealed record EsriDiscoverRequest
{
    /// <summary>
    /// URL of the ArcGIS service to discover.
    /// </summary>
    public string? ServiceUrl { get; init; }

    /// <summary>
    /// Timeout in seconds for the discovery request.
    /// </summary>
    public int? TimeoutSeconds { get; init; }
}

/// <summary>
/// Response from service discovery.
/// </summary>
internal sealed record EsriDiscoverResponse
{
    /// <summary>
    /// The service URL.
    /// </summary>
    public required string ServiceUrl { get; init; }

    /// <summary>
    /// Name of the service.
    /// </summary>
    public required string ServiceName { get; init; }

    /// <summary>
    /// Service description.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Spatial reference WKID.
    /// </summary>
    public int? SpatialReferenceWkid { get; init; }

    /// <summary>
    /// Maximum records per query.
    /// </summary>
    public int? MaxRecordCount { get; init; }

    /// <summary>
    /// Available layers.
    /// </summary>
    public EsriLayerSummary[] Layers { get; init; } = [];
}

/// <summary>
/// Summary of a layer from discovery.
/// </summary>
internal sealed record EsriLayerSummary
{
    /// <summary>
    /// Layer ID.
    /// </summary>
    public int Id { get; init; }

    /// <summary>
    /// Layer name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Layer description.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Geometry type.
    /// </summary>
    public string? GeometryType { get; init; }

    /// <summary>
    /// Estimated feature count.
    /// </summary>
    public int? FeatureCount { get; init; }

    /// <summary>
    /// Whether the layer supports attachments.
    /// </summary>
    public bool HasAttachments { get; init; }
}

/// <summary>
/// Request to start an import job.
/// </summary>
internal sealed record EsriStartImportRequest
{
    /// <summary>
    /// URL of the ArcGIS service.
    /// </summary>
    public string? ServiceUrl { get; init; }

    /// <summary>
    /// ID of the layer to import.
    /// </summary>
    public int LayerId { get; init; }

    /// <summary>
    /// Target table name in PostGIS.
    /// </summary>
    public string? TableName { get; init; }

    /// <summary>
    /// Target SRID (default: 4326).
    /// </summary>
    public int? TargetSrid { get; init; }

    /// <summary>
    /// Whether to overwrite existing table.
    /// </summary>
    public bool? OverwriteExisting { get; init; }

    /// <summary>
    /// Optional WHERE clause to filter features.
    /// </summary>
    public string? WhereClause { get; init; }

    /// <summary>
    /// Optional list of fields to import.
    /// </summary>
    public string[]? OutputFields { get; init; }

    /// <summary>
    /// Batch size for pagination.
    /// </summary>
    public int? BatchSize { get; init; }

    /// <summary>
    /// Request timeout in seconds.
    /// </summary>
    public int? RequestTimeoutSeconds { get; init; }

    /// <summary>
    /// Maximum retry attempts.
    /// </summary>
    public int? MaxRetries { get; init; }

    /// <summary>
    /// Whether to auto-publish the layer.
    /// </summary>
    public bool? AutoPublish { get; init; }
}

/// <summary>
/// Response when a job is queued.
/// </summary>
internal sealed record EsriImportJobResponse
{
    /// <summary>
    /// Job ID.
    /// </summary>
    public required string JobId { get; init; }

    /// <summary>
    /// Status message.
    /// </summary>
    public required string Message { get; init; }

    /// <summary>
    /// URL to check job status.
    /// </summary>
    public required string StatusUrl { get; init; }

    /// <summary>
    /// URL to cancel the job.
    /// </summary>
    public required string CancelUrl { get; init; }
}

/// <summary>
/// Response containing active Esri import jobs.
/// </summary>
internal sealed record EsriImportJobsResponse
{
    /// <summary>
    /// List of active jobs.
    /// </summary>
    public required EsriImportProgress[] Jobs { get; init; }
}

/// <summary>
/// Response for cancel Esri import job request.
/// </summary>
internal sealed record EsriImportCancelResponse
{
    /// <summary>
    /// Job identifier that was cancelled.
    /// </summary>
    public required string JobId { get; init; }

    /// <summary>
    /// Confirmation message.
    /// </summary>
    public required string Message { get; init; }
}
