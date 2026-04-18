// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Security.Abstractions;
using Honua.Server.Features.Import.Models;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Helpers;

namespace Honua.Server.Features.Import;

/// <summary>
/// API endpoints for GeoServer import operations.
/// </summary>
internal static partial class GeoServerImportEndpoints
{
    /// <summary>
    /// Map GeoServer import endpoints to the web application with formal API versioning.
    /// </summary>
    public static void MapGeoServerImportEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v{version:apiVersion}/admin/import/geoserver")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("GeoServerImport")
            .RequireAdminAuthorization();

        // Discover GeoServer service configuration
        _ = group.MapPost("/discover", HandleDiscoverService)
            .WithName("DiscoverGeoServerService")
            .WithSummary("Discover GeoServer configuration and assess migration compatibility");

        // Start a background import job
        _ = group.MapPost("/start", HandleStartImport)
            .WithName("StartGeoServerImport")
            .WithSummary("Start importing configuration from a GeoServer instance");

        // List active jobs
        _ = group.MapGet("/jobs", HandleListJobs)
            .WithName("ListGeoServerImportJobs")
            .WithSummary("List active GeoServer import jobs");

        // Get job status
        _ = group.MapGet("/jobs/{jobId}", HandleGetJobStatus)
            .WithName("GetGeoServerImportJobStatus")
            .WithSummary("Get GeoServer import job status");

        // Cancel job
        _ = group.MapPost("/jobs/{jobId}/cancel", HandleCancelJob)
            .WithName("CancelGeoServerImportJob")
            .WithSummary("Cancel a GeoServer import job");
    }

    /// <summary>
    /// Discover GeoServer service configuration and assess migration compatibility.
    /// </summary>
    private static async Task HandleDiscoverService(HttpContext context)
    {
        var cancellationToken = context.RequestAborted;

        GeoServerDiscoveryApiRequest? request;
        try
        {
            request = await context.Request.ReadFromJsonAsync(
                GeoServerImportApiJsonContext.Default.GeoServerDiscoveryApiRequest,
                cancellationToken);
        }
        catch (Exception ex)
        {
            Log.RequestDeserializationFailed(GetLogger(context), ex);
            await AdminResponseWriter.WriteErrorAsync(context, "Invalid request body", StatusCodes.Status400BadRequest);
            return;
        }

        if (request == null || string.IsNullOrWhiteSpace(request.GeoServerRestUrl))
        {
            await AdminResponseWriter.WriteErrorAsync(context, "GeoServerRestUrl is required", StatusCodes.Status400BadRequest);
            return;
        }

        var allowUnsafeLocalUrls = GeoServerImportExecutionSettings.ShouldAllowUnsafeLocalUrls(context.RequestServices);
        var discoverUrlValidation = await GeoServerServiceUrlValidation.ValidateAsync(
            request.GeoServerRestUrl,
            allowUnsafeLocalUrls,
            cancellationToken);
        if (!discoverUrlValidation.IsValid)
        {
            await AdminResponseWriter.WriteErrorAsync(context,
                discoverUrlValidation.ErrorMessage!,
                StatusCodes.Status400BadRequest);
            return;
        }

        try
        {
            var importService = context.RequestServices.GetRequiredService<IGeoServerImportService>();
            var discoveryRequest = new GeoServerDiscoveryRequest
            {
                GeoServerRestUrl = request.GeoServerRestUrl,
                Username = request.Username,
                Password = request.Password,
                TimeoutSeconds = request.TimeoutSeconds ?? 120,
                IncludeCompatibilityAnalysis = request.IncludeCompatibilityAnalysis ?? true,
                IncludeStyleContent = request.IncludeStyleContent ?? false,
                AllowUnsafeLocalUrls = allowUnsafeLocalUrls
            };

            var serviceInfo = await importService.DiscoverServiceAsync(discoveryRequest, cancellationToken);

            await Results.Json(serviceInfo, GeoServerImportApiJsonContext.Default.GeoServerServiceInfo)
                .ExecuteAsync(context);
        }
        catch (InvalidOperationException ex)
        {
            Log.ServiceDiscoveryFailed(GetLogger(context), request.GeoServerRestUrl, ex);
            await AdminResponseWriter.WriteErrorAsync(context,
                "Failed to discover GeoServer service with the supplied configuration.",
                StatusCodes.Status400BadRequest);
        }
        catch (Exception ex)
        {
            Log.ServiceDiscoveryFailed(GetLogger(context), request.GeoServerRestUrl, ex);
            await AdminResponseWriter.WriteErrorAsync(context,
                "Failed to discover service",
                StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>
    /// Start a background GeoServer import job.
    /// </summary>
    private static async Task HandleStartImport(HttpContext context)
    {
        var cancellationToken = context.RequestAborted;
        GeoServerImportJobManager? jobManager = null;
        string? jobId = null;
        var jobQueued = false;

        GeoServerImportApiRequest? request;
        try
        {
            request = await context.Request.ReadFromJsonAsync(
                GeoServerImportApiJsonContext.Default.GeoServerImportApiRequest,
                cancellationToken);
        }
        catch (Exception ex)
        {
            Log.RequestDeserializationFailed(GetLogger(context), ex);
            await AdminResponseWriter.WriteErrorAsync(context, "Invalid request body", StatusCodes.Status400BadRequest);
            return;
        }

        if (request == null)
        {
            await AdminResponseWriter.WriteErrorAsync(context, "Request body is required", StatusCodes.Status400BadRequest);
            return;
        }

        // Validate required fields
        if (string.IsNullOrWhiteSpace(request.GeoServerRestUrl))
        {
            await AdminResponseWriter.WriteErrorAsync(context, "GeoServerRestUrl is required", StatusCodes.Status400BadRequest);
            return;
        }

        var allowUnsafeLocalStartUrls = GeoServerImportExecutionSettings.ShouldAllowUnsafeLocalUrls(context.RequestServices);
        var startUrlValidation = await GeoServerServiceUrlValidation.ValidateAsync(
            request.GeoServerRestUrl,
            allowUnsafeLocalStartUrls,
            cancellationToken);
        if (!startUrlValidation.IsValid)
        {
            await AdminResponseWriter.WriteErrorAsync(context,
                startUrlValidation.ErrorMessage!,
                StatusCodes.Status400BadRequest);
            return;
        }

        // For now, only dry-run is supported until we have full Honua API integration
        if (request.DryRun != true)
        {
            await AdminResponseWriter.WriteErrorAsync(context, "Only dry-run imports are currently supported. Set 'dryRun' to true.", StatusCodes.Status400BadRequest);
            return;
        }

        var queuedCredentialValidationError = ValidateQueuedCredentialRequest(context, request);
        if (queuedCredentialValidationError != null)
        {
            await AdminResponseWriter.WriteErrorAsync(context, queuedCredentialValidationError, StatusCodes.Status400BadRequest);
            return;
        }

        try
        {
            jobManager = context.RequestServices.GetRequiredService<GeoServerImportJobManager>();
            if (!jobManager.CanAcceptNewJobs)
            {
                await AdminResponseWriter.WriteErrorAsync(
                    context,
                    "Distributed GeoServer import coordination is unavailable. Retry when Redis is healthy.",
                    StatusCodes.Status503ServiceUnavailable);
                return;
            }

            // Generate job ID
            jobId = Guid.NewGuid().ToString("N")[..12];

            // Create import request
            var importRequest = new GeoServerImportRequest
            {
                JobId = jobId,
                GeoServerRestUrl = request.GeoServerRestUrl,
                Username = request.Username,
                PasswordSecretReference = request.PasswordSecretReference,
                TargetHonuaUrl = request.TargetHonuaUrl ?? "https://localhost", // Placeholder for dry run
                HonuaApiKeySecretReference = request.HonuaApiKeySecretReference,
                WorkspaceNames = request.WorkspaceNames,
                DataStoreNames = request.DataStoreNames,
                LayerNames = request.LayerNames,
                ImportStyles = request.ImportStyles ?? false,
                OverwriteExisting = request.OverwriteExisting ?? false,
                DryRun = request.DryRun ?? false,
                TargetSrid = request.TargetSrid,
                RequestTimeoutSeconds = request.RequestTimeoutSeconds ?? 120,
                MaxRetries = request.MaxRetries ?? 3,
                AutoPublishLayers = request.AutoPublishLayers ?? true,
                BatchSize = request.BatchSize ?? 10,
                ImportOptions = request.ImportOptions != null ? new GeoServerImportOptions
                {
                    UnsupportedDataStoreBehavior = request.ImportOptions.UnsupportedDataStoreBehavior ?? UnsupportedResourceBehavior.LogWarning,
                    UnsupportedLayerBehavior = request.ImportOptions.UnsupportedLayerBehavior ?? UnsupportedResourceBehavior.LogWarning,
                    UnsupportedStyleBehavior = request.ImportOptions.UnsupportedStyleBehavior ?? UnsupportedResourceBehavior.LogWarning,
                    ContinueOnResourceFailure = request.ImportOptions.ContinueOnResourceFailure ?? true,
                    WorkspaceNameMappings = request.ImportOptions.WorkspaceNameMappings,
                    DefaultWorkspaceName = request.ImportOptions.DefaultWorkspaceName ?? "geoserver-import"
                } : null,
                AllowUnsafeLocalUrls = allowUnsafeLocalStartUrls
            };

            var progress = GeoServerImportProgress.CreateInitial(
                jobId,
                request.GeoServerRestUrl,
                importRequest.TargetHonuaUrl);

            if (!await ImportEndpointCoordinationHelper.PersistQueuedJobStateAsync(
                    context,
                    jobManager.CanAcceptNewJobs,
                    jobManager.RequestStore,
                    jobManager.ProgressStore,
                    jobId,
                    importRequest,
                    progress,
                    TimeSpan.FromHours(24),
                    "Distributed GeoServer import coordination became unavailable while the job was being enqueued. Retry when Redis is healthy.",
                    cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            await jobManager.JobQueue.EnqueueAsync(jobId, cancellationToken);
            jobQueued = true;

            Log.ImportJobQueued(GetLogger(context), jobId, request.GeoServerRestUrl);

            var response = new GeoServerImportJobResponse
            {
                JobId = jobId,
                Message = "GeoServer import job started",
                StatusUrl = $"jobs/{jobId}",
                CancelUrl = $"jobs/{jobId}/cancel"
            };

            await Results.Json(response, GeoServerImportApiJsonContext.Default.GeoServerImportJobResponse,
                    statusCode: StatusCodes.Status202Accepted)
                .ExecuteAsync(context);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Distributed import", StringComparison.OrdinalIgnoreCase) && ex.Message.Contains("unavailable", StringComparison.OrdinalIgnoreCase))
        {
            await TryRollbackQueuedStateAsync(jobManager, jobId, jobQueued);

            Log.ImportStartFailed(GetLogger(context), request.GeoServerRestUrl, ex);
            await AdminResponseWriter.WriteErrorAsync(
                context,
                "Distributed GeoServer import queue is temporarily unavailable. Retry when Redis is healthy.",
                StatusCodes.Status503ServiceUnavailable);
        }
        catch (ArgumentException ex)
        {
            await TryRollbackQueuedStateAsync(jobManager, jobId, jobQueued);

            Log.ImportStartFailed(GetLogger(context), request.GeoServerRestUrl, ex);
            await AdminResponseWriter.WriteErrorAsync(
                context,
                "Invalid GeoServer import request.",
                StatusCodes.Status400BadRequest);
        }
        catch (Exception ex)
        {
            await TryRollbackQueuedStateAsync(jobManager, jobId, jobQueued);

            Log.ImportStartFailed(GetLogger(context), request.GeoServerRestUrl, ex);
            await AdminResponseWriter.WriteErrorAsync(context, "Failed to queue import job", StatusCodes.Status500InternalServerError);
        }
    }

    private static string? ValidateQueuedCredentialRequest(HttpContext context, GeoServerImportApiRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.Password))
        {
            return "Password must be provided as passwordSecretReference for queued GeoServer imports.";
        }

        if (!string.IsNullOrWhiteSpace(request.HonuaApiKey))
        {
            return "HonuaApiKey must be provided as honuaApiKeySecretReference for queued GeoServer imports.";
        }

        if (string.IsNullOrWhiteSpace(request.PasswordSecretReference) &&
            string.IsNullOrWhiteSpace(request.HonuaApiKeySecretReference))
        {
            return null;
        }

        var secretProvider = context.RequestServices.GetService<ISecretProvider>();
        if (!IsSupportedSecretReference(request.PasswordSecretReference, secretProvider))
        {
            return "PasswordSecretReference must use a supported secret reference format.";
        }

        if (!IsSupportedSecretReference(request.HonuaApiKeySecretReference, secretProvider))
        {
            return "HonuaApiKeySecretReference must use a supported secret reference format.";
        }

        return null;
    }

    private static bool IsSupportedSecretReference(string? secretReference, ISecretProvider? secretProvider)
    {
        if (string.IsNullOrWhiteSpace(secretReference))
        {
            return true;
        }

        return SecretReferenceResolver.IsEnvironmentReference(secretReference) ||
            (secretProvider?.IsSecretReference(secretReference) ?? false);
    }

    private static async Task TryRollbackQueuedStateAsync(
        GeoServerImportJobManager? jobManager, string? jobId, bool jobQueued)
    {
        if (jobManager == null || string.IsNullOrWhiteSpace(jobId) || jobQueued)
        {
            return;
        }

        try
        {
            await ImportEndpointCoordinationHelper.DeleteQueuedStateAsync(
                jobManager.RequestStore,
                jobManager.ProgressStore,
                jobId,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort rollback; state will expire via TTL.
        }
    }

    /// <summary>
    /// List active GeoServer import jobs.
    /// </summary>
    private static async Task HandleListJobs(HttpContext context)
    {
        var cancellationToken = context.RequestAborted;
        var jobManager = context.RequestServices.GetRequiredService<GeoServerImportJobManager>();
        var jobIds = await jobManager.ProgressStore.GetActiveJobIdsAsync(cancellationToken).ConfigureAwait(false);
        var jobs = new List<GeoServerImportJob>(jobIds.Count);

        foreach (var jobId in jobIds)
        {
            var progress = await jobManager.ProgressStore.GetProgressAsync(jobId, cancellationToken).ConfigureAwait(false);
            if (progress != null && IsActiveStatus(progress.Status))
            {
                jobs.Add(ToJob(progress));
            }
        }

        var response = new GeoServerImportJobsResponse { Jobs = jobs.ToArray() };
        await Results.Json(response, GeoServerImportApiJsonContext.Default.GeoServerImportJobsResponse)
            .ExecuteAsync(context);
    }

    /// <summary>
    /// Get GeoServer import job status.
    /// </summary>
    private static async Task HandleGetJobStatus(HttpContext context)
    {
        var jobId = context.GetRouteValue("jobId")?.ToString();
        if (string.IsNullOrWhiteSpace(jobId))
        {
            await AdminResponseWriter.WriteErrorAsync(context, "Job ID is required", StatusCodes.Status400BadRequest);
            return;
        }

        var cancellationToken = context.RequestAborted;
        var jobManager = context.RequestServices.GetRequiredService<GeoServerImportJobManager>();

        try
        {
            var progress = await jobManager.ProgressStore.GetProgressAsync(jobId, cancellationToken);
            if (progress == null)
            {
                await AdminResponseWriter.WriteErrorAsync(context, "GeoServer import job not found", StatusCodes.Status404NotFound);
                return;
            }

            await Results.Json(ToJob(progress), GeoServerImportApiJsonContext.Default.GeoServerImportJob)
                .ExecuteAsync(context);
        }
        catch (Exception ex)
        {
            Log.JobStatusFailed(GetLogger(context), jobId, ex);
            await AdminResponseWriter.WriteErrorAsync(context, "Failed to retrieve import job", StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>
    /// Cancel a GeoServer import job.
    /// </summary>
    private static async Task HandleCancelJob(HttpContext context)
    {
        var jobId = context.GetRouteValue("jobId")?.ToString();
        if (string.IsNullOrWhiteSpace(jobId))
        {
            await AdminResponseWriter.WriteErrorAsync(context, "Job ID is required", StatusCodes.Status400BadRequest);
            return;
        }

        var cancellationToken = context.RequestAborted;
        var jobManager = context.RequestServices.GetRequiredService<GeoServerImportJobManager>();

        try
        {
            var progress = await jobManager.ProgressStore.GetProgressAsync(jobId, cancellationToken);
            if (progress == null)
            {
                await AdminResponseWriter.WriteErrorAsync(context, "GeoServer import job not found", StatusCodes.Status404NotFound);
                return;
            }

            if (!IsActiveStatus(progress.Status))
            {
                await AdminResponseWriter.WriteErrorAsync(context,
                    $"Cannot cancel job in {progress.Status} status",
                    StatusCodes.Status409Conflict);
                return;
            }

            var cancelledProgress = progress with
            {
                Status = GeoServerImportStatus.Cancelled,
                CompletedAt = DateTimeOffset.UtcNow,
                CurrentPhase = "Cancellation requested"
            };

            await jobManager.ProgressStore.SetProgressAsync(jobId, cancelledProgress, TimeSpan.FromHours(24), cancellationToken);
            await jobManager.RequestStore.DeleteProgressAsync(jobId, cancellationToken).ConfigureAwait(false);

            Log.ImportJobCancelled(GetLogger(context), jobId);

            var response = new GeoServerImportCancelResponse
            {
                JobId = jobId,
                Message = "GeoServer import job cancelled successfully"
            };

            await Results.Json(response, GeoServerImportApiJsonContext.Default.GeoServerImportCancelResponse)
                .ExecuteAsync(context);
        }
        catch (Exception ex)
        {
            Log.JobCancelFailed(GetLogger(context), jobId, ex);
            await AdminResponseWriter.WriteErrorAsync(context, "Failed to cancel import job", StatusCodes.Status500InternalServerError);
        }
    }

    private static bool IsActiveStatus(GeoServerImportStatus status)
        => status is not (GeoServerImportStatus.Completed or GeoServerImportStatus.Failed or GeoServerImportStatus.Cancelled);

    private static GeoServerImportJob ToJob(GeoServerImportProgress progress)
        => new()
        {
            JobId = progress.JobId,
            Status = MapProgressStatusToJobStatus(progress.Status),
            QueuedAt = progress.StartedAt,
            StartedAt = progress.StartedAt,
            CompletedAt = progress.CompletedAt,
            GeoServerUrl = progress.SourceGeoServerUrl,
            ErrorMessage = progress.ErrorMessage,
            Progress = progress
        };

    private static GeoServerImportJobStatus MapProgressStatusToJobStatus(GeoServerImportStatus status) => status switch
    {
        GeoServerImportStatus.Queued => GeoServerImportJobStatus.Queued,
        GeoServerImportStatus.Discovering => GeoServerImportJobStatus.Processing,
        GeoServerImportStatus.ImportingWorkspaces => GeoServerImportJobStatus.Processing,
        GeoServerImportStatus.ImportingDataStores => GeoServerImportJobStatus.Processing,
        GeoServerImportStatus.ImportingLayers => GeoServerImportJobStatus.Processing,
        GeoServerImportStatus.ImportingStyles => GeoServerImportJobStatus.Processing,
        GeoServerImportStatus.Validating => GeoServerImportJobStatus.Processing,
        GeoServerImportStatus.Completed => GeoServerImportJobStatus.Completed,
        GeoServerImportStatus.Failed => GeoServerImportJobStatus.Failed,
        GeoServerImportStatus.Cancelled => GeoServerImportJobStatus.Cancelled,
        _ => GeoServerImportJobStatus.Queued
    };

    private static ILogger<GeoServerImportEndpointsLog> GetLogger(HttpContext context) =>
        context.RequestServices.GetRequiredService<ILogger<GeoServerImportEndpointsLog>>();

    internal sealed class GeoServerImportEndpointsLog;

    private static partial class Log
    {
        [LoggerMessage(7950, LogLevel.Warning, "Failed to deserialize request")]
        public static partial void RequestDeserializationFailed(ILogger logger, Exception exception);

        [LoggerMessage(7951, LogLevel.Warning, "Service discovery failed for {GeoServerUrl}")]
        public static partial void ServiceDiscoveryFailed(ILogger logger, string geoServerUrl, Exception exception);

        [LoggerMessage(7952, LogLevel.Information, "GeoServer import job {JobId} queued: {GeoServerUrl}")]
        public static partial void ImportJobQueued(ILogger logger, string jobId, string geoServerUrl);

        [LoggerMessage(7953, LogLevel.Warning, "Failed to start import for {GeoServerUrl}")]
        public static partial void ImportStartFailed(ILogger logger, string geoServerUrl, Exception exception);

        [LoggerMessage(7954, LogLevel.Information, "GeoServer import job {JobId} cancelled by user")]
        public static partial void ImportJobCancelled(ILogger logger, string jobId);

        [LoggerMessage(7955, LogLevel.Warning, "Failed to list import jobs")]
        public static partial void JobListFailed(ILogger logger, Exception exception);

        [LoggerMessage(7956, LogLevel.Warning, "Failed to get import job status for {JobId}")]
        public static partial void JobStatusFailed(ILogger logger, string jobId, Exception exception);

        [LoggerMessage(7957, LogLevel.Warning, "Failed to cancel import job {JobId}")]
        public static partial void JobCancelFailed(ILogger logger, string jobId, Exception exception);
    }
}

// Request/Response DTOs for the API

/// <summary>
/// Response when a GeoServer import job is queued.
/// </summary>
internal sealed record GeoServerImportJobResponse
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
/// Response containing active GeoServer import jobs.
/// </summary>
internal sealed record GeoServerImportJobsResponse
{
    /// <summary>
    /// List of active jobs.
    /// </summary>
    public required GeoServerImportJob[] Jobs { get; init; }
}

/// <summary>
/// Response for cancel GeoServer import job request.
/// </summary>
internal sealed record GeoServerImportCancelResponse
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
