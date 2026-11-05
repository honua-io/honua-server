// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Collaboration.Operations;
using Honua.Server.Features.Collaboration.Sessions;

namespace Honua.Server.Features.Collaboration.Operations;

// Cursor assignment and fan-out share one per-map gate so a broadcast cannot invert the replay order, and a multi-replica deployment fails closed unless ordering is distributed.
internal sealed class SavedMapOperationAppendCoordinator : IDisposable
{
    private const int StripeCount = 64;

    private readonly ISavedMapOperationLogRepository _repository;
    private readonly InMemoryCollaborationSessionService _sessions;
    private readonly SavedMapCollaborationTopology _topology;
    private readonly ICollaborationSessionBackplane _backplane;
    private readonly SemaphoreSlim[] _stripes;
    private bool _disposed;

    public SavedMapOperationAppendCoordinator(
        ISavedMapOperationLogRepository repository,
        InMemoryCollaborationSessionService sessions,
        SavedMapCollaborationTopology topology,
        ICollaborationSessionBackplane? backplane = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _topology = topology ?? throw new ArgumentNullException(nameof(topology));
        _backplane = backplane ?? NullCollaborationSessionBackplane.Instance;
        _stripes = new SemaphoreSlim[StripeCount];
        for (var i = 0; i < StripeCount; i++)
        {
            // codeql[cs/local-not-disposed]: stripe gates live on the coordinator and are disposed in Dispose
            _stripes[i] = new SemaphoreSlim(1, 1);
        }
    }

    public bool CanAcceptEdits => !_topology.IsMultiReplica ||
        _repository.SupportsReplicaSharedReplay &&
        _backplane.SupportsCrossReplicaDelivery &&
        _backplane.SupportsOrderedOperationDelivery;

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
