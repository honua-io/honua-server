// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Migration.Watermark;

/// <summary>
/// Durable store for per-pipeline+source high-water marks used by incremental ("changed-since")
/// extraction. The watermark is read before a pull to bound the scan, and advanced only on a
/// successful pull so a failed or interrupted run resumes from the last durable mark rather than
/// re-scanning from scratch.
/// </summary>
public interface ISourceWatermarkStore
{
    /// <summary>
    /// Reads the persisted watermark for the pipeline+source, or <c>null</c> when none exists
    /// (first extraction → full pull).
    /// </summary>
    Task<SourceWatermark?> GetAsync(
        string pipelineId,
        string sourceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Advances the watermark monotonically. Implementations must never move a timestamp-based
    /// watermark backwards so concurrent replicas and out-of-order completions cannot cause a
    /// later run to re-pull already-extracted records. Returns the effective stored watermark.
    /// </summary>
    Task<SourceWatermark> AdvanceAsync(
        SourceWatermark watermark,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears the watermark for the pipeline+source so the next run performs a full re-scan.
    /// </summary>
    Task ClearAsync(
        string pipelineId,
        string sourceId,
        CancellationToken cancellationToken = default);
}
