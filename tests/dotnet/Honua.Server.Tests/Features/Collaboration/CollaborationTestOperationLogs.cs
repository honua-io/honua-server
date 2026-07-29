// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Collaboration.Operations;

namespace Honua.Server.Tests.Features.Collaboration;

/// <summary>
/// Op-log repository that declares the restart-durable replay contract the checkpoint surface
/// requires (honua-server#2999 review), delegating everything else to the in-memory log.
/// </summary>
/// <remarks>
/// The shipped <see cref="InMemorySavedMapOperationLogRepository"/> reports
/// <c>SupportsRestartDurableReplay = false</c> because an acknowledged operation is lost when the
/// process restarts, so checkpointing fails closed with it. Checkpoint behavior is therefore
/// exercised against a log that satisfies the contract the endpoint demands, which is what a
/// durable (replica-shared, restart-surviving) implementation will provide.
/// </remarks>
internal sealed class RestartDurableSavedMapOperationLog : ISavedMapOperationLogRepository
{
    private readonly ISavedMapOperationLogRepository _inner;

    public RestartDurableSavedMapOperationLog(ISavedMapOperationLogRepository inner) => _inner = inner;

    public bool SupportsReplicaSharedReplay => _inner.SupportsReplicaSharedReplay;

    public bool SupportsRestartDurableReplay => true;

    public Task<SavedMapOperationAppendResult> AppendAsync(
        SavedMapOperationAppendRequest request,
        CancellationToken cancellationToken = default) => _inner.AppendAsync(request, cancellationToken);

    public Task<SavedMapOperationReplayResult> ReplayAsync(
        SavedMapId mapId,
        SavedMapOperationCursor sinceCursor,
        CancellationToken cancellationToken = default) => _inner.ReplayAsync(mapId, sinceCursor, cancellationToken);
}

/// <summary>
/// Op-log repository that holds the FIRST append's continuation open after its cursor has been
/// assigned, so a second concurrent append completes first. This reproduces the interleaving
/// where cursors are assigned in one order and the requests resume in the other; without a
/// serialization point between assignment and live fan-out the stream broadcasts the later
/// cursor first (honua-server#2999 review).
/// </summary>
internal sealed class DelayedFirstAppendOperationLog : ISavedMapOperationLogRepository
{
    private readonly ISavedMapOperationLogRepository _inner;
    private readonly TimeSpan _delay;
    private readonly TaskCompletionSource _firstAppendAssigned =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _appendCount;

    public DelayedFirstAppendOperationLog(ISavedMapOperationLogRepository inner, TimeSpan delay)
    {
        _inner = inner;
        _delay = delay;
    }

    /// <summary>Completes once the first append has been assigned its server cursor.</summary>
    public Task FirstAppendAssigned => _firstAppendAssigned.Task;

    public bool SupportsReplicaSharedReplay => _inner.SupportsReplicaSharedReplay;

    public bool SupportsRestartDurableReplay => _inner.SupportsRestartDurableReplay;

    public async Task<SavedMapOperationAppendResult> AppendAsync(
        SavedMapOperationAppendRequest request,
        CancellationToken cancellationToken = default)
    {
        var ordinal = Interlocked.Increment(ref _appendCount);
        var result = await _inner.AppendAsync(request, cancellationToken).ConfigureAwait(false);

        if (ordinal == 1)
        {
            _firstAppendAssigned.TrySetResult();
            await Task.Delay(_delay, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    public Task<SavedMapOperationReplayResult> ReplayAsync(
        SavedMapId mapId,
        SavedMapOperationCursor sinceCursor,
        CancellationToken cancellationToken = default) => _inner.ReplayAsync(mapId, sinceCursor, cancellationToken);
}
