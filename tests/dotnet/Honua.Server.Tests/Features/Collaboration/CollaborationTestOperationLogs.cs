// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Collaboration.Operations;

namespace Honua.Server.Tests.Features.Collaboration;

internal sealed class RestartDurableSavedMapOperationLog : ISavedMapOperationLogRepository
{
    private readonly ISavedMapOperationLogRepository _inner;

    public RestartDurableSavedMapOperationLog(ISavedMapOperationLogRepository inner) => _inner = inner;

    public bool SupportsReplicaSharedReplay => _inner.SupportsReplicaSharedReplay;

    public bool SupportsRestartDurableReplay => true;

    public bool SupportsRestartDurableCheckpointCursors => true;

    public Task<SavedMapOperationAppendResult> AppendAsync(
        SavedMapOperationAppendRequest request,
        CancellationToken cancellationToken = default) => _inner.AppendAsync(request, cancellationToken);

    public Task<SavedMapOperationReplayResult> ReplayAsync(
        SavedMapId mapId,
        SavedMapOperationCursor sinceCursor,
        CancellationToken cancellationToken = default) => _inner.ReplayAsync(mapId, sinceCursor, cancellationToken);

    public Task<SavedMapOperationReplayResult> ReplayPendingCheckpointAsync(
        SavedMapId mapId,
        CancellationToken cancellationToken = default) =>
        _inner.ReplayPendingCheckpointAsync(mapId, cancellationToken);

    public Task RecordCheckpointAsync(
        SavedMapId mapId,
        SavedMapOperationCursor checkpointCursor,
        CancellationToken cancellationToken = default) =>
        _inner.RecordCheckpointAsync(mapId, checkpointCursor, cancellationToken);
}

internal sealed class FailFirstCheckpointRecordOperationLog : ISavedMapOperationLogRepository
{
    private readonly ISavedMapOperationLogRepository _inner;
    private int _remainingFailures = 1;

    public FailFirstCheckpointRecordOperationLog(ISavedMapOperationLogRepository inner) =>
        _inner = inner;

    public bool SupportsReplicaSharedReplay => _inner.SupportsReplicaSharedReplay;

    public bool SupportsRestartDurableReplay => _inner.SupportsRestartDurableReplay;

    public bool SupportsRestartDurableCheckpointCursors =>
        _inner.SupportsRestartDurableCheckpointCursors;

    public bool SupportsRestartDurableCheckpointing =>
        _inner.SupportsRestartDurableCheckpointing;

    public Task<SavedMapOperationAppendResult> AppendAsync(
        SavedMapOperationAppendRequest request,
        CancellationToken cancellationToken = default) =>
        _inner.AppendAsync(request, cancellationToken);

    public Task<SavedMapOperationReplayResult> ReplayAsync(
        SavedMapId mapId,
        SavedMapOperationCursor sinceCursor,
        CancellationToken cancellationToken = default) =>
        _inner.ReplayAsync(mapId, sinceCursor, cancellationToken);

    public Task<SavedMapOperationReplayResult> ReplayPendingCheckpointAsync(
        SavedMapId mapId,
        CancellationToken cancellationToken = default) =>
        _inner.ReplayPendingCheckpointAsync(mapId, cancellationToken);

    public Task RecordCheckpointAsync(
        SavedMapId mapId,
        SavedMapOperationCursor checkpointCursor,
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _remainingFailures, 0) != 0)
        {
            throw new InvalidOperationException("Simulated checkpoint cursor persistence failure.");
        }

        return _inner.RecordCheckpointAsync(mapId, checkpointCursor, cancellationToken);
    }
}

internal sealed class WindowAdvancesAfterFirstReplayOperationLog : ISavedMapOperationLogRepository
{
    public const long PrunedMinimumCursor = 2;

    private readonly ISavedMapOperationLogRepository _inner;
    private int _replayCount;

    public WindowAdvancesAfterFirstReplayOperationLog(ISavedMapOperationLogRepository inner) => _inner = inner;

    public bool SupportsReplicaSharedReplay => _inner.SupportsReplicaSharedReplay;

    public bool SupportsRestartDurableReplay => _inner.SupportsRestartDurableReplay;

    public bool SupportsRestartDurableCheckpointCursors =>
        _inner.SupportsRestartDurableCheckpointCursors;

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

    public Task<SavedMapOperationReplayResult> ReplayPendingCheckpointAsync(
        SavedMapId mapId,
        CancellationToken cancellationToken = default) =>
        _inner.ReplayPendingCheckpointAsync(mapId, cancellationToken);

    public Task RecordCheckpointAsync(
        SavedMapId mapId,
        SavedMapOperationCursor checkpointCursor,
        CancellationToken cancellationToken = default) =>
        _inner.RecordCheckpointAsync(mapId, checkpointCursor, cancellationToken);
}

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

    public Task FirstAppendAssigned => _firstAppendAssigned.Task;

    public bool SupportsReplicaSharedReplay => _inner.SupportsReplicaSharedReplay;

    public bool SupportsRestartDurableReplay => _inner.SupportsRestartDurableReplay;

    public bool SupportsRestartDurableCheckpointCursors =>
        _inner.SupportsRestartDurableCheckpointCursors;

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

    public Task<SavedMapOperationReplayResult> ReplayPendingCheckpointAsync(
        SavedMapId mapId,
        CancellationToken cancellationToken = default) =>
        _inner.ReplayPendingCheckpointAsync(mapId, cancellationToken);

    public Task RecordCheckpointAsync(
        SavedMapId mapId,
        SavedMapOperationCursor checkpointCursor,
        CancellationToken cancellationToken = default) =>
        _inner.RecordCheckpointAsync(mapId, checkpointCursor, cancellationToken);
}
