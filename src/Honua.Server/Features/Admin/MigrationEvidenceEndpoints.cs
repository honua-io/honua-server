// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Server.Features.Import;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Migration;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Admin endpoints for generating and retrieving migration evidence reports.
/// </summary>
internal static partial class MigrationEvidenceEndpoints
{
    /// <summary>
    /// Maps migration evidence endpoints.
    /// </summary>
    public static void MapMigrationEvidenceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/migrations/reports")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Migration")
            .RequireAdminAuthorization();

        _ = group.MapPost(string.Empty, HandleStartReport)
            .WithName("StartMigrationEvidenceReport")
            .WithSummary("Start background generation of a migration evidence report");

        _ = group.MapGet(string.Empty, HandleListReports)
            .WithName("ListMigrationEvidenceReports")
            .WithSummary("List persisted migration evidence report artifacts");

        _ = group.MapGet("/jobs/{jobId}", HandleGetJobStatus)
            .WithName("GetMigrationEvidenceJobStatus")
            .WithSummary("Get migration evidence job progress");

        _ = group.MapPost("/jobs/{jobId}/cancel", HandleCancelJob)
            .WithName("CancelMigrationEvidenceJob")
            .WithSummary("Cancel a queued or running migration evidence job");

        _ = group.MapGet("/{reportId:guid}", HandleGetReport)
            .WithName("GetMigrationEvidenceReport")
            .WithSummary("Fetch a persisted migration evidence report artifact");
    }

    private static async Task HandleStartReport(HttpContext context)
    {
        var cancellationToken = context.RequestAborted;
        MigrationEvidenceJobManager? jobManager = null;
        string? jobId = null;

        MigrationEvidenceRequest? request;
        try
        {
            request = await context.Request.ReadFromJsonAsync(
                MigrationEvidenceApiJsonContext.Default.MigrationEvidenceRequest,
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

        if (!ValidateRequest(request, out var validationError))
        {
            await AdminResponseWriter.WriteErrorAsync(context, validationError!, StatusCodes.Status400BadRequest);
            return;
        }

        var sourceValidation = await GeoservicesServiceUrlValidation.ValidateAsync(request.SourceServiceUrl, cancellationToken).ConfigureAwait(false);
        if (!sourceValidation.IsValid)
        {
            await AdminResponseWriter.WriteErrorAsync(context, sourceValidation.ErrorMessage!, StatusCodes.Status400BadRequest);
            return;
        }

        var targetValidation = await ValidateTargetBaseUrlAsync(request.TargetBaseUrl, cancellationToken).ConfigureAwait(false);
        if (!targetValidation.IsValid)
        {
            await AdminResponseWriter.WriteErrorAsync(context, targetValidation.ErrorMessage!, StatusCodes.Status400BadRequest);
            return;
        }

        try
        {
            jobManager = context.RequestServices.GetRequiredService<MigrationEvidenceJobManager>();
            if (!jobManager.CanAcceptNewJobs)
            {
                await AdminResponseWriter.WriteErrorAsync(
                    context,
                    "Distributed migration evidence coordination is unavailable. Retry when Redis is healthy.",
                    StatusCodes.Status503ServiceUnavailable);
                return;
            }

            jobId = Guid.NewGuid().ToString("N")[..12];
            await jobManager.RequestStore.SetProgressAsync(jobId, request, TimeSpan.FromHours(24), cancellationToken).ConfigureAwait(false);
            await jobManager.ProgressStore.SetProgressAsync(
                jobId,
                MigrationEvidenceProgress.CreateInitial(jobId, request),
                TimeSpan.FromHours(24),
                cancellationToken).ConfigureAwait(false);

            if (!await EnsureCoordinationStillDurableAsync(context, jobManager, jobId, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            await jobManager.JobQueue.EnqueueAsync(jobId, cancellationToken).ConfigureAwait(false);
            Log.JobQueued(GetLogger(context), jobId, request.SourceServiceUrl, request.TargetServiceName);

            var response = new MigrationEvidenceStartResponse
            {
                JobId = jobId,
                Message = "Migration evidence generation started",
                StatusUrl = $"jobs/{jobId}",
                CancelUrl = $"jobs/{jobId}/cancel"
            };

            await Results.Json(
                    response,
                    MigrationEvidenceApiJsonContext.Default.MigrationEvidenceStartResponse,
                    statusCode: StatusCodes.Status202Accepted)
                .ExecuteAsync(context)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Distributed import queue is unavailable", StringComparison.OrdinalIgnoreCase))
        {
            if (jobManager != null && !string.IsNullOrWhiteSpace(jobId))
            {
                await DeleteQueuedJobStateAsync(jobManager, jobId, CancellationToken.None).ConfigureAwait(false);
            }

            Log.JobQueueFailed(GetLogger(context), request.SourceServiceUrl, ex);
            await AdminResponseWriter.WriteErrorAsync(
                context,
                "Distributed migration evidence queue is temporarily unavailable. Retry when Redis is healthy.",
                StatusCodes.Status503ServiceUnavailable);
        }
        catch (Exception ex)
        {
            Log.JobQueueFailed(GetLogger(context), request.SourceServiceUrl, ex);
            await AdminResponseWriter.WriteErrorAsync(context, "Failed to queue migration evidence job", StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task HandleListReports(HttpContext context)
    {
        if (!TryParseIntQuery(context.Request.Query["limit"], 50, out var limit, out var limitError))
        {
            await AdminResponseWriter.WriteErrorAsync(context, limitError!, StatusCodes.Status400BadRequest);
            return;
        }

        if (!TryParseIntQuery(context.Request.Query["offset"], 0, out var offset, out var offsetError))
        {
            await AdminResponseWriter.WriteErrorAsync(context, offsetError!, StatusCodes.Status400BadRequest);
            return;
        }

        if (!TryParseProvider(context.Request.Query["provider"], out var provider, out var providerError))
        {
            await AdminResponseWriter.WriteErrorAsync(context, providerError!, StatusCodes.Status400BadRequest);
            return;
        }

        if (!TryParseCutoverProfile(context.Request.Query["cutoverProfile"], out var cutoverProfile, out var cutoverProfileError))
        {
            await AdminResponseWriter.WriteErrorAsync(context, cutoverProfileError!, StatusCodes.Status400BadRequest);
            return;
        }

        if (!TryParseReadiness(context.Request.Query["readiness"], out var readiness, out var readinessError))
        {
            await AdminResponseWriter.WriteErrorAsync(context, readinessError!, StatusCodes.Status400BadRequest);
            return;
        }

        var reportStore = context.RequestServices.GetRequiredService<IMigrationEvidenceReportStore>();
        var reports = await reportStore.ListAsync(limit, offset, provider, cutoverProfile, readiness, context.RequestAborted).ConfigureAwait(false);

        var response = new MigrationEvidenceReportsResponse
        {
            Reports = reports.ToArray(),
            Limit = limit,
            Offset = offset
        };

        await Results.Json(response, MigrationEvidenceApiJsonContext.Default.MigrationEvidenceReportsResponse)
            .ExecuteAsync(context)
            .ConfigureAwait(false);
    }

    private static async Task HandleGetReport(HttpContext context)
    {
        var reportIdText = context.GetRouteValue("reportId")?.ToString();
        if (!Guid.TryParse(reportIdText, out var reportId))
        {
            await AdminResponseWriter.WriteErrorAsync(context, "Report ID is required", StatusCodes.Status400BadRequest);
            return;
        }

        var reportStore = context.RequestServices.GetRequiredService<IMigrationEvidenceReportStore>();
        var report = await reportStore.GetAsync(reportId, context.RequestAborted).ConfigureAwait(false);
        if (report == null)
        {
            await AdminResponseWriter.WriteErrorAsync(context, "Migration evidence report not found", StatusCodes.Status404NotFound);
            return;
        }

        await Results.Json(report, MigrationEvidenceApiJsonContext.Default.MigrationEvidenceReport)
            .ExecuteAsync(context)
            .ConfigureAwait(false);
    }

    private static async Task HandleGetJobStatus(HttpContext context)
    {
        var jobId = context.GetRouteValue("jobId")?.ToString();
        if (string.IsNullOrWhiteSpace(jobId))
        {
            await AdminResponseWriter.WriteErrorAsync(context, "Job ID is required", StatusCodes.Status400BadRequest);
            return;
        }

        var jobManager = context.RequestServices.GetRequiredService<MigrationEvidenceJobManager>();
        var progress = await jobManager.ProgressStore.GetProgressAsync(jobId, context.RequestAborted).ConfigureAwait(false);
        if (progress == null)
        {
            await AdminResponseWriter.WriteErrorAsync(context, "Migration evidence job not found", StatusCodes.Status404NotFound);
            return;
        }

        await Results.Json(progress, MigrationEvidenceApiJsonContext.Default.MigrationEvidenceProgress)
            .ExecuteAsync(context)
            .ConfigureAwait(false);
    }

    private static async Task HandleCancelJob(HttpContext context)
    {
        var jobId = context.GetRouteValue("jobId")?.ToString();
        if (string.IsNullOrWhiteSpace(jobId))
        {
            await AdminResponseWriter.WriteErrorAsync(context, "Job ID is required", StatusCodes.Status400BadRequest);
            return;
        }

        var jobManager = context.RequestServices.GetRequiredService<MigrationEvidenceJobManager>();
        var progress = await jobManager.ProgressStore.GetProgressAsync(jobId, context.RequestAborted).ConfigureAwait(false);
        if (progress == null)
        {
            await AdminResponseWriter.WriteErrorAsync(context, "Migration evidence job not found", StatusCodes.Status404NotFound);
            return;
        }

        if (!IsActiveStatus(progress.Status))
        {
            await AdminResponseWriter.WriteErrorAsync(
                context,
                $"Cannot cancel job in {progress.Status} status",
                StatusCodes.Status409Conflict);
            return;
        }

        var cancelledProgress = progress with
        {
            Status = MigrationEvidenceJobStatus.Cancelled,
            CompletedAt = DateTimeOffset.UtcNow,
            CurrentPhase = "Cancellation requested"
        };

        await jobManager.ProgressStore.SetProgressAsync(jobId, cancelledProgress, TimeSpan.FromHours(24), context.RequestAborted).ConfigureAwait(false);
        Log.JobCancelled(GetLogger(context), jobId);

        var response = new MigrationEvidenceCancelResponse
        {
            JobId = jobId,
            Message = "Migration evidence job cancelled successfully"
        };

        await Results.Json(response, MigrationEvidenceApiJsonContext.Default.MigrationEvidenceCancelResponse)
            .ExecuteAsync(context)
            .ConfigureAwait(false);
    }

    private static bool ValidateRequest(MigrationEvidenceRequest request, out string? validationError)
    {
        if (request.Provider != MigrationEvidenceProvider.ArcGisGeoservices)
        {
            validationError = "Provider must be 'arcgis-geoservices'.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(request.SourceServiceUrl))
        {
            validationError = "SourceServiceUrl is required";
            return false;
        }

        if (string.IsNullOrWhiteSpace(request.TargetBaseUrl))
        {
            validationError = "TargetBaseUrl is required";
            return false;
        }

        if (string.IsNullOrWhiteSpace(request.TargetServiceName))
        {
            validationError = "TargetServiceName is required";
            return false;
        }

        if (request.Layers.Length == 0)
        {
            validationError = "At least one layer mapping is required";
            return false;
        }

        if (request.Layers.Any(static mapping => mapping.SourceLayerId < 0 || mapping.TargetLayerId < 0))
        {
            validationError = "Layer mappings must use non-negative layer IDs";
            return false;
        }

        if (string.IsNullOrWhiteSpace(request.RollbackPlanReference))
        {
            validationError = "RollbackPlanReference is required";
            return false;
        }

        validationError = null;
        return true;
    }

    private static async Task<GeoservicesServiceUrlValidationResult> ValidateTargetBaseUrlAsync(
        string targetBaseUrl,
        CancellationToken cancellationToken)
    {
        var result = await GeoservicesServiceUrlValidation.ValidateAsync(targetBaseUrl, cancellationToken).ConfigureAwait(false);
        if (result.IsValid)
        {
            return result;
        }

        return GeoservicesServiceUrlValidationResult.Failure(
            (result.ErrorMessage ?? "TargetBaseUrl is invalid").Replace("ServiceUrl", "TargetBaseUrl", StringComparison.Ordinal));
    }

    private static bool TryParseIntQuery(string? rawValue, int defaultValue, out int parsedValue, out string? error)
    {
        parsedValue = defaultValue;
        error = null;

        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return true;
        }

        if (!int.TryParse(rawValue, out parsedValue) || parsedValue < 0)
        {
            error = $"Query parameter value '{rawValue}' must be a non-negative integer.";
            return false;
        }

        return true;
    }

    private static bool TryParseProvider(string? rawValue, out MigrationEvidenceProvider? provider, out string? error)
    {
        provider = null;
        error = null;
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return true;
        }

        if (string.Equals(rawValue, "arcgis-geoservices", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(rawValue, nameof(MigrationEvidenceProvider.ArcGisGeoservices), StringComparison.OrdinalIgnoreCase))
        {
            provider = MigrationEvidenceProvider.ArcGisGeoservices;
            return true;
        }

        error = "provider must be one of: arcgis-geoservices";
        return false;
    }

    private static bool TryParseCutoverProfile(string? rawValue, out MigrationCutoverProfile? cutoverProfile, out string? error)
    {
        cutoverProfile = null;
        error = null;
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return true;
        }

        if (string.Equals(rawValue, "pilot", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(rawValue, nameof(MigrationCutoverProfile.Pilot), StringComparison.OrdinalIgnoreCase))
        {
            cutoverProfile = MigrationCutoverProfile.Pilot;
            return true;
        }

        if (string.Equals(rawValue, "production", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(rawValue, nameof(MigrationCutoverProfile.Production), StringComparison.OrdinalIgnoreCase))
        {
            cutoverProfile = MigrationCutoverProfile.Production;
            return true;
        }

        error = "cutoverProfile must be one of: pilot, production";
        return false;
    }

    private static bool TryParseReadiness(string? rawValue, out MigrationReadinessState? readiness, out string? error)
    {
        readiness = null;
        error = null;
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return true;
        }

        if (string.Equals(rawValue, "blocked", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(rawValue, nameof(MigrationReadinessState.Blocked), StringComparison.OrdinalIgnoreCase))
        {
            readiness = MigrationReadinessState.Blocked;
            return true;
        }

        if (string.Equals(rawValue, "pilot_ready", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(rawValue, nameof(MigrationReadinessState.PilotReady), StringComparison.OrdinalIgnoreCase))
        {
            readiness = MigrationReadinessState.PilotReady;
            return true;
        }

        if (string.Equals(rawValue, "production_ready", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(rawValue, nameof(MigrationReadinessState.ProductionReady), StringComparison.OrdinalIgnoreCase))
        {
            readiness = MigrationReadinessState.ProductionReady;
            return true;
        }

        error = "readiness must be one of: blocked, pilot_ready, production_ready";
        return false;
    }

    private static bool IsActiveStatus(MigrationEvidenceJobStatus status)
        => status is not (MigrationEvidenceJobStatus.Completed or MigrationEvidenceJobStatus.Failed or MigrationEvidenceJobStatus.Cancelled);

    private static async Task<bool> EnsureCoordinationStillDurableAsync(
        HttpContext context,
        MigrationEvidenceJobManager jobManager,
        string jobId,
        CancellationToken cancellationToken)
    {
        if (jobManager.CanAcceptNewJobs)
        {
            return true;
        }

        await DeleteQueuedJobStateAsync(jobManager, jobId, cancellationToken).ConfigureAwait(false);
        await AdminResponseWriter.WriteErrorAsync(
            context,
            "Distributed migration evidence coordination became unavailable while the job was being enqueued. Retry when Redis is healthy.",
            StatusCodes.Status503ServiceUnavailable);
        return false;
    }

    private static async Task DeleteQueuedJobStateAsync(
        MigrationEvidenceJobManager jobManager,
        string jobId,
        CancellationToken cancellationToken)
    {
        await jobManager.RequestStore.DeleteProgressAsync(jobId, cancellationToken).ConfigureAwait(false);
        await jobManager.ProgressStore.DeleteProgressAsync(jobId, cancellationToken).ConfigureAwait(false);
    }

    private static ILogger<MigrationEvidenceEndpointsLog> GetLogger(HttpContext context) =>
        context.RequestServices.GetRequiredService<ILogger<MigrationEvidenceEndpointsLog>>();

    internal sealed class MigrationEvidenceEndpointsLog;

    private static partial class Log
    {
        [LoggerMessage(9140, LogLevel.Warning, "Failed to deserialize migration evidence request.")]
        public static partial void RequestDeserializationFailed(ILogger logger, Exception exception);

        [LoggerMessage(9141, LogLevel.Information, "Migration evidence job {JobId} queued for {SourceServiceUrl} -> {TargetServiceName}.")]
        public static partial void JobQueued(ILogger logger, string jobId, string sourceServiceUrl, string targetServiceName);

        [LoggerMessage(9142, LogLevel.Warning, "Failed to queue migration evidence job for {SourceServiceUrl}.")]
        public static partial void JobQueueFailed(ILogger logger, string sourceServiceUrl, Exception exception);

        [LoggerMessage(9143, LogLevel.Information, "Migration evidence job {JobId} cancelled by user.")]
        public static partial void JobCancelled(ILogger logger, string jobId);
    }
}

/// <summary>
/// Response when a migration evidence job is queued.
/// </summary>
internal sealed record MigrationEvidenceStartResponse
{
    /// <summary>
    /// Job identifier.
    /// </summary>
    public required string JobId { get; init; }

    /// <summary>
    /// Status message.
    /// </summary>
    public required string Message { get; init; }

    /// <summary>
    /// Relative job-status URL.
    /// </summary>
    public required string StatusUrl { get; init; }

    /// <summary>
    /// Relative cancellation URL.
    /// </summary>
    public required string CancelUrl { get; init; }
}

/// <summary>
/// Response containing persisted report summaries.
/// </summary>
internal sealed record MigrationEvidenceReportsResponse
{
    /// <summary>
    /// Returned report summaries.
    /// </summary>
    public required MigrationEvidenceReportSummary[] Reports { get; init; }

    /// <summary>
    /// Applied limit.
    /// </summary>
    public required int Limit { get; init; }

    /// <summary>
    /// Applied offset.
    /// </summary>
    public required int Offset { get; init; }
}

/// <summary>
/// Response returned after a cancellation request.
/// </summary>
internal sealed record MigrationEvidenceCancelResponse
{
    /// <summary>
    /// Job identifier.
    /// </summary>
    public required string JobId { get; init; }

    /// <summary>
    /// Confirmation message.
    /// </summary>
    public required string Message { get; init; }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(MigrationEvidenceRequest))]
[JsonSerializable(typeof(MigrationEvidenceProgress))]
[JsonSerializable(typeof(MigrationEvidenceReport))]
[JsonSerializable(typeof(MigrationEvidenceReportSummary))]
[JsonSerializable(typeof(MigrationEvidenceReportSummary[]))]
[JsonSerializable(typeof(MigrationEvidenceStartResponse))]
[JsonSerializable(typeof(MigrationEvidenceReportsResponse))]
[JsonSerializable(typeof(MigrationEvidenceCancelResponse))]
internal sealed partial class MigrationEvidenceApiJsonContext : JsonSerializerContext
{
}
