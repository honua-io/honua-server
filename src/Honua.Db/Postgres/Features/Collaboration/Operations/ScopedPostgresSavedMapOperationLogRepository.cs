// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Collaboration.Operations;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Db.Postgres.Features.Collaboration.Operations;

internal sealed class ScopedPostgresSavedMapOperationLogRepository(
    IServiceScopeFactory scopeFactory) : ISavedMapOperationLogRepository
{
    private readonly IServiceScopeFactory _scopeFactory =
        scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));

    public bool SupportsReplicaSharedReplay => true;

    public bool SupportsRestartDurableReplay => true;

    public bool SupportsRestartDurableCheckpointCursors => true;

    public bool SupportsRestartDurableCheckpointing => true;

    public Task<SavedMapOperationAppendResult> AppendAsync(
        SavedMapOperationAppendRequest request,
        CancellationToken cancellationToken = default) =>
        InvokeAsync(repository => repository.AppendAsync(request, cancellationToken));

    public Task<SavedMapOperationReplayResult> ReplayAsync(
        SavedMapId mapId,
        SavedMapOperationCursor sinceCursor,
        CancellationToken cancellationToken = default) =>
        InvokeAsync(repository => repository.ReplayAsync(mapId, sinceCursor, cancellationToken));

    public Task<SavedMapOperationReplayResult> ReplayPendingCheckpointAsync(
        SavedMapId mapId,
        CancellationToken cancellationToken = default) =>
        InvokeAsync(repository => repository.ReplayPendingCheckpointAsync(mapId, cancellationToken));

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
