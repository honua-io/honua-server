// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Protocols.Ogc.Api.Processes;

/// <summary>
/// Source-generated structured log methods for OGC API Processes operations.
/// Event ID range: 8100-8199.
/// </summary>
internal static partial class OgcProcessesLog
{
    // 8100-8109: Landing/Conformance
    [LoggerMessage(8100, LogLevel.Debug, "OGC Processes landing page requested")]
    public static partial void LandingPageRequested(ILogger logger);

    [LoggerMessage(8101, LogLevel.Debug, "OGC Processes conformance requested")]
    public static partial void ConformanceRequested(ILogger logger);

    // 8110-8119: Process discovery
    [LoggerMessage(8110, LogLevel.Information, "OGC Processes list requested")]
    public static partial void ProcessListRequested(ILogger logger);

    [LoggerMessage(8111, LogLevel.Information, "OGC Process description requested: ProcessId={ProcessId}")]
    public static partial void ProcessDescriptionRequested(ILogger logger, string processId);

    [LoggerMessage(8112, LogLevel.Warning, "OGC Process not found: ProcessId={ProcessId}")]
    public static partial void ProcessNotFound(ILogger logger, string processId);

    // 8120-8129: Execution
    [LoggerMessage(8120, LogLevel.Information, "OGC Process execution requested: ProcessId={ProcessId}, Async={IsAsync}")]
    public static partial void ExecutionRequested(ILogger logger, string processId, bool isAsync);

    [LoggerMessage(8121, LogLevel.Information, "OGC Process job created: JobId={JobId}, ProcessId={ProcessId}")]
    public static partial void JobCreated(ILogger logger, string jobId, string processId);

    [LoggerMessage(8122, LogLevel.Warning, "OGC Process sync execution not supported: ProcessId={ProcessId}")]
    public static partial void SyncExecutionNotSupported(ILogger logger, string processId);

    [LoggerMessage(8123, LogLevel.Warning, "OGC Process execution request invalid (missing plan): ProcessId={ProcessId}")]
    public static partial void ExecutionRequestInvalid(ILogger logger, string processId);

    [LoggerMessage(8124, LogLevel.Warning, "OGC Process plan structure invalid: ProcessId={ProcessId}, Reason={Reason}")]
    public static partial void PlanStructureInvalid(ILogger logger, string processId, string reason);

    [LoggerMessage(8125, LogLevel.Warning, "OGC Process unsupported response mode: ProcessId={ProcessId}, Response={ResponseMode}")]
    public static partial void UnsupportedResponseMode(ILogger logger, string processId, string responseMode);

    // 8130-8139: Job lifecycle
    [LoggerMessage(8130, LogLevel.Information, "OGC Job status requested: JobId={JobId}")]
    public static partial void JobStatusRequested(ILogger logger, string jobId);

    [LoggerMessage(8131, LogLevel.Information, "OGC Job results requested: JobId={JobId}")]
    public static partial void JobResultsRequested(ILogger logger, string jobId);

    [LoggerMessage(8132, LogLevel.Information, "OGC Job dismiss requested: JobId={JobId}")]
    public static partial void JobDismissRequested(ILogger logger, string jobId);

    [LoggerMessage(8133, LogLevel.Warning, "OGC Job not found: JobId={JobId}")]
    public static partial void JobNotFound(ILogger logger, string jobId);

    [LoggerMessage(8134, LogLevel.Warning, "OGC Job results not available: JobId={JobId}, Status={Status}")]
    public static partial void JobResultsNotAvailable(ILogger logger, string jobId, string status);

    [LoggerMessage(8135, LogLevel.Warning, "OGC Job dismiss rejected (terminal): JobId={JobId}, Status={Status}")]
    public static partial void DismissRejectedTerminal(ILogger logger, string jobId, string status);

    [LoggerMessage(8136, LogLevel.Warning, "OGC dismiss queue cleanup failed: JobId={JobId}; stale-claim reconciler will repair")]
    public static partial void QueueRemovalFailed(ILogger logger, string jobId, Exception exception);

    // 8140-8149: Job list
    [LoggerMessage(8140, LogLevel.Information, "OGC Job list requested")]
    public static partial void JobListRequested(ILogger logger);

    // 8150-8159: Errors
    [LoggerMessage(8150, LogLevel.Warning, "OGC Processes job store unavailable")]
    public static partial void JobStoreUnavailable(ILogger logger);

    // 8160-8169: Authorization
    [LoggerMessage(8160, LogLevel.Warning, "OGC Processes authorization denied: Resource={ResourceType}, Operation={Operation}")]
    public static partial void AuthorizationDenied(ILogger logger, string resourceType, string operation);

    [LoggerMessage(8161, LogLevel.Warning, "OGC Processes execution rejected: approval required (policy: {PolicyRef})")]
    public static partial void ExecutionRejectedApprovalRequired(ILogger logger, string policyRef);

    [LoggerMessage(8162, LogLevel.Warning, "OGC Job dismiss rejected (approval required): JobId={JobId}, Policy={PolicyRef}")]
    public static partial void DismissRejectedApprovalRequired(ILogger logger, string jobId, string policyRef);

    // 8170-8179: Admission
    [LoggerMessage(8170, LogLevel.Warning, "OGC Processes execution rejected by admission: Outcome={Outcome}, Dimension={Dimension}, Policy={PolicyRef}")]
    public static partial void ExecutionRejectedByAdmission(ILogger logger, string outcome, string dimension, string policyRef);
}
