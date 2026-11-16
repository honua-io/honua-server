// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Attachments.Domain;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Npgsql;

namespace Honua.Server.Tests;

public partial class PostgresAttachmentStoreTests
{
    [IntegrationTest]
    [Operation(Operations.AddAttachment)]
    public async Task CommitSession_ConcurrentParentDeletion_WaitsAndCapturesPublishedAttachment()
    {
        var store = await CreateUploadSessionStoreAsync();
        var key = await PrepareCommitSessionAsync(store, CreateSessionRequest());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commit = store.CommitAsync(key, async (session, cancellationToken) =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(cancellationToken);
            return AttachmentUploadObjectKey.ForSession(session.SessionId);
        });
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = ParentDeleteCommand(connection, transaction);
        Task<int>? delete = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            delete = command.ExecuteNonQueryAsync();
            await WaitForAttachmentRowLockAsync(connection.ProcessID);
        }
        finally
        {
            release.TrySetResult();
        }
        var published = await commit.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(delete);
        Assert.Equal(1, await delete.WaitAsync(TimeSpan.FromSeconds(10)));
        var removed = await _attachmentStore.DeleteRowsAsync(connection, transaction, TestLayerId, TestFeatureId, CancellationToken.None);
        Assert.Equal(published.StoragePath, Assert.Single(removed).StoragePath);
        await transaction.CommitAsync();
        Assert.Empty(await _attachmentStore.ListAsync(TestLayerId, TestFeatureId));
    }

    [IntegrationTest]

    [Operation(Operations.AddAttachment)]
    public async Task CommitSession_ParentDeletionWins_DoesNotPublishContent()
    {
        var store = await CreateUploadSessionStoreAsync();
        var key = await PrepareCommitSessionAsync(store, CreateSessionRequest());
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = ParentDeleteCommand(connection, transaction);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
        var published = false;
        var commit = store.CommitAsync(key, (session, _) =>
        {
            published = true;
            return Task.FromResult(AttachmentUploadObjectKey.ForSession(session.SessionId));
        });
        // Observe the real lock dependency before letting the deleting transaction win.
        await WaitForParentDeleteBlockerAsync(connection.ProcessID);
        await transaction.CommitAsync();
        var failure = await Assert.ThrowsAsync<AttachmentUploadException>(() => commit);
        Assert.Equal(AttachmentUploadFailure.NotFound, failure.Failure);
        Assert.False(published);
        Assert.Equal(AttachmentUploadState.Uploading, (await store.GetAsync(key)).State);
        Assert.Empty(await _attachmentStore.ListAsync(TestLayerId, TestFeatureId));
    }

    private NpgsqlCommand ParentDeleteCommand(NpgsqlConnection connection, NpgsqlTransaction transaction)
        => new($"DELETE FROM \"{_schemaName}\".features WHERE layer_id = {TestLayerId} AND objectid = {TestFeatureId}", connection, transaction);

    private Task WaitForAttachmentRowLockAsync(int blockedPid)
        => WaitForDatabaseLockAsync("SELECT cardinality(pg_blocking_pids(@pid)) > 0", blockedPid);

    private Task WaitForParentDeleteBlockerAsync(int blockerPid)
        => WaitForDatabaseLockAsync("SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE @pid = ANY(pg_blocking_pids(pid)))", blockerPid);

    private async Task WaitForDatabaseLockAsync(string sql, int pid)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var connection = await _fixture.DataSource.OpenConnectionAsync(timeout.Token);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("pid", pid);
        while (await command.ExecuteScalarAsync(timeout.Token) is not true)
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}
