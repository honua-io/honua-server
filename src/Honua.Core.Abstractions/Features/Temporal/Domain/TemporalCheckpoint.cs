// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Temporal.Domain;

/// <summary>
/// The kind of checkpoint a temporal cursor addresses. Slice 1 keyed exclusively on a change-tracker
/// generation; slices 2+ generalize that to a stable checkpoint cursor model so clients can address a
/// point in history by generation, wall-clock timestamp, transaction, GitOps release, job, edit session,
/// or a named checkpoint. The server resolves every checkpoint kind down to a concrete change-tracker
/// generation before reading the change log, so the underlying read path stays the slice-1 foundation.
/// </summary>
public enum TemporalCheckpointKind
{
    /// <summary>Monotonic change-tracker generation (the canonical, always-supported cursor).</summary>
    Generation = 0,

    /// <summary>Wall-clock timestamp, resolved to the generation in effect at that instant.</summary>
    Timestamp = 1,

    /// <summary>Database transaction id, resolved to the highest generation produced by that transaction.</summary>
    Transaction = 2,

    /// <summary>GitOps metadata release id, resolved to the generation captured when the release was applied.</summary>
    Release = 3,

    /// <summary>Job id (ETL/import/geoprocessing), resolved to the generation produced by that job.</summary>
    Job = 4,

    /// <summary>Edit-session id, resolved to the generation produced by that session.</summary>
    EditSession = 5,

    /// <summary>Operator-assigned named checkpoint, resolved through the named-checkpoint registry.</summary>
    Named = 6
}

/// <summary>
/// A stable, resolvable reference to a point in a layer's history. A checkpoint pairs a
/// <see cref="Kind"/> with its raw <see cref="Value"/> (for example a timestamp string, a release id, or
/// a named checkpoint label); generation checkpoints additionally carry the numeric
/// <see cref="Generation"/>. The temporal service resolves every checkpoint to a concrete generation via
/// <see cref="Honua.Core.Features.Temporal.Abstractions.ITemporalCheckpointResolver"/>.
/// </summary>
/// <param name="Kind">The checkpoint kind.</param>
/// <param name="Value">
/// The raw checkpoint value as supplied by the client (timestamp/transaction/release/job/edit-session/
/// named identifier). Null for a bare generation checkpoint that carries only <see cref="Generation"/>.
/// </param>
/// <param name="Generation">The numeric generation for a <see cref="TemporalCheckpointKind.Generation"/> checkpoint.</param>
public sealed record TemporalCheckpoint(
    TemporalCheckpointKind Kind,
    string? Value = null,
    long? Generation = null)
{
    /// <summary>Creates a generation checkpoint.</summary>
    /// <param name="generation">The change-tracker generation.</param>
    /// <returns>A generation-kind checkpoint.</returns>
    public static TemporalCheckpoint ForGeneration(long generation)
        => new(TemporalCheckpointKind.Generation, Value: null, Generation: generation);
}

/// <summary>
/// A checkpoint that has been resolved to a concrete change-tracker generation. Surfacing both the
/// originally requested checkpoint and the resolved generation lets clients confirm what point in
/// history the server actually read.
/// </summary>
/// <param name="Requested">The checkpoint the client requested.</param>
/// <param name="Generation">The change-tracker generation the checkpoint resolved to.</param>
public sealed record ResolvedTemporalCheckpoint(
    TemporalCheckpoint Requested,
    long Generation);
