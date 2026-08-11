// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.FeatureStore.Abstractions;

/// <summary>
/// Serializes the client side of a conflicting uploaded edit into the opaque state envelope the
/// durable conflict record carries. Supplied by the protocol adapter, which owns the wire shape of a
/// feature; the canonical sync service treats the result as opaque JSON.
/// </summary>
/// <remarks>
/// Invoked while the conflict record is being written, not afterwards (#2430). Under
/// <c>conflictHandling=manualReview</c> the conflicting edit is deliberately withheld, so that record
/// is the only copy of the client's intent — and everything that happens after the insert (the server
/// snapshot, the edit batch, the adapter's later state attachment) can be cut short by a client
/// disconnect or a process failure. A record inserted without its client envelope is then treated as
/// settled once the detection window passes, and <c>acceptClient</c> has nothing to apply.
/// </remarks>
public interface IReplicaClientStateSerializer
{
    /// <summary>
    /// Produces the client state envelope for one uploaded edit, or <see langword="null"/> when the
    /// edit carries no reconstructible client state (a delete, say, which has only an object id).
    /// </summary>
    /// <param name="edit">The uploaded edit that conflicted.</param>
    string? Serialize(ReplicaUploadEdit edit);
}
