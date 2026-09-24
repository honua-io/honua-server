// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Geoprocessing;

/// <summary>
/// Source-generated structured log methods for execution admission decisions.
/// Event IDs 8020-8039 reserved for admission (geoprocessing service uses 8000-8019).
/// </summary>
internal static partial class ExecutionAdmissionLog
{
    [LoggerMessage(8020, LogLevel.Information,
        "Execution admitted: JobKind={JobKind}, PartitionKey={PartitionKey}, PrincipalId={PrincipalId}, ActiveInPartition={ActiveInPartition}, ActiveGlobal={ActiveGlobal}")]
    public static partial void Admitted(
        ILogger logger,
        string jobKind,
        string partitionKey,
        string principalId,
        int activeInPartition,
        int activeGlobal);

    [LoggerMessage(8021, LogLevel.Warning,
        "Execution throttled ({Dimension}): Policy={PolicyRef}, JobKind={JobKind}, PartitionKey={PartitionKey}, PrincipalId={PrincipalId}, Reason={Reason}")]
    public static partial void Throttled(
        ILogger logger,
        string dimension,
        string policyRef,
        string jobKind,
        string partitionKey,
        string principalId,
        string reason);

    [LoggerMessage(8022, LogLevel.Warning,
        "Execution denied ({Dimension}): Policy={PolicyRef}, JobKind={JobKind}, PartitionKey={PartitionKey}, PrincipalId={PrincipalId}, Reason={Reason}")]
    public static partial void Denied(
        ILogger logger,
        string dimension,
        string policyRef,
        string jobKind,
        string partitionKey,
        string principalId,
        string reason);

    [LoggerMessage(8023, LogLevel.Debug,
        "Admission snapshot: JobKind={JobKind}, PartitionKey={PartitionKey}, ActiveInPartition={ActiveInPartition}, ActiveGlobal={ActiveGlobal}, SubmissionsInWindow={SubmissionsInWindow}, ActiveCostWeight={ActiveCostWeight}")]
    public static partial void Snapshot(
        ILogger logger,
        string jobKind,
        string partitionKey,
        int activeInPartition,
        int activeGlobal,
        int submissionsInWindow,
        double activeCostWeight);

    [LoggerMessage(8024, LogLevel.Warning,
        "ExecutionAdmission is enabled but IExecutionJobStore is not registered; backpressure and concurrency gates will skip")]
    public static partial void JobStoreUnavailable(ILogger logger);

    [LoggerMessage(8025, LogLevel.Debug,
        "Evicted idle rate bucket for principal:kind key '{RateKey}'")]
    public static partial void RateBucketEvicted(ILogger logger, string rateKey);

    [LoggerMessage(8026, LogLevel.Warning,
        "Distributed execution-admission rate limiting unavailable; rejecting with backpressure instead of a per-node fallback.")]
    public static partial void RedisUnavailable(ILogger logger, Exception exception);

    // Event IDs 8027-8030 live on ExecutionAdmissionLeaseLog in Honua.Hosting, beside the
    // shared lease. A LoggerMessage partial class cannot span assemblies.

    [LoggerMessage(8031, LogLevel.Warning,
        "Active-job snapshot read failed on attempt {Attempt}; retrying before the admission decision.")]
    public static partial void ActiveStateReadRetrying(ILogger logger, int attempt, Exception exception);

    [LoggerMessage(8032, LogLevel.Warning,
        "Active-job snapshot unavailable after {Attempts} attempts; rejecting the submission with backpressure.")]
    public static partial void ActiveStateUnavailable(ILogger logger, int attempts, Exception exception);
}
