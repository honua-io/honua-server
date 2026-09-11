// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;

namespace Honua.Geoprocessing;

/// <summary>
/// Configuration for runtime admission controls on operator execution.
/// Bound from the <see cref="SectionName"/> section and validated at startup.
/// Limits set to <c>0</c> disable that dimension.
/// </summary>
internal sealed class ExecutionAdmissionOptions
{
    /// <summary>
    /// Configuration section name.
    /// </summary>
    public const string SectionName = "ExecutionAdmission";

    /// <summary>
    /// Master switch. When false, the evaluator admits all requests.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Maximum active jobs per partition + kind. 0 = unlimited.
    /// </summary>
    [Range(0, 10000, ErrorMessage = "MaxConcurrentJobsPerPartition must be between 0 and 10000")]
    public int MaxConcurrentJobsPerPartition { get; set; } = 10;

    /// <summary>
    /// Maximum active jobs across all partitions and kinds. 0 = unlimited.
    /// </summary>
    [Range(0, 100000, ErrorMessage = "MaxConcurrentJobsGlobal must be between 0 and 100000")]
    public int MaxConcurrentJobsGlobal { get; set; } = 50;

    /// <summary>
    /// Maximum submissions per principal per rate window. 0 = unlimited.
    /// </summary>
    [Range(0, 100000, ErrorMessage = "MaxSubmissionsPerWindow must be between 0 and 100000")]
    public int MaxSubmissionsPerWindow { get; set; } = 20;

    /// <summary>
    /// Sliding window length in seconds for the rate gate.
    /// </summary>
    [Range(1, 3600, ErrorMessage = "RateWindowSeconds must be between 1 and 3600")]
    public int RateWindowSeconds { get; set; } = 60;

    /// <summary>
    /// Maximum aggregate cost weight active in a partition. 0 = unlimited.
    /// </summary>
    [Range(0, 100000, ErrorMessage = "MaxCostWeightPerPartition must be between 0 and 100000")]
    public double MaxCostWeightPerPartition { get; set; } = 20.0;

    /// <summary>
    /// Retry-After hint returned to callers for throttled or denied outcomes, in seconds.
    /// </summary>
    [Range(1, 3600, ErrorMessage = "DefaultRetryAfterSeconds must be between 1 and 3600")]
    public int DefaultRetryAfterSeconds { get; set; } = 10;

    /// <summary>
    /// Time-to-live, in seconds, of the shared admission lease that fences the check-and-create
    /// window across nodes when Redis is composed. Bounds how long a node that dies mid-admission
    /// can block the others.
    /// </summary>
    [Range(1, 300, ErrorMessage = "SharedLeaseSeconds must be between 1 and 300")]
    public int SharedLeaseSeconds { get; set; } = 30;

    /// <summary>
    /// Maximum time, in milliseconds, a submission waits for the shared admission lease before it
    /// is rejected with backpressure and <see cref="DefaultRetryAfterSeconds"/>.
    /// </summary>
    [Range(10, 60000, ErrorMessage = "SharedLeaseAcquireTimeoutMilliseconds must be between 10 and 60000")]
    public int SharedLeaseAcquireTimeoutMilliseconds { get; set; } = 5000;
}
