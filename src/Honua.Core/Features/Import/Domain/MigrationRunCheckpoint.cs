// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Import.Domain;

/// <summary>
/// Release-safe checkpoint snapshot persisted between migration retries so a run that
/// failed (source error, cancellation, transient network error) can resume from the
/// last successfully completed phase without re-doing prior work.
/// </summary>
/// <remarks>
/// <para>
/// Checkpoints are intentionally opaque: callers store a stable phase identifier and a
/// caller-chosen <see cref="ResumeMarker"/> that is safe to publish (e.g. a manifest
/// item index, an apply step ordinal). URLs and credential material must not be
/// embedded in the marker. The checkpoint store implementations enforce this via
/// <see cref="SafeMarker"/> on read.
/// </para>
/// </remarks>
public sealed record MigrationRunCheckpoint
{
    /// <summary>Stable run identifier assigned by the migration harness.</summary>
    public required string RunId { get; init; }

    /// <summary>Migration phase at which the checkpoint was captured.</summary>
    public required string Phase { get; init; }

    /// <summary>
    /// Caller-chosen opaque resume marker. Must not contain URLs or credential material;
    /// the checkpoint store sanitizes it on write.
    /// </summary>
    public required string ResumeMarker { get; init; }

    /// <summary>Number of items completed before the checkpoint was captured.</summary>
    public int CompletedItemCount { get; init; }

    /// <summary>Wall-clock instant the checkpoint was captured.</summary>
    public required DateTimeOffset CapturedAt { get; init; }

    /// <summary>
    /// Monotonically increasing attempt counter. Slot 1 is the first attempt; the value
    /// increments on every recovery attempt that resumes from this checkpoint.
    /// </summary>
    public int Attempt { get; init; } = 1;
}
