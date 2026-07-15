// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace Honua.Core.Features.Tiles;

/// <summary>
/// Release-safe, resumable checkpoint snapshot for a bounded generated tile-cache seed/warm
/// generation (issue #2661). A generation is identified by a stable <see cref="GenerationId"/>
/// so a failed or cancelled seed can be retried in place: the retry loads this snapshot, skips
/// the metatile blocks already completed, skips units that already rendered successfully, and
/// regenerates only the failed or not-yet-attempted units.
/// </summary>
/// <remarks>
/// <para>
/// The checkpoint is intentionally small and opaque: it records an ordered grid cursor
/// (<see cref="CompletedMetatileBlocks"/>) over the deterministic metatile block ordering the
/// seeder produces, cumulative completed/failed counts, and a <em>bounded</em> set of failed
/// unit keys. The failed-unit set is truncated to a deterministic upper bound
/// (<see cref="TileCacheGenerationCheckpointBounds.MaxFailedUnits"/>) by the checkpoint store so
/// persisted state stays release-safe regardless of how large the requested gridset is.
/// </para>
/// </remarks>
public sealed record TileCacheGenerationCheckpoint
{
    /// <summary>Stable generation identifier shared by every attempt of the same seed/warm run.</summary>
    public required string GenerationId { get; init; }

    /// <summary>Tile operation verb the generation is running (<c>seed</c> or <c>warm</c>).</summary>
    public required string Operation { get; init; }

    /// <summary>
    /// Number of metatile blocks fully processed, in the deterministic block ordering the seeder
    /// produces. On resume the seeder skips this many leading blocks (re-rendering only the
    /// entries recorded in <see cref="FailedUnits"/>).
    /// </summary>
    public int CompletedMetatileBlocks { get; init; }

    /// <summary>Cumulative number of units that have rendered successfully across all attempts.</summary>
    public long CompletedUnitCount { get; init; }

    /// <summary>Number of units recorded as failed at the time the checkpoint was captured.</summary>
    public long FailedUnitCount { get; init; }

    /// <summary>
    /// Bounded set of failed unit keys (<c>layerId/z/x/y</c>) that a retry must regenerate. The
    /// checkpoint store truncates this to a deterministic upper bound on write.
    /// </summary>
    public required IReadOnlyList<string> FailedUnits { get; init; }

    /// <summary>Wall-clock instant the checkpoint was captured.</summary>
    public required DateTimeOffset CapturedAt { get; init; }

    /// <summary>
    /// Monotonically increasing attempt counter. Slot 1 is the first attempt; the value increments
    /// on every retry that resumes from this checkpoint.
    /// </summary>
    public int Attempt { get; init; } = 1;
}

/// <summary>
/// Deterministic bounds and truncation helpers that keep a
/// <see cref="TileCacheGenerationCheckpoint"/> release-safe regardless of gridset size. Applied by
/// every checkpoint store on write so no store persists an unbounded payload.
/// </summary>
public static class TileCacheGenerationCheckpointBounds
{
    /// <summary>Maximum number of failed unit keys a checkpoint retains.</summary>
    public const int MaxFailedUnits = 1_000;

    /// <summary>Maximum length of a single persisted failed-unit key.</summary>
    public const int MaxFailedUnitLength = 64;

    /// <summary>
    /// Returns a copy of <paramref name="checkpoint"/> with counts clamped non-negative, the
    /// attempt counter floored at 1, and the failed-unit set de-duplicated and truncated to
    /// <see cref="MaxFailedUnits"/> entries of at most <see cref="MaxFailedUnitLength"/> characters.
    /// </summary>
    public static TileCacheGenerationCheckpoint Sanitize(TileCacheGenerationCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(checkpoint.GenerationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(checkpoint.Operation);

        var boundedFailedUnits = (checkpoint.FailedUnits ?? [])
            .Where(static unit => !string.IsNullOrWhiteSpace(unit))
            .Select(static unit => unit.Length > MaxFailedUnitLength ? unit[..MaxFailedUnitLength] : unit)
            .Distinct(StringComparer.Ordinal)
            .Take(MaxFailedUnits)
            .ToArray();

        return checkpoint with
        {
            GenerationId = checkpoint.GenerationId.Trim(),
            Operation = checkpoint.Operation.Trim(),
            CompletedMetatileBlocks = checkpoint.CompletedMetatileBlocks < 0 ? 0 : checkpoint.CompletedMetatileBlocks,
            CompletedUnitCount = checkpoint.CompletedUnitCount < 0 ? 0 : checkpoint.CompletedUnitCount,
            FailedUnitCount = checkpoint.FailedUnitCount < 0 ? 0 : checkpoint.FailedUnitCount,
            FailedUnits = boundedFailedUnits,
            Attempt = checkpoint.Attempt < 1 ? 1 : checkpoint.Attempt
        };
    }
}

/// <summary>
/// AOT-friendly source-generated JSON context for persisting
/// <see cref="TileCacheGenerationCheckpoint"/> snapshots (for example to Redis).
/// </summary>
[JsonSerializable(typeof(TileCacheGenerationCheckpoint))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public sealed partial class TileCacheGenerationCheckpointJsonContext : JsonSerializerContext
{
}
