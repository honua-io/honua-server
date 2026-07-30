// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Collaboration.Operations;
using Honua.Server.Features.Collaboration.Sessions;

namespace Honua.Server.Features.Collaboration.Operations;

/// <summary>
/// Serialization point that keeps op-log cursor ASSIGNMENT and live FAN-OUT in the same order
/// (honua-server#2999 review). Appending and publishing were previously independent steps, so two
/// concurrent requests could be assigned cursors 1 and 2 by the log and then resume in the
/// opposite order, broadcasting cursor 2 before cursor 1 — live clients would apply same-aspect
/// edits in the reverse of the order replay and checkpointing use.
/// </summary>
/// <remarks>
/// <para>
/// Ordering is enforced by taking a per-map gate around "assign cursor, then publish", so no
/// operation for a map can be broadcast while another operation for the same map is between
/// those two steps. Gates are striped over a fixed array rather than kept per map id, so a
/// long-lived server does not accumulate one lock per saved map ever edited; two maps sharing a
/// stripe only contend, never interleave incorrectly.
/// </para>
/// <para>
/// This is an IN-PROCESS ordering point. It is sufficient because the append surface already
/// fails closed (503) in a declared multi-replica deployment whenever the op log is not
/// replica-shared, so every append for a map is accepted by exactly one process. A
/// replica-shared log implementation must move this ordering point into the shared store (for
/// example by publishing from the same transaction/stream that assigns the cursor) — a
/// process-local gate cannot order appends accepted on different nodes.
/// </para>
/// <para>
/// The stripe gates are owned for the coordinator's lifetime, so the type is
/// <see cref="IDisposable"/> and is registered as a singleton — the DI container disposes it with
/// the host, releasing every <see cref="SemaphoreSlim"/> exactly once.
/// </para>
/// </remarks>
internal sealed class SavedMapOperationAppendCoordinator : IDisposable
{
    private const int StripeCount = 64;

    private readonly ISavedMapOperationLogRepository _repository;
    private readonly InMemoryCollaborationSessionService _sessions;
    private readonly SavedMapCollaborationTopology _topology;
    private readonly SemaphoreSlim[] _stripes;
    private bool _disposed;

    public SavedMapOperationAppendCoordinator(
        ISavedMapOperationLogRepository repository,
        InMemoryCollaborationSessionService sessions,
        SavedMapCollaborationTopology topology)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _topology = topology ?? throw new ArgumentNullException(nameof(topology));
        _stripes = new SemaphoreSlim[StripeCount];
        for (var i = 0; i < StripeCount; i++)
        {
            _stripes[i] = new SemaphoreSlim(1, 1);
        }
    }

    /// <summary>
    /// Whether this deployment can assign authoritative operation cursors. False when the
    /// deployment is declared multi-replica but the op log is process-local: two replicas would
    /// each run an independent per-map cursor sequence and could both accept "cursor 1".
    /// </summary>
    public bool CanAcceptEdits => !_topology.IsMultiReplica || _repository.SupportsReplicaSharedReplay;

    /// <summary>
    /// Appends an operation and, when it is newly accepted, publishes it to the live session
    /// fan-out before any later operation for the same map can be assigned a cursor.
    /// </summary>
    public async Task<SavedMapOperationAppendResult> AppendAndPublishAsync(
        string canonicalMapId,
        SavedMapOperationAppendRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalMapId);
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var stripe = (int)((uint)StringComparer.Ordinal.GetHashCode(canonicalMapId) % StripeCount);
        var gate = _stripes[stripe];
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await _repository.AppendAsync(request, cancellationToken).ConfigureAwait(false);

            // Bridge the committed op into the live session fan-out (#2999, REQ-002/REQ-004):
            // clients submit edits over the REST append (typed conflict semantics stay there) and
            // the WebSocket stream echoes the committed, server-ordered operation to every
            // participant of the map — the op-log server cursor is the authoritative order, and
            // publishing inside this gate is what makes the broadcast follow it.
            if (result.Status == SavedMapOperationAppendStatus.Accepted &&
                !result.IsDuplicate &&
                result.Operation is not null)
            {
                _sessions.PublishOperation(canonicalMapId, CollaborationOperationWire.FromEnvelope(result.Operation));
            }

            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Releases the per-map stripe gates. Idempotent, so a double dispose by the container (or a
    /// test that disposes explicitly and then disposes its provider) is safe.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var stripe in _stripes)
        {
            stripe.Dispose();
        }
    }
}
