// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Collaboration.Operations;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Db.Postgres.Features.Collaboration.Operations;

/// <summary>
/// Singleton collaboration seam that resolves the scoped Postgres repository for each complete
/// operation. This keeps singleton session/coordinator consumers from capturing the scoped
/// connection provider while preserving one transaction and one scope per repository call.
/// </summary>
internal sealed class ScopedPostgresSavedMapOperationLogRepository(
    IServiceScopeFactory scopeFactory) : ISavedMapOperationLogRepository
{
    private readonly IServiceScopeFactory _scopeFactory =
        scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));

    /// <inheritdoc />
    public bool SupportsReplicaSharedReplay => true;

    /// <inheritdoc />
    public bool SupportsRestartDurableReplay => true;

    /// <inheritdoc />
    public bool SupportsRestartDurableCheckpointCursors => true;

    /// <inheritdoc />
    public bool SupportsRestartDurableCheckpointing => true;

    /// <inheritdoc />
    public Task<SavedMapOperationAppendResult> AppendAsync(
        SavedMapOperationAppendRequest request,
        CancellationToken cancellationToken = default) =>
        InvokeAsync(repository => repository.AppendAsync(request, cancellationToken));

    /// <inheritdoc />
    public Task<SavedMapOperationReplayResult> ReplayAsync(
        SavedMapId mapId,
        SavedMapOperationCursor sinceCursor,
        CancellationToken cancellationToken = default) =>
        InvokeAsync(repository => repository.ReplayAsync(mapId, sinceCursor, cancellationToken));

    /// <inheritdoc />
    public Task<SavedMapOperationReplayResult> ReplayPendingCheckpointAsync(
        SavedMapId mapId,
        CancellationToken cancellationToken = default) =>
        InvokeAsync(repository => repository.ReplayPendingCheckpointAsync(mapId, cancellationToken));

    /// <inheritdoc />
    public Task RecordCheckpointAsync(
        SavedMapId mapId,
        SavedMapOperationCursor checkpointCursor,
        CancellationToken cancellationToken = default) =>
        InvokeAsync(repository => repository.RecordCheckpointAsync(mapId, checkpointCursor, cancellationToken));

    private async Task<TResult> InvokeAsync<TResult>(
        Func<PostgresSavedMapOperationLogRepository, Task<TResult>> operation)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<PostgresSavedMapOperationLogRepository>();
        return await operation(repository).ConfigureAwait(false);
    }

    private async Task InvokeAsync(
        Func<PostgresSavedMapOperationLogRepository, Task> operation)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<PostgresSavedMapOperationLogRepository>();
        await operation(repository).ConfigureAwait(false);
    }
}
