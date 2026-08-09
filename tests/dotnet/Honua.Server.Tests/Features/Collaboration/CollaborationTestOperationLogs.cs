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
/// Op-log repository that reproduces the replay-window race in the stream handshake: the resume
/// cursor is inside the retained window for the FIRST replay (so the preliminary check reports Ok
/// and no <c>resync-required</c> is announced), then concurrent appends prune it before the
/// snapshot tail is read, so the next replay reports
/// <see cref="SavedMapOperationReplayStatus.ResyncRequired"/> (honua-server#2999 review).
/// </summary>
/// <remarks>
/// Only the replay path is simulated; appends pass through, so the retained operations the handshake
/// finally delivers are real log entries. The prune is expressed as "every replay after the first
/// can only start at <see cref="PrunedMinimumCursor"/>", which is exactly what the in-memory log
/// reports once the requested cursor ages out of the retained window.
/// </remarks>
internal sealed class WindowAdvancesAfterFirstReplayOperationLog : ISavedMapOperationLogRepository
{
    /// <summary>Window base the log falls back to once the resume cursor has been pruned.</summary>
    public const long PrunedMinimumCursor = 2;

    private readonly ISavedMapOperationLogRepository _inner;
    private int _replayCount;

    public WindowAdvancesAfterFirstReplayOperationLog(ISavedMapOperationLogRepository inner) => _inner = inner;

    public bool SupportsReplicaSharedReplay => _inner.SupportsReplicaSharedReplay;

    public bool SupportsRestartDurableReplay => _inner.SupportsRestartDurableReplay;

    public Task<SavedMapOperationAppendResult> AppendAsync(
        SavedMapOperationAppendRequest request,
        CancellationToken cancellationToken = default) => _inner.AppendAsync(request, cancellationToken);

    public async Task<SavedMapOperationReplayResult> ReplayAsync(
        SavedMapId mapId,
        SavedMapOperationCursor sinceCursor,
        CancellationToken cancellationToken = default)
    {
        var ordinal = Interlocked.Increment(ref _replayCount);
        var result = await _inner.ReplayAsync(mapId, sinceCursor, cancellationToken).ConfigureAwait(false);

        // First replay: the client's cursor is still resumable, so the handshake sees a healthy
        // window and announces nothing.
        if (ordinal == 1 || sinceCursor.Value >= PrunedMinimumCursor)
        {
            return result;
        }

        return result with
        {
            Status = SavedMapOperationReplayStatus.ResyncRequired,
            MinimumReplayCursor = new SavedMapOperationCursor(PrunedMinimumCursor),
            Operations = [],
            Message = "The requested cursor is outside the retained replay window.",
        };
    }
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
