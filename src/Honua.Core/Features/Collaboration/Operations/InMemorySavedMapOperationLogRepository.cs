// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Collaboration.Operations;

/// <summary>
/// In-memory saved-map operation log for development seams and unit tests.
/// </summary>
/// <remarks>
/// The store is scoped to one process. It preserves monotonic per-map cursors, operation-id and
/// idempotency-key dedupe, bounded replay, and conflict-policy dispatch without providing durable
/// multi-node coordination.
/// </remarks>
public sealed class InMemorySavedMapOperationLogRepository : ISavedMapOperationLogRepository
{
    private readonly object _gate = new();
    private readonly Dictionary<SavedMapId, MapLogState> _logs = [];
    private readonly ISavedMapOperationConflictPolicy _conflictPolicy;
    private readonly TimeProvider _timeProvider;
    private readonly int _retainedOperationCount;

    /// <summary>
    /// Creates an in-memory saved-map operation log repository.
    /// </summary>
    public InMemorySavedMapOperationLogRepository(
        ISavedMapOperationConflictPolicy? conflictPolicy = null,
        TimeProvider? timeProvider = null,
        int retainedOperationCount = 512)
    {
        if (retainedOperationCount < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(retainedOperationCount),
                retainedOperationCount,
                "Retained operation count must be at least one.");
        }

        _conflictPolicy = conflictPolicy ?? new MvpSavedMapOperationConflictPolicy();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _retainedOperationCount = retainedOperationCount;
    }

    /// <inheritdoc />
    public Task<SavedMapOperationAppendResult> AppendAsync(
        SavedMapOperationAppendRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var state = GetOrCreateState(request.MapId);
            var headCursor = new SavedMapOperationCursor(state.HeadCursor);

            if (state.ByOperationId.TryGetValue(request.OperationId, out var existingOperation) ||
                TryFindByIdempotencyKey(state, request.IdempotencyKey, out existingOperation))
            {
                return Task.FromResult(new SavedMapOperationAppendResult
                {
                    Status = SavedMapOperationAppendStatus.Accepted,
                    Operation = existingOperation,
                    HeadCursor = headCursor,
                    IsDuplicate = true,
                });
            }

            var windowCheck = CheckCursorWindow(state, request.BaseCursor);
            if (windowCheck is not null)
            {
                return Task.FromResult(windowCheck);
            }

            var concurrentOperations = state.Operations
                .Where(operation => operation.ServerCursor.Value > request.BaseCursor.Value)
                .ToArray();
            var conflict = _conflictPolicy.DetectConflict(request, concurrentOperations, headCursor);
            if (conflict is not null)
            {
                return Task.FromResult(new SavedMapOperationAppendResult
                {
                    Status = SavedMapOperationAppendStatus.Conflict,
                    Conflict = conflict,
                    HeadCursor = headCursor,
                    Message = conflict.Message,
                });
            }

            var nextCursor = new SavedMapOperationCursor(state.HeadCursor + 1);
            var operation = new SavedMapOperationEnvelope
            {
                OperationId = request.OperationId,
                MapId = request.MapId,
                ActorId = request.ActorId,
                BaseCursor = request.BaseCursor,
                Kind = request.Kind,
                Payload = ClonePayload(request.Payload),
                IdempotencyKey = request.IdempotencyKey,
                ServerCursor = nextCursor,
                AcceptedAt = _timeProvider.GetUtcNow(),
            };

            state.HeadCursor = nextCursor.Value;
            state.Operations.Add(operation);
            state.ByOperationId[operation.OperationId] = operation;
            if (!string.IsNullOrWhiteSpace(operation.IdempotencyKey))
            {
                state.ByIdempotencyKey[operation.IdempotencyKey] = operation;
            }

            PruneRetainedOperations(state);

            return Task.FromResult(new SavedMapOperationAppendResult
            {
                Status = SavedMapOperationAppendStatus.Accepted,
                Operation = operation,
                HeadCursor = nextCursor,
            });
        }
    }

    /// <inheritdoc />
    public Task<SavedMapOperationReplayResult> ReplayAsync(
        SavedMapId mapId,
        SavedMapOperationCursor sinceCursor,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (!_logs.TryGetValue(mapId, out var state))
            {
                var empty = SavedMapOperationCursor.Empty;
                if (sinceCursor.Value == 0)
                {
                    return Task.FromResult(new SavedMapOperationReplayResult
                    {
                        Status = SavedMapOperationReplayStatus.Ok,
                        SinceCursor = sinceCursor,
                        HeadCursor = empty,
                        MinimumReplayCursor = empty,
                        Operations = [],
                    });
                }

                return Task.FromResult(BuildReplayResyncRequired(
                    sinceCursor,
                    empty,
                    empty,
                    "Cursor is not known for this saved map."));
            }

            var headCursor = new SavedMapOperationCursor(state.HeadCursor);
            var minimumReplayCursor = GetMinimumReplayCursor(state);
            if (sinceCursor.Value > state.HeadCursor || sinceCursor.Value < minimumReplayCursor.Value)
            {
                return Task.FromResult(BuildReplayResyncRequired(
                    sinceCursor,
                    headCursor,
                    minimumReplayCursor,
                    "Cursor is outside the retained operation-log replay window."));
            }

            var operations = state.Operations
                .Where(operation => operation.ServerCursor.Value > sinceCursor.Value)
                .OrderBy(operation => operation.ServerCursor.Value)
                .ToArray();

            return Task.FromResult(new SavedMapOperationReplayResult
            {
                Status = SavedMapOperationReplayStatus.Ok,
                SinceCursor = sinceCursor,
                HeadCursor = headCursor,
                MinimumReplayCursor = minimumReplayCursor,
                Operations = operations,
            });
        }
    }

    private MapLogState GetOrCreateState(SavedMapId mapId)
    {
        if (!_logs.TryGetValue(mapId, out var state))
        {
            state = new MapLogState();
            _logs.Add(mapId, state);
        }

        return state;
    }

    private static SavedMapOperationAppendResult? CheckCursorWindow(
        MapLogState state,
        SavedMapOperationCursor baseCursor)
    {
        var headCursor = new SavedMapOperationCursor(state.HeadCursor);
        var minimumReplayCursor = GetMinimumReplayCursor(state);
        if (baseCursor.Value <= state.HeadCursor && baseCursor.Value >= minimumReplayCursor.Value)
        {
            return null;
        }

        return new SavedMapOperationAppendResult
        {
            Status = SavedMapOperationAppendStatus.ResyncRequired,
            HeadCursor = headCursor,
            Message = "Base cursor is outside the retained operation-log replay window.",
        };
    }

    private static SavedMapOperationCursor GetMinimumReplayCursor(MapLogState state)
    {
        if (state.Operations.Count == 0)
        {
            return SavedMapOperationCursor.Empty;
        }

        return new SavedMapOperationCursor(state.Operations[0].ServerCursor.Value - 1);
    }

    private void PruneRetainedOperations(MapLogState state)
    {
        var excess = state.Operations.Count - _retainedOperationCount;
        if (excess > 0)
        {
            foreach (var prunedOperation in state.Operations.Take(excess))
            {
                state.ByOperationId.Remove(prunedOperation.OperationId);
                if (!string.IsNullOrWhiteSpace(prunedOperation.IdempotencyKey))
                {
                    state.ByIdempotencyKey.Remove(prunedOperation.IdempotencyKey);
                }
            }

            state.Operations.RemoveRange(0, excess);
        }
    }

    private static bool TryFindByIdempotencyKey(
        MapLogState state,
        string? idempotencyKey,
        out SavedMapOperationEnvelope operation)
    {
        if (!string.IsNullOrWhiteSpace(idempotencyKey) &&
            state.ByIdempotencyKey.TryGetValue(idempotencyKey, out operation!))
        {
            return true;
        }

        operation = null!;
        return false;
    }

    private static System.Text.Json.JsonElement ClonePayload(System.Text.Json.JsonElement payload) =>
        payload.ValueKind == System.Text.Json.JsonValueKind.Undefined ? default : payload.Clone();

    private static SavedMapOperationReplayResult BuildReplayResyncRequired(
        SavedMapOperationCursor sinceCursor,
        SavedMapOperationCursor headCursor,
        SavedMapOperationCursor minimumReplayCursor,
        string message) => new()
        {
            Status = SavedMapOperationReplayStatus.ResyncRequired,
            SinceCursor = sinceCursor,
            HeadCursor = headCursor,
            MinimumReplayCursor = minimumReplayCursor,
            Operations = [],
            Message = message,
        };

    private sealed class MapLogState
    {
        public List<SavedMapOperationEnvelope> Operations { get; } = [];

        public Dictionary<SavedMapOperationId, SavedMapOperationEnvelope> ByOperationId { get; } = [];

        public Dictionary<string, SavedMapOperationEnvelope> ByIdempotencyKey { get; } = new(StringComparer.Ordinal);

        public long HeadCursor { get; set; }
    }
}
