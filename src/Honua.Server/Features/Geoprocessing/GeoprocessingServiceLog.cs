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
}
