// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Collaboration.Sessions;

/// <summary>
/// Cross-node fan-out seam for saved-map collaboration presence/cursor/follow events.
/// A multi-replica deployment publishes locally-originated events to all peers so a
/// participant connected to one node observes presence and cursors produced on another.
/// </summary>
/// <remarks>
/// The backplane is best-effort and fire-and-forget: a transient outage degrades to
/// local-only delivery, matching the feature-stream cluster-broadcast contract. The
/// default registration is a no-op so single-node and Redis-less deployments work
/// unchanged.
/// </remarks>
internal interface ICollaborationSessionBackplane
{
    /// <summary>
    /// Whether this backplane connects multiple replicas (a distributed deployment). Used by
    /// consumers that must fail closed when replica-local state cannot prove cross-node
    /// continuity (honua-server#2999): a distributed backplane with a process-local op log
    /// means an append and its checkpoint replay can land on different nodes.
    /// </summary>
    bool IsDistributed { get; }

    /// <summary>
    /// Publishes a locally-originated collaboration event to peer nodes. Implementations
    /// must not block the caller and must swallow transport failures.
    /// </summary>
    void Publish(CollaborationEventEnvelope ev);
}

/// <summary>
/// No-op backplane used when no Redis multiplexer is registered. Cross-node fan-out is
/// disabled and all delivery is local to the process.
/// </summary>
internal sealed class NullCollaborationSessionBackplane : ICollaborationSessionBackplane
{
    /// <summary>Shared singleton instance.</summary>
    public static NullCollaborationSessionBackplane Instance { get; } = new();

    private NullCollaborationSessionBackplane()
    {
    }

    /// <inheritdoc />
    public bool IsDistributed => false;

    /// <inheritdoc />
    public void Publish(CollaborationEventEnvelope ev)
    {
        // Intentionally no-op: single-node delivery is handled by the in-memory outboxes.
    }
}
