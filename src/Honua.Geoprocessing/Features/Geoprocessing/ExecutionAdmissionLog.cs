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
}
