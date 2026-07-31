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
    /// Publishes a locally-originated collaboration event to peer nodes. Implementations
    /// must not block the caller and must swallow transport failures.
    /// </summary>
    void Publish(CollaborationEventEnvelope ev);

    /// <summary>
    /// Whether this backplane actually delivers events to peer replicas.
    /// </summary>
    /// <remarks>
    /// The advertised presence capabilities are derived from this rather than from the declared
    /// topology alone. <c>Collaboration:MultiReplica=true</c> without <c>Deployment:Mode</c>
    /// does not require Redis, so a deployment could take that documented override, install the
    /// no-op backplane, and still advertise cursors, selections and follow — participants routed
    /// to different replicas would then never see each other's events despite the handshake
    /// promising them (honua-server#2999 review).
    /// </remarks>
    bool SupportsCrossReplicaDelivery { get; }
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
    public bool SupportsCrossReplicaDelivery => false;

    /// <inheritdoc />
    public void Publish(CollaborationEventEnvelope ev)
    {
        // Intentionally no-op: single-node delivery is handled by the in-memory outboxes.
    }
}
