// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Collaboration.Operations;

namespace Honua.Server.Features.Collaboration.Sessions;

/// <summary>
/// Computes the collaboration capabilities advertised to joining participants, ON DEMAND.
/// </summary>
/// <remarks>
/// <para>
/// The advertised flags must match what the endpoints will actually accept: a surface that
/// answers a request it declared unavailable is worse than one that refuses it, because the
/// client cannot tell the answer is untrustworthy (honua-server#2999 review).
/// </para>
/// <para>
/// This is deliberately a source rather than a captured record. The flags depend on
/// <see cref="ICollaborationSessionBackplane.SupportsCrossReplicaDelivery"/>, which reports
/// ACTIVATION and only becomes true inside <c>RedisCollaborationSessionBackplane.StartAsync</c>.
/// That method resolves <see cref="InMemoryCollaborationSessionService"/> to close a
/// constructor dependency cycle, so a capabilities value captured at construction time was
/// computed while the backplane still read as inactive — and then kept that value for the
/// lifetime of the process. Every declared multi-replica Redis deployment therefore advertised
/// operations, cursors, selections and follow as UNAVAILABLE even after the subscription
/// succeeded, disabling live co-editing in exactly the topology it exists for.
/// Recomputing per read removes the ordering dependency entirely instead of relying on a
/// startup sequence staying correct (honua-server#2999 review).
/// </para>
/// </remarks>
internal sealed class CollaborationCapabilitySource
{
    private readonly SavedMapCollaborationTopology _topology;
    private readonly ISavedMapOperationLogRepository _log;
    private readonly ICollaborationSessionBackplane _backplane;

    /// <summary>
    /// Creates the source over the topology declaration, the operation log, and the backplane.
    /// </summary>
    /// <param name="topology">Declared deployment topology.</param>
    /// <param name="log">Operation log whose replay guarantees gate replay and checkpoints.</param>
    /// <param name="backplane">Backplane whose activation gates live delivery.</param>
    public CollaborationCapabilitySource(
        SavedMapCollaborationTopology topology,
        ISavedMapOperationLogRepository log,
        ICollaborationSessionBackplane backplane)
    {
        _topology = topology ?? throw new ArgumentNullException(nameof(topology));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _backplane = backplane ?? throw new ArgumentNullException(nameof(backplane));
    }

    /// <summary>
    /// The capabilities as they stand right now.
    /// </summary>
    public CollaborationCapabilities Current
    {
        get
        {
            // Live operation delivery needs BOTH: the shared log lets a peer replica replay an
            // operation, but SavedMapOperationAppendCoordinator.PublishOperation reaches other
            // replicas exclusively through the backplane, so with the no-op backplane a
            // participant on another replica never receives a committed operation even though
            // the log is shared. Replay stays dependent on the log alone, which is the seam it
            // actually uses (honua-server#2999 review).
            var sharedLog = !_topology.IsMultiReplica || _log.SupportsReplicaSharedReplay;
            var crossReplica = !_topology.IsMultiReplica || _backplane.SupportsCrossReplicaDelivery;

            // Presence (cursors/selections/follow) rides the BACKPLANE, not the operation log,
            // so it is advertised only when one actually reaches peer replicas. MultiReplica can
            // be declared without Redis — configuration validation does not require it for that
            // override alone — and the no-op backplane would otherwise promise live presence
            // that participants on other replicas never receive (honua-server#2999 review).
            //
            // Checkpointing additionally needs a restart-durable log: it mints an immutable
            // version and must not claim completeness it cannot prove. It proves that from the
            // LOG, so it keys on the shared-log condition rather than on live delivery.
            return CollaborationCapabilities.Default with
            {
                Operations = sharedLog && crossReplica,
                Replay = sharedLog,
                Checkpoints = sharedLog && _log.SupportsRestartDurableReplay,
                Cursors = crossReplica,
                Selections = crossReplica,
                Follow = crossReplica,
            };
        }
    }
}
