// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Geoprocessing;

/// <summary>
/// Source-generated structured log methods for geoprocessing service operations.
/// </summary>
internal static partial class GeoprocessingServiceLog
{
    [LoggerMessage(8000, LogLevel.Information, "Plan validated: PlanId={PlanId}, IsExecutable={IsExecutable}, Violations={ViolationCount}")]
    public static partial void PlanValidated(
        ILogger logger,
        string planId,
        bool isExecutable,
        int violationCount);

    [LoggerMessage(8001, LogLevel.Information, "Dry run completed: PlanId={PlanId}, EstimatedDuration={EstimatedDurationSeconds}s")]
    public static partial void DryRunCompleted(
        ILogger logger,
        string planId,
        double estimatedDurationSeconds);

    [LoggerMessage(8002, LogLevel.Information, "Job submitted: JobId={JobId}, PlanId={PlanId}")]
    public static partial void JobSubmitted(
        ILogger logger,
        string jobId,
        string planId);

    [LoggerMessage(8003, LogLevel.Debug, "Job retrieved: JobId={JobId}, Status={Status}")]
    public static partial void JobRetrieved(
        ILogger logger,
        string jobId,
        string status);

    [LoggerMessage(8004, LogLevel.Information, "Job cancelled: JobId={JobId}")]
    public static partial void JobCancelled(
        ILogger logger,
        string jobId);

    [LoggerMessage(8005, LogLevel.Warning, "Job not found: JobId={JobId}")]
    public static partial void JobNotFound(
        ILogger logger,
        string jobId);

    [LoggerMessage(8006, LogLevel.Debug, "Job results retrieved: JobId={JobId}")]
    public static partial void JobResultsRetrieved(
        ILogger logger,
        string jobId);

    [LoggerMessage(8007, LogLevel.Warning, "Authorization denied for geoprocessing: ResourceType={ResourceType}, Operation={Operation}")]
    public static partial void AuthorizationDenied(
        ILogger logger,
        string resourceType,
        string operation);

    [LoggerMessage(8008, LogLevel.Information, "Job submitted (idempotent replay): JobId={JobId}")]
    public static partial void JobSubmittedIdempotent(
        ILogger logger,
        string jobId);

    [LoggerMessage(8009, LogLevel.Warning, "Cancel rejected for terminal job: JobId={JobId}, Status={Status}")]
    public static partial void CancelRejectedTerminal(
        ILogger logger,
        string jobId,
        string status);

    [LoggerMessage(8010, LogLevel.Warning, "Submit rejected: approval required (policy: {PolicyRef})")]
    public static partial void SubmitRejectedApprovalRequired(
        ILogger logger,
        string policyRef);

    [LoggerMessage(8011, LogLevel.Information, "Job cancellation delegated to worker: JobId={JobId}")]
    public static partial void JobCancellationDelegated(
        ILogger logger,
        string jobId);

    [LoggerMessage(8012, LogLevel.Debug, "Job result package unavailable: JobId={JobId}")]
    public static partial void JobResultsUnavailable(
        ILogger logger,
        string jobId);

    [LoggerMessage(8013, LogLevel.Warning, "Job {JobId} queued in stub mode: backend dispatch deferred until execution engine is available")]
    public static partial void JobSubmittedStubbed(
        ILogger logger,
        string jobId);

    [LoggerMessage(8014, LogLevel.Warning, "Cancel rejected: approval required (policy: {PolicyRef})")]
    public static partial void CancelRejectedApprovalRequired(
        ILogger logger,
        string policyRef);

    [LoggerMessage(8015, LogLevel.Warning, "Queue cleanup failed for cancelled job {JobId}; stale-claim reconciler will repair")]
    public static partial void QueueRemovalFailed(
        ILogger logger,
        string jobId,
        Exception exception);

    [LoggerMessage(8016, LogLevel.Information, "Process catalog loaded with {Count} built-in processes")]
    public static partial void ProcessCatalogLoaded(
        ILogger logger,
        int count);

    [LoggerMessage(8017, LogLevel.Warning, "Unknown process referenced: Field={FieldPath}, Detail={Detail}")]
    public static partial void UnknownProcessReferenced(
        ILogger logger,
        string fieldPath,
        string detail);

    [LoggerMessage(8020, LogLevel.Information, "Map package created: MapPackageId={MapPackageId}, Status={Status}")]
    public static partial void MapPackageCreated(
        ILogger logger,
        string mapPackageId,
        string status);

    [LoggerMessage(8021, LogLevel.Information, "App package created: AppPackageId={AppPackageId}, TargetSdk={TargetSdk}")]
    public static partial void AppPackageCreated(
        ILogger logger,
        string appPackageId,
        string targetSdk);

    [LoggerMessage(8022, LogLevel.Information, "Package status changed: PackageId={PackageId}, OldStatus={OldStatus}, NewStatus={NewStatus}")]
    public static partial void PackageStatusChanged(
        ILogger logger,
        string packageId,
        string oldStatus,
        string newStatus);

    [LoggerMessage(8023, LogLevel.Information, "Destructive plan detected: PlanId={PlanId}, ProcessId={ProcessId}")]
    public static partial void DestructivePlanDetected(
        ILogger logger,
        string planId,
        string processId);

    [LoggerMessage(8024, LogLevel.Warning, "Cancel refused for job {JobId}: remote backend '{Backend}' does not support cancellation")]
    public static partial void RemoteCancelUnavailable(
        ILogger logger,
        string jobId,
        string backend);

    [LoggerMessage(8024, LogLevel.Warning, "Remote cancel CAS conflict for job {JobId}: retrying with fresh record")]
    public static partial void RemoteCancelCasRetry(
        ILogger logger,
        string jobId);

    [LoggerMessage(8025, LogLevel.Warning, "Post-start CAS conflict for job {JobId}: returning authoritative store record")]
    public static partial void SubmitPostStartCasConflict(
        ILogger logger,
        string jobId);

    [LoggerMessage(8026, LogLevel.Warning, "Remote cancel CAS exhausted for job {JobId}: cancellation could not be confirmed")]
    public static partial void RemoteCancelCasExhausted(
        ILogger logger,
        string jobId);
}
