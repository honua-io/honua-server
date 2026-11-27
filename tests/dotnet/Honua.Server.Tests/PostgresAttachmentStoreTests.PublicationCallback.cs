// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Attachments.Domain;
using Honua.Db.Postgres.Features.Attachments;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests;

public partial class PostgresAttachmentStoreTests
{
    [IntegrationTest]
    [Operation(Operations.AddAttachment)]
    public async Task CommitSession_ConcurrentPublicationCallbacks_InvokesPublisherOnce()
    {
        var store = await CreateUploadSessionStoreAsync();
        var key = await PrepareCommitSessionAsync(store, CreateSessionRequest());
        var calls = 0;
        Task<string> Publish(AttachmentUploadSession session, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            Assert.Equal(key.SessionId, session.SessionId);
            return Task.FromResult(AttachmentUploadObjectKey.ForSession(session.SessionId));
        }
        var results = await Task.WhenAll(store.CommitAsync(key, Publish), NewUploadSessionStore().CommitAsync(key, Publish));
        Assert.Equal(1, calls);
        Assert.Equivalent(results[0], results[1]);
    }

    [IntegrationTest]

    [Operation(Operations.AddAttachment)]
    public async Task CommitSession_PublisherFailure_RetainsStagingForRetry()
    {
        var store = await CreateUploadSessionStoreAsync();
        var key = await PrepareCommitSessionAsync(store, CreateSessionRequest());
        await Assert.ThrowsAsync<IOException>(() => store.CommitAsync(key,
            (_, _) => Task.FromException<string>(new IOException("Publication unavailable."))));
        Assert.Equal(AttachmentUploadState.Uploading, (await store.GetAsync(key)).State);
        Assert.Empty(await _attachmentStore.ListAsync(TestLayerId, TestFeatureId));
        await using var content = new MemoryStream();
        await store.CopyContentAsync(key, content);
        Assert.Equal("abcdef"u8.ToArray(), content.ToArray());
        var published = await store.CommitAsync(key, (session, _) => Task.FromResult(AttachmentUploadObjectKey.ForSession(session.SessionId)));
        Assert.Equal(published with { NativeMetadata = null }, Assert.Single(await _attachmentStore.ListAsync(TestLayerId, TestFeatureId)));
    }

    [IntegrationTest]

    [Operation(Operations.DeleteAttachments)]
    public async Task AttachmentMaintenance_ExpiredUncommittedPublication_RemovesUnreferencedBlob()
    {
        var store = await CreateUploadSessionStoreAsync();
        var key = await PrepareCommitSessionAsync(store, CreateSessionRequest());
        var objectKey = AttachmentUploadObjectKey.ForSession(key.SessionId);
        await using var content = new MemoryStream("abcdef"u8.ToArray());
        var uploaded = await _fileStorage.UploadIfMatchAsync(new Honua.Core.Features.Infrastructure.Domain.FileUploadRequest
        {
            Content = content,
            FileName = "notes.txt",
            ContentType = "text/plain",
            ObjectKeyOverride = objectKey
        }, null);
        Assert.True(uploaded.Success);
        await _fixture.ExecuteAsync($"""
            UPDATE "{_schemaName}".attachment_upload_sessions
            SET created_at = clock_timestamp() - interval '2 hours', expires_at = clock_timestamp() - interval '1 hour'
            WHERE session_id = '{key.SessionId}';
            """);
        var maintenance = new PostgresAttachmentMaintenance(
            new TestDatabaseConnectionProvider(_fixture.DataSource), _schemaName, _attachmentStore);
        await maintenance.RunOnceAsync();
        Assert.False(await _fileStorage.ExistsAsync(objectKey));
        await AssertMaintenanceQueuesEmptyAsync(key.SessionId);
    }
    [IntegrationTest]
    [Operation(Operations.DeleteAttachments)]
    public async Task AttachmentMaintenance_LateUploadAfterExpiry_IsRemovedOnLaterCleanupPass()
    {
        var store = await CreateUploadSessionStoreAsync();
        var key = await PrepareCommitSessionAsync(store, CreateSessionRequest());
        var objectKey = AttachmentUploadObjectKey.ForSession(key.SessionId);
        await _fixture.ExecuteAsync($"""
            UPDATE "{_schemaName}".attachment_upload_sessions
            SET created_at = clock_timestamp() - interval '2 hours', expires_at = clock_timestamp() - interval '1 hour'
            WHERE session_id = '{key.SessionId}';
            """);
        var maintenance = new PostgresAttachmentMaintenance(
            new TestDatabaseConnectionProvider(_fixture.DataSource), _schemaName, _attachmentStore);
        await maintenance.RunOnceAsync();
        await using var content = new MemoryStream("abcdef"u8.ToArray());
        var uploaded = await _fileStorage.UploadIfMatchAsync(new Honua.Core.Features.Infrastructure.Domain.FileUploadRequest
        {
            Content = content,
            FileName = "notes.txt",
            ContentType = "text/plain",
            ObjectKeyOverride = objectKey
        }, null);
        Assert.True(uploaded.Success);
        await _fixture.ExecuteAsync($"""
            UPDATE "{_schemaName}".attachment_upload_cleanup_ledger
            SET next_attempt_at = clock_timestamp() - interval '1 second'
            WHERE session_id = '{key.SessionId}';
            """);
        await maintenance.RunOnceAsync();
        Assert.False(await _fileStorage.ExistsAsync(objectKey));
        await AssertMaintenanceQueuesEmptyAsync(key.SessionId);
    }

    [IntegrationTest]
    [Operation(Operations.AddAttachment)]
    public async Task CommitSession_PublishCallback_RunsOutsideOpenDatabaseConnectionAndTransaction()
    {
        await CreateUploadSessionStoreAsync();
        var trackingProvider = new TrackingDatabaseConnectionProvider(_fixture.DataSource);
        var store = new PostgresAttachmentUploadSessionStore(trackingProvider, _schemaName, _attachmentStore);
        var key = await PrepareCommitSessionAsync(store, CreateSessionRequest());

        var activeConnectionsDuringPublish = -1;
        var published = await store.CommitAsync(key, (session, _) =>
        {
            activeConnectionsDuringPublish = trackingProvider.ActiveConnections;
            return Task.FromResult(AttachmentUploadObjectKey.ForSession(session.SessionId));
        });

        Assert.Equal(0, activeConnectionsDuringPublish);
        Assert.Equal(0, trackingProvider.ActiveConnections);
        Assert.Equal(published with { NativeMetadata = null }, Assert.Single(await _attachmentStore.ListAsync(TestLayerId, TestFeatureId)));
    }

    [IntegrationTest]
    [Operation(Operations.AddAttachment)]
    public async Task CommitSession_ParentDeletedBeforeCommit_RecordsOrphanInCleanupLedger()
    {
        var store = await CreateUploadSessionStoreAsync();
        var key = await PrepareCommitSessionAsync(store, CreateSessionRequest());
        var objectKey = AttachmentUploadObjectKey.ForSession(key.SessionId);

        var failure = await Assert.ThrowsAsync<AttachmentUploadException>(() => store.CommitAsync(key, async (_, token) =>
        {
            await using var content = new MemoryStream("abcdef"u8.ToArray());
            var uploaded = await _fileStorage.UploadIfMatchAsync(new Honua.Core.Features.Infrastructure.Domain.FileUploadRequest
            {
                Content = content,
                FileName = "notes.txt",
                ContentType = "text/plain",
                ObjectKeyOverride = objectKey
            }, null, token);
            Assert.True(uploaded.Success);

            // Simulate concurrent parent feature deletion while cloud upload was in flight.
            await _fixture.ExecuteAsync($"""DELETE FROM "{_schemaName}".features WHERE objectid = {TestFeatureId};""");
            return objectKey;
        }));

        Assert.Equal(AttachmentUploadFailure.NotFound, failure.Failure);

        await using var conn = await _fixture.DataSource.OpenConnectionAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(
            $"""SELECT count(*) FROM "{_schemaName}".attachment_upload_cleanup_ledger WHERE session_id = @id""",
            conn);
        cmd.Parameters.AddWithValue("id", key.SessionId);
        Assert.Equal(1L, await cmd.ExecuteScalarAsync());

        // Maintenance immediately drains the orphaned blob because the failure was terminal.
        await new PostgresAttachmentMaintenance(
            new TestDatabaseConnectionProvider(_fixture.DataSource), _schemaName, _attachmentStore).RunOnceAsync();
        Assert.False(await _fileStorage.ExistsAsync(objectKey));
    }

    private sealed class TrackingDatabaseConnectionProvider(Npgsql.NpgsqlDataSource dataSource)
        : Honua.Core.Features.Infrastructure.Abstractions.IAdoNetDatabaseConnectionProvider
    {
        private int _activeConnections;
        public int ActiveConnections => Volatile.Read(ref _activeConnections);
        public string GetConnectionString() => dataSource.ConnectionString;

        public async Task<System.Data.Common.DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
        {
            var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            Interlocked.Increment(ref _activeConnections);
            return new Honua.Db.Postgres.Features.Infrastructure.SemaphoreReleasingConnection(
                connection,
                () => Interlocked.Decrement(ref _activeConnections));
        }

        public async Task<(System.Data.Common.DbConnection Connection, System.Data.Common.DbTransaction Transaction)> OpenTransactionAsync(
            System.Data.IsolationLevel isolationLevel = System.Data.IsolationLevel.RepeatableRead,
            CancellationToken cancellationToken = default)
        {
            var connection = await OpenConnectionAsync(cancellationToken);
            return (connection, await connection.BeginTransactionAsync(isolationLevel, cancellationToken));
        }

        public Task<T> ExecuteWithDeadlockRetryAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken = default) => operation();
        public Task ExecuteWithDeadlockRetryAsync(Func<Task> operation, CancellationToken cancellationToken = default) => operation();
    }

}
