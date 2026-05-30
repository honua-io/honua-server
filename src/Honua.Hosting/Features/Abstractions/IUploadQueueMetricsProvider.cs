// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Infrastructure.Abstractions;

/// <summary>
/// Snapshot of the in-process upload queue state.
/// </summary>
internal readonly record struct UploadQueueSnapshot(
    int QueueDepth,
    int MaxQueueDepth,
    int ActiveUploads,
    int MaxConcurrentUploads);

/// <summary>
/// Shared abstraction for querying upload queue metrics without depending on Import internals.
/// </summary>
internal interface IUploadQueueMetricsProvider
{
    /// <summary>
    /// Returns a point-in-time snapshot of upload queue metrics.
    /// </summary>
    UploadQueueSnapshot GetQueueSnapshot();
}
